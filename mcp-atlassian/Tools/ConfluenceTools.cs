using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpAtlassian.Formatting;
using McpAtlassian.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpAtlassian.Tools;

[McpServerToolType]
public static partial class ConfluenceTools
{
    const string CloudIdDoc = "Ignored; the site comes from ATLASSIAN_SITE. Accepted for compatibility with the old Atlassian MCP.";
    const string ReadFormatDoc = "Body format to return: \"markdown\" (default), \"adf\" (Atlassian Document Format JSON) or \"html\" (raw Confluence storage-format XHTML).";
    const string WriteFormatDoc = "Format of body: \"markdown\" (default; GFM tables, task lists, code blocks supported), \"adf\" (ADF JSON object or string) or \"html\" (Confluence storage-format XHTML, sent verbatim; use for macros).";
    const string V2 = "/wiki/api/v2";

    // ---------------------------------------------------------------- pages

    [McpServerTool(Name = "getConfluencePage", ReadOnly = true)]
    [Description("Get a Confluence page or blog post by ID, including its body. Accepts a numeric ID, a tiny-link ID (the part after /wiki/x/), or a full page URL.")]
    public static async Task<string> GetConfluencePage(
        AtlassianClient client,
        [Description("Page or blog post ID, tiny-link ID (e.g. Fc1bBw), or page URL")] string pageId,
        [Description(ReadFormatDoc)] string? contentFormat = null,
        [Description("\"page\" (default) or \"blog\"")] string? contentType = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var id = ResolveContentId(pageId);
        var node = await client.ConfluenceGetAsync($"{V2}/{Collection(contentType)}/{id}?body-format={BodyFormat(fmt)}", ct);
        var shaped = ShapeContent(client, node);
        shaped["body"] = RenderBody(node?["body"], fmt);
        return Json.Out(shaped);
    }

    [McpServerTool(Name = "getConfluencePageByTitle", ReadOnly = true)]
    [Description("Find pages in a space by exact title. Returns matches with bodies.")]
    public static async Task<string> GetConfluencePageByTitle(
        AtlassianClient client,
        [Description("Numeric space ID or space key (e.g. ENG)")] string spaceId,
        [Description("Exact page title")] string title,
        [Description(ReadFormatDoc)] string? contentFormat = null,
        [Description("Include page bodies (default true)")] bool includeBody = true,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var sid = await ResolveSpaceIdAsync(client, spaceId, ct);
        var qs = Query(("space-id", sid), ("title", title), ("body-format", includeBody ? BodyFormat(fmt) : null));
        var node = await client.ConfluenceGetAsync($"{V2}/pages{qs}", ct);
        var results = new JsonArray();
        foreach (var p in Results(node))
        {
            var shaped = ShapeContent(client, p);
            if (includeBody)
                shaped["body"] = RenderBody(p?["body"], fmt);
            results.Add(shaped);
        }
        return Json.Out(new JsonObject { ["results"] = results });
    }

    [McpServerTool(Name = "getConfluenceSpaces", ReadOnly = true)]
    [Description("List Confluence spaces, optionally filtered by key, ID, type, status or label.")]
    public static async Task<string> GetConfluenceSpaces(
        AtlassianClient client,
        [Description("Space keys (array or comma-separated string)")] JsonElement? keys = null,
        [Description("Numeric space IDs (array or comma-separated string)")] JsonElement? ids = null,
        [Description("global, personal, collaboration or knowledge_base")] string? type = null,
        [Description("current or archived")] string? status = null,
        [Description("Space labels (array or comma-separated string)")] JsonElement? labels = null,
        [Description("Sort: id, -id, key, -key, name, -name")] string? sort = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var qs = Query(("keys", ListParam(keys)), ("ids", ListParam(ids)), ("type", type), ("status", status),
            ("labels", ListParam(labels)), ("sort", sort), ("limit", Limit(limit)), ("cursor", cursor));
        var node = await client.ConfluenceGetAsync($"{V2}/spaces{qs}", ct);
        var spaces = new JsonArray();
        foreach (var s in Results(node))
        {
            spaces.Add(new JsonObject
            {
                ["id"] = C(s?["id"]),
                ["key"] = C(s?["key"]),
                ["name"] = C(s?["name"]),
                ["type"] = C(s?["type"]),
                ["status"] = C(s?["status"]),
                ["homepageId"] = C(s?["homepageId"]),
                ["url"] = WebUrl(client, s?["_links"]?["webui"]),
            });
        }
        return Json.Out(Page("spaces", spaces, node));
    }

    [McpServerTool(Name = "getPagesInConfluenceSpace", ReadOnly = true)]
    [Description("List pages (or blog posts) in a space. Bodies are not included; use getConfluencePage for content.")]
    public static async Task<string> GetPagesInConfluenceSpace(
        AtlassianClient client,
        [Description("Numeric space ID or space key (e.g. ENG)")] string spaceId,
        [Description("Filter by exact title")] string? title = null,
        [Description("current, archived, deleted or trashed (default current)")] string? status = null,
        [Description("Sort: id, -id, created-date, -created-date, modified-date, -modified-date, title, -title")] string? sort = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description("Only return pages of this subtype (e.g. live)")] string? subtype = null,
        [Description("\"page\" (default) or \"blog\"")] string? contentType = null,
        [Description("\"all\" (default) or \"root\" for top-level pages only (pages only)")] string? depth = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var sid = await ResolveSpaceIdAsync(client, spaceId, ct);
        var collection = Collection(contentType);
        var qs = Query(("title", title), ("status", status), ("sort", sort), ("limit", Limit(limit)), ("cursor", cursor),
            ("depth", collection == "pages" ? depth : null));
        var node = await client.ConfluenceGetAsync($"{V2}/spaces/{sid}/{collection}{qs}", ct);
        var pages = new JsonArray();
        foreach (var p in Results(node))
        {
            if (subtype is not null && !string.Equals(p?["subtype"]?.ToString(), subtype, StringComparison.OrdinalIgnoreCase))
                continue;
            pages.Add(ShapeContent(client, p));
        }
        return Json.Out(Page("pages", pages, node));
    }

    [McpServerTool(Name = "getConfluencePageDescendants", ReadOnly = true)]
    [Description("List all descendants of a page (children, grandchildren, ...), including folders, whiteboards and databases.")]
    public static async Task<string> GetConfluencePageDescendants(
        AtlassianClient client,
        [Description("Page ID")] string pageId,
        [Description("Max depth to traverse (default 5)")] int? depth = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var id = ResolveContentId(pageId);
        var qs = Query(("depth", depth?.ToString()), ("limit", Limit(limit)), ("cursor", cursor));
        var node = await client.ConfluenceGetAsync($"{V2}/pages/{id}/descendants{qs}", ct);
        var items = new JsonArray();
        foreach (var d in Results(node))
        {
            items.Add(new JsonObject
            {
                ["id"] = C(d?["id"]),
                ["title"] = C(d?["title"]),
                ["type"] = C(d?["type"]),
                ["status"] = C(d?["status"]),
                ["parentId"] = C(d?["parentId"]),
                ["depth"] = C(d?["depth"]),
                ["childPosition"] = C(d?["childPosition"]),
            });
        }
        return Json.Out(Page("descendants", items, node));
    }

    [McpServerTool(Name = "getConfluencePageChildren", ReadOnly = true)]
    [Description("List the direct child pages of a page.")]
    public static async Task<string> GetConfluencePageChildren(
        AtlassianClient client,
        [Description("Page ID")] string pageId,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var id = ResolveContentId(pageId);
        var node = await client.ConfluenceGetAsync($"{V2}/pages/{id}/children{Query(("limit", Limit(limit)), ("cursor", cursor))}", ct);
        var items = new JsonArray();
        foreach (var c in Results(node))
        {
            items.Add(new JsonObject
            {
                ["id"] = C(c?["id"]),
                ["title"] = C(c?["title"]),
                ["status"] = C(c?["status"]),
                ["spaceId"] = C(c?["spaceId"]),
                ["childPosition"] = C(c?["childPosition"]),
            });
        }
        return Json.Out(Page("children", items, node));
    }

    [McpServerTool(Name = "createConfluencePage")]
    [Description("Create a Confluence page or blog post. Body defaults to Markdown.")]
    public static async Task<string> CreateConfluencePage(
        AtlassianClient client,
        [Description("Numeric space ID or space key (e.g. ENG, or ~accountId for a personal space)")] string spaceId,
        [Description("Page body")] string body,
        [Description("Page or blog post title (required unless status is draft)")] string? title = null,
        [Description("ID of the parent page/folder (pages only; omit to create at the space root)")] string? parentId = null,
        [Description(WriteFormatDoc)] string? contentFormat = null,
        [Description("\"page\" (default) or \"blog\"")] string? contentType = null,
        [Description("\"current\" (published, default) or \"draft\"")] string? status = null,
        [Description("Create as a private page visible only to you")] bool? isPrivate = null,
        [Description("Page subtype, e.g. \"live\" for a live doc (pages only)")] string? subtype = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var collection = Collection(contentType);
        var sid = await ResolveSpaceIdAsync(client, spaceId, ct);
        var payload = new JsonObject
        {
            ["spaceId"] = sid,
            ["status"] = status ?? "current",
            ["title"] = title,
            ["body"] = BuildBody(body, fmt),
        };
        if (collection == "pages")
        {
            if (parentId is not null) payload["parentId"] = parentId;
            if (subtype is not null) payload["subtype"] = subtype;
        }
        var qs = isPrivate == true ? "?private=true" : "";
        var node = await client.ConfluenceAsync(HttpMethod.Post, $"{V2}/{collection}{qs}", payload, ct);
        return Json.Out(ShapeContent(client, node));
    }

    [McpServerTool(Name = "updateConfluencePage")]
    [Description("Replace the body (and optionally title/parent/space) of a Confluence page or blog post. The version number is incremented automatically. Body defaults to Markdown.")]
    public static async Task<string> UpdateConfluencePage(
        AtlassianClient client,
        [Description("Page or blog post ID, tiny-link ID, or page URL")] string pageId,
        [Description("Replacement page body (the full content, not a diff)")] string body,
        [Description("New title (defaults to the current title)")] string? title = null,
        [Description("Move the page under this parent page/folder ID (pages only)")] string? parentId = null,
        [Description("Move the page to this space (ID or key); omit when unchanged")] string? spaceId = null,
        [Description(WriteFormatDoc)] string? contentFormat = null,
        [Description("\"page\" (default) or \"blog\"")] string? contentType = null,
        [Description("\"current\" (default) or \"draft\"")] string? status = null,
        [Description("Version message shown in page history")] string? versionMessage = null,
        [Description("Include the updated body in the response (default false)")] bool includeBody = false,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var collection = Collection(contentType);
        var id = ResolveContentId(pageId);
        var current = await client.ConfluenceGetAsync($"{V2}/{collection}/{id}", ct);
        var version = current?["version"]?["number"]?.GetValue<int>()
            ?? throw new McpException($"Could not read the current version of {collection[..^1]} {id}.");

        var payload = new JsonObject
        {
            ["id"] = id,
            ["status"] = status ?? "current",
            ["title"] = title ?? current?["title"]?.ToString(),
            ["body"] = BuildBody(body, fmt),
            ["version"] = new JsonObject { ["number"] = version + 1, ["message"] = versionMessage },
        };
        if (spaceId is not null)
            payload["spaceId"] = await ResolveSpaceIdAsync(client, spaceId, ct);
        if (parentId is not null && collection == "pages")
            payload["parentId"] = parentId;

        var node = await client.ConfluenceAsync(HttpMethod.Put, $"{V2}/{collection}/{id}", payload, ct);
        var shaped = ShapeContent(client, node);
        if (includeBody)
        {
            var fresh = await client.ConfluenceGetAsync($"{V2}/{collection}/{id}?body-format={BodyFormat(fmt)}", ct);
            shaped["body"] = RenderBody(fresh?["body"], fmt);
        }
        return Json.Out(shaped);
    }

    // ---------------------------------------------------------------- search

    [McpServerTool(Name = "searchConfluenceUsingCql", ReadOnly = true)]
    [Description("""
        Search Confluence (pages, blog posts, comments, attachments) with CQL. CQL is not JQL.
        Common fields: title, text, space (key), space.title, type, creator, contributor, label, ancestor, parent, lastmodified, created, mention.
        Types: page, blogpost, comment, attachment. Use ~ for contains, = for exact. Escape inner quotes with backslash.
        Examples: title ~ "meeting notes" AND type = page | space = ENG AND lastmodified >= now("-2w") | text ~ "release" AND creator = currentUser()
        """)]
    public static async Task<string> SearchConfluenceUsingCql(
        AtlassianClient client,
        [Description("CQL query string")] string cql,
        [Description("CQL context JSON to narrow scope, e.g. {\"spaceKey\":\"ENG\"}")] string? cqlcontext = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Extra properties to expand (v1 expand syntax, e.g. content.space)")] string? expand = null,
        [Description("Include next page link")] bool? next = null,
        [Description("Include previous page link")] bool? prev = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cql))
            throw new McpException("cql is required.");
        var qs = Query(("cql", cql), ("cqlcontext", cqlcontext), ("cursor", cursor), ("limit", Limit(limit) ?? "25"),
            ("expand", expand), ("next", Bool(next)), ("prev", Bool(prev)));
        var node = await client.ConfluenceGetAsync($"/wiki/rest/api/search{qs}", ct);
        var results = new JsonArray();
        foreach (var r in Results(node))
        {
            var content = r?["content"];
            var spaceKey = content?["space"]?["key"]?.ToString()
                ?? SpaceKeyFromUrl(r?["resultGlobalContainer"]?["displayUrl"]?.ToString());
            results.Add(new JsonObject
            {
                ["id"] = C(content?["id"]),
                ["type"] = C(content?["type"]) ?? C(r?["entityType"]),
                ["title"] = StripHighlight(content?["title"]?.ToString() ?? r?["title"]?.ToString()),
                ["spaceKey"] = spaceKey,
                ["space"] = C(r?["resultGlobalContainer"]?["title"]),
                ["excerpt"] = StripHighlight(r?["excerpt"]?.ToString()),
                ["url"] = WebUrl(client, content?["_links"]?["webui"] ?? r?["url"]),
                ["lastModified"] = C(r?["lastModified"]),
            });
        }
        var output = new JsonObject
        {
            ["results"] = results,
            ["totalSize"] = C(node?["totalSize"]),
            ["nextCursor"] = NextCursor(node),
        };
        if (prev == true)
            output["prevCursor"] = CursorFrom(node?["_links"]?["prev"]?.ToString());
        return Json.Out(output);
    }

    // ---------------------------------------------------------------- comments

    [McpServerTool(Name = "getConfluencePageFooterComments", ReadOnly = true)]
    [Description("List footer (page-bottom) comments on a page, with bodies. Use getConfluenceCommentChildren for replies.")]
    public static async Task<string> GetConfluencePageFooterComments(
        AtlassianClient client,
        [Description("Page ID")] string pageId,
        [Description("current, archived, trashed, deleted, historical or draft")] string? status = null,
        [Description("Sort: created-date, -created-date, modified-date, -modified-date")] string? sort = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(ReadFormatDoc)] string? contentFormat = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var id = ResolveContentId(pageId);
        var qs = Query(("body-format", BodyFormat(fmt)), ("status", status), ("sort", sort), ("limit", Limit(limit)), ("cursor", cursor));
        var node = await client.ConfluenceGetAsync($"{V2}/pages/{id}/footer-comments{qs}", ct);
        return Json.Out(Page("comments", ShapeComments(client, node, fmt), node));
    }

    [McpServerTool(Name = "getConfluencePageInlineComments", ReadOnly = true)]
    [Description("List inline comments (anchored to highlighted text) on a page, with bodies and the selected text.")]
    public static async Task<string> GetConfluencePageInlineComments(
        AtlassianClient client,
        [Description("Page ID")] string pageId,
        [Description("open, resolved, reopened or dangling")] string? resolutionStatus = null,
        [Description("current, archived, trashed, deleted, historical or draft")] string? status = null,
        [Description("Sort: created-date, -created-date, modified-date, -modified-date")] string? sort = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(ReadFormatDoc)] string? contentFormat = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var id = ResolveContentId(pageId);
        var qs = Query(("body-format", BodyFormat(fmt)), ("resolution-status", resolutionStatus), ("status", status),
            ("sort", sort), ("limit", Limit(limit)), ("cursor", cursor));
        var node = await client.ConfluenceGetAsync($"{V2}/pages/{id}/inline-comments{qs}", ct);
        return Json.Out(Page("comments", ShapeComments(client, node, fmt), node));
    }

    [McpServerTool(Name = "getConfluenceCommentChildren", ReadOnly = true)]
    [Description("List replies to a footer or inline comment.")]
    public static async Task<string> GetConfluenceCommentChildren(
        AtlassianClient client,
        [Description("Parent comment ID")] string commentId,
        [Description("\"footer\" (default) or \"inline\"")] string? commentType = null,
        [Description("Max results (default 25, max 250)")] int? limit = null,
        [Description("Pagination cursor from a previous nextCursor")] string? cursor = null,
        [Description(ReadFormatDoc)] string? contentFormat = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var collection = (commentType ?? "footer").Trim().ToLowerInvariant() switch
        {
            "footer" => "footer-comments",
            "inline" => "inline-comments",
            _ => throw new McpException("commentType must be \"footer\" or \"inline\"."),
        };
        var qs = Query(("body-format", BodyFormat(fmt)), ("limit", Limit(limit)), ("cursor", cursor));
        var node = await client.ConfluenceGetAsync($"{V2}/{collection}/{Json.Q(commentId)}/children{qs}", ct);
        return Json.Out(Page("comments", ShapeComments(client, node, fmt), node));
    }

    [McpServerTool(Name = "createConfluenceFooterComment")]
    [Description("Add a footer comment to a page or blog post, or reply to an existing footer comment (parentCommentId). Body defaults to Markdown.")]
    public static async Task<string> CreateConfluenceFooterComment(
        AtlassianClient client,
        [Description("Comment body")] string body,
        [Description("Page ID to comment on")] string? pageId = null,
        [Description("Footer comment ID to reply to")] string? parentCommentId = null,
        [Description("Blog post ID to comment on")] string? blogPostId = null,
        [Description("Attachment ID to comment on")] string? attachmentId = null,
        [Description("Custom content ID to comment on")] string? customContentId = null,
        [Description(WriteFormatDoc)] string? contentFormat = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var payload = new JsonObject { ["body"] = BuildBody(body, fmt) };
        if (!SetTarget(payload, ("pageId", pageId), ("parentCommentId", parentCommentId), ("blogPostId", blogPostId),
                ("attachmentId", attachmentId), ("customContentId", customContentId)))
            throw new McpException("Provide one of pageId, parentCommentId, blogPostId, attachmentId or customContentId.");
        var node = await client.ConfluenceAsync(HttpMethod.Post, $"{V2}/footer-comments", payload, ct);
        return Json.Out(ShapeComment(client, node, null));
    }

    [McpServerTool(Name = "createConfluenceInlineComment")]
    [Description("Add an inline comment anchored to text on a page, or reply to an inline comment (parentCommentId). New top-level comments need inlineCommentProperties.textSelection. Body defaults to Markdown.")]
    public static async Task<string> CreateConfluenceInlineComment(
        AtlassianClient client,
        [Description("Comment body")] string body,
        [Description("Page ID to comment on")] string? pageId = null,
        [Description("Inline comment ID to reply to")] string? parentCommentId = null,
        [Description("Blog post ID to comment on")] string? blogPostId = null,
        [Description(WriteFormatDoc)] string? contentFormat = null,
        [Description("Anchor for a new top-level comment: {\"textSelection\": \"exact text on the page\", \"textSelectionMatchCount\": <occurrences of that text>, \"textSelectionMatchIndex\": <0-based occurrence to anchor to>}")] JsonElement? inlineCommentProperties = null,
        [Description(CloudIdDoc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fmt = Format(contentFormat);
        var payload = new JsonObject { ["body"] = BuildBody(body, fmt) };
        if (!SetTarget(payload, ("pageId", pageId), ("parentCommentId", parentCommentId), ("blogPostId", blogPostId)))
            throw new McpException("Provide one of pageId, parentCommentId or blogPostId.");
        var props = Json.ToObject(inlineCommentProperties);
        if (props is not null)
            payload["inlineCommentProperties"] = props;
        else if (parentCommentId is null)
            throw new McpException("inlineCommentProperties.textSelection is required for a new inline comment.");
        var node = await client.ConfluenceAsync(HttpMethod.Post, $"{V2}/inline-comments", payload, ct);
        return Json.Out(ShapeComment(client, node, null));
    }

    // ---------------------------------------------------------------- helpers

    static string Format(string? contentFormat) => (contentFormat ?? "markdown").Trim().ToLowerInvariant() switch
    {
        "" or "markdown" or "md" => "markdown",
        "adf" => "adf",
        "html" or "storage" => "html",
        var other => throw new McpException($"Unsupported contentFormat \"{other}\". Use markdown, adf or html."),
    };

    static string BodyFormat(string fmt) => fmt == "html" ? "storage" : "atlas_doc_format";

    static string Collection(string? contentType) => (contentType ?? "page").Trim().ToLowerInvariant() switch
    {
        "" or "page" or "pages" => "pages",
        "blog" or "blogpost" or "blogposts" => "blogposts",
        var other => throw new McpException($"Unsupported contentType \"{other}\". Use page or blog."),
    };

    static JsonObject BuildBody(string body, string fmt)
    {
        if (fmt == "html")
            return new JsonObject { ["representation"] = "storage", ["value"] = body };
        var adf = Adf.FromInput(body, fmt) ?? Adf.FromMarkdown("");
        return new JsonObject { ["representation"] = "atlas_doc_format", ["value"] = adf.ToJsonString() };
    }

    static JsonNode? RenderBody(JsonNode? body, string fmt)
    {
        if (fmt == "html")
            return C(body?["storage"]?["value"]);
        var raw = body?["atlas_doc_format"]?["value"]?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Adf.ForOutput(JsonNode.Parse(raw), fmt);
    }

    static JsonObject ShapeContent(AtlassianClient client, JsonNode? node)
    {
        var v = node?["version"];
        return new JsonObject
        {
            ["id"] = C(node?["id"]),
            ["title"] = C(node?["title"]),
            ["status"] = C(node?["status"]),
            ["spaceId"] = C(node?["spaceId"]),
            ["parentId"] = C(node?["parentId"]),
            ["parentType"] = C(node?["parentType"]),
            ["subtype"] = C(node?["subtype"]),
            ["authorId"] = C(node?["authorId"]),
            ["createdAt"] = C(node?["createdAt"]),
            ["version"] = v is null ? null : new JsonObject
            {
                ["number"] = C(v["number"]),
                ["createdAt"] = C(v["createdAt"]),
                ["message"] = string.IsNullOrEmpty(v["message"]?.ToString()) ? null : C(v["message"]),
                ["authorId"] = C(v["authorId"]),
            },
            ["url"] = WebUrl(client, node?["_links"]?["webui"]),
        };
    }

    static JsonArray ShapeComments(AtlassianClient client, JsonNode? node, string fmt)
    {
        var comments = new JsonArray();
        foreach (var c in Results(node))
            comments.Add(ShapeComment(client, c, fmt));
        return comments;
    }

    static JsonObject ShapeComment(AtlassianClient client, JsonNode? c, string? fmt)
    {
        var shaped = new JsonObject
        {
            ["id"] = C(c?["id"]),
            ["status"] = C(c?["status"]),
            ["pageId"] = C(c?["pageId"]),
            ["blogPostId"] = C(c?["blogPostId"]),
            ["parentCommentId"] = C(c?["parentCommentId"]),
            ["resolutionStatus"] = C(c?["resolutionStatus"]),
            ["textSelection"] = C(c?["properties"]?["inlineOriginalSelection"]) ?? C(c?["properties"]?["inline-original-selection"]),
            ["authorId"] = C(c?["version"]?["authorId"]),
            ["createdAt"] = C(c?["version"]?["createdAt"]),
            ["version"] = C(c?["version"]?["number"]),
            ["url"] = WebUrl(client, c?["_links"]?["webui"]),
        };
        if (fmt is not null)
            shaped["body"] = RenderBody(c?["body"], fmt);
        return shaped;
    }

    static JsonObject Page(string name, JsonArray items, JsonNode? node) => new()
    {
        [name] = items,
        ["nextCursor"] = NextCursor(node),
    };

    static IEnumerable<JsonNode?> Results(JsonNode? node) =>
        node?["results"] is JsonArray arr ? arr : Enumerable.Empty<JsonNode?>();

    static string? NextCursor(JsonNode? node) => CursorFrom(node?["_links"]?["next"]?.ToString());

    static string? CursorFrom(string? link)
    {
        if (string.IsNullOrEmpty(link))
            return null;
        var m = CursorRegex().Match(link);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    static string? WebUrl(AtlassianClient client, JsonNode? webui)
    {
        var path = webui?.ToString();
        if (string.IsNullOrEmpty(path))
            return null;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return path;
        var site = client.Options.SiteUrl;
        return path.StartsWith("/wiki/", StringComparison.OrdinalIgnoreCase) ? site + path : site + "/wiki" + path;
    }

    static async Task<string> ResolveSpaceIdAsync(AtlassianClient client, string spaceIdOrKey, CancellationToken ct)
    {
        var s = spaceIdOrKey.Trim();
        if (s.Length > 0 && s.All(char.IsAsciiDigit))
            return s;
        var node = await client.ConfluenceGetAsync($"{V2}/spaces?keys={Json.Q(s)}&limit=1", ct);
        return Results(node).FirstOrDefault()?["id"]?.ToString()
            ?? throw new McpException($"No Confluence space with key \"{s}\" is visible to this account.");
    }

    /// <summary>Accepts a numeric ID, a tiny-link ID (/wiki/x/&lt;id&gt;), or a page URL.</summary>
    internal static string ResolveContentId(string pageId)
    {
        var s = pageId.Trim();
        if (s.Length > 0 && s.All(char.IsAsciiDigit))
            return s;

        var url = PageUrlRegex().Match(s);
        if (url.Success)
            return url.Groups[1].Value;

        var tiny = TinyUrlRegex().Match(s);
        if (tiny.Success)
            s = tiny.Groups[1].Value;

        return DecodeTinyId(s) ?? throw new McpException($"\"{pageId}\" is not a page ID, tiny-link ID or page URL.");
    }

    /// <summary>
    /// Confluence tiny links are the page ID as little-endian bytes, base64-encoded with '/'→'-', '+'→'_'
    /// and trailing 'A'/'=' stripped. Padding with 'A' restores the stripped zero bits.
    /// </summary>
    internal static string? DecodeTinyId(string tiny)
    {
        if (string.IsNullOrEmpty(tiny) || tiny.Length > 11 || !TinyIdRegex().IsMatch(tiny))
            return null;
        var b64 = tiny.Replace('-', '/').Replace('_', '+').PadRight(12, 'A');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }
        ulong id = 0;
        for (var i = Math.Min(bytes.Length, 8) - 1; i >= 0; i--)
            id = (id << 8) | bytes[i];
        return id == 0 ? null : id.ToString();
    }

    static string? SpaceKeyFromUrl(string? displayUrl)
    {
        if (string.IsNullOrEmpty(displayUrl))
            return null;
        var m = SpaceUrlRegex().Match(displayUrl);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    static string? StripHighlight(string? s) =>
        s?.Replace("@@@hl@@@", "").Replace("@@@endhl@@@", "");

    static bool SetTarget(JsonObject payload, params (string Key, string? Value)[] targets)
    {
        var set = targets.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Value));
        if (set.Key is null)
            return false;
        payload[set.Key] = set.Value;
        return true;
    }

    static string? ListParam(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        var joined = e.ValueKind == JsonValueKind.Array
            ? string.Join(",", e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()))
            : e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    static string? Limit(int? limit) => limit is { } l ? Math.Clamp(l, 1, 250).ToString() : null;

    static string? Bool(bool? b) => b is { } v ? (v ? "true" : "false") : null;

    static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Json.Q(p.Value!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    static JsonNode? C(JsonNode? node) => node?.DeepClone();

    [GeneratedRegex(@"[?&]cursor=([^&]+)")]
    private static partial Regex CursorRegex();

    [GeneratedRegex(@"/pages/(?:edit-v2/|viewpage\.action\?pageId=)?(\d+)")]
    private static partial Regex PageUrlRegex();

    [GeneratedRegex(@"/x/([A-Za-z0-9_\-]+)")]
    private static partial Regex TinyUrlRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex TinyIdRegex();

    [GeneratedRegex(@"/spaces/([^/?#]+)")]
    private static partial Regex SpaceUrlRegex();
}
