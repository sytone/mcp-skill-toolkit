using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class TransportTests
{
    [TestMethod]
    public async Task StdioSupportsDiscoverySchemaInstructionsAndToolOnlySearch()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var list = await workspace.RunAsync([]);
        Assert.AreEqual(0, list.ExitCode);
        StringAssert.StartsWith(Normalize(list.Stdout), "offline\n  Instructions: Use these offline tools....\n  \u2022 echo");
        Assert.IsFalse(list.Stdout.Contains("Description for echo", StringComparison.Ordinal));
        var info = await workspace.RunAsync(["offline"]);
        Assert.AreEqual(0, info.ExitCode);
        StringAssert.Contains(info.Stdout, "Transport: stdio");
        StringAssert.Contains(info.Stdout, "  Second instruction.");
        StringAssert.Contains(info.Stdout, "Tools (11):");
        var schema = await workspace.RunAsync(["info", "offline/echo"]);
        Assert.AreEqual(0, schema.ExitCode);
        StringAssert.StartsWith(Normalize(schema.Stdout), "Tool: echo\nServer: offline\n\nDescription:\n  Description for echo");
        var search = await workspace.RunAsync(["grep", "*file*"]);
        Assert.AreEqual("offline read_file Description for read_file\noffline delete_file Description for delete_file\n", Normalize(search.Stdout));
        var noMatch = await workspace.RunAsync(["grep", "offline/*"]);
        StringAssert.Contains(noMatch.Stdout, "No tools found");
        StringAssert.Contains(noMatch.Stdout, "tool names only");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InlineAndStdinUnicodePayloadsPreserveRawText(bool stdin)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var text = "Quotes ' \" & pipe | newline\n\u96ea \ud83d\ude80";
        var json = new JsonObject { ["text"] = text }.ToJsonString();
        var result = stdin ? await workspace.RunAsync(["call", "offline/echo", "-"], json) :
            await workspace.RunAsync(["call", "offline", "echo", json]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(text + "\n", Normalize(result.Stdout));
        Assert.AreEqual("", result.Stderr);
    }

    [TestMethod]
    public async Task CallsDoNotDiscoverFirstAndHonorWorkingDirectoryAndEnvironment()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "requests.jsonl");
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig(new JsonObject
        {
            ["FAKE_VALUE"] = "configured", ["FAKE_REQUEST_LOG"] = log
        })));
        var result = await workspace.RunAsync(["call", "offline", "state"]);
        Assert.AreEqual(0, result.ExitCode);
        var state = JsonNode.Parse(result.Stdout)!;
        Assert.AreEqual(workspace.Root, state["cwd"]!.GetValue<string>());
        Assert.AreEqual("configured", state["env"]!.GetValue<string>());
        var methods = (await File.ReadAllLinesAsync(log)).Select(l => JsonNode.Parse(l)!["method"]!.ToString()).ToArray();
        CollectionAssert.AreEqual(new[] { "initialize", "notifications/initialized", "tools/call" }, methods);
    }

    [TestMethod]
    [DataRow("multi", "first\nsecond\n")]
    [DataRow("error_content", "Tool reported an error\n")]
    [DataRow("ping", "{}\n")]
    public async Task TextResultsAndInBandToolErrorsFollowUpstream(string tool, string expected)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync(["call", "offline", tool, "{}"]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(expected, Normalize(result.Stdout));
    }

    [TestMethod]
    [DataRow("binary")]
    [DataRow("empty")]
    public async Task NonTextResultsRemainJson(string tool)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync(["call", "offline", tool, "{}"]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsInstanceOfType<JsonArray>(JsonNode.Parse(result.Stdout)!["content"]);
    }

    [TestMethod]
    public async Task FilteringAppliesToEveryCommandAndCannotBeBypassedByDirectCall()
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        config.Value["allowedTools"] = new JsonArray("*file*");
        config.Value["disabledTools"] = new JsonArray("delete_*");
        await workspace.WriteConfigAsync(("offline", config));
        foreach (var command in new[] { Array.Empty<string>(), ["info", "offline"], ["grep", "*"] })
        {
            var result = await workspace.RunAsync(command);
            Assert.AreEqual(0, result.ExitCode);
            StringAssert.Contains(result.Stdout, "read_file");
            Assert.IsFalse(result.Stdout.Contains("delete_file", StringComparison.Ordinal));
        }
        var denied = await workspace.RunAsync(["call", "offline", "delete_file", "{}"]);
        Assert.AreEqual(2, denied.ExitCode);
        StringAssert.Contains(denied.Stderr, "disabled by configuration");
    }

    [TestMethod]
    [DataRow("info", "missing", 1, "TOOL_NOT_FOUND")]
    [DataRow("call", "missing", 2, "TOOL_NOT_FOUND")]
    [DataRow("call", "fail", 2, "TOOL_EXECUTION_FAILED")]
    [DataRow("call", "malformed_result", 2, "expected content array")]
    [DataRow("call", "malformed_content", 2, "missing required fields")]
    [DataRow("call", "malformed_error_flag", 2, "isError must be a boolean")]
    public async Task ErrorsHaveCorrectStreamAndExitCode(string command, string tool, int code, string error)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync([command, "offline", tool, "{}"]);
        Assert.AreEqual(code, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        StringAssert.Contains(result.Stderr, error);
    }

    [TestMethod]
    public async Task ServerFailureIsConnectionExitThreeAndStderrIsPreserved()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig(new JsonObject
        {
            ["FAKE_STDERR"] = "Local authentication prompt", ["FAKE_PROTOCOL"] = "unsupported"
        })));
        var result = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(3, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SERVER_CONNECTION_FAILED");
        StringAssert.Contains(result.Stderr, "[offline] Local authentication prompt");
    }

    [TestMethod]
    public async Task ListingContainsPartialFailuresAndGrepWarns()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("z-good", workspace.StdioConfig()),
            ("a-bad", new(new JsonObject { ["command"] = "mcp-cli-nonexistent-fixture-command" })));
        var list = await workspace.RunAsync([]);
        Assert.AreEqual(0, list.ExitCode);
        StringAssert.StartsWith(list.Stdout, "a-bad");
        StringAssert.Contains(list.Stdout, "<error:");
        StringAssert.Contains(list.Stdout, "z-good");
        var grep = await workspace.RunAsync(["grep", "echo"]);
        Assert.AreEqual(0, grep.ExitCode);
        StringAssert.Contains(grep.Stdout, "z-good echo");
        StringAssert.Contains(grep.Stderr, "1 server(s) failed to connect: a-bad");
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("sse")]
    [DataRow("batch")]
    public async Task HttpSupportsJsonAndFragmentedSseWithSessionHeaders(string mode)
    {
        await using var workspace = new TestWorkspace();
        var server = await workspace.StartHttpAsync(new()
        {
            ["FAKE_HTTP_MODE"] = mode, ["FAKE_HTTP_AUTH"] = "Bearer public-fixture"
        });
        server.Value["headers"] = new JsonObject { ["Authorization"] = "Bearer public-fixture" };
        await workspace.WriteConfigAsync(("http", server));
        var info = await workspace.RunAsync(["info", "http"]);
        Assert.AreEqual(0, info.ExitCode);
        StringAssert.Contains(info.Stdout, "Transport: HTTP");
        StringAssert.Contains(info.Stdout, "Tools (11):");
        var result = await workspace.RunAsync(["call", "http", "echo", "{\"text\":\"\\u96ea\"}"]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("\u96ea\n", Normalize(result.Stdout));
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("wrong-content-type")]
    public async Task InvalidHttpResponsesAreNotSuccess(string mode)
    {
        await using var workspace = new TestWorkspace();
        var server = await workspace.StartHttpAsync(new() { ["FAKE_HTTP_MODE"] = mode });
        await workspace.WriteConfigAsync(("http", server));
        var result = await workspace.RunAsync(["info", "http"]);
        Assert.AreEqual(3, result.ExitCode);
        StringAssert.Contains(result.Stderr, "SERVER_CONNECTION_FAILED");
    }

    [TestMethod]
    public async Task InterruptedHttpPostResumesUsingEventIdWithoutReplayingCall()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "http-requests.jsonl");
        var server = await workspace.StartHttpAsync(new() { ["FAKE_HTTP_MODE"] = "resume", ["FAKE_REQUEST_LOG"] = log });
        await workspace.WriteConfigAsync(("http", server));
        var result = await workspace.RunAsync(["call", "http", "echo", "{\"text\":\"resumed\"}"]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("resumed\n", Normalize(result.Stdout));
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(log)).Count(l => JsonNode.Parse(l)!["method"]?.ToString() == "tools/call"));
    }

    [TestMethod]
    public async Task StructuredOnlyResponseGetsTheSdkDefaultEmptyContent()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync(["call", "offline", "structured_only", "{}"]);
        Assert.AreEqual(0, result.ExitCode);
        var response = JsonNode.Parse(result.Stdout)!;
        Assert.AreEqual(0, response["content"]!.AsArray().Count);
        Assert.AreEqual(42, response["structuredContent"]!["value"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task AuthenticationFailureIsNotRetried()
    {
        await using var workspace = new TestWorkspace();
        var server = await workspace.StartHttpAsync(new() { ["FAKE_HTTP_AUTH"] = "Bearer expected" });
        await workspace.WriteConfigAsync(("http", server));
        workspace.EnvironmentVariables["MCP_MAX_RETRIES"] = "3";
        workspace.EnvironmentVariables["MCP_DEBUG"] = "1";
        var result = await workspace.RunAsync(["info", "http"]);
        Assert.AreEqual(3, result.ExitCode);
        StringAssert.Contains(result.Stderr, "Authentication required");
        Assert.IsFalse(result.Stderr.Contains("Retrying", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TransientHttpConnectionFailureIsRetried()
    {
        await using var workspace = new TestWorkspace();
        var server = await workspace.StartHttpAsync(new() { ["FAKE_HTTP_FAILURES"] = "1" });
        await workspace.WriteConfigAsync(("http", server));
        workspace.EnvironmentVariables["MCP_MAX_RETRIES"] = "1";
        workspace.EnvironmentVariables["MCP_RETRY_DELAY"] = "1";
        workspace.EnvironmentVariables["MCP_DEBUG"] = "1";
        var result = await workspace.RunAsync(["call", "http", "echo", "{}"]);
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stderr, "Retrying");
    }

    [TestMethod]
    public async Task ToolTimeoutIsBounded()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "1";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await workspace.RunAsync(["call", "offline", "slow", "{\"delay\":5000}"]);
        Assert.AreEqual(2, result.ExitCode);
        StringAssert.Contains(result.Stderr, "timeout");
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(8), $"Took {clock.Elapsed}");
    }

    [TestMethod]
    public async Task WindowsBatchEntrypointWithSpacesWorks()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var workspace = new TestWorkspace();
        var script = Path.Combine(workspace.Root, "fixture server.cmd");
        await File.WriteAllTextAsync(script, $"@echo off\r\ndotnet \"{TestWorkspace.ServerPath}\" stdio %*\r\n");
        var arguments = new[] { "with spaces", "\"quotes\"", "a&b|c", "percent%unknown%", "bang!", "caret^", @"trailing\" };
        await workspace.WriteConfigAsync(("batch", new(new JsonObject { ["command"] = "fixture server", ["cwd"] = workspace.Root,
            ["args"] = new JsonArray(arguments.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()) })));
        var result = await workspace.RunAsync(["call", "batch", "state", "{}"]);
        Assert.AreEqual(0, result.ExitCode);
        CollectionAssert.AreEqual(arguments, JsonNode.Parse(result.Stdout)!["args"]!.AsArray().Skip(2).Select(a => a!.GetValue<string>()).ToArray());
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n");
}
