using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpCli;

public static class McpJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = int.MaxValue };
    private static readonly JsonSerializerOptions CompactOptions = new() { MaxDepth = int.MaxValue };
    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        MaxDepth = int.MaxValue,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonNode? Parse(string json) => JsonNode.Parse(json, documentOptions: DocumentOptions);
    public static string Serialize(JsonNode? value) => value?.ToJsonString(CompactOptions) ?? "null";
}
