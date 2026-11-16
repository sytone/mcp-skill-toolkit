using System.Text.Json.Nodes;

namespace McpCli.Tests;

[TestClass]
public sealed class OutputSchemaTests
{
    [TestMethod]
    [DataRow("""{"type":"object","required":["x"],"properties":{"x":{"type":"integer","minimum":1}}}""", """{"x":2}""", true)]
    [DataRow("""{"type":"object","required":["x"],"properties":{"x":{"type":"integer","minimum":1}}}""", """{"x":0}""", false)]
    [DataRow("""{"type":"object","required":["x"]}""", "{}", false)]
    [DataRow("""{"additionalProperties":false,"properties":{"x":{"type":"string"}}}""", """{"x":"a","other":1}""", false)]
    [DataRow("""{"patternProperties":{"^x":{"type":"integer"}},"additionalProperties":false}""", """{"x_one":2}""", true)]
    [DataRow("""{"properties":{"x":{"enum":["a","b"]}}}""", """{"x":"c"}""", false)]
    [DataRow("""{"properties":{"x":{"const":1}}}""", """{"x":1}""", true)]
    [DataRow("""{"properties":{"x":{"type":["integer","null"]}}}""", """{"x":null}""", true)]
    [DataRow("""{"dependencies":{"x":["y"]}}""", """{"x":1}""", false)]
    [DataRow("""{"dependencies":{"x":{"required":["y"]}}}""", """{"x":1,"y":2}""", true)]
    [DataRow("""{"if":{"required":["x"]},"then":{"required":["y"]},"else":{"required":["z"]}}""", """{"x":1}""", false)]
    [DataRow("""{"oneOf":[{"required":["x"]},{"required":["y"]}]}""", """{"x":1,"y":2}""", false)]
    [DataRow("""{"anyOf":[{"required":["x"]},{"required":["y"]}]}""", """{"x":1,"y":2}""", true)]
    [DataRow("""{"allOf":[{"required":["x"]},{"not":{"required":["y"]}}]}""", """{"x":1}""", true)]
    [DataRow("""{"properties":{"x":{"type":"array","items":{"type":"integer"},"uniqueItems":true}}}""", """{"x":[1,1]}""", false)]
    [DataRow("""{"properties":{"x":{"items":[{"type":"integer"},{"type":"string"}],"additionalItems":false}}}""", """{"x":[1,"a"]}""", true)]
    [DataRow("""{"properties":{"x":{"contains":{"const":2}}}}""", """{"x":[1,3]}""", false)]
    [DataRow("""{"properties":{"x":{"maxLength":1}}}""", """{"x":"\ud83d\ude80"}""", true)]
    [DataRow("""{"properties":{"x":{"pattern":"^[a-z]+$"}}}""", """{"x":"A"}""", false)]
    [DataRow("""{"properties":{"x":{"format":"date"}}}""", """{"x":"2025-02-29"}""", false)]
    [DataRow("""{"properties":{"x":{"format":"ipv4"}}}""", """{"x":"127.0.0.1"}""", true)]
    [DataRow("""{"properties":{"x":{"format":"uuid"}}}""", """{"x":"not-a-uuid"}""", false)]
    [DataRow("""{"definitions":{"positive":{"type":"integer","exclusiveMinimum":0}},"properties":{"x":{"$ref":"#/definitions/positive"}}}""", """{"x":1}""", true)]
    [DataRow("""{"$id":"https://example.invalid/schema","definitions":{"positive":{"$id":"#positive","minimum":1}},"properties":{"x":{"$ref":"#positive"}}}""", """{"x":0}""", false)]
    public void ValidatesCachedOutputSchema(string schema, string value, bool expected) =>
        Assert.AreEqual(expected, new OutputSchema(JsonNode.Parse(schema)!.AsObject()).IsValid(JsonNode.Parse(value)));

    [TestMethod]
    public void RecursiveReferencesFollowDataAndUnresolvedReferencesFailWithoutNetworkAccess()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"next":{"$ref":"#"}}}""")!.AsObject();
        Assert.IsTrue(new OutputSchema(schema).IsValid(JsonNode.Parse("""{"next":{"next":{}}}""")));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new OutputSchema(JsonNode.Parse("""{"$ref":"https://example.invalid/unavailable"}""")!.AsObject()));
    }

    [TestMethod]
    public void RegisteredSchemaIdsCanBeReusedBySubsequentlyDiscoveredTools()
    {
        var registry = new Dictionary<string, OutputSchema>();
        var registered = OutputSchema.Compile(JsonNode.Parse(
            """{"$id":"https://example.invalid/common","definitions":{"value":{"type":"integer","minimum":1}}}""")!.AsObject(), registry);
        var dependent = OutputSchema.Compile(JsonNode.Parse(
            """{"type":"object","properties":{"value":{"$ref":"https://example.invalid/common#/definitions/value"}}}""")!.AsObject(), registry);
        Assert.IsTrue(dependent.IsValid(JsonNode.Parse("""{"value":2}""")));
        Assert.IsFalse(dependent.IsValid(JsonNode.Parse("""{"value":0}""")));
        Assert.AreSame(registered, OutputSchema.Compile(JsonNode.Parse(
            """{"$id":"https://example.invalid/common","type":"string"}""")!.AsObject(), registry));
    }

    [TestMethod]
    public async Task DaemonPreservesSdkOutputValidationAfterDiscovery()
    {
        await using var workspace = new TestWorkspace();
        var server = workspace.StdioConfig(new JsonObject
        {
            ["FAKE_OUTPUT_SCHEMA"] = """{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}"""
        });
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        await workspace.WriteConfigAsync(("offline", server));
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        var missing = await workspace.RunAsync(["call", "offline", "echo", "{}"]);
        Assert.AreEqual(2, missing.ExitCode);
        StringAssert.Contains(missing.Stderr, "did not return structured content");
        var invalid = await workspace.RunAsync(["call", "offline", "echo", """{"structured":{"value":"wrong"}}"""]);
        Assert.AreEqual(2, invalid.ExitCode);
        StringAssert.Contains(invalid.Stderr, "does not match");
        var valid = await workspace.RunAsync(["call", "offline", "echo", """{"structured":{"value":2},"text":"valid"}"""]);
        Assert.AreEqual(0, valid.ExitCode);
        Assert.AreEqual("valid", valid.Stdout.Trim());
        var error = await workspace.RunAsync(["call", "offline", "echo", """{"text":"in-band error","isError":true}"""]);
        Assert.AreEqual(0, error.ExitCode);
        Assert.AreEqual("in-band error", error.Stdout.Trim());
    }

    [TestMethod]
    public async Task TaskRequiredToolsAreRejectedAfterDiscoveryWithoutCallingServer()
    {
        await using var workspace = new TestWorkspace();
        var log = Path.Combine(workspace.Root, "task-requests.jsonl");
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_REQUIRED_TASK"] = "1", ["FAKE_REQUEST_LOG"] = log });
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        await workspace.WriteConfigAsync(("offline", server));
        Assert.AreEqual(0, (await workspace.RunAsync(["info", "offline"])).ExitCode);
        var result = await workspace.RunAsync(["call", "offline", "echo", "{}"]);
        Assert.AreEqual(2, result.ExitCode);
        StringAssert.Contains(result.Stderr, "requires task-based execution");
        Assert.IsFalse((await File.ReadAllLinesAsync(log)).Any(l => JsonNode.Parse(l)!["method"]?.ToString() == "tools/call"));
    }
}
