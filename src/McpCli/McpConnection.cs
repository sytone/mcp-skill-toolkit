using System.Text.Json.Nodes;

namespace McpCli;

public interface IMcpConnection : IAsyncDisposable
{
    bool IsDaemon { get; }
    Task<ToolInfo[]> ListToolsAsync(CancellationToken cancellationToken);
    Task<JsonNode?> CallToolAsync(string name, JsonNode? arguments, CancellationToken cancellationToken);
    Task<string?> GetInstructionsAsync(CancellationToken cancellationToken);
}

public sealed class McpConnection(RpcConnection rpc, ServerConfig config, RuntimeSettings settings) : IMcpConnection
{
    private sealed record Metadata(OutputSchema? Output, bool RequiresTask);
    private Dictionary<string, Metadata> metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutputSchema> outputSchemas = new(StringComparer.Ordinal);
    public bool IsDaemon => false;

    public static async Task<IMcpConnection> ConnectAsync(string name, ServerConfig config,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        if (settings.DaemonEnabled)
        {
            try
            {
                var daemon = await Daemon.ConnectAsync(name, config, settings, cancellationToken);
                settings.Debug($"Using daemon connection for {name}");
                return daemon;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                settings.Debug($"Daemon connection failed for {name}: {error.Message}, falling back to direct");
            }
        }
        settings.Debug($"Using direct connection for {name}");
        return await ConnectDirectAsync(name, config, settings, cancellationToken);
    }

    public static Task<McpConnection> ConnectDirectAsync(string name, ServerConfig config,
        RuntimeSettings settings, CancellationToken cancellationToken) =>
        Retry.RunAsync(async token =>
        {
            var rpc = new RpcConnection(config.IsHttp ? new HttpTransport(config, settings) :
                new StdioTransport(name, config, settings), settings);
            try
            {
                await rpc.InitializeAsync(token);
                return new McpConnection(rpc, config, settings);
            }
            catch
            {
                await SafeCloseAsync(rpc, settings);
                throw;
            }
        }, $"connect to {name}", settings, cancellationToken);

    public Task<ToolInfo[]> ListToolsAsync(CancellationToken cancellationToken) => ListToolsAsync(cancellationToken, null);

    internal async Task<ToolInfo[]> ListToolsAsync(CancellationToken cancellationToken, RetryOptions? options)
    {
        return await Retry.RunAsync(async token =>
        {
            var result = await rpc.RequestAsync("tools/list", new(), token);
            if (result is not JsonObject obj || obj["tools"] is not JsonArray tools)
                throw new InvalidDataException("Invalid tools/list response: expected tools array");
            var parsed = tools.Select(ToolInfo.FromJson).ToArray();
            var discovered = new Dictionary<string, Metadata>(StringComparer.Ordinal);
            foreach (var tool in tools.OfType<JsonObject>())
            {
                if (tool["outputSchema"] is JsonObject output && output["type"]?.ToString() != "object")
                    throw new InvalidDataException("Tool outputSchema must have type object");
                discovered[tool["name"]!.GetValue<string>()] = new(
                    tool["outputSchema"] is JsonObject schema ? OutputSchema.Compile(schema, outputSchemas) : null,
                    tool["execution"]?["taskSupport"]?.ToString() == "required");
            }
            Interlocked.Exchange(ref metadata, discovered);
            return parsed.Where(t => config.IsToolAllowed(t.Name)).ToArray();
        }, "list tools", settings, cancellationToken, options);
    }

    public Task<JsonNode?> CallToolAsync(string name, JsonNode? arguments, CancellationToken cancellationToken) =>
        CallToolAsync(name, arguments, cancellationToken, null);

    internal Task<JsonNode?> CallToolAsync(string name, JsonNode? arguments, CancellationToken cancellationToken, RetryOptions? options)
    {
        if (!config.IsToolAllowed(name)) throw new InvalidOperationException($"Tool \"{name}\" is disabled by configuration");
        var tool = Volatile.Read(ref metadata).GetValueOrDefault(name);
        if (tool?.RequiresTask == true)
            throw new McpException(-32600, $"Tool \"{name}\" requires task-based execution. Use client.experimental.tasks.callToolStream() instead.");
        return Retry.RunAsync<JsonNode?>(async token =>
        {
            var result = await rpc.RequestAsync("tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments?.DeepClone() }, token);
            var obj = McpSchema.CallResult(result);
            if (tool?.Output is { } validator)
            {
                if (obj["structuredContent"] is null && obj["isError"]?.ToString() != "true")
                    throw new McpException(-32600, $"Tool {name} has an output schema but did not return structured content");
                if (obj["structuredContent"] is { } structured && !validator.IsValid(structured))
                    throw new McpException(-32602, "Structured content does not match the tool's output schema");
            }
            return obj;
        }, $"call tool {name}", settings, cancellationToken, options);
    }

    public Task<string?> GetInstructionsAsync(CancellationToken cancellationToken) => Task.FromResult(rpc.Instructions);
    public ValueTask DisposeAsync() => rpc.DisposeAsync();

    public static async ValueTask SafeCloseAsync(IAsyncDisposable connection, RuntimeSettings settings)
    {
        try { await connection.DisposeAsync(); }
        catch (Exception error) { settings.Debug($"Failed to close connection: {error.Message}"); }
    }
}
