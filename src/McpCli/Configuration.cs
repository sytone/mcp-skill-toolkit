using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpCli;

public sealed class RuntimeSettings
{
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
    public Action<string> Diagnostics { get; set; } = Console.Error.WriteLine;
    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;
    public string HomeDirectory { get; init; } =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    public int TimeoutMs => ReadInt("MCP_TIMEOUT", 1800, 1, int.MaxValue / 1000) * 1000;
    public int Concurrency => ReadInt("MCP_CONCURRENCY", 5, 1);
    public int MaxRetries => ReadInt("MCP_MAX_RETRIES", 3, 0);
    public int RetryDelayMs => ReadInt("MCP_RETRY_DELAY", 1000, 1);
    public int DaemonTimeoutMs => ReadInt("MCP_DAEMON_TIMEOUT", 60, 1, int.MaxValue / 1000) * 1000;
    public bool DaemonEnabled => Environment("MCP_NO_DAEMON") != "1";
    public bool StrictEnvironment => Environment("MCP_STRICT_ENV")?.ToLowerInvariant() is not ("false" or "0");

    public void Debug(string message)
    {
        if (!string.IsNullOrEmpty(Environment("MCP_DEBUG"))) Diagnostics($"[mcp-cli] {message}");
    }

    private int ReadInt(string name, int fallback, int minimum, int maximum = int.MaxValue)
    {
        var value = Environment(name);
        if (string.IsNullOrEmpty(value)) return fallback;
        // Match JavaScript parseInt: a numeric prefix is accepted, including "5s".
        var match = Regex.Match(value, @"^\s*([+-]?\d+)", RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var number) &&
            number >= minimum && number <= maximum) return number;
        Debug($"Invalid {name}; using default {fallback}");
        return fallback;
    }
}

public sealed record ServerConfig(JsonObject Value)
{
    public bool IsHttp => Value.ContainsKey("url");
    public string Command => Value["command"]!.GetValue<string>();
    public string Url => Value["url"]!.GetValue<string>();
    public string? Cwd => Value["cwd"]?.GetValue<string>();
    public string[] Args => Strings("args");
    public string[] AllowedTools => Strings("allowedTools");
    public string[] DisabledTools => Strings("disabledTools");

    public string[] Strings(string property) => Value[property] is JsonArray array ?
        array.Select(n => n!.GetValue<string>()).ToArray() : [];

    public IEnumerable<KeyValuePair<string, string>> Map(string property) =>
        Value[property] is JsonObject obj ?
            obj.Select(p => KeyValuePair.Create(p.Key, p.Value!.GetValue<string>())) : [];

    public bool IsToolAllowed(string name) =>
        !DisabledTools.Any(p => Glob.Compile(p, false).IsMatch(name)) &&
        (AllowedTools.Length == 0 || AllowedTools.Any(p => Glob.Compile(p, false).IsMatch(name)));

    public string Hash()
    {
        static JsonNode? Canonicalize(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
            _ => node?.DeepClone()
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            McpJson.Serialize(Canonicalize(Value)))))[..16];
    }
}

public static class Configuration
{
    public static string[] SearchPaths(RuntimeSettings settings) =>
    [
        Path.Combine(settings.WorkingDirectory, "mcp_servers.json"),
        Path.Combine(settings.HomeDirectory, ".mcp_servers.json"),
        Path.Combine(settings.HomeDirectory, ".config", "mcp", "mcp_servers.json")
    ];

    public static async Task<Dictionary<string, ServerConfig>> LoadAsync(string? explicitPath,
        RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        var selected = !string.IsNullOrEmpty(explicitPath) ? explicitPath : settings.Environment("MCP_CONFIG_PATH");
        string path;
        if (!string.IsNullOrEmpty(selected))
        {
            path = Path.GetFullPath(selected, settings.WorkingDirectory);
            if (!File.Exists(path))
                throw new CliError(1, "CONFIG_NOT_FOUND", $"Config file not found: {path}",
                    suggestion: "Create mcp_servers.json with: { \"mcpServers\": { \"server-name\": { \"command\": \"...\" } } }");
        }
        else
        {
            path = SearchPaths(settings).FirstOrDefault(File.Exists) ??
                throw new CliError(1, "CONFIG_NOT_FOUND", "No mcp_servers.json found in search paths",
                    "Searched: ./mcp_servers.json, ~/.mcp_servers.json, ~/.config/mcp/mcp_servers.json",
                    "Create mcp_servers.json in current directory or use -c/--config to specify path");
        }

        JsonNode? root;
        try
        {
            root = McpJson.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (JsonException error)
        {
            throw new CliError(1, "CONFIG_INVALID_JSON", $"Invalid JSON in config file: {path}", error.Message,
                "Check for syntax errors: missing commas, unquoted keys, trailing commas");
        }
        if (root is not JsonObject || root["mcpServers"] is not JsonObject servers)
            throw new CliError(1, "CONFIG_MISSING_FIELD", "Config file missing required \"mcpServers\" object",
                $"File: {path}", "Config must have structure: { \"mcpServers\": { ... } }");
        if (servers.Count == 0)
            settings.Diagnostics("[mcp-cli] Warning: No servers configured in mcpServers. Add server configurations to use MCP tools.");

        foreach (var (name, node) in servers)
        {
            if (node is not JsonObject server)
                throw InvalidServer(name, $"Invalid server configuration for \"{name}\"", "Server config must be an object");
            if (!server.ContainsKey("command") && !server.ContainsKey("url"))
                throw InvalidServer(name, $"Server \"{name}\" missing required field",
                    "Must have either \"command\" (for stdio) or \"url\" (for HTTP)");
            if (server.ContainsKey("command") && server.ContainsKey("url"))
                throw InvalidServer(name, $"Server \"{name}\" has both \"command\" and \"url\"",
                    "A server must be either stdio (command) or HTTP (url), not both");
        }
        Substitute(root, settings);
        var result = new Dictionary<string, ServerConfig>(StringComparer.Ordinal);
        foreach (var (name, node) in servers)
        {
            var server = node!.AsObject();
            ValidateServer(name, server);
            result.Add(name, new(server));
        }
        return result;
    }

    public static void ValidateServer(string name, JsonObject server)
    {
        var primary = server.ContainsKey("url") ? "url" : "command";
        if (!IsString(server[primary]) || string.IsNullOrWhiteSpace(server[primary]!.GetValue<string>()))
            throw InvalidServer(name, $"Invalid server configuration for \"{name}\"", $"{primary} must be a non-empty string");
        foreach (var field in new[] { "args", "allowedTools", "disabledTools" })
            if (server.ContainsKey(field) && (server[field] is not JsonArray array || array.Any(n => !IsString(n))))
                throw InvalidServer(name, $"Invalid server configuration for \"{name}\"", $"{field} must be an array of strings");
        foreach (var field in new[] { "env", "headers" })
            if (server.ContainsKey(field) && (server[field] is not JsonObject map || map.Any(p => !IsString(p.Value))))
                throw InvalidServer(name, $"Invalid server configuration for \"{name}\"", $"{field} must be an object of strings");
        if (server.ContainsKey("cwd") && !IsString(server["cwd"]))
            throw InvalidServer(name, $"Invalid server configuration for \"{name}\"", "cwd must be a string");
    }

    private static bool IsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    private static CliError InvalidServer(string name, string message, string details) =>
        new(1, "CONFIG_INVALID_SERVER", message, details,
            $"Use {{ \"command\": \"...\" }} for stdio or {{ \"url\": \"...\" }} for HTTP in \"{name}\"");

    private static void Substitute(JsonNode node, RuntimeSettings settings)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (IsString(value)) obj[key] = SubstituteString(value!.GetValue<string>(), settings);
                else if (value is not null) Substitute(value, settings);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (IsString(array[i])) array[i] = SubstituteString(array[i]!.GetValue<string>(), settings);
                else if (array[i] is { } child) Substitute(child, settings);
            }
        }
    }

    private static string SubstituteString(string input, RuntimeSettings settings)
    {
        List<string> missing = [];
        var result = Regex.Replace(input, @"\$\{([^}]+)\}", match =>
        {
            var value = settings.Environment(match.Groups[1].Value);
            if (value is not null) return value;
            missing.Add(match.Groups[1].Value);
            return "";
        });
        if (missing.Count == 0) return result;
        var message = $"Missing environment variable{(missing.Count > 1 ? "s" : "")}: " +
            string.Join(", ", missing.Select(n => "${" + n + "}"));
        if (settings.StrictEnvironment)
            throw new CliError(1, "MISSING_ENV_VAR", message, "Referenced in config but not set in environment",
                $"Set the variable(s) before running: export {missing[0]}=\"value\" or set MCP_STRICT_ENV=false to use empty values");
        settings.Diagnostics($"[mcp-cli] Warning: {message}");
        return result;
    }
}

public static class Glob
{
    public static Regex Compile(string pattern, bool pathAware = true)
    {
        var expression = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
            {
                var globstar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                expression.Append(!pathAware || globstar ? ".*" : "[^/]*");
            }
            else if (pattern[i] == '?') expression.Append(pathAware ? "[^/]" : ".");
            else expression.Append(Regex.Escape(pattern[i].ToString()));
        }
        expression.Append('$');
        return new(expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.NonBacktracking);
    }
}
