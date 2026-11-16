using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpCli;

// The upstream SDK uses AJV's draft-07 vocabulary for cached tool output schemas.
public sealed class OutputSchema
{
    private readonly JsonObject root;
    private readonly IDictionary<string, OutputSchema> registry;
    private readonly Dictionary<string, JsonNode> references = new(StringComparer.Ordinal);
    private readonly Dictionary<JsonNode, Uri> bases = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(JsonNode Schema, JsonNode? Value)> evaluating = [];
    private static readonly Uri DefaultBase = new("https://mcp-cli.invalid/schema");
    private static readonly string[] SchemaMaps = ["properties", "patternProperties", "definitions", "$defs"];
    private static readonly string[] SchemaSingles =
        ["additionalProperties", "additionalItems", "contains", "propertyNames", "not", "if", "then", "else"];

    public OutputSchema(JsonObject schema) : this(schema, new Dictionary<string, OutputSchema>(StringComparer.Ordinal)) { }

    private OutputSchema(JsonObject schema, IDictionary<string, OutputSchema> registry)
    {
        root = schema;
        this.registry = registry;
        Index(schema, DefaultBase);
        foreach (var node in bases.Keys.OfType<JsonObject>())
        {
            if (node["$ref"] is { } reference) _ = Resolve(node, reference.GetValue<string>());
            if (node["pattern"] is { } pattern) _ = Pattern(pattern.GetValue<string>());
            if (node["patternProperties"] is JsonObject patterns)
                foreach (var name in patterns.Select(p => p.Key)) _ = Pattern(name);
        }
        foreach (var id in references.Keys) registry.TryAdd(id, this);
    }

    public static OutputSchema Compile(JsonObject schema, IDictionary<string, OutputSchema> registry)
    {
        lock (registry)
        {
            if (schema["$id"] is { } id && registry.TryGetValue(new Uri(DefaultBase, id.GetValue<string>()).AbsoluteUri, out var cached))
                return cached;
            return new OutputSchema(schema, registry);
        }
    }

    private void Index(JsonNode schema, Uri baseUri)
    {
        if (schema is JsonObject obj && obj["$id"] is { } id)
        {
            baseUri = new Uri(baseUri, id.GetValue<string>());
            if (!references.TryAdd(baseUri.AbsoluteUri, schema))
                throw new InvalidDataException($"Duplicate output schema ID: {baseUri}");
        }
        bases[schema] = baseUri;
        if (schema is not JsonObject map) return;
        foreach (var key in SchemaMaps)
            if (map[key] is JsonObject properties)
                foreach (var child in properties.Select(p => p.Value).OfType<JsonNode>()) Index(child, baseUri);
        foreach (var key in SchemaSingles)
            if (map[key] is { } child) Index(child, baseUri);
        foreach (var key in new[] { "allOf", "anyOf", "oneOf", "items" })
        {
            if (map[key] is JsonArray children)
                foreach (var child in children.OfType<JsonNode>()) Index(child, baseUri);
            else if (map[key] is { } child) Index(child, baseUri);
        }
        if (map["dependencies"] is JsonObject dependencies)
            foreach (var child in dependencies.Select(p => p.Value).Where(n => n is JsonObject or JsonValue).OfType<JsonNode>())
                Index(child, baseUri);
    }

    private (OutputSchema Owner, JsonNode Node) Resolve(JsonNode origin, string reference)
    {
        var uri = new Uri(bases[origin], reference);
        if (references.TryGetValue(uri.AbsoluteUri, out var exact)) return (this, exact);
        if (registry.TryGetValue(uri.AbsoluteUri, out var owner) && owner != this)
            return owner.Resolve(owner.root, uri.AbsoluteUri);
        var document = new UriBuilder(uri) { Fragment = "" }.Uri;
        var rootUri = new UriBuilder(bases[root]) { Fragment = "" }.Uri;
        if (document != rootUri && registry.TryGetValue(document.AbsoluteUri, out owner) && owner != this)
            return owner.Resolve(owner.root, uri.AbsoluteUri);
        var source = document == rootUri ? root :
            references.GetValueOrDefault(document.AbsoluteUri) ??
            throw new InvalidDataException($"Unresolved output schema reference: {reference}");
        var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (fragment.Length == 0) return (this, source);
        if (!fragment.StartsWith('/')) throw new InvalidDataException($"Unresolved output schema reference: {reference}");
        JsonNode? target = source;
        foreach (var part in fragment[1..].Split('/'))
        {
            var key = part.Replace("~1", "/").Replace("~0", "~");
            target = target switch
            {
                JsonObject obj => obj[key],
                JsonArray array when int.TryParse(key, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
        }
        return (this, target ?? throw new InvalidDataException($"Unresolved output schema reference: {reference}"));
    }

    public bool IsValid(JsonNode? value)
    {
        lock (registry)
        {
            evaluating.Clear();
            return Validate(root, value);
        }
    }

    private bool Validate(JsonNode schema, JsonNode? value)
    {
        if (schema is JsonValue boolean && boolean.TryGetValue<bool>(out var allowed)) return allowed;
        if (schema is not JsonObject rules) throw new InvalidDataException("Output schema must be an object or boolean");
        if (!evaluating.Add((schema, value))) throw new InvalidDataException("Output schema reference cycle makes no progress");
        try
        {
            if (rules["$ref"] is { } reference)
            {
                var (owner, target) = Resolve(schema, reference.GetValue<string>());
                if (!owner.Validate(target, value)) return false;
            }
            if (rules["type"] is JsonArray types)
            {
                if (!types.Any(t => MatchesType(t!.GetValue<string>(), value))) return false;
            }
            else if (rules["type"] is { } type && !MatchesType(type.GetValue<string>(), value) &&
                !(value is null && rules["nullable"]?.ToString() == "true")) return false;
            if (rules.ContainsKey("const") && !JsonNode.DeepEquals(rules["const"], value)) return false;
            if (rules["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value))) return false;
            if (rules["allOf"] is JsonArray all && !all.All(s => Validate(s!, value))) return false;
            if (rules["anyOf"] is JsonArray any && !any.Any(s => Validate(s!, value))) return false;
            if (rules["oneOf"] is JsonArray one && one.Count(s => Validate(s!, value)) != 1) return false;
            if (rules["not"] is { } not && Validate(not, value)) return false;
            if (rules["if"] is { } condition)
            {
                var branch = rules[Validate(condition, value) ? "then" : "else"];
                if (branch is not null && !Validate(branch, value)) return false;
            }
            if (value is JsonObject obj && !ValidateObject(rules, obj)) return false;
            if (value is JsonArray array && !ValidateArray(rules, array)) return false;
            if (value?.GetValueKind() == JsonValueKind.String)
            {
                var text = value.GetValue<string>();
                var length = text.EnumerateRunes().Count();
                if (Below(rules, "minLength", length) || Above(rules, "maxLength", length)) return false;
                if (rules["pattern"] is { } pattern && !Pattern(pattern.GetValue<string>()).IsMatch(text)) return false;
                if (rules["format"] is { } format && !ValidFormat(format.GetValue<string>(), text)) return false;
            }
            if (value?.GetValueKind() == JsonValueKind.Number)
            {
                var number = Number(value);
                if (Below(rules, "minimum", number) || Above(rules, "maximum", number)) return false;
                if (rules["exclusiveMinimum"] is { } min && number <= Number(min)) return false;
                if (rules["exclusiveMaximum"] is { } max && number >= Number(max)) return false;
                if (rules["multipleOf"] is { } multiple)
                {
                    var divisor = Number(multiple);
                    if (divisor <= 0) throw new InvalidDataException("Output schema multipleOf must be positive");
                    var quotient = number / divisor;
                    if (Math.Abs(quotient - Math.Round(quotient)) > 1e-12 * Math.Max(1, Math.Abs(quotient))) return false;
                }
                if (rules["format"]?.ToString() == "int32" && (number < int.MinValue || number > int.MaxValue || number != Math.Truncate(number))) return false;
                if (rules["format"]?.ToString() == "int64" && number != Math.Truncate(number)) return false;
            }
            return true;
        }
        finally { evaluating.Remove((schema, value)); }
    }

    private bool ValidateObject(JsonObject rules, JsonObject value)
    {
        if (Below(rules, "minProperties", value.Count) || Above(rules, "maxProperties", value.Count)) return false;
        if (rules["required"] is JsonArray required && required.Any(n => !value.ContainsKey(n!.GetValue<string>()))) return false;
        foreach (var (name, item) in value)
        {
            if (rules["propertyNames"] is { } names && !Validate(names, JsonValue.Create(name))) return false;
            var matched = false;
            if (rules["properties"] is JsonObject properties && properties[name] is { } property)
            {
                matched = true;
                if (!Validate(property, item)) return false;
            }
            if (rules["patternProperties"] is JsonObject patterns)
            {
                foreach (var (pattern, propertySchema) in patterns)
                    if (Pattern(pattern).IsMatch(name))
                    {
                        matched = true;
                        if (!Validate(propertySchema!, item)) return false;
                    }
            }
            if (!matched && rules["additionalProperties"] is { } additional && !Validate(additional, item)) return false;
        }
        if (rules["dependencies"] is JsonObject dependencies)
        {
            foreach (var (name, dependency) in dependencies)
            {
                if (!value.ContainsKey(name)) continue;
                if (dependency is JsonArray names)
                {
                    if (names.Any(n => !value.ContainsKey(n!.GetValue<string>()))) return false;
                }
                else if (dependency is not null && !Validate(dependency, value)) return false;
            }
        }
        return true;
    }

    private bool ValidateArray(JsonObject rules, JsonArray value)
    {
        if (Below(rules, "minItems", value.Count) || Above(rules, "maxItems", value.Count)) return false;
        if (rules["uniqueItems"]?.ToString() == "true")
            for (var i = 0; i < value.Count; i++)
                for (var j = 0; j < i; j++)
                    if (JsonNode.DeepEquals(value[i], value[j])) return false;
        if (rules["items"] is JsonArray tuple)
        {
            for (var i = 0; i < value.Count; i++)
            {
                var itemSchema = i < tuple.Count ? tuple[i] : rules["additionalItems"];
                if (itemSchema is not null && !Validate(itemSchema, value[i])) return false;
            }
        }
        else if (rules["items"] is { } itemSchema && !value.All(v => Validate(itemSchema, v))) return false;
        return rules["contains"] is not { } contains || value.Any(v => Validate(contains, v));
    }

    private static bool MatchesType(string type, JsonNode? value) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value?.GetValueKind() == JsonValueKind.String,
        "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value?.GetValueKind() == JsonValueKind.Number,
        "integer" => value?.GetValueKind() == JsonValueKind.Number && Number(value) == Math.Truncate(Number(value)),
        _ => throw new InvalidDataException($"Unknown output schema type: {type}")
    };
    private static double Number(JsonNode node) => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
    private static bool Below(JsonObject rules, string key, double value) => rules[key] is { } limit && value < Number(limit);
    private static bool Above(JsonObject rules, string key, double value) => rules[key] is { } limit && value > Number(limit);
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.ECMAScript | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static bool ValidFormat(string format, string text)
    {
        switch (format)
        {
            case "date": return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            case "time": return Pattern(@"^(?:[01]\d|2[0-3]):[0-5]\d:(?:[0-5]\d|60)(?:\.\d+)?(?:[zZ]|[+-](?:[01]\d|2[0-3]):[0-5]\d)$").IsMatch(text);
            case "iso-time": return Pattern(@"^(?:[01]\d|2[0-3]):[0-5]\d:(?:[0-5]\d|60)(?:\.\d+)?(?:[zZ]|[+-](?:[01]\d|2[0-3]):[0-5]\d)?$").IsMatch(text);
            case "date-time":
            case "iso-date-time":
                var separator = text.IndexOfAny(['T', 't', ' ']);
                return separator > 0 && ValidFormat("date", text[..separator]) &&
                    ValidFormat(format == "date-time" ? "time" : "iso-time", text[(separator + 1)..]);
            case "duration": return Pattern(@"^P(?=\d|T\d)(?:\d+Y)?(?:\d+M)?(?:\d+D)?(?:T(?=\d)(?:\d+H)?(?:\d+M)?(?:\d+S)?)?$|^P\d+W$").IsMatch(text);
            case "email": return MailAddress.TryCreate(text, out var mail) && mail.Address == text && text.Contains('@');
            case "hostname": return text.Length <= 253 && text.TrimEnd('.').Split('.').All(label =>
                Pattern(@"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$").IsMatch(label));
            case "ipv4": return text.Split('.') is { Length: 4 } parts &&
                parts.All(p => Pattern(@"^(0|[1-9]\d{0,2})$").IsMatch(p) && int.Parse(p) <= 255);
            case "ipv6": return IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            case "uri":
            case "url": return !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out _);
            case "uri-reference": return !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out _);
            case "uri-template": return Pattern(@"^(?:[^{}\s]|\{[+#./;?&=,!@|]?[a-zA-Z0-9_%]+(?::[1-9]\d{0,3}|\*)?(?:,[a-zA-Z0-9_%]+(?::[1-9]\d{0,3}|\*)?)*\})*$").IsMatch(text);
            case "uuid": return Guid.TryParseExact(text, "D", out _);
            case "json-pointer": return Pattern(@"^(?:/(?:[^~/]|~[01])*)*$").IsMatch(text);
            case "relative-json-pointer": return Pattern(@"^(?:0|[1-9]\d*)(?:#|(?:/(?:[^~/]|~[01])*)*)$").IsMatch(text);
            case "byte": return Pattern(@"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$").IsMatch(text);
            case "regex":
                try { _ = Pattern(text); return true; }
                catch (ArgumentException) { return false; }
            default: return true; // Unknown formats are annotations in upstream AJV's non-strict mode.
        }
    }
}
