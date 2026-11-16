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
        foreach (var pid in await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt")))
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(int.Parse(pid));
                Assert.IsTrue(process.HasExited, "Idle daemon must close its server process");
            }
            catch (ArgumentException) { /* Expected: process has exited. */ }
        }
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
        foreach (var result in results) Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, results.Select(r => JsonNode.Parse(r.Stdout)!["pid"]!.ToString()).Distinct().Count());
    }
}
