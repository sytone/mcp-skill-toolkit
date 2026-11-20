using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class DaemonTests
{
    [TestMethod]
    public async Task ForegroundProcessAndOutputCloseWithoutWaitingForDaemonIdle()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig();
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        var process = workspace.StartCli(["info", "offline"]);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExitAsync();
        Assert.AreSame(exited, await Task.WhenAny(exited, Task.Delay(5000)));
        var drained = Task.WhenAll(stdout, stderr);
        Assert.IsTrue(await Task.WhenAny(drained, Task.Delay(5000)) == drained,
            "Foreground exited, but a daemon inherited the foreground stdout/stderr pipe");
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    public async Task SeparateCliInvocationsShareConnectionUntilConfigurationChanges()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_VALUE"] = "first" });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = null;
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.EnvironmentVariables["MCP_DEBUG"] = "1";
        workspace.TrackDaemon("offline", server);
        var info = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(0, info.ExitCode);
        StringAssert.Contains(info.Stderr, "Using daemon connection");
        var first = await workspace.RunAsync(["call", "offline", "state", "{}"]);
        Assert.AreEqual(0, first.ExitCode);
        var state1 = JsonNode.Parse(first.Stdout)!;
        Assert.IsTrue(state1["lists"]!.GetValue<int>() == 1, $"Info: {info.Stderr}\nCall: {first.Stderr}");
        var second = await workspace.RunAsync(["call", "offline", "state", "{}"]);
        Assert.AreEqual(0, second.ExitCode);
        var state2 = JsonNode.Parse(second.Stdout)!;
        Assert.AreEqual(state1["pid"]!.ToString(), state2["pid"]!.ToString());
        Assert.AreEqual(2, state2["calls"]!.GetValue<int>());
        var changed = new ServerConfig(server.Value.DeepClone().AsObject());
        changed.Value["env"]!["FAKE_VALUE"] = "changed";
        workspace.TrackDaemon("offline", changed);
        await workspace.WriteConfigAsync(("offline", changed));
        var third = await workspace.RunAsync(["call", "offline", "state", "{}"]);
        Assert.AreEqual(0, third.ExitCode);
        var state3 = JsonNode.Parse(third.Stdout)!;
        Assert.AreNotEqual(state1["pid"]!.ToString(), state3["pid"]!.ToString());
        Assert.AreEqual("changed", state3["env"]!.ToString());
    }

    [TestMethod]
    public async Task DaemonFramesLargeResponsesAndDoesNotExpireDuringLongCalls()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig();
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "1";
        workspace.EnvironmentVariables["MCP_DEBUG"] = "1";
        workspace.TrackDaemon("offline", server);
        var longCall = await workspace.RunAsync(["call", "offline", "slow", "{\"delay\":5500}"]);
        Assert.AreEqual(0, longCall.ExitCode);
        StringAssert.Contains(longCall.Stderr, "Using daemon connection");
        Assert.AreEqual("finished", longCall.Stdout.Trim());
        var text = new string('x', 200000) + "\u96ea";
        var result = await workspace.RunAsync(["call", "offline/echo"], new JsonObject { ["text"] = text }.ToJsonString());
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(text, result.Stdout.TrimEnd('\r', '\n'));
        await Task.Delay(3000);
        Assert.IsFalse(Directory.EnumerateFiles(workspace.Root, "server-pids.txt.*.alive").Any(),
            "Idle daemon must close its server process");
    }

    [TestMethod]
    public async Task ConcurrentStartupAndRequestsUseOneDaemon()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig();
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            workspace.RunAsync(["call", "offline", "state", "{}"])));
        foreach (var result in results)
        {
            Assert.AreEqual(0, result.ExitCode, result.Stderr);
            Assert.AreEqual("", result.Stderr, "A losing worker must not report a fatal startup error.");
        }
        Assert.AreEqual(1, results.Select(r => JsonNode.Parse(r.Stdout)!["pid"]!.ToString()).Distinct().Count());
    }

    [TestMethod]
    public async Task RoundTwoCachedRequestsUseTheCallingCliTimeout()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "1";
        var result = await workspace.RunAsync(["call", "offline", "cancellable", "{}"]);
        Assert.AreEqual(2, result.ExitCode);
        Assert.IsTrue(await WaitForMessageAsync(log, "fixture/cancelled"), "A warm worker must not keep its original longer timeout.");
        Assert.IsFalse((await File.ReadAllLinesAsync(log)).Any(l => JsonNode.Parse(l)!["method"]?.ToString() == "fixture/completed"));
    }

    [TestMethod]
    public async Task RoundTwoDisconnectedCliCancelsOnlyItsOwnCachedRequest()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        var process = workspace.StartCli(["call", "offline", "cancellable", "{}"]);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(await WaitForMessageAsync(log, "tools/call"));
        process.Kill();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        Assert.IsTrue(await WaitForMessageAsync(log, "fixture/cancelled"), "Closing the caller must cancel its server operation.");
        var next = await workspace.RunAsync(["call", "offline", "echo", "{\"text\":\"worker alive\"}"]);
        Assert.AreEqual(0, next.ExitCode, next.Stderr);
        Assert.AreEqual("worker alive", next.Stdout.Trim());
    }

    [TestMethod]
    public async Task RoundTwoCachedRequestsCanIncreaseTheirTimeout()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig();
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "2";
        workspace.TrackDaemon("offline", server);
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "6";
        var result = await workspace.RunAsync(["call", "offline", "slow", "{\"delay\":3000}"]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual("finished", result.Stdout.Trim());
    }

    [TestMethod]
    public async Task RoundTwoCachedRequestsHonorDisabledRetries()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.EnvironmentVariables["MCP_MAX_RETRIES"] = "1";
        workspace.EnvironmentVariables["MCP_RETRY_DELAY"] = "1";
        workspace.TrackDaemon("offline", server);
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        workspace.EnvironmentVariables["MCP_MAX_RETRIES"] = "0";
        var result = await workspace.RunAsync(["call", "offline", "transient_once", "{}"]);
        Assert.AreEqual(2, result.ExitCode, result.Stdout);
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(log)).Count(l => JsonNode.Parse(l)!["method"]?.ToString() == "tools/call"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task HeldUnresponsiveEndpointFallbackDoesNotReportFatalDaemonFailure()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.TrackDaemon("offline", server);

        // Own the exact child CLI endpoint before launch. Never accept or answer pings:
        // the first-instance worker start must fail, and readiness cannot succeed.
        // This is a held, unresponsive endpoint, not a race between healthy workers.
        using var heldEndpoint = HoldDaemonEndpoint(workspace, server);
        var result = await CallStateExactlyOnceAsync(workspace, log);

        Assert.IsFalse(result.Stderr.Contains("Daemon failed:", StringComparison.Ordinal),
            $"Successful direct fallback must not report a fatal worker failure. Stderr: {result.Stderr}");
        Assert.AreEqual("", result.Stderr, "Debug-off successful fallback should be quiet.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task HeldUnresponsiveEndpointWithDaemonDisabledSucceedsWithCleanStderr()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "1";
        using var heldEndpoint = HoldDaemonEndpoint(workspace, server);
        var result = await CallStateExactlyOnceAsync(workspace, log);
        Assert.AreEqual("", result.Stderr, "Direct mode must ignore the held daemon endpoint.");
    }

    [TestMethod]
    public async Task HealthyDaemonWithDebugDisabledSucceedsWithCleanStderr()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        var info = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(0, info.ExitCode, info.Stderr);
        Assert.AreEqual("", info.Stderr);
        var result = await CallStateExactlyOnceAsync(workspace, log);
        Assert.AreEqual(1, JsonNode.Parse(result.Stdout)!["lists"]!.GetValue<int>(),
            "The second CLI must reuse the connection that handled info.");
        Assert.AreEqual("", result.Stderr);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task HeldUnresponsiveEndpointWithDebugEnabledPreservesWorkerDiagnosis()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DEBUG"] = "1";
        workspace.TrackDaemon("offline", server);
        using var heldEndpoint = HoldDaemonEndpoint(workspace, server);
        var result = await CallStateExactlyOnceAsync(workspace, log);
        StringAssert.Contains(result.Stderr, "[mcp-cli] Daemon failed: ");
        StringAssert.Contains(result.Stderr, "falling back to direct");
        StringAssert.Contains(result.Stderr, "Using direct connection for offline");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task HeldUnresponsiveEndpointFallbackConnectionFailureRemainsActionable()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig(new JsonObject
        {
            ["FAKE_PROTOCOL"] = "unsupported", ["FAKE_STDERR"] = "Local authentication prompt"
        });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.TrackDaemon("offline", server);
        using var heldEndpoint = HoldDaemonEndpoint(workspace, server);
        var result = await workspace.RunAsync(["call", "offline", "state", "{}"]);
        Assert.AreEqual(3, result.ExitCode, result.Stderr);
        Assert.AreEqual("", result.Stdout);
        StringAssert.Contains(result.Stderr, "SERVER_CONNECTION_FAILED");
        StringAssert.Contains(result.Stderr, "Invalid or unsupported MCP initialize response");
        StringAssert.Contains(result.Stderr, "[offline] Local authentication prompt");
        Assert.IsFalse(result.Stderr.Contains("Daemon failed:", StringComparison.Ordinal), result.Stderr);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task HeldUnresponsiveEndpointFallbackToolFailureIsNotReplayed()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.TrackDaemon("offline", server);
        using var heldEndpoint = HoldDaemonEndpoint(workspace, server);
        var result = await workspace.RunAsync(["call", "offline", "error_code_text", "{}"]);
        Assert.AreEqual(2, result.ExitCode, result.Stderr);
        Assert.AreEqual("", result.Stdout);
        StringAssert.Contains(result.Stderr, "TOOL_EXECUTION_FAILED");
        StringAssert.Contains(result.Stderr, "EPIPE while validating input");
        Assert.IsFalse(result.Stderr.Contains("Daemon failed:", StringComparison.Ordinal), result.Stderr);
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(log)).Count(line =>
            JsonNode.Parse(line)!["method"]?.ToString() == "tools/call"));
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt"))).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ServerStderrIsForwardedOnceDuringDaemonStartupOrFallback(bool holdEndpoint)
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        // These are server messages, even when they resemble the worker's old error.
        const string diagnostic = "Local authentication prompt; Daemon failed: server-owned diagnostic";
        var server = workspace.StdioConfig(new JsonObject
        {
            ["FAKE_REQUEST_LOG"] = log, ["FAKE_STDERR"] = diagnostic
        });
        await workspace.WriteConfigAsync(("offline", server));
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.TrackDaemon("offline", server);
        using var heldEndpoint = holdEndpoint ? HoldDaemonEndpoint(workspace, server) : null;
        var result = await CallStateExactlyOnceAsync(workspace, log);
        Assert.AreEqual("[offline] " + diagnostic, result.Stderr.TrimEnd('\r', '\n'),
            "Server stderr must not be hidden, filtered by message text, or forwarded twice.");
    }

    private static NamedPipeServerStream HoldDaemonEndpoint(TestWorkspace workspace, ServerConfig server)
    {
        var settings = new RuntimeSettings { WorkingDirectory = workspace.Root };
        var applicationDirectory = Path.GetDirectoryName(TestWorkspace.CliPath) + Path.DirectorySeparatorChar;
        var pipe = Daemon.PipeName("offline", server, settings, applicationDirectory);
        // Declared after workspace so this handle closes before workspace daemon cleanup.
        return new NamedPipeServerStream(pipe, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
    }

    private static async Task<CliResult> CallStateExactlyOnceAsync(TestWorkspace workspace, string log)
    {
        // RunAsync bounds stdin, process exit, stdout and stderr together (30 seconds).
        var result = await workspace.RunAsync(["call", "offline", "state", "{}"]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var state = JsonNode.Parse(result.Stdout)!.AsObject();
        Assert.AreEqual(1, state["calls"]!.GetValue<int>(), "The tool must execute once.");
        Assert.AreEqual(workspace.Root, state["cwd"]!.GetValue<string>());
        var requests = (await File.ReadAllLinesAsync(log)).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.AreEqual(1, requests.Count(request => request["method"]?.ToString() == "tools/call"),
            "The request log must show no replay, including across fixture instances.");
        var serverPids = await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt"));
        Assert.AreEqual(1, serverPids.Length, "Only one stdio fixture should have started.");
        Assert.AreEqual(state["pid"]!.ToString(), serverPids[0]);
        return result;
    }

    private static async Task<bool> WaitForMessageAsync(string log, string method)
    {
        using var timeout = new CancellationTokenSource(3000);
        while (!timeout.IsCancellationRequested)
        {
            if ((await File.ReadAllLinesAsync(log)).Any(l => l.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal)))
                return true;
            await Task.Delay(50);
        }
        return false;
    }
}
