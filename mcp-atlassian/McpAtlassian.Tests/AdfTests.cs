using System.Text.Json;
using System.Text.Json.Nodes;
using McpAtlassian.Formatting;
using ModelContextProtocol;

namespace McpAtlassian.Tests;

public class MarkdownToAdfTests
{
    static JsonArray Content(JsonObject doc) => (JsonArray)doc["content"]!;

    static JsonObject First(JsonObject doc) => (JsonObject)Content(doc)[0]!;

    static string Type(JsonNode? n) => (string?)n?["type"] ?? "";

    [Fact]
    public void Doc_HasVersionAndType()
    {
        var doc = Adf.FromMarkdown("hello");
        Assert.Equal("doc", Type(doc));
        Assert.Equal(1, (int)doc["version"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void EmptyInput_ProducesSingleEmptyParagraph(string md)
    {
        var doc = Adf.FromMarkdown(md);
        var content = Content(doc);
        Assert.Single(content);
        Assert.Equal("paragraph", Type(content[0]));
        Assert.Null(content[0]!["content"]);
    }

    [Fact]
    public void Paragraph_PlainText()
    {
        var p = First(Adf.FromMarkdown("Hello world"));
        Assert.Equal("paragraph", Type(p));
        Assert.Equal("Hello world", (string?)p["content"]![0]!["text"]);
        Assert.Null(p["content"]![0]!["marks"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Headings(int level)
    {
        var h = First(Adf.FromMarkdown(new string('#', level) + " Title"));
        Assert.Equal("heading", Type(h));
        Assert.Equal(level, (int)h["attrs"]!["level"]!);
        Assert.Equal("Title", (string?)h["content"]![0]!["text"]);
    }

    [Theory]
    [InlineData("**b**", "strong")]
    [InlineData("__b__", "strong")]
    [InlineData("*b*", "em")]
    [InlineData("_b_", "em")]
    [InlineData("~~b~~", "strike")]
    [InlineData("`b`", "code")]
    public void SimpleMarks(string md, string mark)
    {
        var text = First(Adf.FromMarkdown(md))["content"]![0]!;
        Assert.Equal("b", (string?)text["text"]);
        Assert.Equal(mark, Type(text["marks"]![0]));
        Assert.Single((JsonArray)text["marks"]!);
    }

    [Fact]
    public void Link_HasHrefAttr()
    {
        var text = First(Adf.FromMarkdown("[site](https://example.com \"T\")"))["content"]![0]!;
        var mark = text["marks"]![0]!;
        Assert.Equal("link", Type(mark));
        Assert.Equal("https://example.com", (string?)mark["attrs"]!["href"]);
        Assert.Equal("T", (string?)mark["attrs"]!["title"]);
    }

    [Fact]
    public void Autolink_BareUrl()
    {
        var content = (JsonArray)First(Adf.FromMarkdown("see https://example.com/x now"))["content"]!;
        var linked = content.Single(n => n!["marks"] is not null)!;
        Assert.Equal("https://example.com/x", (string?)linked["marks"]![0]!["attrs"]!["href"]);
    }

    [Fact]
    public void CodeMark_DropsOtherMarksExceptLink()
    {
        var p = First(Adf.FromMarkdown("**`x`** and [`y`](https://a.b)"));
        var nodes = ((JsonArray)p["content"]!).ToList();
        var x = nodes.First(n => (string?)n!["text"] == "x")!;
        Assert.Equal(["code"], ((JsonArray)x["marks"]!).Select(m => Type(m)));
        var y = nodes.First(n => (string?)n!["text"] == "y")!;
        Assert.Equal(new[] { "code", "link" }.OrderBy(s => s), ((JsonArray)y["marks"]!).Select(m => Type(m)).OrderBy(s => s));
    }

    [Fact]
    public void NestedMarks_StrongEm()
    {
        var text = First(Adf.FromMarkdown("***both***"))["content"]![0]!;
        var marks = ((JsonArray)text["marks"]!).Select(m => Type(m)).OrderBy(s => s);
        Assert.Equal(["em", "strong"], marks);
    }

    [Fact]
    public void NoEmptyTextNodes_Anywhere()
    {
        var doc = Adf.FromMarkdown("a  \nb\n\n- \n- x\n\n| a | |\n|---|---|\n| | b |\n\n```\n```\n");
        AssertNoEmptyText(doc);
    }

    static void AssertNoEmptyText(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (Type(o) == "text")
                Assert.False(string.IsNullOrEmpty((string?)o["text"]));
            if (o["content"] is JsonArray a)
            {
                Assert.NotEmpty(a);
                foreach (var c in a)
                    AssertNoEmptyText(c);
            }
        }
    }

    [Fact]
    public void HardAndSoftBreaks_BecomeHardBreak()
    {
        var p = First(Adf.FromMarkdown("line1  \nline2\nline3"));
        var types = ((JsonArray)p["content"]!).Select(n => Type(n)).ToList();
        Assert.Equal(["text", "hardBreak", "text", "hardBreak", "text"], types);
    }

    [Fact]
    public void BulletList_ItemsStartWithParagraph()
    {
        var list = First(Adf.FromMarkdown("- a\n- b"));
        Assert.Equal("bulletList", Type(list));
        foreach (var item in (JsonArray)list["content"]!)
        {
            Assert.Equal("listItem", Type(item));
            Assert.Equal("paragraph", Type(item!["content"]![0]));
        }
    }

    [Fact]
    public void OrderedList_StartNumber()
    {
        var list = First(Adf.FromMarkdown("3. a\n4. b"));
        Assert.Equal("orderedList", Type(list));
        Assert.Equal(3, (int)list["attrs"]!["order"]!);

        var defaultList = First(Adf.FromMarkdown("1. a"));
        Assert.Null(defaultList["attrs"]);
    }

    [Fact]
    public void NestedLists()
    {
        var list = First(Adf.FromMarkdown("- a\n  - b\n    1. c\n- d"));
        var firstItem = list["content"]![0]!;
        Assert.Equal("paragraph", Type(firstItem["content"]![0]));
        var nested = firstItem["content"]![1]!;
        Assert.Equal("bulletList", Type(nested));
        var deeper = nested["content"]![0]!["content"]![1]!;
        Assert.Equal("orderedList", Type(deeper));
    }

    [Fact]
    public void ListItem_StartingWithCodeBlock_GetsLeadingParagraph()
    {
        var list = First(Adf.FromMarkdown("- ```\n  code\n  ```"));
        var item = list["content"]![0]!;
        Assert.Equal("paragraph", Type(item["content"]![0]));
        Assert.Equal("codeBlock", Type(item["content"]![1]));
    }

    [Fact]
    public void HeadingInsideList_BecomesStrongParagraph()
    {
        var list = First(Adf.FromMarkdown("- # Big"));
        var p = list["content"]![0]!["content"]![0]!;
        Assert.Equal("paragraph", Type(p));
        Assert.Equal("strong", Type(p["content"]![0]!["marks"]![0]));
    }

    [Fact]
    public void TaskList()
    {
        var tl = First(Adf.FromMarkdown("- [ ] todo\n- [x] done"));
        Assert.Equal("taskList", Type(tl));
        Assert.False(string.IsNullOrEmpty((string?)tl["attrs"]!["localId"]));
        var items = (JsonArray)tl["content"]!;
        Assert.Equal("taskItem", Type(items[0]));
        Assert.Equal("TODO", (string?)items[0]!["attrs"]!["state"]);
        Assert.Equal("DONE", (string?)items[1]!["attrs"]!["state"]);
        Assert.Equal("todo", (string?)items[0]!["content"]![0]!["text"]);
        Assert.NotEqual((string?)items[0]!["attrs"]!["localId"], (string?)items[1]!["attrs"]!["localId"]);
    }

    [Fact]
    public void NestedTaskList()
    {
        var tl = First(Adf.FromMarkdown("- [ ] parent\n  - [x] child"));
        var items = (JsonArray)tl["content"]!;
        Assert.Equal("taskItem", Type(items[0]));
        Assert.Equal("taskList", Type(items[1]));
        Assert.Equal("DONE", (string?)items[1]!["content"]![0]!["attrs"]!["state"]);
    }

    [Fact]
    public void TaskListInsideQuote_FallsBackToBulletWithCheckboxText()
    {
        var q = First(Adf.FromMarkdown("> - [x] done"));
        Assert.Equal("blockquote", Type(q));
        var list = q["content"]![0]!;
        Assert.Equal("bulletList", Type(list));
        Assert.Equal("[x] done", (string?)list["content"]![0]!["content"]![0]!["content"]![0]!["text"]);
    }

    [Fact]
    public void CodeBlock_LanguageAndText()
    {
        var cb = First(Adf.FromMarkdown("```csharp\nvar x = 1;\nvar y = 2;\n```"));
        Assert.Equal("codeBlock", Type(cb));
        Assert.Equal("csharp", (string?)cb["attrs"]!["language"]);
        Assert.Equal("var x = 1;\nvar y = 2;", (string?)cb["content"]![0]!["text"]);
        Assert.Null(cb["content"]![0]!["marks"]);
    }

    [Fact]
    public void IndentedCodeBlock()
    {
        var cb = First(Adf.FromMarkdown("    indented"));
        Assert.Equal("codeBlock", Type(cb));
        Assert.Null(cb["attrs"]);
    }

    [Fact]
    public void Blockquote_And_NestedQuoteFlattened()
    {
        var q = First(Adf.FromMarkdown("> outer\n>\n> > inner"));
        Assert.Equal("blockquote", Type(q));
        Assert.All((JsonArray)q["content"]!, n => Assert.Equal("paragraph", Type(n)));
        Assert.Equal(2, ((JsonArray)q["content"]!).Count);
    }

    [Fact]
    public void Rule()
    {
        var doc = Adf.FromMarkdown("a\n\n---\n\nb");
        Assert.Equal("rule", Type(Content(doc)[1]));
    }

    [Fact]
    public void Table_HeaderAndCellsWrappedInParagraphs()
    {
        var table = First(Adf.FromMarkdown("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 |"));
        Assert.Equal("table", Type(table));
        var rows = (JsonArray)table["content"]!;
        Assert.Equal(3, rows.Count);
        Assert.All((JsonArray)rows[0]!["content"]!, c => Assert.Equal("tableHeader", Type(c)));
        Assert.All((JsonArray)rows[1]!["content"]!, c => Assert.Equal("tableCell", Type(c)));
        // Short row padded to full width.
        Assert.Equal(2, ((JsonArray)rows[2]!["content"]!).Count);
        foreach (var row in rows)
            foreach (var cell in (JsonArray)row!["content"]!)
                Assert.Equal("paragraph", Type(cell!["content"]![0]));
    }

    [Fact]
    public void Table_BrTagBecomesHardBreak()
    {
        var table = First(Adf.FromMarkdown("| A |\n|---|\n| x<br>y |"));
        var para = table["content"]![1]!["content"]![0]!["content"]![0]!;
        Assert.Contains((JsonArray)para["content"]!, n => Type(n) == "hardBreak");
    }

    [Fact]
    public void Image_BecomesLinkText()
    {
        var p = First(Adf.FromMarkdown("![diagram](https://x.y/a.png)"));
        var text = p["content"]![0]!;
        Assert.Equal("diagram", (string?)text["text"]);
        Assert.Equal("https://x.y/a.png", (string?)text["marks"]![0]!["attrs"]!["href"]);
    }

    [Fact]
    public void RawHtml_BecomesText()
    {
        var doc = Adf.FromMarkdown("<div>hi</div>\n\ninline <span>x</span>");
        Assert.Equal("paragraph", Type(Content(doc)[0]));
        Assert.Contains("<div>hi</div>", Adf.ToMarkdown(doc));
        Assert.Contains("<span>", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void AdjacentTextMerged()
    {
        var p = First(Adf.FromMarkdown("a &amp; b"));
        Assert.Single((JsonArray)p["content"]!);
        Assert.Equal("a & b", (string?)p["content"]![0]!["text"]);
    }
}

public class AdfToMarkdownTests
{
    static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Null_ReturnsEmpty() => Assert.Equal("", Adf.ToMarkdown(null));

    [Fact]
    public void StringNode_ReturnedVerbatim() => Assert.Equal("plain", Adf.ToMarkdown(JsonValue.Create("plain")));

    [Fact]
    public void Mention_Emoji_Date_Status_Card()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"paragraph","content":[
          {"type":"mention","attrs":{"id":"abc","text":"@Jane Doe"}},
          {"type":"text","text":" "},
          {"type":"mention","attrs":{"id":"def","text":"Bob"}},
          {"type":"text","text":" "},
          {"type":"emoji","attrs":{"shortName":":smile:","text":"😄"}},
          {"type":"emoji","attrs":{"shortName":":custom:"}},
          {"type":"text","text":" due "},
          {"type":"date","attrs":{"timestamp":"1700000000000"}},
          {"type":"text","text":" "},
          {"type":"status","attrs":{"text":"IN PROGRESS","color":"blue"}},
          {"type":"text","text":" "},
          {"type":"inlineCard","attrs":{"url":"https://x.atlassian.net/browse/ABC-1"}}
        ]}]}
        """);
        Assert.Equal("@Jane Doe @Bob 😄:custom: due 2023-11-14 [IN PROGRESS] https://x.atlassian.net/browse/ABC-1", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void Panel_RendersAsLabelledQuote()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"panel","attrs":{"panelType":"warning"},"content":[
          {"type":"paragraph","content":[{"type":"text","text":"Careful"}]},
          {"type":"paragraph","content":[{"type":"text","text":"Second"}]}
        ]}]}
        """);
        Assert.Equal("> **Warning:** Careful\n>\n> Second", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void Expand_TitleAndBody()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"expand","attrs":{"title":"More"},"content":[
          {"type":"paragraph","content":[{"type":"text","text":"hidden"}]}]}]}
        """);
        Assert.Equal("**More**\n\nhidden", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void Media_RendersAttachmentPlaceholder()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[
          {"type":"mediaSingle","content":[{"type":"media","attrs":{"id":"m1","type":"file","alt":"screenshot.png"}}]},
          {"type":"mediaGroup","content":[{"type":"media","attrs":{"id":"m2","type":"file"}}]}
        ]}
        """);
        Assert.Equal("[attachment: screenshot.png]\n\n[attachment: m2]", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void DecisionList()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"decisionList","attrs":{"localId":"a"},"content":[
          {"type":"decisionItem","attrs":{"localId":"b","state":"DECIDED"},"content":[{"type":"text","text":"Ship it"}]}]}]}
        """);
        Assert.Equal("- **Decision:** Ship it", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void Table_WithHeaders_PipesEscaped_BreaksAsBr()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"table","content":[
          {"type":"tableRow","content":[
            {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Name","marks":[{"type":"strong"}]}]}]},
            {"type":"tableHeader","content":[{"type":"paragraph","content":[{"type":"text","text":"Value"}]}]}]},
          {"type":"tableRow","content":[
            {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"a|b"}]}]},
            {"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"x"},{"type":"hardBreak"},{"type":"text","text":"y"}]}]}]}
        ]}]}
        """);
        Assert.Equal("| **Name** | Value |\n| --- | --- |\n| a\\|b | x<br>y |", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void DroppedMarks_UnderlineColorSubsup()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"paragraph","content":[
          {"type":"text","text":"u","marks":[{"type":"underline"}]},
          {"type":"text","text":"c","marks":[{"type":"textColor","attrs":{"color":"#ff0000"}}]},
          {"type":"text","text":"s","marks":[{"type":"subsup","attrs":{"type":"sub"}}]}]}]}
        """);
        Assert.Equal("ucs", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void MarksKeepWhitespaceOutsideDelimiters()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"paragraph","content":[
          {"type":"text","text":"a"},{"type":"text","text":" bold ","marks":[{"type":"strong"}]},{"type":"text","text":"c"}]}]}
        """);
        Assert.Equal("a **bold** c", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void AdjacentSameMarkTextMergedBeforeRender()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"paragraph","content":[
          {"type":"text","text":"ab","marks":[{"type":"strong"}]},{"type":"text","text":"cd","marks":[{"type":"strong"}]}]}]}
        """);
        Assert.Equal("**abcd**", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void UnknownNodes_RecurseIntoContent()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"layoutSection","content":[
          {"type":"layoutColumn","attrs":{"width":50},"content":[{"type":"paragraph","content":[{"type":"text","text":"left"}]}]},
          {"type":"layoutColumn","attrs":{"width":50},"content":[{"type":"paragraph","content":[{"type":"text","text":"right"}]}]}]},
          {"type":"someFutureNode","content":[{"type":"text","text":"future"}]},
          {"type":"extension","attrs":{"extensionKey":"toc","extensionType":"com.atlassian.confluence.macro.core"}}
        ]}
        """);
        Assert.Equal("left\n\nright\n\nfuture\n\n[macro: toc]", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void CodeBlockContainingFence_UsesTildes()
    {
        var doc = Parse("""
        {"type":"doc","version":1,"content":[{"type":"codeBlock","attrs":{"language":"md"},"content":[{"type":"text","text":"```x```"}]}]}
        """);
        Assert.Equal("~~~md\n```x```\n~~~", Adf.ToMarkdown(doc));
    }

    [Fact]
    public void BareNode_NotWrappedInDoc()
    {
        Assert.Equal("**hi**", Adf.ToMarkdown(Parse("""{"type":"text","text":"hi","marks":[{"type":"strong"}]}""")));
        Assert.Equal("# T", Adf.ToMarkdown(Parse("""{"type":"heading","attrs":{"level":1},"content":[{"type":"text","text":"T"}]}""")));
    }
}

public class RoundTripTests
{
    static string RoundTrip(string md) => Adf.ToMarkdown(Adf.FromMarkdown(md));

    [Theory]
    [InlineData("Hello world")]
    [InlineData("# H1")]
    [InlineData("###### H6")]
    [InlineData("**bold** *italic* ~~gone~~ `code`")]
    [InlineData("***both***")]
    [InlineData("[link](https://example.com)")]
    [InlineData("[**bold link**](https://example.com)")]
    [InlineData("line one\nline two")]
    [InlineData("- a\n- b\n- c")]
    [InlineData("1. a\n2. b")]
    [InlineData("5. five\n6. six")]
    [InlineData("- a\n  - nested\n    - deeper\n- b")]
    [InlineData("1. first\n   - sub\n2. second")]
    [InlineData("- [ ] todo\n- [x] done")]
    [InlineData("- [ ] parent\n  - [x] child")]
    [InlineData("```python\nprint('hi')\n\nx = 1\n```")]
    [InlineData("```\nno lang\n```")]
    [InlineData("> quoted\n>\n> second para")]
    [InlineData("> - item in quote")]
    [InlineData("---")]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |")]
    [InlineData("| A |\n| --- |\n| x<br>y |")]
    [InlineData("# Title\n\nIntro paragraph.\n\n- one\n- two\n\n```js\nlet a;\n```\n\n> note\n\n---\n\n| k | v |\n| --- | --- |\n| a | b |")]
    public void MarkdownSurvivesRoundTrip(string md) => Assert.Equal(md, RoundTrip(md));

    [Fact]
    public void HardBreakSyntax_NormalizesToNewline() => Assert.Equal("a\nb", RoundTrip("a  \nb"));

    [Fact]
    public void ListItemWithMultipleParagraphs()
    {
        var md = "- para one\n\n  para two\n- next";
        Assert.Equal(md, RoundTrip(md));
    }

    [Fact]
    public void ListItemWithCodeBlock()
    {
        var md = "- step\n\n  ```sh\n  ls\n  ```";
        Assert.Equal(md, RoundTrip(md));
    }

    [Fact]
    public void StarBullets_NormalizeToDash() => Assert.Equal("- a\n- b", RoundTrip("* a\n* b"));

    [Fact]
    public void AdfRoundTrip_IsStable()
    {
        var md = "# T\n\n- [x] done\n\n| a | b |\n| --- | --- |\n| `c` | **d** |";
        var once = RoundTrip(md);
        Assert.Equal(once, RoundTrip(once));
    }
}

public class InputOutputTests
{
    static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void FromInput_Null_ReturnsNull()
    {
        Assert.Null(Adf.FromInput((string?)null, "markdown"));
        Assert.Null(Adf.FromInput((JsonElement?)null, "adf"));
        Assert.Null(Adf.FromInput(El("null"), "markdown"));
    }

    [Fact]
    public void FromInput_MarkdownString()
    {
        var doc = Adf.FromInput("**x**", null)!;
        Assert.Equal("strong", (string?)doc["content"]![0]!["content"]![0]!["marks"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_MarkdownElementString()
    {
        var doc = Adf.FromInput(El("\"# h\""), "markdown")!;
        Assert.Equal("heading", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_AdfObject()
    {
        var doc = Adf.FromInput(El("""{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"x"}]}]}"""), "ADF")!;
        Assert.Equal("x", (string?)doc["content"]![0]!["content"]![0]!["text"]);
    }

    [Fact]
    public void FromInput_AdfString()
    {
        var doc = Adf.FromInput("""{"type":"doc","content":[{"type":"rule"}]}""", "adf")!;
        Assert.Equal(1, (int)doc["version"]!);
        Assert.Equal("rule", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_AdfObjectWhileMarkdownFormat_AcceptedAsAdf()
    {
        var doc = Adf.FromInput(El("""{"type":"doc","version":1,"content":[{"type":"rule"}]}"""), "markdown")!;
        Assert.Equal("rule", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_AdfJsonStringWhileMarkdownFormat_AcceptedAsAdf()
    {
        var doc = Adf.FromInput("""{"type":"doc","version":1,"content":[{"type":"rule"}]}""", null)!;
        Assert.Equal("rule", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_BareContentArray_Wrapped()
    {
        var doc = Adf.FromInput(El("""[{"type":"paragraph","content":[{"type":"text","text":"x"}]}]"""), "adf")!;
        Assert.Equal("doc", (string?)doc["type"]);
        Assert.Equal("paragraph", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_NonDocNode_Wrapped()
    {
        var doc = Adf.FromInput("""{"type":"paragraph","content":[{"type":"text","text":"x"}]}""", "adf")!;
        Assert.Equal("doc", (string?)doc["type"]);
        Assert.Equal("paragraph", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void FromInput_InlineNodes_WrappedInParagraph()
    {
        var doc = Adf.FromInput("""[{"type":"text","text":"a"},{"type":"hardBreak"},{"type":"text","text":"b"}]""", "adf")!;
        Assert.Single((JsonArray)doc["content"]!);
        Assert.Equal("paragraph", (string?)doc["content"]![0]!["type"]);
        Assert.Equal(3, ((JsonArray)doc["content"]![0]!["content"]!).Count);
    }

    [Fact]
    public void FromInput_EmptyDoc_GetsParagraph()
    {
        var doc = Adf.FromInput("""{"type":"doc","version":1,"content":[]}""", "adf")!;
        Assert.Single((JsonArray)doc["content"]!);
    }

    [Fact]
    public void FromInput_InvalidAdfJson_ThrowsMcpException() =>
        Assert.Throws<McpException>(() => Adf.FromInput("not json", "adf"));

    [Fact]
    public void FromInput_MarkdownThatLooksLikeBraces_StaysMarkdown()
    {
        var doc = Adf.FromInput("{ not json \"type\" }", "markdown")!;
        Assert.Equal("paragraph", (string?)doc["content"]![0]!["type"]);
    }

    [Fact]
    public void ForOutput_DefaultIsMarkdownString()
    {
        var adf = Adf.FromMarkdown("**x**");
        var outNode = Adf.ForOutput(adf, null);
        Assert.Equal("**x**", outNode!.GetValue<string>());
        Assert.Equal("**x**", Adf.ForOutput(adf, "markdown")!.GetValue<string>());
    }

    [Fact]
    public void ForOutput_AdfReturnsClone()
    {
        var adf = Adf.FromMarkdown("x");
        var outNode = Adf.ForOutput(adf, "adf");
        Assert.IsType<JsonObject>(outNode);
        Assert.NotSame(adf, outNode);
        Assert.Equal(adf.ToJsonString(), outNode!.ToJsonString());
        // Clone can be attached to another parent without throwing.
        _ = new JsonObject { ["body"] = outNode };
    }

    [Fact]
    public void ForOutput_Null() => Assert.Null(Adf.ForOutput(null, "adf"));

    [Fact]
    public void ToMarkdown_SkipsEmptyHeadingsAndTrimsHeadingText()
    {
        var adf = JsonNode.Parse("""
            {"type":"doc","version":1,"content":[
              {"type":"heading","attrs":{"level":2}},
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"    Goal"}]},
              {"type":"paragraph","content":[{"type":"text","text":"body"}]}]}
            """);
        Assert.Equal("## Goal\n\nbody", Adf.ToMarkdown(adf));
    }
}
