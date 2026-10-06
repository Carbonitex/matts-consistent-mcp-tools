using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace McpAtlassian.Formatting;

/// <summary>Markdig AST -> ADF. Respects ADF nesting rules by flattening constructs a container can't hold.</summary>
internal static partial class MarkdownToAdf
{
    enum Ctx { Top, ListItem, Quote, Cell }

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static JsonObject Convert(string markdown)
    {
        var doc = Markdown.Parse(markdown.Replace("\r\n", "\n"), Pipeline);
        var content = new JsonArray();
        foreach (var block in doc)
            foreach (var node in ConvertBlock(block, Ctx.Top))
                content.Add(node);
        if (content.Count == 0)
            content.Add(Paragraph(new JsonArray()));
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    static IEnumerable<JsonObject> ConvertBlocks(IEnumerable<Block> blocks, Ctx ctx) =>
        blocks.SelectMany(b => ConvertBlock(b, ctx));

    static IEnumerable<JsonObject> ConvertBlock(Block block, Ctx ctx)
    {
        switch (block)
        {
            case LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock:
                yield break;

            case HeadingBlock h:
                if (ctx is Ctx.Top or Ctx.Cell)
                    yield return new JsonObject
                    {
                        ["type"] = "heading",
                        ["attrs"] = new JsonObject { ["level"] = Math.Clamp(h.Level, 1, 6) },
                        ["content"] = Inlines(h.Inline),
                    }.WithoutEmptyContent();
                else
                    yield return Paragraph(Inlines(h.Inline, [Mark("strong")]));
                break;

            case ParagraphBlock p:
                yield return Paragraph(Inlines(p.Inline));
                break;

            case CodeBlock code:
            {
                var text = code.Lines.ToString().TrimEnd('\n');
                var node = new JsonObject { ["type"] = "codeBlock" };
                if (code is FencedCodeBlock { Info: { Length: > 0 } info })
                    node["attrs"] = new JsonObject { ["language"] = info.Trim() };
                if (text.Length > 0)
                    node["content"] = new JsonArray(Text(text, []));
                yield return node;
                break;
            }

            case ThematicBreakBlock:
                if (ctx is Ctx.Top or Ctx.Cell)
                    yield return new JsonObject { ["type"] = "rule" };
                break;

            case QuoteBlock q:
                if (ctx is Ctx.Top or Ctx.Cell)
                {
                    var inner = new JsonArray();
                    foreach (var n in ConvertBlocks(q, Ctx.Quote))
                        inner.Add(n);
                    if (inner.Count == 0)
                        inner.Add(Paragraph(new JsonArray()));
                    yield return new JsonObject { ["type"] = "blockquote", ["content"] = inner };
                }
                else
                {
                    foreach (var n in ConvertBlocks(q, ctx))
                        yield return n;
                }
                break;

            case ListBlock list:
                yield return ConvertList(list, ctx);
                break;

            case Table table:
                if (ctx == Ctx.Top)
                    yield return ConvertTable(table);
                else
                    foreach (var row in table.OfType<TableRow>())
                        yield return Paragraph(JoinCells(row));
                break;

            case LeafBlock leaf:
                if (leaf.Inline is not null)
                    yield return Paragraph(Inlines(leaf.Inline));
                else if (leaf.Lines.Count > 0)
                    yield return Paragraph(TextNodes(leaf.Lines.ToString()));
                break;

            case ContainerBlock container:
                foreach (var n in ConvertBlocks(container, ctx))
                    yield return n;
                break;
        }
    }

    static JsonObject ConvertList(ListBlock list, Ctx ctx)
    {
        var items = list.OfType<ListItemBlock>().ToList();
        var allTasks = items.Count > 0 && items.All(IsTaskItem);
        if (allTasks && ctx is Ctx.Top or Ctx.Cell)
            return ConvertTaskList(items);

        var node = new JsonObject { ["type"] = list.IsOrdered ? "orderedList" : "bulletList" };
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start != 1)
            node["attrs"] = new JsonObject { ["order"] = Math.Max(0, start) };

        var content = new JsonArray();
        foreach (var item in items)
        {
            var children = new List<JsonObject>();
            foreach (var child in item)
            {
                if (children.Count == 0 && child is ParagraphBlock p && IsTaskItem(item))
                {
                    // Task list nested where ADF can't hold one: keep the checkbox as text.
                    var inl = Inlines(p.Inline);
                    if (inl.Count > 0 && inl[0] is JsonObject t0 && (string?)t0["type"] == "text")
                    {
                        var s = ((string?)t0["text"] ?? "").TrimStart();
                        if (s.Length == 0)
                            inl.RemoveAt(0);
                        else
                            t0["text"] = s;
                    }
                    var check = TaskOf(item)!.Checked ? "[x] " : "[ ] ";
                    inl.Insert(0, Text(check, []));
                    children.Add(Paragraph(MergeText(inl)));
                    continue;
                }
                children.AddRange(ConvertBlock(child, Ctx.ListItem));
            }
            if (children.Count == 0 || (string?)children[0]["type"] != "paragraph")
                children.Insert(0, Paragraph(new JsonArray()));
            var arr = new JsonArray();
            foreach (var c in children)
                arr.Add(c);
            content.Add(new JsonObject { ["type"] = "listItem", ["content"] = arr });
        }
        node["content"] = content;
        return node;
    }

    static JsonObject ConvertTaskList(List<ListItemBlock> items)
    {
        var content = new JsonArray();
        foreach (var item in items)
        {
            var inline = new JsonArray();
            var nested = new List<JsonObject>();
            var first = true;
            foreach (var child in item)
            {
                if (first && child is ParagraphBlock p)
                {
                    foreach (var n in Inlines(p.Inline))
                        inline.Add(n!.DeepClone());
                }
                else if (child is ListBlock sub && sub.OfType<ListItemBlock>().All(IsTaskItem) && sub.Count > 0)
                {
                    nested.Add(ConvertTaskList(sub.OfType<ListItemBlock>().ToList()));
                }
                else
                {
                    // taskItem is inline-only; fold other content in as text.
                    foreach (var block in ConvertBlock(child, Ctx.ListItem))
                    {
                        var text = PlainText(block);
                        if (text.Length == 0)
                            continue;
                        if (inline.Count > 0)
                            inline.Add(new JsonObject { ["type"] = "hardBreak" });
                        inline.Add(Text(text, []));
                    }
                }
                first = false;
            }
            // Trim the space Markdig leaves after "[ ]".
            if (inline.Count > 0 && inline[0] is JsonObject t && (string?)t["type"] == "text")
            {
                var s = ((string?)t["text"] ?? "").TrimStart();
                if (s.Length == 0)
                    inline.RemoveAt(0);
                else
                    t["text"] = s;
            }

            var taskItem = new JsonObject
            {
                ["type"] = "taskItem",
                ["attrs"] = new JsonObject
                {
                    ["localId"] = Guid.NewGuid().ToString(),
                    ["state"] = TaskOf(item)!.Checked ? "DONE" : "TODO",
                },
            };
            if (inline.Count > 0)
                taskItem["content"] = MergeText(inline);
            content.Add(taskItem);
            foreach (var n in nested)
                content.Add(n);
        }
        return new JsonObject
        {
            ["type"] = "taskList",
            ["attrs"] = new JsonObject { ["localId"] = Guid.NewGuid().ToString() },
            ["content"] = content,
        };
    }

    static bool IsTaskItem(ListItemBlock item) => TaskOf(item) is not null;

    static TaskList? TaskOf(ListItemBlock item) =>
        item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList t } ? t : null;

    static JsonObject ConvertTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var content = new JsonArray();
        foreach (var row in rows)
        {
            var cells = new JsonArray();
            var cellType = row.IsHeader ? "tableHeader" : "tableCell";
            for (var i = 0; i < columns; i++)
            {
                var cellContent = new JsonArray();
                if (i < row.Count && row[i] is TableCell cell)
                    foreach (var n in ConvertBlocks(cell, Ctx.Cell))
                        cellContent.Add(n);
                if (cellContent.Count == 0)
                    cellContent.Add(Paragraph(new JsonArray()));
                cells.Add(new JsonObject { ["type"] = cellType, ["attrs"] = new JsonObject(), ["content"] = cellContent });
            }
            content.Add(new JsonObject { ["type"] = "tableRow", ["content"] = cells });
        }
        return new JsonObject
        {
            ["type"] = "table",
            ["attrs"] = new JsonObject { ["isNumberColumnEnabled"] = false, ["layout"] = "default" },
            ["content"] = content,
        };
    }

    static JsonArray JoinCells(TableRow row)
    {
        var result = new JsonArray();
        var first = true;
        foreach (var cell in row.OfType<TableCell>())
        {
            if (!first)
                result.Add(Text(" | ", []));
            first = false;
            foreach (var block in cell.OfType<ParagraphBlock>())
                foreach (var n in Inlines(block.Inline))
                    result.Add(n!.DeepClone());
        }
        return MergeText(result);
    }

    // ---------- inlines ----------

    static JsonArray Inlines(ContainerInline? container, List<JsonObject>? marks = null)
    {
        var output = new JsonArray();
        if (container is not null)
            AppendInlines(container, marks ?? [], output);
        return MergeText(output);
    }

    static void AppendInlines(ContainerInline container, List<JsonObject> marks, JsonArray output)
    {
        foreach (var inline in container)
            AppendInline(inline, marks, output);
    }

    static void AppendInline(Inline inline, List<JsonObject> marks, JsonArray output)
    {
        switch (inline)
        {
            case TaskList:
                break;
            case LiteralInline lit:
                AddText(output, lit.Content.ToString(), marks);
                break;
            case CodeInline code:
                AddText(output, code.Content, [.. marks, Mark("code")]);
                break;
            case EmphasisInline em:
            {
                var mark = em.DelimiterChar switch
                {
                    '~' => "strike",
                    _ => em.DelimiterCount >= 2 ? "strong" : "em",
                };
                AppendInlines(em, With(marks, Mark(mark)), output);
                break;
            }
            case LinkInline link:
            {
                var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                var linkMark = Mark("link", new JsonObject { ["href"] = url });
                if (!string.IsNullOrEmpty(link.Title))
                    linkMark["attrs"]!["title"] = link.Title;
                var withLink = With(marks.Where(m => (string?)m["type"] != "link").ToList(), linkMark);
                var before = output.Count;
                AppendInlines(link, withLink, output);
                if (output.Count == before)
                    AddText(output, link.IsImage ? (url.Length > 0 ? url : "image") : url, withLink);
                break;
            }
            case AutolinkInline auto:
            {
                var href = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                AddText(output, auto.Url, With(marks, Mark("link", new JsonObject { ["href"] = href })));
                break;
            }
            case LineBreakInline:
                output.Add(new JsonObject { ["type"] = "hardBreak" });
                break;
            case HtmlEntityInline entity:
                AddText(output, entity.Transcoded.ToString(), marks);
                break;
            case HtmlInline html:
                if (BrTag().IsMatch(html.Tag))
                    output.Add(new JsonObject { ["type"] = "hardBreak" });
                else
                    AddText(output, html.Tag, marks);
                break;
            case ContainerInline c:
                AppendInlines(c, marks, output);
                break;
            default:
                AddText(output, inline.ToString() ?? "", marks);
                break;
        }
    }

    static List<JsonObject> With(List<JsonObject> marks, JsonObject mark)
    {
        var type = (string?)mark["type"];
        if (marks.Any(m => (string?)m["type"] == type))
            return marks;
        return [.. marks, mark];
    }

    static void AddText(JsonArray output, string text, List<JsonObject> marks)
    {
        if (string.IsNullOrEmpty(text))
            return;
        output.Add(Text(text, marks));
    }

    static JsonObject Text(string text, List<JsonObject> marks)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        IEnumerable<JsonObject> effective = marks;
        if (marks.Any(m => (string?)m["type"] == "code"))
            effective = marks.Where(m => (string?)m["type"] is "code" or "link");
        var arr = new JsonArray();
        foreach (var m in effective)
            arr.Add(m.DeepClone());
        if (arr.Count > 0)
            node["marks"] = arr;
        return node;
    }

    static JsonArray TextNodes(string text)
    {
        var arr = new JsonArray();
        var lines = text.TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                arr.Add(new JsonObject { ["type"] = "hardBreak" });
            if (lines[i].Length > 0)
                arr.Add(Text(lines[i], []));
        }
        return arr;
    }

    static JsonObject Mark(string type, JsonObject? attrs = null)
    {
        var m = new JsonObject { ["type"] = type };
        if (attrs is not null)
            m["attrs"] = attrs;
        return m;
    }

    static JsonObject Paragraph(JsonArray content)
    {
        var p = new JsonObject { ["type"] = "paragraph" };
        if (content.Count > 0)
            p["content"] = content;
        return p;
    }

    static JsonObject WithoutEmptyContent(this JsonObject node)
    {
        if (node["content"] is JsonArray { Count: 0 })
            node.Remove("content");
        return node;
    }

    /// <summary>Merges adjacent text nodes carrying identical marks.</summary>
    internal static JsonArray MergeText(JsonArray nodes)
    {
        var result = new JsonArray();
        JsonObject? last = null;
        foreach (var n in nodes.ToList())
        {
            nodes.Remove(n);
            if (n is not JsonObject o)
                continue;
            if ((string?)o["type"] == "text" && last is not null && (string?)last["type"] == "text" &&
                (last["marks"]?.ToJsonString() ?? "") == (o["marks"]?.ToJsonString() ?? ""))
            {
                last["text"] = (string?)last["text"] + (string?)o["text"];
                continue;
            }
            result.Add(o);
            last = o;
        }
        return result;
    }

    static string PlainText(JsonNode? node) => node switch
    {
        JsonObject o when (string?)o["type"] == "text" => (string?)o["text"] ?? "",
        JsonObject o when o["content"] is JsonArray a => string.Concat(a.Select(PlainText)),
        _ => "",
    };

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex BrTag();
}
