using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpCli;

public sealed record ToolInfo(string Name, string? Description, JsonObject InputSchema)
{
    public static ToolInfo FromJson(JsonNode? node)
    {
        if (node is not JsonObject tool || tool["name"] is not JsonValue name ||
            !name.TryGetValue<string>(out var text) || tool["inputSchema"] is not JsonObject schema ||
            schema["type"]?.ToString() != "object")
            throw new InvalidDataException("Invalid tools/list response: expected tool name and inputSchema");
        return new(text, tool["description"]?.GetValue<string>(), schema);
    }
}

public sealed record ServerTools(string Name, ToolInfo[] Tools, string? Instructions = null, string? Error = null);

public sealed class Output(bool colorize)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Json(JsonNode? value) => value?.ToJsonString(JsonOptions) ?? "null";
    private string Color(string value, string code) => colorize ? $"\x1b[{code}m{value}\x1b[0m" : value;
    private string Bold(string value) => Color(value, "1");

    public string ServerList(IEnumerable<ServerTools> servers, bool descriptions)
    {
        List<string> lines = [];
        foreach (var server in servers)
        {
            lines.Add(colorize ? $"\x1b[1m\x1b[36m{server.Name}\x1b[0m" : server.Name);
            if (!string.IsNullOrEmpty(server.Instructions))
            {
                var parts = server.Instructions.Split('\n');
                var first = parts[0][..Math.Min(100, parts[0].Length)];
                var suffix = parts.Length > 1 || parts[0].Length > 100 ? "..." : "";
                lines.Add($"  {Color($"Instructions: {first}{suffix}", "2")}");
            }
            var tools = server.Error is null ? server.Tools :
                [new ToolInfo($"<error: {server.Error}>", null, new())];
            foreach (var tool in tools)
                lines.Add($"  \u2022 {tool.Name}" +
                    (descriptions && !string.IsNullOrEmpty(tool.Description) ? $" - {Color(tool.Description, "2")}" : ""));
            lines.Add("");
        }
        return string.Join('\n', lines).TrimEnd();
    }

    public string SearchResults(IEnumerable<(string Server, ToolInfo Tool)> results) =>
        string.Join('\n', results.Select(r => $"{Color(r.Server, "36")} {Color(r.Tool.Name, "32")}" +
            (string.IsNullOrEmpty(r.Tool.Description) ? "" : $" {Color(r.Tool.Description, "2")}")));

    public string ServerDetails(string name, ServerConfig config, ToolInfo[] tools, bool descriptions, string? instructions)
    {
        List<string> lines = [$"{Bold("Server:")} {Color(name, "36")}"];
        if (config.IsHttp)
        {
            lines.Add($"{Bold("Transport:")} HTTP");
            lines.Add($"{Bold("URL:")} {config.Url}");
        }
        else
        {
            lines.Add($"{Bold("Transport:")} stdio");
            lines.Add($"{Bold("Command:")} {config.Command} {string.Join(' ', config.Args)}");
        }
        if (!string.IsNullOrEmpty(instructions))
        {
            lines.Add("");
            lines.Add(Bold("Instructions:"));
            lines.Add(string.Join('\n', instructions.Split('\n').Select(s => $"  {s}")));
        }
        lines.Add("");
        lines.Add(Bold($"Tools ({tools.Length}):"));
        foreach (var tool in tools)
        {
            lines.Add($"  {Color(tool.Name, "32")}");
            if (descriptions && !string.IsNullOrEmpty(tool.Description)) lines.Add($"    {Color(tool.Description, "2")}");
            if (tool.InputSchema["properties"] is JsonObject properties)
            {
                lines.Add($"    {Color("Parameters:", "33")}");
                foreach (var (parameter, schema) in properties)
                {
                    var required = tool.InputSchema["required"] is JsonArray array &&
                        array.Any(n => n?.ToString() == parameter) ? "required" : "optional";
                    var typeNode = schema is JsonObject property ? property["type"] : null;
                    var type = typeNode is JsonArray types ? string.Join(',', types.Select(n => n?.ToString())) :
                        typeNode?.ToString() ?? "any";
                    var description = descriptions && schema is JsonObject prop && prop["description"] is { } desc ?
                        $" - {desc.GetValue<string>()}" : "";
                    lines.Add($"      \u2022 {parameter} ({type}, {required}){description}");
                }
            }
            lines.Add("");
        }
        return string.Join('\n', lines).TrimEnd();
    }

    public string ToolSchema(string server, ToolInfo tool)
    {
        List<string> lines = [$"{Bold("Tool:")} {Color(tool.Name, "32")}", $"{Bold("Server:")} {Color(server, "36")}", ""];
        if (!string.IsNullOrEmpty(tool.Description))
        {
            lines.Add(Bold("Description:"));
            lines.Add($"  {tool.Description}");
            lines.Add("");
        }
        lines.Add(Bold("Input Schema:"));
        lines.Add(Json(tool.InputSchema));
        return string.Join('\n', lines);
    }

    public static string ToolResult(JsonNode? result)
    {
        if (result is JsonObject obj && obj["content"] is JsonArray content)
        {
            var text = content.OfType<JsonObject>()
                .Where(c => c["type"]?.ToString() == "text" && !string.IsNullOrEmpty(c["text"]?.ToString()))
                .Select(c => c["text"]!.GetValue<string>()).ToArray();
            if (text.Length > 0) return string.Join('\n', text);
        }
        return Json(result);
    }
}
