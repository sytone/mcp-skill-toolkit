using System.Collections.Concurrent;
using System.Globalization;
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
        var message = McpJson.Parse(json);
        if (message is not JsonObject obj || obj["jsonrpc"]?.GetValueKind() != JsonValueKind.String ||
            obj["jsonrpc"]!.GetValue<string>() != "2.0")
            throw new InvalidDataException("Invalid JSON-RPC message from server");
        await ReceiveAsync(obj, cancellationToken);
    }
}

public sealed class RpcConnection : IAsyncDisposable
{
    private readonly RpcTransport transport;
    private readonly RuntimeSettings settings;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private long nextId;
    private Exception? failure;
    public string? Instructions { get; private set; }
    public JsonObject? Capabilities { get; private set; }
    public string? ProtocolVersion => transport.ProtocolVersion;
    public static readonly string[] SupportedVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05", "2024-10-07"];

    public RpcConnection(RpcTransport transport, RuntimeSettings settings)
    {
        this.transport = transport;
        this.settings = settings;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!completion.Task.IsCompleted && pending.TryRemove(key, out _))
            {
                // The request's token has expired; cancellation needs its own bounded send.
                using var cancellation = new CancellationTokenSource(1000);
                try
                {
                    await transport.SendAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                        ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "Request cancelled or timed out" }
                    }, cancellation.Token);
                }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                {
                    settings.Debug($"Failed to send cancellation: {error.Message}");
                }
            }
            throw;
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
        if (CorrelationId(message["id"]) is not { } key || !pending.TryGetValue(key, out var completion))
            return;
        if (message["error"] is JsonObject error)
        {
            if (error["code"]?.GetValueKind() != JsonValueKind.Number ||
                !error["code"]!.AsValue().TryGetValue<double>(out var code) ||
                !double.IsFinite(code) || code != Math.Truncate(code) || Math.Abs(code) > 9007199254740991 ||
                error["message"]?.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException("Invalid JSON-RPC error response");
            completion.TrySetException(new McpException((long)code,
                error["message"]!.GetValue<string>(), error["data"]?.DeepClone()));
        }
        else if (message.ContainsKey("result")) completion.TrySetResult(message["result"]?.DeepClone());
        else completion.TrySetException(new InvalidDataException("Invalid JSON-RPC response: no result or error"));
    }

    internal static string? CorrelationId(JsonNode? id)
    {
        double number;
        if (id?.GetValueKind() == JsonValueKind.Number)
        {
            if (!id.AsValue().TryGetValue<double>(out number)) return null;
        }
        else if (id?.GetValueKind() == JsonValueKind.String)
        {
            if (!double.TryParse(id.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        }
        else return null;
        return double.IsFinite(number) ? number.ToString("R", CultureInfo.InvariantCulture) : null;
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

public sealed class McpException(long code, string message, JsonNode? data = null)
    : Exception($"MCP error {code}: {message}")
{
    public long Code { get; } = code;
    public JsonNode? DataValue { get; } = data;
}
