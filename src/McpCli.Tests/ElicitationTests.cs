using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class ElicitationTests
{
    private const string Form = """
        {"message":"Approve the offline fixture?","requestedSchema":{"type":"object","properties":{"approved":{"type":"boolean"}},"required":["approved"]}}
        """;

    [TestMethod]
    [DataRow("null")]
    [DataRow("true")]
    [DataRow("{}")]
    [DataRow("""{"action":"ACCEPT"}""")]
    [DataRow("""{"action":"accept","contents":{}}""")]
    [DataRow("""{"action":"decline","content":{}}""")]
    [DataRow("""{"action":"ask","content":{}}""")]
    [DataRow("""{"action":"accept","content":{"secret":null}}""")]
    [DataRow("""{"action":"accept","content":{"nested":{}}}""")]
    [DataRow("""{"action":"accept","content":{"array":[true]}}""")]
    public async Task InvalidPolicyFailsConfiguration(string policy)
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        config.Value["elicitation"] = JsonNode.Parse(policy);
        await workspace.WriteConfigAsync(("offline", config));
        var result = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.Stderr, "CONFIG_INVALID_SERVER");
        StringAssert.Contains(result.Stderr, "elicitation");
        Assert.AreEqual("", result.Stdout);
    }

    [TestMethod]
    [DataRow(null, -32601, false)]
    [DataRow("accept", 0, false)]
    [DataRow("decline", 0, false)]
    [DataRow("cancel", 0, false)]
    [DataRow("accept", 0, true)]
    [DataRow("decline", 0, true)]
    [DataRow("cancel", 0, true)]
    public async Task ConfiguredResponsesAdvertiseOnlyFormSupport(string? action, int errorCode, bool cached)
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        if (cached)
        {
            workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
            workspace.TrackDaemon("offline", config);
        }
        if (action is not null) config.Value["elicitation"] = Policy(action);
        await workspace.WriteConfigAsync(("offline", config));
        var result = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var received = JsonNode.Parse(result.Stdout)!;
        var reply = received["response"]!;
        StringAssert.StartsWith(reply["id"]!.ToString(), "approval-");
        if (errorCode != 0)
        {
            Assert.AreEqual(errorCode, reply["error"]!["code"]!.GetValue<int>());
            Assert.IsNull(received["capabilities"]!["elicitation"]);
        }
        else
        {
            Assert.IsInstanceOfType<JsonObject>(received["capabilities"]!["elicitation"]!["form"]);
            Assert.IsNull(received["capabilities"]!["elicitation"]!["url"]);
            Assert.AreEqual(action, reply["result"]!["action"]!.ToString());
            Assert.AreEqual(action == "accept", reply["result"]!["content"] is not null);
            if (action == "accept") Assert.IsTrue(reply["result"]!["content"]!["approved"]!.GetValue<bool>());
            StringAssert.Contains(result.Stderr, "configured unattended action");
        }
    }

    [TestMethod]
    [DataRow("""{"mode":"url","message":"Open a URL","url":"https://example.invalid","elicitationId":"test"}""")]
    [DataRow("""{"message":"Missing schema"}""")]
    [DataRow("""{"message":"Different field","requestedSchema":{"type":"object","properties":{"other":{"type":"boolean"}},"required":["other"]}}""")]
    [DataRow("""{"message":"Invalid schema","requestedSchema":{"type":"object","required":5}}""")]
    public async Task UnsupportedOrMismatchedRequestsNeverReceiveAcceptance(string request)
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        config.Value["elicitation"] = Policy("accept");
        await workspace.WriteConfigAsync(("offline", config));
        var result = await workspace.RunAsync(["call", "offline", "elicit", request]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var reply = JsonNode.Parse(result.Stdout)!["response"]!;
        Assert.IsNull(reply["result"]);
        Assert.AreEqual(-32602, reply["error"]!["code"]!.GetValue<int>());
        StringAssert.Contains(result.Stderr, "Elicitation rejected");
    }

    [TestMethod]
    [DataRow("accept")]
    [DataRow("decline")]
    [DataRow("cancel")]
    public async Task AskResumesSameCallOnceAndDoesNotReplay(string action)
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        ConfigureAsk(workspace, config);
        await workspace.WriteConfigAsync(("offline", config));
        var initial = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(4, initial.ExitCode, initial.Stderr);
        var approval = JsonNode.Parse(initial.Stdout)!;
        Assert.AreEqual("approval_required", approval["status"]!.ToString());
        Assert.AreEqual("offline", approval["server"]!.ToString());
        Assert.AreEqual("elicit", approval["tool"]!.ToString());
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(Form), approval["request"]));
        var id = approval["approvalId"]!.ToString();
        var invalid = await workspace.RunAsync(["approve", "offline", "elicit", id, "accept", "{}"]);
        Assert.AreNotEqual(0, invalid.ExitCode);
        StringAssert.Contains(invalid.Stderr, "does not match requestedSchema");
        var resumed = await workspace.RunAsync(["approve", "offline", "elicit", id, action,
            action == "accept" ? """{"approved":true}""" : "{}"]);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Stderr);
        var received = JsonNode.Parse(resumed.Stdout)!;
        Assert.AreEqual(1, received["calls"]!.GetValue<int>());
        Assert.AreEqual(action, received["response"]!["result"]!["action"]!.ToString());
        var replay = await workspace.RunAsync(["approve", "offline", "elicit", id, action, "{}"]);
        Assert.AreNotEqual(0, replay.ExitCode);
        StringAssert.Contains(replay.Stderr, "unknown or expired");
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt"))).Length);
    }

    [TestMethod]
    public async Task AskRejectsDisabledWorkersAndUnknownApprovalWithoutStartingServer()
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        config.Value["elicitation"] = Policy("ask");
        await workspace.WriteConfigAsync(("offline", config));
        var direct = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(3, direct.ExitCode);
        StringAssert.Contains(direct.Stderr, "requires cached workers");
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        var unknown = await workspace.RunAsync(["approve", "offline", "elicit", Guid.NewGuid().ToString("N"), "cancel"]);
        Assert.AreNotEqual(0, unknown.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(workspace.Root, "server-pids.txt")));
    }

    [TestMethod]
    public async Task FurtherApprovalsRemainBoundToOriginalCallAndRejectOtherCallsAndIds()
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig(new JsonObject { ["FAKE_ELICIT_COUNT"] = "2" });
        ConfigureAsk(workspace, config);
        await workspace.WriteConfigAsync(("offline", config));
        var initial = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(4, initial.ExitCode, initial.Stderr);
        var firstId = JsonNode.Parse(initial.Stdout)!["approvalId"]!.ToString();
        var wrongTool = await workspace.RunAsync(["approve", "offline", "echo", firstId, "cancel"]);
        Assert.AreNotEqual(0, wrongTool.ExitCode);
        StringAssert.Contains(wrongTool.Stderr, "does not match");
        var competingCall = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(2, competingCall.ExitCode);
        StringAssert.Contains(competingCall.Stderr, "already running or awaiting approval");
        var next = await workspace.RunAsync(["approve", "offline", "elicit", firstId, "accept", """{"approved":true}"""]);
        Assert.AreEqual(4, next.ExitCode, next.Stderr);
        var secondId = JsonNode.Parse(next.Stdout)!["approvalId"]!.ToString();
        Assert.AreNotEqual(firstId, secondId);
        var stale = await workspace.RunAsync(["approve", "offline", "elicit", firstId, "cancel"]);
        Assert.AreNotEqual(0, stale.ExitCode);
        var final = await workspace.RunAsync(["approve", "offline", "elicit", secondId, "cancel"]);
        Assert.AreEqual(0, final.ExitCode, final.Stderr);
        Assert.AreEqual(1, JsonNode.Parse(final.Stdout)!["calls"]!.GetValue<int>());
    }

    [TestMethod]
    public void EmptyFormsCanBeAcceptedButRequiredDefaultsAreNeverInvented()
    {
        var policy = ElicitationPolicy.Parse(JsonNode.Parse("""{"action":"accept"}"""));
        var empty = policy.Respond(JsonNode.Parse("""{"mode":"form","message":"Approve?","requestedSchema":{"type":"object","properties":{}}}"""));
        Assert.AreEqual("accept", empty["action"]!.ToString());
        Assert.AreEqual(0, empty["content"]!.AsObject().Count);
        var request = JsonNode.Parse(Form)!;
        request["requestedSchema"]!["properties"]!["approved"]!["default"] = true;
        Assert.ThrowsExactly<McpException>(() => policy.Respond(request));
    }

    [TestMethod]
    public async Task AskTimesOutAndCannotResumeOrReplayExpiredCalls()
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        ConfigureAsk(workspace, config);
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "2";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        await workspace.WriteConfigAsync(("offline", config));
        var initial = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        Assert.AreEqual(4, initial.ExitCode, initial.Stderr);
        var id = JsonNode.Parse(initial.Stdout)!["approvalId"]!.ToString();
        await Task.Delay(2500);
        var expired = await workspace.RunAsync(["approve", "offline", "elicit", id, "accept", """{"approved":true}"""]);
        Assert.AreNotEqual(0, expired.ExitCode);
        StringAssert.Contains(expired.Stderr, "unknown or expired");
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt"))).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HttpServerRequestsUseSamePolicyAndResumableApproval(bool ask)
    {
        await using var workspace = new TestWorkspace();
        var config = await workspace.StartHttpAsync(new() { ["FAKE_HTTP_MODE"] = "get-responses" });
        if (ask) ConfigureAsk(workspace, config);
        else config.Value["elicitation"] = Policy("accept");
        await workspace.WriteConfigAsync(("offline", config));
        var result = await workspace.RunAsync(["call", "offline", "elicit", Form]);
        if (ask)
        {
            Assert.AreEqual(4, result.ExitCode, result.Stderr);
            var id = JsonNode.Parse(result.Stdout)!["approvalId"]!.ToString();
            result = await workspace.RunAsync(["approve", "offline", "elicit", id, "accept", """{"approved":true}"""]);
        }
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual("accept", JsonNode.Parse(result.Stdout)!["response"]!["result"]!["action"]!.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneratedScriptsResumeApprovalThroughRealCliAndStdioBridge(bool generic)
    {
        await using var workspace = new TestWorkspace();
        var config = workspace.StdioConfig();
        config.Value["env"]!["FAKE_NO_NEXT_CURSOR"] = "1";
        config.Value["env"]!["FAKE_STDERR"] = "Offline approval diagnostic";
        config.Value["elicitation"] = Policy("ask");
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_TIMEOUT"] = "20";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.EnvironmentVariables["MCP_SKILL_TOOLKIT_LOG"] = "off";
        await workspace.WriteConfigAsync(("offline", config));
        var setup = Path.Combine(workspace.Root, "generate.ps1");
        var scripts = Path.Combine(workspace.Root, "scripts");
        var expectedConfig = Path.Combine(workspace.Root, "bridged.json");
        var cli = OperatingSystem.IsWindows() ? Path.ChangeExtension(TestWorkspace.CliPath, ".exe") :
            Path.Combine(Path.GetDirectoryName(TestWorkspace.CliPath)!, "mcp-cli");
        await File.WriteAllTextAsync(setup, """
            param($Repository, $Config, $Scripts, $Cli, $ExpectedConfig)
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $Repository 'psakefile.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            foreach ($definition in $ast.EndBlock.Statements | Where-Object {
                $_ -is [Management.Automation.Language.FunctionDefinitionAst]
            }) {
                Set-Item -Path ('Function:' + $definition.Name) -Value $definition.Body.GetScriptBlock()
            }
            Write-McpToolScripts -ScriptsDir $Scripts -ServerName offline -ConfigReference $Config -McpCli $Cli -ToolMetadata @(
                [pscustomobject]@{ Name = 'elicit'; Parameters = @('message', 'requestedSchema', 'ApprovalId') }
            )
            # Retain only the expected worker configuration for deterministic test cleanup.
            $runtime = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json -AsHashtable
            $entry = $runtime.mcpServers.offline
            $entry.args = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                (Join-Path $Scripts 'Invoke-McpStdioBridge.ps1'), $entry.command) + @($entry.args)
            $entry.command = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
            $runtime | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ExpectedConfig -Encoding utf8
            """);
        var generated = await RunScriptAsync(workspace, setup,
            TestWorkspace.RepositoryRoot, workspace.ConfigPath, scripts, cli, expectedConfig);
        Assert.AreEqual(0, generated.ExitCode, generated.Stderr);
        var bridged = (await Configuration.LoadAsync(expectedConfig, new()))["offline"];
        workspace.TrackDaemon("offline", bridged);
        var script = Path.Combine(scripts, generic ? "Invoke-Tool.ps1" : "elicit.ps1");
        string[] ToolArguments(params string[] values) => generic ? new[] { "-Tool", "elicit" }.Concat(values).ToArray() : values;
        var initial = await RunScriptAsync(workspace, script, ToolArguments("-Json", Form));
        Assert.AreEqual(4, initial.ExitCode, initial.Stderr + initial.Stdout);
        StringAssert.Contains(initial.Stderr, "Offline approval diagnostic");
        Assert.IsTrue(Directory.EnumerateFiles(workspace.Root, "server-pids.txt.*.alive").Any(),
            "The cached server must survive the initial PowerShell wrapper process");
        var id = JsonNode.Parse(initial.Stdout)!["approvalId"]!.ToString();
        var resumed = await RunScriptAsync(workspace, script,
            ToolArguments("-ApprovalId", id, "-ApprovalAction", "accept", "-ApprovalJson", """{"approved":true}"""));
        Assert.AreEqual(0, resumed.ExitCode, resumed.Stderr + resumed.Stdout);
        var result = JsonNode.Parse(resumed.Stdout)!;
        Assert.AreEqual(1, result["calls"]!.GetValue<int>());
        Assert.IsTrue(result["response"]!["result"]!["content"]!["approved"]!.GetValue<bool>());
        var replay = await RunScriptAsync(workspace, script, ToolArguments("-ApprovalId", id, "-ApprovalAction", "cancel"));
        Assert.AreNotEqual(0, replay.ExitCode);
        StringAssert.Contains(replay.Stdout + replay.Stderr, "unknown or expired");
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "server-pids.txt"))).Length);
    }

    private static async Task<CliResult> RunScriptAsync(TestWorkspace workspace, string script, params string[] arguments)
    {
        var process = workspace.StartPowerShell(script, arguments);
        using var timeout = new CancellationTokenSource(30000);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token);
        return new(process.ExitCode, await stdout, await stderr);
    }

    private static JsonObject Policy(string action)
    {
        var policy = new JsonObject { ["action"] = action };
        if (action == "accept") policy["content"] = new JsonObject { ["approved"] = true };
        return policy;
    }

    private static void ConfigureAsk(TestWorkspace workspace, ServerConfig config)
    {
        config.Value["elicitation"] = Policy("ask");
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", config);
    }
}
