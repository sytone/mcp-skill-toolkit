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
                new StdioTransport(name, config, settings));
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

    public async Task<ToolInfo[]> ListToolsAsync(CancellationToken cancellationToken)
    {
        if (rpc.Capabilities?.ContainsKey("tools") != true)
            throw new InvalidOperationException("Server does not support tools (required for tools/list)");
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
        }, "list tools", settings, cancellationToken);
    }

    public Task<JsonNode?> CallToolAsync(string name, JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (!config.IsToolAllowed(name)) throw new InvalidOperationException($"Tool \"{name}\" is disabled by configuration");
        if (rpc.Capabilities?.ContainsKey("tools") != true)
            throw new InvalidOperationException("Server does not support tools (required for tools/call)");
        var tool = Volatile.Read(ref metadata).GetValueOrDefault(name);
        if (tool?.RequiresTask == true)
            throw new McpException(-32600, $"Tool \"{name}\" requires task-based execution. Use client.experimental.tasks.callToolStream() instead.");
        return Retry.RunAsync<JsonNode?>(async token =>
        {
            var result = await rpc.RequestAsync("tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments?.DeepClone() }, token);
            if (result is not JsonObject obj)
                throw new InvalidDataException("Invalid tools/call response: expected result object");
            if (!obj.ContainsKey("content")) obj["content"] = new JsonArray();
            if (obj["content"] is not JsonArray content)
                throw new InvalidDataException("Invalid tools/call response: expected content array");
            foreach (var block in content) ValidateContent(block);
            if (obj.ContainsKey("isError") && obj["isError"]?.GetValueKind() is not
                (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                throw new InvalidDataException("Invalid tools/call response: isError must be a boolean");
            if (obj.ContainsKey("structuredContent") && obj["structuredContent"] is not JsonObject)
                throw new InvalidDataException("Invalid tools/call response: structuredContent must be an object");
            if (tool?.Output is { } validator)
            {
                if (obj["structuredContent"] is null && obj["isError"]?.ToString() != "true")
                    throw new McpException(-32600, $"Tool {name} has an output schema but did not return structured content");
                if (obj["structuredContent"] is { } structured && !validator.IsValid(structured))
                    throw new McpException(-32602, "Structured content does not match the tool's output schema");
            }
            return result;
        }, $"call tool {name}", settings, cancellationToken);
    }

    public Task<string?> GetInstructionsAsync(CancellationToken cancellationToken) => Task.FromResult(rpc.Instructions);
    public ValueTask DisposeAsync() => rpc.DisposeAsync();

    private static void ValidateContent(JsonNode? node)
    {
        static bool IsString(JsonNode? value) => value?.GetValueKind() == System.Text.Json.JsonValueKind.String;
        if (node is not JsonObject block)
            throw new InvalidDataException("Invalid tools/call content: expected content object");
        var valid = block["type"]?.ToString() switch
        {
            "text" => IsString(block["text"]),
            "image" or "audio" => IsString(block["data"]) && IsString(block["mimeType"]),
            "resource_link" => IsString(block["name"]) && IsString(block["uri"]),
            "resource" => block["resource"] is JsonObject resource && IsString(resource["uri"]) &&
                (IsString(resource["text"]) || IsString(resource["blob"])),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid tools/call content: unsupported type or missing required fields");
    }

    public static async ValueTask SafeCloseAsync(IAsyncDisposable connection, RuntimeSettings settings)
    {
        try { await connection.DisposeAsync(); }
        catch (Exception error) { settings.Debug($"Failed to close connection: {error.Message}"); }
    }
}
