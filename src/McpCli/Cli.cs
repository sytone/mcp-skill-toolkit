using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpCli;

public static class Cli
{
    public const string Version = "0.3.0";

    public static async Task<int> RunAsync(string[] arguments, TextReader input, TextWriter output, TextWriter error,
        bool inputRedirected, bool colorize, RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = CliArguments.Parse(arguments);
            if (args.Command == "help") { await output.WriteLineAsync(Help); return 0; }
            if (args.Command == "version") { await output.WriteLineAsync($"mcp-cli v{Version}"); return 0; }
            if (args.Command == "info" && string.IsNullOrEmpty(args.Server))
            {
                var names = await AvailableServersAsync(args.ConfigPath, settings, cancellationToken);
                await error.WriteLineAsync("Error [MISSING_ARGUMENT]: Missing required argument for info: server");
                await error.WriteLineAsync($"  Available servers: {(names.Length > 0 ? string.Join(", ", names) : "(none found)")}");
                await error.WriteLineAsync("  Suggestion: Use 'mcp-cli info <server>' to see server details, or just 'mcp-cli' to list all");
                return 1;
            }
            var config = await Configuration.LoadAsync(args.ConfigPath, settings, cancellationToken);
            var format = new Output(colorize);
            if (args.Command is "list" or "grep")
            {
                if (config.Count == 0)
                {
                    await error.WriteLineAsync("Warning: No servers configured. Add servers to mcp_servers.json");
                    return 0;
                }
                var servers = await FetchServersAsync(config, settings, cancellationToken);
                if (args.Command == "list")
                {
                    await output.WriteLineAsync(format.ServerList(servers.OrderBy(s => s.Name, StringComparer.InvariantCulture),
                        args.WithDescriptions));
                    return 0;
                }
                var pattern = Glob.Compile(args.Pattern!);
                var failed = servers.Where(s => s.Error is not null).Select(s => s.Name).ToArray();
                if (failed.Length > 0)
                    await error.WriteLineAsync($"Warning: {failed.Length} server(s) failed to connect: {string.Join(", ", failed)}");
                var matches = servers.SelectMany(s => s.Tools.Where(t => pattern.IsMatch(t.Name)).Select(t => (s.Name, t))).ToArray();
                if (matches.Length > 0) await output.WriteLineAsync(format.SearchResults(matches));
                else
                {
                    await output.WriteLineAsync($"No tools found matching \"{args.Pattern}\"");
                    await output.WriteLineAsync("  Tip: Pattern matches tool names only (not server names)");
                    await output.WriteLineAsync("  Tip: Use '*' for wildcards, e.g. '*file*' or 'read_*'");
                    await output.WriteLineAsync("  Tip: Run 'mcp-cli' to list all available tools");
                }
                return 0;
            }

            var name = args.Server!;
            if (!config.TryGetValue(name, out var server)) throw CliError.ServerNotFound(name, config.Keys);
            var payload = args.Command is "call" or "approve" ?
                await ReadArgumentsAsync(args.Json, input, inputRedirected, settings, cancellationToken) : null;
            if (args.Command == "approve")
            {
                if (server.Elicitation?.Action != "ask" || !settings.DaemonEnabled)
                    throw new CliError(2, "APPROVAL_UNAVAILABLE", "Resuming approval requires elicitation action ask and cached workers");
                var response = new JsonObject { ["action"] = args.ApprovalAction };
                if (args.ApprovalAction == "accept" || payload is not JsonObject { Count: 0 })
                    response["content"] = payload?.DeepClone();
                ElicitationPolicy.Parse(response);
                try
                {
                    var result = await Daemon.RequestAsync(Daemon.PipeName(name, server, settings), "approve", args.Tool,
                        new JsonObject { ["approvalId"] = args.ApprovalId, ["response"] = response },
                        settings, settings.TimeoutMs, cancellationToken, connectTimeoutMs: 1000);
                    await output.WriteLineAsync(Output.ToolResult(result));
                    return 0;
                }
                catch (ApprovalRequiredException) { throw; }
                catch (Exception cause) when (cause is not OperationCanceledException)
                {
                    throw CliError.ToolExecution(name, args.Tool!, cause.Message);
                }
            }
            IMcpConnection connection;
            try { connection = await McpConnection.ConnectAsync(name, server, settings, cancellationToken); }
            catch (Exception cause) when (cause is not OperationCanceledException) { throw CliError.Connection(name, cause.Message); }
            try
            {
                if (args.Command == "info")
                {
                    var tools = await connection.ListToolsAsync(cancellationToken);
                    if (args.Tool is { } toolName)
                    {
                        var tool = tools.FirstOrDefault(t => t.Name == toolName) ??
                            throw CliError.ToolNotFound(name, toolName, tools.Select(t => t.Name).ToArray());
                        await output.WriteLineAsync(format.ToolSchema(name, tool));
                    }
                    else await output.WriteLineAsync(format.ServerDetails(name, server, tools, args.WithDescriptions,
                        await connection.GetInstructionsAsync(cancellationToken)));
                    return 0;
                }
                try
                {
                    var result = await connection.CallToolAsync(args.Tool!, payload, cancellationToken);
                    await output.WriteLineAsync(Output.ToolResult(result));
                }
                catch (ApprovalRequiredException) { throw; }
                catch (Exception cause) when (cause is not OperationCanceledException)
                {
                    string[]? available = null;
                    try { available = (await connection.ListToolsAsync(cancellationToken)).Select(t => t.Name).ToArray(); }
                    catch (Exception discoveryError) when (discoveryError is not OperationCanceledException)
                    {
                        settings.Debug($"Could not list available tools after call failure: {discoveryError.Message}");
                    }
                    if (cause.Message.Contains("not found") || cause.Message.Contains("unknown tool"))
                        throw CliError.ToolNotFound(name, args.Tool!, available, 2);
                    throw CliError.ToolExecution(name, args.Tool!, cause.Message);
                }
            }
            finally { await McpConnection.SafeCloseAsync(connection, settings); }
            return 0;
        }
        catch (ApprovalRequiredException approval)
        {
            await output.WriteLineAsync(McpJson.Serialize(approval.Request));
            return 4;
        }
        catch (CliError failure) { await error.WriteLineAsync(failure.Message); return failure.ExitCode; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Error [CANCELLED]: Operation cancelled");
            return 130;
        }
        catch (Exception failure) { await error.WriteLineAsync(failure.Message); return 1; }
    }

    private static async Task<JsonNode?> ReadArgumentsAsync(string? json, TextReader input, bool redirected,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(json) && redirected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TimeoutMs);
            try
            {
                // Console.In can perform a synchronous read even through its Async API on Windows.
                json = (await Task.Run(() => input.ReadToEndAsync(timeout.Token), CancellationToken.None)
                    .WaitAsync(timeout.Token)).Trim();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"stdin read timed out after {settings.TimeoutMs}ms");
            }
        }
        if (string.IsNullOrEmpty(json)) return new JsonObject();
        try { return McpJson.Parse(json); }
        catch (JsonException cause)
        {
            throw new CliError(1, "INVALID_JSON_ARGUMENTS", "Invalid JSON in tool arguments", $"Parse error: {cause.Message}",
                "Use valid JSON: '{\"path\": \"./file.txt\"}'. Run 'mcp-cli info <server> <tool>' for the schema.");
        }
    }

    private static async Task<ServerTools[]> FetchServersAsync(Dictionary<string, ServerConfig> servers,
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        var entries = servers.ToArray();
        var results = new ServerTools[entries.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = settings.Concurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var (name, server) = entries[index];
                IMcpConnection? connection = null;
                try
                {
                    connection = await McpConnection.ConnectAsync(name, server, settings, token);
                    results[index] = new(name, await connection.ListToolsAsync(token),
                        await connection.GetInstructionsAsync(token));
                }
                catch (Exception cause) when (cause is not OperationCanceledException)
                {
                    settings.Debug($"{name}: connection failed - {cause.Message}");
                    results[index] = new(name, [], Error: cause.Message);
                }
                finally { if (connection is not null) await McpConnection.SafeCloseAsync(connection, settings); }
            });
        return results;
    }

    private static async Task<string[]> AvailableServersAsync(string? path, RuntimeSettings settings, CancellationToken token)
    {
        var candidates = new[] { path, settings.Environment("MCP_CONFIG_PATH") }.Concat(Configuration.SearchPaths(settings));
        foreach (var candidate in candidates.Where(p => !string.IsNullOrEmpty(p)))
        {
            try
            {
                var root = McpJson.Parse(await File.ReadAllTextAsync(
                    Path.GetFullPath(candidate!, settings.WorkingDirectory), token));
                if (root is JsonObject obj && obj["mcpServers"] is JsonObject servers) return servers.Select(p => p.Key).ToArray();
            }
            catch (Exception cause) when (cause is IOException or UnauthorizedAccessException or JsonException)
            {
                settings.Debug($"Could not read available servers: {cause.Message}");
            }
        }
        return [];
    }

    public static string Help => $$"""

        mcp-cli v{{Version}} - A lightweight CLI for MCP servers

        Usage:
          mcp-cli [options]                              List all servers and tools
          mcp-cli [options] info <server>                Show server details
          mcp-cli [options] info <server> <tool>         Show tool schema
          mcp-cli [options] grep <pattern>               Search tools by glob pattern
          mcp-cli [options] call <server> <tool>         Call tool (reads JSON from stdin if no args)
          mcp-cli [options] call <server> <tool> <json>  Call tool with JSON arguments
          mcp-cli [options] approve <server> <tool> <approvalId> <accept|decline|cancel> [json]
                                                        Resume a pending call, never replay it

        Formats (both work):
          mcp-cli info server tool                       Space-separated
          mcp-cli info server/tool                       Slash-separated
          mcp-cli call server tool '{}'                  Space-separated
          mcp-cli call server/tool '{}'                  Slash-separated

        Options:
          -h, --help               Show this help message
          -v, --version            Show version number
          -d, --with-descriptions  Include tool descriptions
          -c, --config <path>      Path to mcp_servers.json config file

        Output:
          mcp-cli/info/grep        Human-readable text to stdout
          call                    Text content, or JSON for non-text results
          Approval needed         JSON status approval_required, exit code 4
          Errors                  Always to stderr

        Examples:
          mcp-cli
          mcp-cli -d
          mcp-cli grep "*file*"
          mcp-cli info filesystem
          mcp-cli info filesystem read_file
          mcp-cli call filesystem read_file '{}'
          Get-Content -Raw input.json | mcp-cli call server tool

        Environment Variables:
          MCP_NO_DAEMON=1        Disable connection caching (force fresh connections)
          MCP_DAEMON_TIMEOUT=N   Set daemon idle timeout in seconds (default: 60)

        Config File:
          The CLI looks for mcp_servers.json in:
            1. Path specified by -c/--config or MCP_CONFIG_PATH
            2. ./mcp_servers.json (current directory)
            3. ~/.mcp_servers.json
            4. ~/.config/mcp/mcp_servers.json

        """;
}
