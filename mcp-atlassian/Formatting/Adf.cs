using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace McpAtlassian.Formatting;

/// <summary>
/// Markdown &lt;-&gt; Atlassian Document Format conversion, shared by Jira (descriptions, comments)
/// and Confluence (page bodies via atlas_doc_format).
/// </summary>
public static class Adf
{
    /// <summary>Converts Markdown (CommonMark + GFM tables/strikethrough/task lists) to an ADF doc node.</summary>
    public static JsonObject FromMarkdown(string markdown) => MarkdownToAdf.Convert(markdown ?? "");

    /// <summary>Renders an ADF doc (or any ADF node) to Markdown. Unknown nodes degrade to their text content.</summary>
    public static string ToMarkdown(JsonNode? adf) => adf switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => AdfToMarkdown.Render(adf),
    };

    /// <summary>
    /// Resolves a tool's body argument to ADF. contentFormat "adf" accepts an ADF object or a JSON string of one;
    /// anything else (default "markdown") treats a string as Markdown. Returns null when value is absent.
    /// </summary>
    public static JsonObject? FromInput(JsonElement? value, string? contentFormat)
    {
        if (value is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        if (e.ValueKind == JsonValueKind.String)
            return FromInput(e.GetString(), contentFormat);
        if (e.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return Normalize(JsonNode.Parse(e.GetRawText()));
        // Numbers/booleans: treat their text as markdown.
        return FromMarkdown(e.GetRawText());
    }

    /// <summary>String-argument overload of <see cref="FromInput(JsonElement?, string?)"/>.</summary>
    public static JsonObject? FromInput(string? value, string? contentFormat)
    {
        if (value is null)
            return null;

        var trimmed = value.TrimStart();
        if (IsAdf(contentFormat))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(value);
            }
            catch (JsonException ex)
            {
                throw new McpException($"contentFormat is \"adf\" but the body is not valid JSON: {ex.Message}");
            }
            return Normalize(node);
        }

        // Markdown is the default, but accept an ADF document passed as a string anyway.
        if (trimmed.StartsWith('{') && trimmed.Contains("\"type\""))
        {
            try
            {
                if (JsonNode.Parse(value) is JsonObject o && (string?)o["type"] == "doc")
                    return Normalize(o);
            }
            catch (JsonException)
            {
            }
        }
        return FromMarkdown(value);
    }

    /// <summary>
    /// Prepares an ADF body for a tool response: "adf" returns the node unchanged, anything else
    /// (default "markdown") returns a Markdown string. Null-safe.
    /// </summary>
    public static JsonNode? ForOutput(JsonNode? adf, string? responseContentFormat)
    {
        if (adf is null)
            return null;
        if (IsAdf(responseContentFormat))
            return adf.DeepClone();
        return JsonValue.Create(ToMarkdown(adf));
    }

    static bool IsAdf(string? format) => string.Equals(format?.Trim(), "adf", StringComparison.OrdinalIgnoreCase);

    static readonly HashSet<string> InlineTypes = new(StringComparer.Ordinal)
    {
        "text", "hardBreak", "mention", "emoji", "inlineCard", "date", "status", "mediaInline", "placeholder", "inlineExtension",
    };

    /// <summary>Coerces arbitrary ADF-ish JSON into a valid root doc node.</summary>
    internal static JsonObject Normalize(JsonNode? node)
    {
        JsonArray content;
        switch (node)
        {
            case JsonObject o when (string?)o["type"] == "doc":
                o["version"] = 1;
                if (o["content"] is not JsonArray arr || arr.Count == 0)
                    o["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" });
                return o;
            case JsonArray a:
                content = a;
                break;
            case JsonObject o:
                content = new JsonArray(o);
                break;
            default:
                throw new McpException("ADF body must be a JSON object with type \"doc\" (or an array of ADF nodes).");
        }

        var blocks = new JsonArray();
        JsonArray? pendingInline = null;
        foreach (var child in content.ToList())
        {
            content.Remove(child);
            if (child is JsonObject c && InlineTypes.Contains((string?)c["type"] ?? ""))
            {
                if (pendingInline is null)
                {
                    pendingInline = new JsonArray();
                    blocks.Add(new JsonObject { ["type"] = "paragraph", ["content"] = pendingInline });
                }
                pendingInline.Add(c);
            }
            else if (child is not null)
            {
                pendingInline = null;
                blocks.Add(child);
            }
        }
        if (blocks.Count == 0)
            blocks.Add(new JsonObject { ["type"] = "paragraph" });
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = blocks };
    }
}
