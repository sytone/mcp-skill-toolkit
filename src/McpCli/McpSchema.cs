using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpCli;

internal static class McpSchema
{
    public static JsonObject Tool(JsonNode? node)
    {
        var tool = Object(node, "tool");
        String(tool, "name");
        String(tool, "title", optional: true);
        String(tool, "description", optional: true);
        ObjectField(tool, "_meta");
        ObjectSchema(tool["inputSchema"], "inputSchema");
        if (tool.ContainsKey("outputSchema")) ObjectSchema(tool["outputSchema"], "outputSchema");
        var result = Keep(tool, "name", "title", "icons", "description", "inputSchema", "outputSchema", "annotations", "execution", "_meta");
        if (tool.ContainsKey("icons")) result["icons"] = Icons(tool["icons"]);
        if (tool.ContainsKey("annotations"))
        {
            var annotations = Object(tool["annotations"], "tool annotations");
            String(annotations, "title", optional: true);
            foreach (var name in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                Boolean(annotations, name);
            result["annotations"] = Keep(annotations, "title", "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint");
        }
        if (tool.ContainsKey("execution"))
        {
            var execution = Object(tool["execution"], "execution");
            Enum(execution, "taskSupport", ["required", "optional", "forbidden"]);
            result["execution"] = Keep(execution, "taskSupport");
        }
        return result;
    }

    public static JsonObject CallResult(JsonNode? node)
    {
        var result = Object(node, "tools/call result").DeepClone().AsObject();
        if (!result.ContainsKey("content")) result["content"] = new JsonArray();
        if (result["content"] is not JsonArray content)
            throw new InvalidDataException("Invalid tools/call response: expected content array");
        for (var i = 0; i < content.Count; i++) content[i] = Content(content[i]);
        Boolean(result, "isError");
        ObjectField(result, "structuredContent");
        ObjectField(result, "_meta");
        var normalized = Keep(result, "_meta", "content", "structuredContent", "isError");
        foreach (var (key, value) in result)
            if (!normalized.ContainsKey(key)) normalized[key] = value?.DeepClone();
        return normalized;
    }

    private static void ObjectSchema(JsonNode? node, string name)
    {
        var schema = Object(node, name);
        if (String(schema, "type") != "object") throw Invalid(name, "type object");
        if (schema.ContainsKey("properties"))
            foreach (var property in Object(schema["properties"], $"{name}.properties"))
                Object(property.Value, $"{name}.properties.{property.Key}");
        if (schema.ContainsKey("required")) StringArray(schema["required"], $"{name}.required");
    }

    private static JsonObject Content(JsonNode? node)
    {
        var block = Object(node, "content");
        var type = String(block, "type");
        ObjectField(block, "_meta");
        JsonObject result;
        switch (type)
        {
            case "text":
                String(block, "text");
                result = Keep(block, "type", "text", "annotations", "_meta");
                break;
            case "image":
            case "audio":
                Base64(String(block, "data")!);
                String(block, "mimeType");
                result = Keep(block, "type", "data", "mimeType", "annotations", "_meta");
                break;
            case "resource":
                var resource = Object(block["resource"], "resource");
                String(resource, "uri");
                String(resource, "mimeType", optional: true);
                ObjectField(resource, "_meta");
                var isText = IsString(resource["text"]);
                if (!isText) Base64(String(resource, "blob")!);
                result = Keep(block, "type", "annotations", "_meta");
                result["resource"] = Keep(resource, "uri", "mimeType", "_meta", isText ? "text" : "blob");
                result = Keep(result, "type", "resource", "annotations", "_meta");
                break;
            case "resource_link":
                String(block, "name");
                String(block, "uri");
                foreach (var name in new[] { "title", "description", "mimeType" }) String(block, name, optional: true);
                result = Keep(block, "name", "title", "icons", "uri", "description", "mimeType", "annotations", "_meta", "type");
                if (block.ContainsKey("icons")) result["icons"] = Icons(block["icons"]);
                break;
            default: throw Invalid("content.type", "text, image, audio, resource or resource_link");
        }
        if (block.ContainsKey("annotations")) result["annotations"] = Annotations(block["annotations"]);
        return result;
    }

    private static JsonObject Annotations(JsonNode? node)
    {
        var annotations = Object(node, "annotations");
        if (annotations.ContainsKey("audience"))
            foreach (var role in StringArray(annotations["audience"], "audience"))
                if (role is not ("user" or "assistant")) throw Invalid("audience", "user or assistant");
        if (annotations.ContainsKey("priority") &&
            (annotations["priority"]?.GetValueKind() != JsonValueKind.Number ||
             !annotations["priority"]!.AsValue().TryGetValue<double>(out var priority) || priority is < 0 or > 1))
            throw Invalid("priority", "a number between 0 and 1");
        if (annotations.ContainsKey("lastModified"))
        {
            var text = String(annotations, "lastModified")!;
            if (!Regex.IsMatch(text, @"^\d{4}-\d\d-\d\dT\d\d:\d\d(?::\d\d(?:\.\d+)?)?(?:Z|[+-]\d\d:\d\d)$", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw Invalid("lastModified", "an ISO datetime with offset");
        }
        return Keep(annotations, "audience", "priority", "lastModified");
    }

    private static JsonArray Icons(JsonNode? node)
    {
        if (node is not JsonArray icons) throw Invalid("icons", "an array");
        return new JsonArray(icons.Select(item =>
        {
            var icon = Object(item, "icon");
            String(icon, "src");
            String(icon, "mimeType", optional: true);
            if (icon.ContainsKey("sizes")) StringArray(icon["sizes"], "icon.sizes");
            Enum(icon, "theme", ["light", "dark"]);
            return (JsonNode)Keep(icon, "src", "mimeType", "sizes", "theme");
        }).ToArray());
    }

    private static void Base64(string text)
    {
        var compact = Regex.Replace(text, @"[\t\n\f\r ]", "");
        if (compact.Length % 4 == 1 || (compact.Contains('=') && compact.Length % 4 != 0))
            throw Invalid("base64", "valid Base64 text");
        try { Convert.FromBase64String(compact.PadRight((compact.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { throw Invalid("base64", "valid Base64 text"); }
    }

    private static JsonObject Object(JsonNode? node, string name) => node as JsonObject ?? throw Invalid(name, "an object");
    private static bool IsString(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String;
    private static string? String(JsonObject obj, string name, bool optional = false)
    {
        if (optional && !obj.ContainsKey(name)) return null;
        return IsString(obj[name]) ? obj[name]!.GetValue<string>() : throw Invalid(name, "a string");
    }
    private static void Boolean(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name) && obj[name]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Invalid MCP response: {name} must be a boolean");
    }
    private static void ObjectField(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name)) Object(obj[name], name);
    }
    private static string[] StringArray(JsonNode? node, string name) =>
        node is JsonArray array && array.All(IsString) ? array.Select(n => n!.GetValue<string>()).ToArray() :
            throw Invalid(name, "an array of strings");
    private static void Enum(JsonObject obj, string name, string[] allowed)
    {
        if (obj.ContainsKey(name) && !allowed.Contains(String(obj, name))) throw Invalid(name, string.Join(", ", allowed));
    }
    private static JsonObject Keep(JsonObject obj, params string[] fields) =>
        new(fields.Where(obj.ContainsKey).Select(name => KeyValuePair.Create(name, obj[name]?.DeepClone())));
    private static InvalidDataException Invalid(string name, string expected) =>
        new($"Invalid MCP response: missing required fields or invalid type for {name}; expected {expected}");
}
