using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var lifetimeMarker = Environment.GetEnvironmentVariable("FAKE_PID_PATH") is { } markerBase ?
    new FileStream($"{markerBase}.{Guid.NewGuid():N}.alive", FileMode.CreateNew, FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose) : null;
if (Environment.GetEnvironmentVariable("FAKE_PID_PATH") is { } pidPath)
    await File.AppendAllTextAsync(pidPath, Environment.ProcessId + "\n");
if (Environment.GetEnvironmentVariable("FAKE_STDERR") is { } diagnostic) Console.Error.WriteLine(diagnostic);
if (args.FirstOrDefault() != "http")
{
    var session = new FakeSession();
    var responses = new List<Task>();
    if (Environment.GetEnvironmentVariable("FAKE_STDOUT_PREFIX") is { } prefix) Console.WriteLine(prefix);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = FixtureJson.Parse(line)!.AsObject();
        if (request["method"]?.ToString() == "tools/call" &&
            request["params"]?["name"]?.ToString() == "ping")
        {
            Console.WriteLine("""{"jsonrpc":"2.0","id":"server-ping","method":"ping"}""");
            var pong = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "")!;
            if (pong["id"]?.ToString() != "server-ping" || pong["result"] is not JsonObject)
                throw new InvalidDataException("Client did not answer server ping");
        }
        responses.Add(RespondAsync(request));
    }
    await Task.WhenAll(responses);
    return;

    async Task RespondAsync(JsonObject request)
    {
        var response = await session.HandleAsync(request);
        if (response is not null)
        {
            if (Environment.GetEnvironmentVariable("FAKE_MALFORMED") == "1")
                Console.WriteLine("{not-json}");
            else Console.WriteLine(FixtureJson.Serialize(response));
        }
    }
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
var sessions = new ConcurrentDictionary<string, FakeSession>();
var resume = new ConcurrentDictionary<string, JsonObject>();
var httpFailures = 0;
app.MapMethods("/redirect", ["GET", "POST"], context =>
{
    context.Response.StatusCode = int.TryParse(Environment.GetEnvironmentVariable("FAKE_REDIRECT_STATUS"), out var status) ? status : 307;
    context.Response.Headers.Location = Environment.GetEnvironmentVariable("FAKE_REDIRECT_TARGET") ?? "/mcp";
    return Task.CompletedTask;
});
app.MapMethods("/mcp", ["GET", "POST", "DELETE"], async context =>
{
    var mode = Environment.GetEnvironmentVariable("FAKE_HTTP_MODE") ?? "json";
    if (Environment.GetEnvironmentVariable("FAKE_HTTP_HEADERS_LOG") is { } headerLog)
        await FakeSession.LogAsync(headerLog, new JsonObject
        {
            ["method"] = context.Request.Method,
            ["authorization"] = context.Request.Headers.Authorization.ToString(),
            ["contentType"] = context.Request.ContentType,
            ["session"] = context.Request.Headers["Mcp-Session-Id"].ToString(),
            ["protocol"] = context.Request.Headers["MCP-Protocol-Version"].ToString()
        });
    if (Environment.GetEnvironmentVariable("FAKE_HTTP_AUTH") is { } auth &&
        context.Request.Headers.Authorization.ToString() != auth)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_HTTP_FAILURES"), out var failures) &&
        Interlocked.Increment(ref httpFailures) <= failures)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync("Service Unavailable");
        return;
    }
    if (context.Request.Method == "GET")
    {
        if (mode == "get-responses" && sessions.TryGetValue(context.Request.Headers["Mcp-Session-Id"].ToString(), out var activeSession))
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(": connected\n\n");
            await context.Response.Body.FlushAsync();
            try
            {
                await foreach (var message in activeSession.Responses.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync("data: " + FixtureJson.Serialize(message) + "\n\n");
                    await context.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            return;
        }
        if (resume.TryRemove(context.Request.Headers["Last-Event-ID"].ToString(), out var replay))
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + replay.ToJsonString() + "\n\n");
            return;
        }
        context.Response.StatusCode = 405;
        return;
    }
    if (context.Request.Method == "DELETE") { context.Response.StatusCode = 405; return; }
    if (!context.Request.Headers.Accept.ToString().Contains("application/json") ||
        !context.Request.Headers.Accept.ToString().Contains("text/event-stream"))
    {
        context.Response.StatusCode = 406;
        return;
    }
    var request = (await JsonNode.ParseAsync(context.Request.Body,
        documentOptions: new JsonDocumentOptions { MaxDepth = int.MaxValue }))!.AsObject();
    var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
    FakeSession session;
    if (request["method"]?.ToString() == "initialize")
    {
        sessionId = Guid.NewGuid().ToString("N");
        session = new FakeSession();
        sessions[Environment.GetEnvironmentVariable("FAKE_EXPECT_SESSION_ID") ?? sessionId] = session;
        context.Response.Headers["Mcp-Session-Id"] = sessionId;
    }
    else if (!sessions.TryGetValue(sessionId, out session!) ||
        context.Request.Headers["MCP-Protocol-Version"].ToString() !=
            (Environment.GetEnvironmentVariable("FAKE_EXPECT_PROTOCOL_VERSION") ?? session.Version))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing session or protocol header");
        return;
    }
    var response = await session.HandleAsync(request);
    if (response is null) { context.Response.StatusCode = mode == "batch" ? 200 : 202; return; }
    if (mode == "get-responses" && request["method"]?.ToString() != "initialize")
    {
        session.Responses.Writer.TryWrite(response);
        context.Response.StatusCode = 202;
        return;
    }
    if (Environment.GetEnvironmentVariable("FAKE_HTTP_COMPRESSION") is { } compression)
    {
        context.Response.ContentType = mode == "sse" ? "text/event-stream" : "application/json";
        context.Response.Headers.ContentEncoding = compression;
        var json = FixtureJson.Serialize(response);
        var data = Encoding.UTF8.GetBytes(mode == "sse" ? $"data: {json}\n\n" : json);
        await using Stream compressor = compression switch
        {
            "gzip" => new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new ZLibStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new InvalidDataException($"Unknown fixture compression: {compression}")
        };
        await compressor.WriteAsync(data);
        return;
    }
    if (mode == "malformed") { context.Response.ContentType = "application/json"; await context.Response.WriteAsync("{bad"); return; }
    if (mode == "wrong-content-type") { await context.Response.WriteAsync("invalid"); return; }
    if (mode == "resume" && request["method"]?.ToString() == "tools/call")
    {
        var eventId = Guid.NewGuid().ToString("N");
        resume[eventId] = response;
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync($"id: {eventId}\nretry: 10\n\n");
        return;
    }
    if (mode is "sse" or "malformed-event")
    {
        context.Response.ContentType = "text/event-stream";
        if (mode == "malformed-event")
            await context.Response.WriteAsync("data: {not-json}\n\n");
        await context.Response.WriteAsync(": keep-alive\r\n\r\n");
        await context.Response.WriteAsync("event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\r\n\r\n");
        var text = "event: message\r\ndata: " + FixtureJson.Serialize(response) + "\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += 17)
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(i, Math.Min(17, bytes.Length - i)));
            await context.Response.Body.FlushAsync();
        }
        return;
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(FixtureJson.Serialize(mode == "batch" ? new JsonArray(response) : response));
});
await app.StartAsync();
Console.WriteLine(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/mcp");
_ = Task.Run(async () =>
{
    await Console.In.ReadToEndAsync();
    await app.StopAsync();
});
await app.WaitForShutdownAsync();

internal sealed class FakeSession
{
    private bool initialized;
    private int calls;
    private int lists;
    public string Version { get; } = Environment.GetEnvironmentVariable("FAKE_PROTOCOL") ?? "2025-11-25";
    private static readonly SemaphoreSlim LogLock = new(1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new();
    public Channel<JsonObject> Responses { get; } = Channel.CreateUnbounded<JsonObject>();

    public static async Task LogAsync(string path, JsonObject entry)
    {
        await LogLock.WaitAsync();
        try { await File.AppendAllTextAsync(path, FixtureJson.Serialize(entry) + "\n"); }
        finally { LogLock.Release(); }
    }

    public async Task<JsonObject?> HandleAsync(JsonObject request)
    {
        if (Environment.GetEnvironmentVariable("FAKE_REQUEST_LOG") is { } path)
            await LogAsync(path, request);
        var method = request["method"]?.ToString();
        if (method == "notifications/cancelled")
        {
            if (request["params"]?["requestId"] is { } id && pending.TryGetValue(id.ToJsonString(), out var cancellation))
                await cancellation.CancelAsync();
            return null;
        }
        if (method == "notifications/initialized") { initialized = true; return null; }
        if (request["id"] is null) return null;
        JsonNode result;
        if (method == "initialize")
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_INIT_DELAY"), out var delay)) await Task.Delay(delay);
            result = new JsonObject
            {
                ["protocolVersion"] = Version,
                ["capabilities"] = Environment.GetEnvironmentVariable("FAKE_NO_TOOLS") == "1" ? new JsonObject() :
                    new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                ["serverInfo"] = new JsonObject { ["name"] = "offline-fixture", ["version"] = "1.0.0" },
                ["instructions"] = "Use these offline tools.\nSecond instruction."
            };
        }
        else if (!initialized) return Error(request, "not initialized");
        else if (method == "tools/list")
        {
            if (Environment.GetEnvironmentVariable("FAKE_LIST_MALFORMED") == "1")
                return Result(request, new JsonObject { ["tools"] = "not-an-array" });
            lists++;
            var schema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string","description":"Text to echo"},"count":{"type":"integer"}},"required":["text"]}""")!;
            var tools = new JsonArray();
            foreach (var name in new[] { "echo", "multi", "binary", "empty", "fail", "slow", "state", "ping", "read_file", "delete_file", "nested/tool" })
            {
                var tool = new JsonObject { ["name"] = name, ["description"] = "Description for " + name, ["inputSchema"] = schema.DeepClone() };
                if (name == "echo")
                {
                    if (Environment.GetEnvironmentVariable("FAKE_TOOL_OVERRIDES") is { } overrides)
                        foreach (var (key, value) in FixtureJson.Parse(overrides)!.AsObject())
                            tool[key] = value?.DeepClone();
                    if (Environment.GetEnvironmentVariable("FAKE_OUTPUT_SCHEMA") is { } outputSchema)
                        tool["outputSchema"] = FixtureJson.Parse(outputSchema);
                    if (Environment.GetEnvironmentVariable("FAKE_REQUIRED_TASK") == "1")
                        tool["execution"] = new JsonObject { ["taskSupport"] = "required" };
                }
                tools.Add(tool);
            }
            result = new JsonObject { ["tools"] = tools, ["nextCursor"] = "upstream-only-reads-first-page" };
        }
        else if (method == "tools/call")
        {
            Interlocked.Increment(ref calls);
            var name = request["params"]?["name"]?.ToString();
            var arguments = request["params"]?["arguments"];
            if (Environment.GetEnvironmentVariable("FAKE_TOOL_RESULT") is { } toolResult)
                return Result(request, FixtureJson.Parse(toolResult)!);
            switch (name)
            {
                case "error_code_text":
                    if (calls == 1) return Error(request, "EPIPE while validating input");
                    result = Text("unexpected retry");
                    break;
                case "transient_once":
                    if (calls == 1) return Error(request, "HTTP 503 Service Unavailable");
                    result = Text("retried");
                    break;
                case "cancellable":
                    var requestId = request["id"]!.ToJsonString();
                    using (var cancellation = new CancellationTokenSource())
                    {
                        pending[requestId] = cancellation;
                        try
                        {
                            await Task.Delay(5000, cancellation.Token);
                            if (Environment.GetEnvironmentVariable("FAKE_REQUEST_LOG") is { } completedLog)
                                await LogAsync(completedLog, new JsonObject { ["method"] = "fixture/completed" });
                            result = Text("completed");
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            if (Environment.GetEnvironmentVariable("FAKE_REQUEST_LOG") is { } cancelledLog)
                                await LogAsync(cancelledLog, new JsonObject { ["method"] = "fixture/cancelled" });
                            return Error(request, "cancelled");
                        }
                        finally { pending.TryRemove(requestId, out _); }
                    }
                    break;
                case "fail": return Error(request, "validation failed: text required");
                case "missing": return Error(request, "unknown tool");
                case "slow":
                    await Task.Delay(arguments?["delay"]?.GetValue<int>() ?? 2000);
                    result = Text("finished");
                    break;
                case "multi": result = new JsonObject { ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "first" },
                    new JsonObject { ["type"] = "image", ["data"] = "AA==", ["mimeType"] = "image/png" },
                    new JsonObject { ["type"] = "text", ["text"] = "second" }) }; break;
                case "binary": result = new JsonObject { ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image", ["data"] = "AA==", ["mimeType"] = "image/png" }) }; break;
                case "empty": result = new JsonObject { ["content"] = new JsonArray() }; break;
                case "state": result = Text(new JsonObject { ["pid"] = Environment.ProcessId, ["calls"] = calls, ["lists"] = lists,
                    ["cwd"] = Environment.CurrentDirectory, ["env"] = Environment.GetEnvironmentVariable("FAKE_VALUE"),
                    ["args"] = new JsonArray(Environment.GetCommandLineArgs().Select(a => (JsonNode)JsonValue.Create(a)).ToArray()) }.ToJsonString()); break;
                case "error_content": result = Text("Tool reported an error"); result["isError"] = true; break;
                case "malformed_result": result = new JsonObject { ["content"] = "not-an-array" }; break;
                case "malformed_content": result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text" }) }; break;
                case "malformed_error_flag": result = Text("invalid"); result["isError"] = "true"; break;
                case "structured_only": result = new JsonObject { ["structuredContent"] = new JsonObject { ["value"] = 42 } }; break;
                default:
                    if (arguments is not JsonObject) return Error(request, "invalid_type: arguments must be an object");
                    result = Text(arguments["text"]?.GetValue<string>() ?? FixtureJson.Serialize(arguments));
                    if (arguments["structured"] is { } structured) result["structuredContent"] = structured.DeepClone();
                    if (arguments["isError"] is { } isError) result["isError"] = isError.DeepClone();
                    break;
            }

        }
        else return Error(request, "Method not found");
        return Result(request, result);
    }

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
    };
    private static JsonObject Result(JsonObject request, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = ResponseId(request), ["result"] = result
    };
    private static JsonObject Error(JsonObject request, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = ResponseId(request),
        ["error"] = new JsonObject
        {
            ["code"] = long.TryParse(Environment.GetEnvironmentVariable("FAKE_ERROR_CODE"), out var code) ? code : -32602,
            ["message"] = message
        }
    };
    private static JsonNode ResponseId(JsonObject request) =>
        Environment.GetEnvironmentVariable("FAKE_STRING_IDS") == "1" ?
            JsonValue.Create(request["id"]!.ToString()) : request["id"]!.DeepClone();
}

internal static class FixtureJson
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = int.MaxValue };
    public static JsonNode? Parse(string json) => JsonNode.Parse(json,
        documentOptions: new JsonDocumentOptions { MaxDepth = int.MaxValue });
    public static string Serialize(JsonNode node) => node.ToJsonString(Options);
}
