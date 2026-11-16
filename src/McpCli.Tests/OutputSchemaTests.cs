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

    [TestMethod]
    [DataRow("""{"type":["string","number"],"nullable":true}""", "null", true)]
    [DataRow("""{"type":["string","number"],"nullable":true,"enum":["only"]}""", "null", false)]
    [DataRow("""{"type":"string","format":"url"}""", "\"urn:example:abc\"", false)]
    [DataRow("""{"type":"string","format":"url"}""", "\"https://example.com/path\"", true)]
    [DataRow("""{"type":"string","format":"url"}""", "\"http://127.0.0.1/path\"", false)]
    [DataRow("""{"type":"string","format":"uuid"}""", "\"urn:uuid:123e4567-e89b-12d3-a456-426614174000\"", true)]
    [DataRow("""{"type":"string","format":"uuid"}""", "\"urn:uuid:invalid\"", false)]
    [DataRow("""{"type":"string","format":"date-time"}""", "\"2026-01-01T10:00:00+0000\"", true)]
    [DataRow("""{"type":"string","format":"date-time"}""", "\"2026-01-01T10:00:00+00\"", true)]
    [DataRow("""{"type":"string","format":"date-time"}""", "\"2026-01-01T10:00:00+24:00\"", false)]
    [DataRow("""{"type":"string","format":"time"}""", "\"23:59:60Z\"", true)]
    [DataRow("""{"type":"string","format":"time"}""", "\"12:00:60Z\"", false)]
    [DataRow("""{"type":"string","format":"date"}""", "\"0000-02-29\"", true)]
    [DataRow("""{"type":"number","multipleOf":0.1}""", "0.3", false)]
    [DataRow("""{"type":"number","multipleOf":0.1}""", "0.2", true)]
    public void ParitySchemaValidationMatchesRecordedUpstreamResults(string property, string value, bool expected)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = JsonNode.Parse(property) } };
        Assert.AreEqual(expected, new OutputSchema(schema).IsValid(new JsonObject { ["value"] = JsonNode.Parse(value) }));
    }

    [TestMethod]
    [DataRow("^.$", "\U0001F680", true)]
    [DataRow("^..$", "\U0001F680", false)]
    [DataRow("^.$", "\r", false)]
    [DataRow("^.$", "\u2028", false)]
    [DataRow("^a$", "a\n", false)]
    [DataRow("^a$", "a\r\n", false)]
    [DataRow("^\\p{Letter}+$", "abc", true)]
    [DataRow("^\\p{Letter}+$", "\U00010400", true)]
    [DataRow("^\\p{Letter}+$", "123", false)]
    [DataRow("^\\P{Letter}+$", "\U0001F680", true)]
    [DataRow("^[\\p{Letter}0-9]+$", "a\U000104001", true)]
    [DataRow("^[^a]$", "\U0001F680", true)]
    [DataRow("^[^a]{2}$", "\U0001F680", false)]
    [DataRow("^\\u{1F680}{2}$", "\U0001F680\U0001F680", true)]
    [DataRow("^\\.$", ".", true)]
    public void ParitySchemaRegexUsesUnicodeCodePoints(string pattern, string value, bool expected)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
        {
            ["value"] = new JsonObject { ["type"] = "string", ["pattern"] = pattern }
        }};
        Assert.AreEqual(expected, new OutputSchema(schema).IsValid(new JsonObject { ["value"] = value }));
    }

    [TestMethod]
    public void ParityReferencesCanTargetSchemasUnderExtensionKeywords()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","components":{"value":{"$ref":"#/definitions/inner"}},
             "definitions":{"inner":{"type":"string"}},"properties":{"value":{"$ref":"#/components/value"}}}
            """)!.AsObject();
        var validator = new OutputSchema(schema);
        Assert.IsTrue(validator.IsValid(JsonNode.Parse("""{"value":"ok"}""")));
        Assert.IsFalse(validator.IsValid(JsonNode.Parse("""{"value":42}""")));
    }

    [TestMethod]
    [DataRow("""{"type":"object","properties":{"value":{"type":"string","minLength":"1"}}}""")]
    [DataRow("""{"type":"object","properties":{"value":{"type":"array","items":null}}}""")]
    [DataRow("""{"type":"object","properties":{"value":{"nullable":true}}}""")]
    public async Task ParityInvalidOutputSchemasFailDuringDiscovery(string schema)
    {
        await using var workspace = new TestWorkspace();
        await workspace.WriteConfigAsync(("offline", workspace.StdioConfig(new JsonObject { ["FAKE_OUTPUT_SCHEMA"] = schema })));
        var result = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(1, result.ExitCode, result.Stdout);
        StringAssert.Contains(result.Stderr, "output schema");
    }

    [TestMethod]
    [DataRow("""{"type":["string","number"],"nullable":true}""", "null")]
    [DataRow("""{"type":"string","pattern":"^\\p{Letter}+$"}""", "\"abc\"")]
    [DataRow("""{"type":"string","pattern":"^.$"}""", "\"\\ud83d\\ude80\"")]
    public async Task ParityCachedConnectionsAcceptValidStructuredResults(string property, string value)
    {
        await using var workspace = new TestWorkspace();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = JsonNode.Parse(property) } };
        var server = workspace.StdioConfig(new JsonObject { ["FAKE_OUTPUT_SCHEMA"] = schema.ToJsonString() });
        workspace.EnvironmentVariables["MCP_NO_DAEMON"] = "0";
        workspace.EnvironmentVariables["MCP_DAEMON_TIMEOUT"] = "15";
        workspace.TrackDaemon("offline", server);
        await workspace.WriteConfigAsync(("offline", server));
        var info = await workspace.RunAsync(["info", "offline"]);
        Assert.AreEqual(0, info.ExitCode, info.Stderr);
        var payload = new JsonObject { ["text"] = "valid", ["structured"] = new JsonObject { ["value"] = JsonNode.Parse(value) } };
        var result = await workspace.RunAsync(["call", "offline", "echo"], payload.ToJsonString());
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual("valid", result.Stdout.Trim());
    }
}
