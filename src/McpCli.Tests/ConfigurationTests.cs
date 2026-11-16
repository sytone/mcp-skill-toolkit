using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public async Task ExplicitPathPrecedesEnvironmentAndSearchPaths()
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("explicit", workspace.StdioConfig()));
        var alternate = Path.Combine(workspace.Root, "alternate.json");
        await File.WriteAllTextAsync(alternate, """{"mcpServers":{"environment":{"command":"example"}}}""");
        var settings = new RuntimeSettings
        {
            WorkingDirectory = workspace.Root, HomeDirectory = workspace.Root,
            Environment = name => name == "MCP_CONFIG_PATH" ? alternate : null
        };
        Assert.IsTrue((await Configuration.LoadAsync(workspace.ConfigPath, settings)).ContainsKey("explicit"));
        Assert.IsTrue((await Configuration.LoadAsync(null, settings)).ContainsKey("environment"));
        var error = await Assert.ThrowsExactlyAsync<CliError>(() => Configuration.LoadAsync("missing.json", settings));
        StringAssert.Contains(error.Message, "CONFIG_NOT_FOUND");
    }

    [TestMethod]
    public async Task DefaultSearchUsesCurrentDirectoryThenBothHomeLocations()
    {
        await using var workspace = new TestWorkspace();
        var home = Path.Combine(workspace.Root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".config", "mcp"));
        var settings = new RuntimeSettings { WorkingDirectory = workspace.Root, HomeDirectory = home, Environment = _ => null };
        var paths = Configuration.SearchPaths(settings);
        foreach (var path in paths) await File.WriteAllTextAsync(path, """{"mcpServers":{"test":{"url":"http://localhost"}}}""");
        foreach (var path in paths)
        {
            Assert.IsTrue((await Configuration.LoadAsync(null, settings)).ContainsKey("test"));
            File.Delete(path);
        }
        StringAssert.Contains((await Assert.ThrowsExactlyAsync<CliError>(() => Configuration.LoadAsync(null, settings))).Message, "search paths");
    }

    [TestMethod]
    [DataRow("not json", "CONFIG_INVALID_JSON")]
    [DataRow("{}", "CONFIG_MISSING_FIELD")]
    [DataRow("{\"mcpServers\":[]}", "CONFIG_MISSING_FIELD")]
    [DataRow("{\"mcpServers\":{\"bad\":null}}", "CONFIG_INVALID_SERVER")]
    [DataRow("{\"mcpServers\":{\"bad\":{}}}", "missing required field")]
    [DataRow("{\"mcpServers\":{\"bad\":{\"command\":\"a\",\"url\":\"http://localhost\"}}}", "both")]
    [DataRow("{\"mcpServers\":{\"bad\":{\"command\":5}}}", "non-empty string")]
    [DataRow("{\"mcpServers\":{\"bad\":{\"command\":\"a\",\"args\":[1]}}}", "array of strings")]
    [DataRow("{\"mcpServers\":{\"bad\":{\"url\":\"http://localhost\",\"headers\":{\"a\":null}}}}", "object of strings")]
    public async Task ReportsInvalidConfiguration(string json, string expected)
    {
        await using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.ConfigPath, json);
        var error = await Assert.ThrowsExactlyAsync<CliError>(() => Configuration.LoadAsync(workspace.ConfigPath, new()));
        StringAssert.Contains(error.Message, expected);
        Assert.AreEqual(1, error.ExitCode);
    }

    [TestMethod]
    public async Task SubstitutesNestedValuesWithoutReinterpretingJsonOrKeys()
    {
        await using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.ConfigPath,
            """{"mcpServers":{"${NAME}":{"command":"${EXE}","args":["${VALUE}"],"env":{"KEY":"prefix-${VALUE}"},"allowedTools":["${TOOL}"]}}}""");
        var variables = new Dictionary<string, string>
        {
            ["EXE"] = "dotnet", ["VALUE"] = "\"quoted\"\n\u96ea", ["TOOL"] = "read_*"
        };
        var settings = new RuntimeSettings { Environment = name => variables.GetValueOrDefault(name) };
        var servers = await Configuration.LoadAsync(workspace.ConfigPath, settings);
        Assert.AreEqual(variables["VALUE"], servers["${NAME}"].Args[0]);
        Assert.AreEqual("prefix-" + variables["VALUE"], servers["${NAME}"].Map("env").Single().Value);
        Assert.IsTrue(servers["${NAME}"].IsToolAllowed("read_file"));
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("true", true)]
    [DataRow("false", false)]
    [DataRow("FALSE", false)]
    [DataRow("0", false)]
    public async Task MissingEnvironmentIsStrictUnlessExplicitlyDisabled(string? mode, bool strict)
    {
        await using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.ConfigPath, """{"mcpServers":{"test":{"command":"example","args":["${UNSET}"]}}}""");
        var diagnostics = new List<string>();
        var settings = new RuntimeSettings
        {
            Environment = name => name == "MCP_STRICT_ENV" ? mode : null,
            Diagnostics = diagnostics.Add
        };
        if (strict)
            StringAssert.Contains((await Assert.ThrowsExactlyAsync<CliError>(() =>
                Configuration.LoadAsync(workspace.ConfigPath, settings))).Message, "MISSING_ENV_VAR");
        else
        {
            Assert.AreEqual("", (await Configuration.LoadAsync(workspace.ConfigPath, settings))["test"].Args[0]);
            Assert.IsTrue(diagnostics.Any(d => d.Contains("Warning: Missing environment variable")));
        }
    }

    [TestMethod]
    [DataRow("read_*", "delete_*", "READ_file", true)]
    [DataRow("*file*", "delete_*", "delete_file", false)]
    [DataRow("read_*", "", "write_file", false)]
    [DataRow("", "", "anything", true)]
    [DataRow("file?", "", "file12", false)]
    [DataRow("file?", "", "file1", true)]
    [DataRow("[x].*", "", "[x].file", true)]
    [DataRow("*", "", "nested/tool", true)]
    public void FilteringIsCaseInsensitiveAndDenyWins(string allow, string deny, string tool, bool expected)
    {
        var server = new ServerConfig(new JsonObject
        {
            ["command"] = "unused",
            ["allowedTools"] = new JsonArray(allow.Length == 0 ? [] : [JsonValue.Create(allow)]),
            ["disabledTools"] = new JsonArray(deny.Length == 0 ? [] : [JsonValue.Create(deny)])
        });
        Assert.AreEqual(expected, server.IsToolAllowed(tool));
    }

    [TestMethod]
    [DataRow("*file*", "read_FILE", true)]
    [DataRow("server/*", "server/nested/tool", false)]
    [DataRow("server/**", "server/nested/tool", true)]
    [DataRow("***file***", "myfile", true)]
    [DataRow("a?b", "a/b", false)]
    [DataRow("[x].txt", "[x].txt", true)]
    [DataRow("[x].txt", "xatxt", false)]
    [DataRow("a+b^c", "a+b^c", true)]
    public void SearchGlobsPreservePathAndRegexLiteralSemantics(string pattern, string value, bool expected) =>
        Assert.AreEqual(expected, Glob.Compile(pattern).IsMatch(value));

    [TestMethod]
    public void DaemonHashIncludesNestedConfigurationAndIgnoresObjectKeyOrder()
    {
        var first = new ServerConfig(JsonNode.Parse("""{"command":"dotnet","env":{"A":"1","B":"2"}}""")!.AsObject());
        var reordered = new ServerConfig(JsonNode.Parse("""{"env":{"B":"2","A":"1"},"command":"dotnet"}""")!.AsObject());
        var changed = new ServerConfig(JsonNode.Parse("""{"command":"dotnet","env":{"A":"changed","B":"2"}}""")!.AsObject());
        Assert.AreEqual(first.Hash(), reordered.Hash());
        Assert.AreNotEqual(first.Hash(), changed.Hash());
        Assert.IsFalse(Daemon.PipeName("../../escape", first, new()).Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("tool", "tool\n", false)]
    [DataRow("**", "a\rb", false)]
    [DataRow("**", "a\u2028b", false)]
    [DataRow("*", "a\rb", true)]
    public void RoundTwoSearchGlobsMatchJavaScriptLineBoundaries(string pattern, string value, bool expected) =>
        Assert.AreEqual(expected, Glob.Compile(pattern).IsMatch(value));
}
