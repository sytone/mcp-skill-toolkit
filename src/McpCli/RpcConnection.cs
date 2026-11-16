using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpCli;

public abstract class RpcTransport : IAsyncDisposable
{
    public Func<JsonObject, CancellationToken, Task> ReceiveAsync { get; set; } = (_, _) => Task.CompletedTask;
    public Action<Exception> Failed { get; set; } = _ => { };
    public string? ProtocolVersion { get; set; }
    public abstract Task StartAsync(CancellationToken cancellationToken);
    public abstract Task SendAsync(JsonObject message, CancellationToken cancellationToken);
    public virtual void Initialized() { }
    public abstract ValueTask DisposeAsync();

    protected async Task DeliverAsync(string json, CancellationToken cancellationToken)
    {
        var message = JsonNode.Parse(json);
        if (message is not JsonObject obj || obj["jsonrpc"]?.ToString() != "2.0")
            throw new InvalidDataException("Invalid JSON-RPC message from server");
        await ReceiveAsync(obj, cancellationToken);
    }
}

public sealed class RpcConnection : IAsyncDisposable
{
    private readonly RpcTransport transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private long nextId;
    private Exception? failure;
    public string? Instructions { get; private set; }
    public JsonObject? Capabilities { get; private set; }
    public string? ProtocolVersion => transport.ProtocolVersion;
    public static readonly string[] SupportedVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05", "2024-10-07"];

    public RpcConnection(RpcTransport transport)
    {
        this.transport = transport;
        transport.ReceiveAsync = ReceiveAsync;
        transport.Failed = Fail;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await transport.StartAsync(cancellationToken);
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = SupportedVersions[0],
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "mcp-cli", ["version"] = Cli.Version }
        }, cancellationToken);
        if (result is not JsonObject obj || obj["protocolVersion"] is not JsonValue version ||
            !version.TryGetValue<string>(out var protocol) || !SupportedVersions.Contains(protocol) ||
            obj["capabilities"] is not JsonObject capabilities || obj["serverInfo"] is not JsonObject server ||
            server["name"]?.GetValueKind() != JsonValueKind.String || server["version"]?.GetValueKind() != JsonValueKind.String)
            throw new InvalidDataException("Invalid or unsupported MCP initialize response");
        transport.ProtocolVersion = protocol;
        Capabilities = capabilities;
        Instructions = obj["instructions"]?.GetValue<string>();
        await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            cancellationToken);
        transport.Initialized();
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        if (failure is { } prior) throw new IOException("MCP connection is closed", prior);
        var id = Interlocked.Increment(ref nextId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[key] = completion;
        try
        {
            // A read-loop failure can race with registration.
            if (failure is { } error) throw new IOException("MCP connection is closed", error);
            await transport.SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone()
            }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            pending.TryRemove(key, out _);
        }
    }

    private async Task ReceiveAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (message["method"] is { } method)
        {
            if (message["id"] is { } requestId)
            {
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone() };
                if (method.ToString() == "ping") response["result"] = new JsonObject();
                else response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
                await transport.SendAsync(response, cancellationToken);
            }
            return;
        }
        if (message["id"] is not { } id || !pending.TryGetValue(id.ToJsonString(), out var completion))
            return;
        if (message["error"] is JsonObject error)
        {
            completion.TrySetException(new McpException(error["code"]?.GetValue<int>() ?? -32603,
                error["message"]?.GetValue<string>() ?? "Unknown MCP error", error["data"]?.DeepClone()));
        }
        else if (message.ContainsKey("result")) completion.TrySetResult(message["result"]?.DeepClone());
        else completion.TrySetException(new InvalidDataException("Invalid JSON-RPC response: no result or error"));
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        foreach (var item in pending.Values) item.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new IOException("MCP connection closed"));
        await transport.DisposeAsync();
    }
}

public sealed class McpException(int code, string message, JsonNode? data = null)
    : Exception($"MCP error {code}: {message}")
{
    public int Code { get; } = code;
    public JsonNode? DataValue { get; } = data;
}
