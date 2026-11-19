using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpCli;

public sealed record ElicitationPolicy(string Action, JsonObject? Content)
{
    public static ElicitationPolicy Parse(JsonNode? node)
    {
        if (node is not JsonObject policy || policy.Any(p => p.Key is not ("action" or "content")) ||
            policy["action"]?.GetValueKind() != JsonValueKind.String ||
            policy["action"]!.GetValue<string>() is not ("accept" or "decline" or "cancel" or "ask"))
            throw new InvalidDataException("elicitation must be an object with action accept, decline, cancel, or ask and optional content");
        var action = policy["action"]!.GetValue<string>();
        if (policy.ContainsKey("content") &&
            (action != "accept" || policy["content"] is not JsonObject content || content.Any(p => !IsContentValue(p.Value))))
            throw new InvalidDataException("elicitation content is only valid for accept and must be an object of primitives or string arrays");
        return new(action, policy["content"]?.DeepClone().AsObject());
    }

    private static bool IsContentValue(JsonNode? value) =>
        value?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ||
        value is JsonArray array && array.All(item => item?.GetValueKind() == JsonValueKind.String);

    public JsonObject Respond(JsonNode? parameters)
    {
        if (Action == "ask") throw new InvalidOperationException("Ask mode requires an explicit approval response");
        if (parameters is not JsonObject request ||
            (request.ContainsKey("mode") && (request["mode"]?.GetValueKind() != JsonValueKind.String ||
                request["mode"]!.GetValue<string>() != "form")))
            throw new McpException(-32602, "Only form-mode elicitation is supported; URL interactions require a user");
        if (request["message"]?.GetValueKind() != JsonValueKind.String ||
            request["requestedSchema"] is not JsonObject schema || schema["type"]?.ToString() != "object")
            throw new McpException(-32602, "Elicitation requires a message and an object requestedSchema");

        var response = new JsonObject { ["action"] = Action };
        if (Action != "accept") return response;
        var content = Content?.DeepClone().AsObject() ?? new JsonObject();
        bool valid;
        try
        {
            valid = new OutputSchema(schema).IsValid(content) &&
                content.All(p => schema["properties"] is JsonObject properties && properties.ContainsKey(p.Key));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or
            InvalidOperationException or FormatException or RegexMatchTimeoutException)
        {
            throw new McpException(-32602, $"Invalid elicitation requestedSchema: {error.Message}");
        }
        if (!valid)
            throw new McpException(-32602, "Configured elicitation content does not match requestedSchema; no approval was sent");
        response["content"] = content;
        return response;
    }
}
