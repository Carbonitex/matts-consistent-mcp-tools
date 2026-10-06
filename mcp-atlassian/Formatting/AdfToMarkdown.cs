using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpAtlassian.Formatting;

/// <summary>ADF -> Markdown renderer. Lossy by design: aims for something a model (or human) reads naturally.</summary>
internal static partial class AdfToMarkdown
{
    static readonly HashSet<string> InlineTypes = new(StringComparer.Ordinal)
    {
        "text", "hardBreak", "mention", "emoji", "inlineCard", "date", "status", "mediaInline", "placeholder", "inlineExtension",
    };

    static readonly HashSet<string> ListTypes = new(StringComparer.Ordinal) { "bulletList", "orderedList", "taskList" };

    public static string Render(JsonNode node)
    {
        var text = node switch
        {
            JsonArray a => RenderBlocks(a),
            JsonObject o when IsInline(o) => RenderInlines(new JsonArray(o.DeepClone()), false),
            JsonObject o => RenderBlock(o),
            _ => "",
        };
        return ExcessBlankLines().Replace(text, "\n\n").Trim();
    }

    static bool IsInline(JsonObject o) => InlineTypes.Contains(TypeOf(o));

    static string TypeOf(JsonNode? n) => (string?)n?["type"] ?? "";

    static string Attr(JsonNode? n, string name) => n?["attrs"]?[name]?.ToString() ?? "";

    static JsonArray Content(JsonNode? n) => n?["content"] as JsonArray ?? [];

    // ---------- blocks ----------

    static string RenderBlocks(JsonArray blocks, string separator = "\n\n")
    {
        var parts = new List<string>();
        JsonArray? inlineRun = null;
        void Flush()
        {
            if (inlineRun is { Count: > 0 })
                parts.Add(RenderInlines(inlineRun, false));
            inlineRun = null;
        }

        foreach (var b in blocks)
        {
            if (b is not JsonObject o)
                continue;
            if (IsInline(o))
            {
                (inlineRun ??= []).Add(o.DeepClone());
                continue;
            }
            Flush();
            parts.Add(RenderBlock(o));
        }
        Flush();
        return string.Join(separator, parts.Where(p => p.Length > 0));
    }

    static string RenderBlock(JsonObject node)
    {
        switch (TypeOf(node))
        {
            case "doc":
                return RenderBlocks(Content(node));
            case "paragraph":
                return RenderInlines(Content(node), false);
            case "heading":
            {
                var level = int.TryParse(Attr(node, "level"), out var l) ? Math.Clamp(l, 1, 6) : 1;
                // Jira editors leave empty headings and leading spaces behind; both render badly in Markdown.
                var text = RenderInlines(Content(node), false).Replace("\n", " ").Trim();
                return text.Length == 0 ? "" : new string('#', level) + " " + text;
            }
            case "bulletList":
            case "orderedList":
            case "taskList":
            case "decisionList":
                return RenderList(node);
            case "codeBlock":
            {
                var code = string.Concat(Content(node).Select(c => (string?)c?["text"] ?? ""));
                var fence = code.Contains("```") ? "~~~" : "```";
                return $"{fence}{Attr(node, "language")}\n{code}\n{fence}";
            }
            case "blockquote":
                return Quote(RenderBlocks(Content(node)));
            case "rule":
                return "---";
            case "panel":
            {
                var label = Attr(node, "panelType") switch
                {
                    "" => "Info",
                    var t => char.ToUpperInvariant(t[0]) + t[1..],
                };
                var body = RenderBlocks(Content(node));
                return Quote($"**{label}:** {body}");
            }
            case "expand":
            case "nestedExpand":
            {
                var title = Attr(node, "title");
                var body = RenderBlocks(Content(node));
                return $"**{(title.Length > 0 ? title : "Details")}**\n\n{body}";
            }
            case "table":
                return RenderTable(node);
            case "mediaSingle":
            case "mediaGroup":
                return string.Join("\n", Content(node).OfType<JsonObject>().Select(RenderMedia));
            case "media":
                return RenderMedia(node);
            case "blockCard":
            case "embedCard":
                return Attr(node, "url");
            case "extension":
                return $"[macro: {Attr(node, "extensionKey")}]";
            default:
            {
                // bodiedExtension, layoutSection/layoutColumn, multiBodiedExtension, unknown future nodes.
                var content = Content(node);
                if (content.Count == 0)
                    return node["text"]?.ToString() ?? "";
                return content.All(c => c is JsonObject co && IsInline(co))
                    ? RenderInlines(content, false)
                    : RenderBlocks(content);
            }
        }
    }

    static string RenderMedia(JsonObject media)
    {
        if (TypeOf(media) == "caption")
            return RenderInlines(Content(media), false);
        var name = Attr(media, "alt");
        if (name.Length == 0)
            name = Attr(media, "url");
        if (name.Length == 0)
            name = Attr(media, "id");
        return $"[attachment: {(name.Length > 0 ? name : "file")}]";
    }

    static string Quote(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line));

    static string RenderList(JsonObject list)
    {
        var type = TypeOf(list);
        var number = int.TryParse(Attr(list, "order"), out var start) ? start : 1;
        var lines = new List<string>();
        foreach (var item in Content(list).OfType<JsonObject>())
        {
            string marker;
            string body;
            switch (TypeOf(item))
            {
                case "taskItem":
                    marker = Attr(item, "state") == "DONE" ? "- [x] " : "- [ ] ";
                    body = RenderInlines(Content(item), false);
                    break;
                case "decisionItem":
                    marker = "- ";
                    body = "**Decision:** " + RenderInlines(Content(item), false);
                    break;
                case "taskList":
                case "decisionList":
                case "bulletList":
                case "orderedList":
                    // Nested list placed directly in a list (valid for taskList): indent under the previous item.
                    lines.Add(Indent(RenderList(item), "  "));
                    continue;
                default:
                    marker = type == "orderedList" ? $"{number++}. " : "- ";
                    body = RenderListItem(item);
                    break;
            }
            lines.Add(marker + Indent(body, new string(' ', marker.Length), skipFirst: true));
        }
        return string.Join("\n", lines);
    }

    static string RenderListItem(JsonObject item)
    {
        var sb = new StringBuilder();
        JsonObject? prev = null;
        foreach (var child in Content(item).OfType<JsonObject>())
        {
            var rendered = IsInline(child) ? RenderInlines(new JsonArray(child.DeepClone()), false) : RenderBlock(child);
            if (prev is not null)
                sb.Append(ListTypes.Contains(TypeOf(child)) ? "\n" : "\n\n");
            sb.Append(rendered);
            prev = child;
        }
        return sb.ToString();
    }

    static string Indent(string text, string pad, bool skipFirst = false)
    {
        var lines = text.Split('\n');
        for (var i = skipFirst ? 1 : 0; i < lines.Length; i++)
            if (lines[i].Length > 0)
                lines[i] = pad + lines[i];
        return string.Join("\n", lines);
    }

    static string RenderTable(JsonObject table)
    {
        var rows = Content(table).OfType<JsonObject>()
            .Select(r => Content(r).OfType<JsonObject>().Select(RenderCell).ToList())
            .ToList();
        if (rows.Count == 0)
            return "";
        var cols = rows.Max(r => r.Count);
        if (cols == 0)
            return "";
        foreach (var r in rows)
            while (r.Count < cols)
                r.Add("");

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rows[0])).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols)));
        foreach (var r in rows.Skip(1))
            sb.Append("\n| ").Append(string.Join(" | ", r)).Append(" |");
        return sb.ToString();
    }

    static string RenderCell(JsonObject cell)
    {
        var parts = Content(cell).OfType<JsonObject>()
            .Select(b => TypeOf(b) == "paragraph" ? RenderInlines(Content(b), true) : RenderBlock(b))
            .Where(p => p.Length > 0);
        return string.Join("<br>", parts).Replace("\n", "<br>").Replace("|", "\\|");
    }

    // ---------- inlines ----------

    static string RenderInlines(JsonArray inlines, bool inTable)
    {
        var sb = new StringBuilder();
        foreach (var n in MergeText(inlines))
        {
            switch (TypeOf(n))
            {
                case "text":
                    sb.Append(ApplyMarks((string?)n["text"] ?? "", n["marks"] as JsonArray));
                    break;
                case "hardBreak":
                    sb.Append(inTable ? "<br>" : "\n");
                    break;
                case "mention":
                {
                    var name = Attr(n, "text");
                    sb.Append('@').Append(name.Length > 0 ? name.TrimStart('@') : Attr(n, "id"));
                    break;
                }
                case "emoji":
                {
                    var text = Attr(n, "text");
                    sb.Append(text.Length > 0 ? text : Attr(n, "shortName"));
                    break;
                }
                case "inlineCard":
                    sb.Append(Attr(n, "url"));
                    break;
                case "date":
                    sb.Append(long.TryParse(Attr(n, "timestamp"), out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : Attr(n, "timestamp"));
                    break;
                case "status":
                    sb.Append('[').Append(Attr(n, "text")).Append(']');
                    break;
                case "mediaInline":
                    sb.Append(RenderMedia(n));
                    break;
                case "placeholder":
                    sb.Append(Attr(n, "text"));
                    break;
                case "inlineExtension":
                    sb.Append("[macro: ").Append(Attr(n, "extensionKey")).Append(']');
                    break;
                default:
                    if (Content(n).Count > 0)
                        sb.Append(RenderInlines(Content(n), inTable));
                    else if (n["text"] is JsonNode t)
                        sb.Append(t.ToString());
                    break;
            }
        }
        return sb.ToString();
    }

    static List<JsonObject> MergeText(JsonArray inlines)
    {
        var result = new List<JsonObject>();
        foreach (var n in inlines.OfType<JsonObject>())
        {
            if (TypeOf(n) == "text" && result.Count > 0 && TypeOf(result[^1]) == "text" &&
                (result[^1]["marks"]?.ToJsonString() ?? "") == (n["marks"]?.ToJsonString() ?? ""))
            {
                var merged = (JsonObject)result[^1].DeepClone();
                merged["text"] = (string?)merged["text"] + (string?)n["text"];
                result[^1] = merged;
                continue;
            }
            result.Add(n);
        }
        return result;
    }

    static string ApplyMarks(string text, JsonArray? marks)
    {
        if (text.Length == 0 || marks is null || marks.Count == 0)
            return text;

        var types = marks.Select(m => TypeOf(m)).ToHashSet();
        string? href = marks.FirstOrDefault(m => TypeOf(m) == "link")?["attrs"]?["href"]?.ToString();

        // Emphasis delimiters can't hug whitespace, so keep it outside the markers.
        var core = text.Trim();
        if (core.Length == 0)
            return text;
        var lead = text[..text.IndexOf(core, StringComparison.Ordinal)];
        var trail = text[(lead.Length + core.Length)..];

        if (types.Contains("code"))
        {
            var fence = core.Contains('`') ? "``" : "`";
            var pad = core.StartsWith('`') || core.EndsWith('`') ? " " : "";
            core = fence + pad + core + pad + fence;
        }
        if (types.Contains("strike"))
            core = "~~" + core + "~~";
        if (types.Contains("em"))
            core = "*" + core + "*";
        if (types.Contains("strong"))
            core = "**" + core + "**";
        if (href is not null)
            core = $"[{core}]({href.Replace(" ", "%20").Replace(")", "%29")})";
        return lead + core + trail;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();
}
