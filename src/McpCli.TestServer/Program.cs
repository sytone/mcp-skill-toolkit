using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (Environment.GetEnvironmentVariable("FAKE_PID_PATH") is { } pidPath)
    await File.AppendAllTextAsync(pidPath, Environment.ProcessId + "\n");
if (Environment.GetEnvironmentVariable("FAKE_STDERR") is { } diagnostic) Console.Error.WriteLine(diagnostic);
if (args.FirstOrDefault() != "http")
{
    var session = new FakeSession();
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if (request["method"]?.ToString() == "tools/call" &&
            request["params"]?["name"]?.ToString() == "ping")
        {
            Console.WriteLine("""{"jsonrpc":"2.0","id":"server-ping","method":"ping"}""");
            var pong = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "")!;
            if (pong["id"]?.ToString() != "server-ping" || pong["result"] is not JsonObject)
                throw new InvalidDataException("Client did not answer server ping");
        }
        var response = await session.HandleAsync(request);
        if (response is not null)
        {
            if (Environment.GetEnvironmentVariable("FAKE_MALFORMED") == "1")
                Console.WriteLine("{not-json}");
            else Console.WriteLine(response.ToJsonString());
        }
    }
    return;
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
var sessions = new ConcurrentDictionary<string, FakeSession>();
var resume = new ConcurrentDictionary<string, JsonObject>();
var httpFailures = 0;
app.MapMethods("/mcp", ["GET", "POST", "DELETE"], async context =>
{
    var mode = Environment.GetEnvironmentVariable("FAKE_HTTP_MODE") ?? "json";
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
    var request = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
    var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
    FakeSession session;
    if (request["method"]?.ToString() == "initialize")
    {
        sessionId = Guid.NewGuid().ToString("N");
        session = new FakeSession();
        sessions[sessionId] = session;
        context.Response.Headers["Mcp-Session-Id"] = sessionId;
    }
    else if (!sessions.TryGetValue(sessionId, out session!) ||
        context.Request.Headers["MCP-Protocol-Version"].ToString() != session.Version)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing session or protocol header");
        return;
    }
    var response = await session.HandleAsync(request);
    if (response is null) { context.Response.StatusCode = mode == "batch" ? 200 : 202; return; }
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
    if (mode == "sse")
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync(": keep-alive\r\n\r\n");
        await context.Response.WriteAsync("event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\r\n\r\n");
        var text = "event: message\r\ndata: " + response.ToJsonString() + "\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += 17)
        {
            await context.Response.Body.WriteAsync(bytes.AsMemory(i, Math.Min(17, bytes.Length - i)));
            await context.Response.Body.FlushAsync();
        }
        return;
    }
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(mode == "batch" ? new JsonArray(response).ToJsonString() : response.ToJsonString());
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

    public async Task<JsonObject?> HandleAsync(JsonObject request)
    {
        if (Environment.GetEnvironmentVariable("FAKE_REQUEST_LOG") is { } path)
        {
            await LogLock.WaitAsync();
            try { await File.AppendAllTextAsync(path, request.ToJsonString() + "\n"); }
            finally { LogLock.Release(); }
        }
        var method = request["method"]?.ToString();
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
                    if (Environment.GetEnvironmentVariable("FAKE_OUTPUT_SCHEMA") is { } outputSchema)
                        tool["outputSchema"] = JsonNode.Parse(outputSchema);
                    if (Environment.GetEnvironmentVariable("FAKE_REQUIRED_TASK") == "1")
                        tool["execution"] = new JsonObject { ["taskSupport"] = "required" };
                }
                tools.Add(tool);
            }
            result = new JsonObject { ["tools"] = tools, ["nextCursor"] = "upstream-only-reads-first-page" };
        }
        else if (method == "tools/call")
        {
            calls++;
            var name = request["params"]?["name"]?.ToString();
            var arguments = request["params"]?["arguments"];
            switch (name)
            {
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
                    result = Text(arguments["text"]?.GetValue<string>() ?? arguments.ToJsonString());
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
        ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result
    };
    private static JsonObject Error(JsonObject request, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
        ["error"] = new JsonObject { ["code"] = -32602, ["message"] = message }
    };
}
