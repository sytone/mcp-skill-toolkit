using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    [DataRow(new[] { "server", "tool" }, "AMBIGUOUS_COMMAND")]
    [DataRow(new[] { "server", "tool", "{}" }, "AMBIGUOUS_COMMAND")]
    [DataRow(new[] { "server/tool", "{}" }, "AMBIGUOUS_COMMAND")]
    [DataRow(new[] { "run", "server", "tool" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "execute", "server/tool" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "get", "server" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "list" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "search", "*" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "find", "*" }, "UNKNOWN_SUBCOMMAND")]
    [DataRow(new[] { "call" }, "MISSING_ARGUMENT")]
    [DataRow(new[] { "call", "server" }, "MISSING_ARGUMENT")]
    [DataRow(new[] { "call", "server/" }, "MISSING_ARGUMENT")]
    [DataRow(new[] { "grep" }, "MISSING_ARGUMENT")]
    [DataRow(new[] { "grep", "*", "extra" }, "TOO_MANY_ARGUMENTS")]
    [DataRow(new[] { "info", "--server", "fs" }, "UNKNOWN_OPTION")]
    [DataRow(new[] { "call", "server", "tool", "--args", "{}" }, "UNKNOWN_OPTION")]
    [DataRow(new[] { "--call", "server", "tool" }, "UNKNOWN_OPTION")]
    [DataRow(new[] { "--json" }, "UNKNOWN_OPTION")]
    [DataRow(new[] { "-c" }, "MISSING_ARGUMENT")]
    public async Task InvalidCommandsFailBeforeLoadingConfig(string[] args, string error)
    {
        await using var workspace = new TestWorkspace();
        var result = await workspace.RunAsync(args, withConfig: false);
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        StringAssert.Contains(result.Stderr, error);
    }

    [TestMethod]
    [DataRow("--help", "Usage:")]
    [DataRow("-h", "Options:")]
    [DataRow("--version", "mcp-cli v0.3.0")]
    public async Task HelpAndVersionDoNotNeedConfiguration(string option, string expected)
    {
        await using var workspace = new TestWorkspace();
        var result = await workspace.RunAsync([option], withConfig: false);
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stdout, expected);
        Assert.AreEqual("", result.Stderr);
    }

    [TestMethod]
    public async Task MissingServerShowsAvailableServersAndUnknownServerListsChoices()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var missing = await workspace.RunAsync(["info"]);
        Assert.AreEqual(1, missing.ExitCode);
        StringAssert.Contains(missing.Stderr, "Available servers: offline");
        var unknown = await workspace.RunAsync(["info", "absent"]);
        Assert.AreEqual(1, unknown.ExitCode);
        StringAssert.Contains(unknown.Stderr, "SERVER_NOT_FOUND");
        StringAssert.Contains(unknown.Stderr, "Available servers: offline");
    }

    [TestMethod]
    [DataRow("{path:\"x\"}")]
    [DataRow("path=x")]
    [DataRow("{\"path\":test}")]
    [DataRow("{\"path\":\"test\",}")]
    [DataRow("{'path':'test'}")]
    [DataRow("plain text")]
    public async Task InvalidJsonIsRejectedBeforeStartingServer(string json)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync(["call", "offline", "echo", json]);
        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.Stderr, "INVALID_JSON_ARGUMENTS");
        Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "server-pids.txt")));
    }

    [TestMethod]
    public async Task EmptyConfigurationWarnsWithoutConnecting()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync();
        var result = await workspace.RunAsync([]);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        StringAssert.Contains(result.Stderr, "No servers configured");
    }

    [TestMethod]
    public async Task StdinTimeoutIsBoundedAndDoesNotStartServer()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "1";
        var process = workspace.StartCli(["call", "offline", "echo"]);
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(5000);
        await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(1, process.ExitCode);
        StringAssert.Contains(await error, "stdin read timed out after 1000ms");
        Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "server-pids.txt")));
    }

    [TestMethod]
    public async Task DescriptionFlagsAndInlineArgumentsAreAcceptedAfterPositionals()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig()));
        var result = await workspace.RunAsync(["info", "offline", "-d"]);
        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stdout, "Description for echo");
        StringAssert.Contains(result.Stdout, "\u2022 text (string, required) - Text to echo");
    }
}
