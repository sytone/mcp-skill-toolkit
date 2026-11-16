namespace McpCli;

public sealed class CliError(int exitCode, string type, string message,
    string? details = null, string? suggestion = null) : Exception(
        $"Error [{type}]: {message}" +
        (string.IsNullOrEmpty(details) ? "" : $"\n  Details: {details}") +
        (string.IsNullOrEmpty(suggestion) ? "" : $"\n  Suggestion: {suggestion}"))
{
    public int ExitCode { get; } = exitCode;

    public static CliError Missing(string command, string argument) => new(1, "MISSING_ARGUMENT",
        $"Missing required argument for {command}: {argument}", suggestion: command switch
        {
            "call" => "Use 'mcp-cli call <server> <tool> '{\"key\": \"value\"}''",
            "grep" => "Use 'mcp-cli grep \"*pattern*\"'",
            "-c/--config" => "Use 'mcp-cli -c /path/to/mcp_servers.json'",
            _ => "Run 'mcp-cli --help' for usage examples"
        });

    public static CliError UnknownOption(string option) => new(1, "UNKNOWN_OPTION",
        $"Unknown option: {option}", suggestion: option.TrimStart('-').ToLowerInvariant() switch
        {
            "server" or "s" => "Server is a positional argument. Use 'mcp-cli info <server>'",
            "tool" or "t" => "Tool is a positional argument. Use 'mcp-cli call <server> <tool>'",
            "args" or "arguments" or "a" or "input" =>
                "Pass JSON directly: 'mcp-cli call <server> <tool> '{\"key\": \"value\"}''",
            "pattern" or "p" or "search" or "query" => "Use 'mcp-cli grep \"*pattern*\"'",
            "call" or "run" or "exec" => "Use 'call' as a subcommand, not option: 'mcp-cli call <server> <tool>'",
            "info" or "list" or "get" => "Use 'info' as a subcommand, not option: 'mcp-cli info <server>'",
            _ => "Valid options: -h/--help, -v/--version, -c/--config, -d/--with-descriptions"
        });

    public static string? SubcommandAlias(string command) => command.ToLowerInvariant() switch
    {
        "run" or "execute" or "exec" or "invoke" => "call",
        "list" or "ls" or "get" or "show" or "describe" => "info",
        "search" or "find" or "query" => "grep",
        _ => null
    };

    public static CliError Ambiguous(string server, string tool, bool hasArgs) => new(1,
        "AMBIGUOUS_COMMAND", "Ambiguous command: did you mean to call a tool or view info?",
        $"Received: mcp-cli {server} {tool}{(hasArgs ? " ..." : "")}",
        $"Use 'mcp-cli call {server} {tool}{(hasArgs ? " '<json>'" : "")}' to execute, or 'mcp-cli info {server} {tool}' to view schema");

    public static CliError ServerNotFound(string server, IEnumerable<string> available)
    {
        var names = available.ToArray();
        return new(1, "SERVER_NOT_FOUND", $"Server \"{server}\" not found in config",
            $"Available servers: {(names.Length > 0 ? string.Join(", ", names) : "(none)")}",
            names.Length > 0 ? $"Use one of: {string.Join(", ", names.Select(n => $"mcp-cli {n}"))}" :
                $"Add server to mcp_servers.json: {{ \"mcpServers\": {{ \"{server}\": {{ ... }} }} }}");
    }

    public static CliError Connection(string server, string cause) => new(3,
        "SERVER_CONNECTION_FAILED", $"Failed to connect to server \"{server}\"", cause,
        cause.Contains("ENOENT") || cause.Contains("not found") ?
            "Command not found. Install the MCP server: npx -y @modelcontextprotocol/server-<name>" :
        cause.Contains("ECONNREFUSED") ? "Server refused connection. Check if the server is running and URL is correct" :
        cause.Contains("ETIMEDOUT") || cause.Contains("timeout") ?
            "Connection timed out. Check network connectivity and server availability" :
        cause.Contains("401") || cause.Contains("Unauthorized") ? "Authentication required. Add Authorization header to config" :
        cause.Contains("403") || cause.Contains("Forbidden") ? "Access forbidden. Check credentials and permissions" :
            "Check server configuration and ensure the server process can start");

    public static CliError ToolNotFound(string server, string tool, string[]? tools, int exitCode = 1) =>
        new(exitCode, "TOOL_NOT_FOUND", $"Tool \"{tool}\" not found in server \"{server}\"",
            tools is null ? null : $"Available tools: {string.Join(", ", tools.Take(5))}" +
                (tools.Length > 5 ? $" (+{tools.Length - 5} more)" : ""),
            $"Run 'mcp-cli {server}' to see all available tools");

    public static CliError ToolExecution(string server, string tool, string cause) => new(2,
        "TOOL_EXECUTION_FAILED", $"Tool \"{tool}\" execution failed", cause,
        cause.Contains("validation") || cause.Contains("invalid_type") ?
            $"Run 'mcp-cli info {server} {tool}' to see the input schema, then fix arguments" :
        cause.Contains("required") ? $"Missing required argument. Run 'mcp-cli info {server} {tool}' to see required fields" :
        cause.Contains("permission") || cause.Contains("denied") ? "Permission denied. Check file/resource permissions" :
        cause.Contains("not found") || cause.Contains("ENOENT") ? "Resource not found. Verify the path or identifier exists" :
            "Check tool arguments match the expected schema");
}
