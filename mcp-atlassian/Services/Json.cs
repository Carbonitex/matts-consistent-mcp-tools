using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpAtlassian.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    /// <summary>Serializes a tool result for the model. Indented so it reads naturally.</summary>
    public static string Out(object? value) => value switch
    {
        null => "{}",
        string s => s,
        JsonNode n => n.ToJsonString(Indented),
        _ => JsonSerializer.Serialize(value, Indented),
    };

    /// <summary>Converts a tool argument (JsonElement from the MCP layer) to a mutable JsonNode.</summary>
    public static JsonNode? ToNode(JsonElement? element) =>
        element is { } e && e.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? JsonNode.Parse(e.GetRawText())
            : null;

    /// <summary>Accepts either an object or a JSON string containing an object (models do both).</summary>
    public static JsonObject? ToObject(JsonElement? element)
    {
        var node = ToNode(element);
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('{'))
            node = JsonNode.Parse(s);
        return node as JsonObject;
    }

    public static string Q(string value) => Uri.EscapeDataString(value);
}
