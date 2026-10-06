using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpAtlassian.Formatting;
using McpAtlassian.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpAtlassian.Tools;

[McpServerToolType]
public static class JiraTools
{
    const string CloudIdDesc = "Ignored. Kept for compatibility with the hosted Atlassian MCP; the site comes from ATLASSIAN_SITE.";
    const string IssueDesc = "Issue ID or key (e.g., PROJ-123 or 10000)";
    const string ContentFormatDesc = "Format of body input: \"markdown\" (default) or \"adf\" (Atlassian Document Format JSON).";
    const string ResponseFormatDesc = "Format for rich-text fields in the response: \"markdown\" (default) or \"adf\".";

    // ---------------------------------------------------------------- issues

    [McpServerTool(Name = "getJiraIssue", ReadOnly = true)]
    [Description("Get a Jira issue. Defaults to summary, description, status, issuetype, priority, labels, components, assignee, reporter, created, updated, resolution, project, parent, subtasks and issuelinks. Rich text is returned as Markdown unless responseContentFormat is \"adf\".")]
    public static async Task<string> GetJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Fields to return. Pass [\"*all\"] for every field (including custom fields). Include \"comment\" for comments, \"attachment\" for attachments.")] string[]? fields = null,
        [Description("Comma-separated expansions, e.g. \"renderedFields\", \"names\" (field ID -> name map), \"changelog\".")] string? expand = null,
        [Description("Issue property keys to include.")] string[]? properties = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] bool? failFast = null,
        [Description("Ignored.")] bool? fieldsByKeys = null,
        [Description("Ignored.")] bool? updateHistory = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var issue = await FetchIssueAsync(client, issueIdOrKey, fields, expand, properties, ct);
        return Json.Out(JiraShape.Issue(issue, client.Options.SiteUrl, responseContentFormat));
    }

    [McpServerTool(Name = "searchJiraIssuesUsingJql", ReadOnly = true)]
    [Description("Search Jira issues with JQL. Examples: project = ABC AND status != Done ORDER BY updated DESC | assignee = currentUser() AND resolution = Unresolved | text ~ \"login error\" AND created >= -14d. Quote values containing spaces. Paginate with nextPageToken. Use searchResultMode \"count\" only when a total is actually needed.")]
    public static async Task<string> SearchJiraIssuesUsingJql(
        AtlassianClient client,
        [Description("JQL query")] string jql,
        [Description("Fields to return (same defaults as getJiraIssue; [\"*all\"] for everything).")] string[]? fields = null,
        [Description("Max issues per page (default 50, max 100).")] int? maxResults = null,
        [Description("Page token from a previous response's nextPageToken.")] string? nextPageToken = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description("\"issues\" (default), \"count\" (approximate total only), or \"all\" (issues plus count).")] string? searchResultMode = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var mode = (searchResultMode ?? "issues").Trim().ToLowerInvariant();
        if (mode is not ("issues" or "count" or "all"))
            throw new McpException($"searchResultMode must be issues, count or all (got \"{searchResultMode}\").");

        var result = new JsonObject();

        if (mode is "count" or "all")
        {
            var count = await client.JiraAsync(HttpMethod.Post, "/rest/api/3/search/approximate-count",
                new JsonObject { ["jql"] = jql }.ToJsonString(), ct);
            result["count"] = count?["count"]?.DeepClone();
            if (mode == "count")
                return Json.Out(result);
        }

        var body = new JsonObject
        {
            ["jql"] = jql,
            ["maxResults"] = Math.Clamp(maxResults ?? 50, 1, 100),
            ["fields"] = new JsonArray(FieldList(fields).Select(f => (JsonNode?)f).ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(nextPageToken))
            body["nextPageToken"] = nextPageToken;

        var page = await client.JiraAsync(HttpMethod.Post, "/rest/api/3/search/jql", body.ToJsonString(), ct);
        var site = client.Options.SiteUrl;
        result["issues"] = page?["issues"] is JsonArray issues
            ? new JsonArray(issues.Where(i => i is not null).Select(i => (JsonNode?)JiraShape.Issue(i!, site, responseContentFormat)).ToArray())
            : new JsonArray();
        result["isLast"] = page?["isLast"]?.DeepClone();
        if (page?["nextPageToken"] is JsonNode token)
            result["nextPageToken"] = token.DeepClone();
        return Json.Out(result);
    }

    [McpServerTool(Name = "createJiraIssue")]
    [Description("Create a Jira issue. Use additional_fields for anything without its own parameter (priority, labels, components, fixVersions, custom fields), e.g. {\"priority\": {\"name\": \"High\"}, \"labels\": [\"bug\"], \"customfield_10001\": \"value\"}. Use getJiraIssueTypeMetaWithFields to discover required fields.")]
    public static async Task<string> CreateJiraIssue(
        AtlassianClient client,
        [Description("Project key (or numeric project ID)")] string projectKey,
        [Description("Issue type name (Task, Bug, Story, Sub-task, ...) or numeric issue type ID")] string issueTypeName,
        [Description("Issue summary (title)")] string summary,
        [Description("Issue description: Markdown string by default; with contentFormat \"adf\", an ADF doc object or JSON string.")] JsonElement? description = null,
        [Description(ContentFormatDesc)] string? contentFormat = null,
        [Description("Assignee account ID (see lookupJiraAccountId)")] string? assignee_account_id = null,
        [Description("Parent issue key (for subtasks or child issues of an epic)")] string? parent = null,
        [Description("Any other fields, keyed by field name or customfield_* ID. Strings are accepted for priority, components, fixVersions, labels etc.")] JsonElement? additional_fields = null,
        [Description("Optional workflow transition to apply on create: {\"id\": \"<transitionId>\"}")] JsonElement? transition = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var fields = new JsonObject
        {
            ["project"] = IsNumeric(projectKey) ? new JsonObject { ["id"] = projectKey } : new JsonObject { ["key"] = projectKey },
            ["issuetype"] = IsNumeric(issueTypeName) ? new JsonObject { ["id"] = issueTypeName } : new JsonObject { ["name"] = issueTypeName },
            ["summary"] = summary,
        };

        var desc = Adf.FromInput(description, contentFormat);
        if (desc is not null)
            fields["description"] = desc;
        if (!string.IsNullOrWhiteSpace(assignee_account_id))
            fields["assignee"] = new JsonObject { ["accountId"] = assignee_account_id };
        if (!string.IsNullOrWhiteSpace(parent))
            fields["parent"] = IsNumeric(parent) ? new JsonObject { ["id"] = parent } : new JsonObject { ["key"] = parent };

        if (Json.ToObject(additional_fields) is { } extra)
        {
            var normalized = await NormalizeFieldsAsync(client, extra, contentFormat, ct);
            foreach (var (k, v) in normalized)
                fields[k] = v?.DeepClone();
        }

        var body = new JsonObject { ["fields"] = fields };
        if (TransitionObject(transition) is { } t)
            body["transition"] = t;

        var created = await client.JiraAsync(HttpMethod.Post, "/rest/api/3/issue", body.ToJsonString(), ct);
        var key = created?["key"]?.ToString() ?? "";
        return Json.Out(new JsonObject
        {
            ["key"] = key,
            ["id"] = created?["id"]?.ToString(),
            ["url"] = JiraShape.IssueUrl(client.Options.SiteUrl, key),
        });
    }

    [McpServerTool(Name = "editJiraIssue", Idempotent = true)]
    [Description("Update fields on a Jira issue, keyed by field name or customfield_* ID. Pass null to clear a field, e.g. {\"resolution\": null} (the fix when a reopened issue can't transition because Resolution is still set). Description and rich-text custom fields accept Markdown strings. Returns the updated issue with getJiraIssue's default fields.")]
    public static async Task<string> EditJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Fields to set, e.g. {\"summary\": \"New title\", \"description\": \"**markdown**\", \"labels\": [\"a\"], \"priority\": \"High\", \"assignee\": \"<accountId>\"}")] JsonElement fields,
        [Description(ContentFormatDesc)] string? contentFormat = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var raw = Json.ToObject(fields) ?? throw new McpException("fields must be a JSON object of field -> value.");
        if (raw.Count == 0)
            throw new McpException("fields is empty; nothing to update.");

        var normalized = await NormalizeFieldsAsync(client, raw, contentFormat, ct);
        await client.JiraAsync(HttpMethod.Put, $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}?returnIssue=false",
            new JsonObject { ["fields"] = normalized }.ToJsonString(), ct);

        var issue = await FetchIssueAsync(client, issueIdOrKey, null, null, null, ct);
        return Json.Out(JiraShape.Issue(issue, client.Options.SiteUrl, responseContentFormat));
    }

    [McpServerTool(Name = "assignJiraIssue", Idempotent = true)]
    [Description("Assign a Jira issue to a user (account ID from lookupJiraAccountId), or omit accountId to unassign.")]
    public static async Task<string> AssignJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Assignee account ID. Omit or null to unassign; \"-1\" for the project's default assignee.")] string? accountId = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject { ["accountId"] = string.IsNullOrWhiteSpace(accountId) ? null : accountId };
        await client.JiraAsync(HttpMethod.Put, $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/assignee", body.ToJsonString(), ct);
        var issue = await client.JiraGetAsync($"/rest/api/3/issue/{Json.Q(issueIdOrKey)}?fields=assignee", ct);
        return Json.Out(new JsonObject
        {
            ["key"] = issue?["key"]?.ToString(),
            ["assignee"] = JiraShape.User(issue?["fields"]?["assignee"]),
        });
    }

    // ---------------------------------------------------------------- comments & worklogs

    [McpServerTool(Name = "addCommentToJiraIssue")]
    [Description("Add a comment to a Jira issue, or update an existing one when commentId is given. Body is Markdown by default.")]
    public static async Task<string> AddCommentToJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Comment body (Markdown by default; ADF JSON with contentFormat \"adf\")")] string commentBody,
        [Description("ID of an existing comment to update. If omitted, a new comment is added.")] string? commentId = null,
        [Description("Restrict visibility: {\"type\": \"group\"|\"role\", \"value\": \"<name>\"}")] JsonElement? commentVisibility = null,
        [Description(ContentFormatDesc)] string? contentFormat = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["body"] = Adf.FromInput(commentBody, contentFormat) ?? throw new McpException("commentBody is empty."),
        };
        if (Json.ToObject(commentVisibility) is { } vis)
            body["visibility"] = vis;

        var path = $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/comment";
        var comment = string.IsNullOrWhiteSpace(commentId)
            ? await client.JiraAsync(HttpMethod.Post, path, body.ToJsonString(), ct)
            : await client.JiraAsync(HttpMethod.Put, $"{path}/{Json.Q(commentId)}", body.ToJsonString(), ct);

        var shaped = JiraShape.Comment(comment, responseContentFormat);
        shaped["url"] = $"{JiraShape.IssueUrl(client.Options.SiteUrl, issueIdOrKey)}?focusedCommentId={shaped["id"]}";
        return Json.Out(shaped);
    }

    [McpServerTool(Name = "getJiraIssueComments", ReadOnly = true)]
    [Description("List comments on a Jira issue with paging. Bodies are Markdown unless responseContentFormat is \"adf\".")]
    public static async Task<string> GetJiraIssueComments(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Index of the first comment (default 0).")] int? startAt = null,
        [Description("Max comments to return (default 50, max 100).")] int? maxResults = null,
        [Description("\"created\" (oldest first, default) or \"-created\" (newest first).")] string? orderBy = null,
        [Description(ResponseFormatDesc)] string? responseContentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var query = JiraShape.Query(
            ("startAt", startAt),
            ("maxResults", Math.Clamp(maxResults ?? 50, 1, 100)),
            ("orderBy", orderBy));
        var page = await client.JiraGetAsync($"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/comment{query}", ct);
        return Json.Out(page is null ? new JsonObject() : JiraShape.Comments(page, responseContentFormat));
    }

    [McpServerTool(Name = "addWorklogToJiraIssue")]
    [Description("Log time on a Jira issue, or update an existing worklog when worklogId is given.")]
    public static async Task<string> AddWorklogToJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Time spent, e.g. \"2h\", \"30m\", \"1d 4h\".")] string timeSpent,
        [Description("When the work started (ISO 8601). Defaults to now.")] string? started = null,
        [Description("Optional worklog comment (Markdown by default).")] string? commentBody = null,
        [Description(ContentFormatDesc)] string? contentFormat = null,
        [Description("Restrict visibility: {\"type\": \"group\"|\"role\", \"value\": \"<name>\"}")] JsonElement? visibility = null,
        [Description("ID of an existing worklog to update. If omitted, a new worklog is created.")] string? worklogId = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject { ["timeSpent"] = timeSpent };
        if (!string.IsNullOrWhiteSpace(started) || string.IsNullOrWhiteSpace(worklogId))
            body["started"] = JiraTimestamp(started);
        if (Adf.FromInput(commentBody, contentFormat) is { } comment)
            body["comment"] = comment;
        if (Json.ToObject(visibility) is { } vis)
            body["visibility"] = vis;

        var path = $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/worklog";
        var worklog = string.IsNullOrWhiteSpace(worklogId)
            ? await client.JiraAsync(HttpMethod.Post, path, body.ToJsonString(), ct)
            : await client.JiraAsync(HttpMethod.Put, $"{path}/{Json.Q(worklogId)}", body.ToJsonString(), ct);

        return Json.Out(new JsonObject
        {
            ["id"] = worklog?["id"]?.ToString(),
            ["issueId"] = worklog?["issueId"]?.ToString(),
            ["author"] = JiraShape.User(worklog?["author"]),
            ["started"] = worklog?["started"]?.ToString(),
            ["timeSpent"] = worklog?["timeSpent"]?.ToString(),
            ["timeSpentSeconds"] = worklog?["timeSpentSeconds"]?.DeepClone(),
        });
    }

    // ---------------------------------------------------------------- transitions

    [McpServerTool(Name = "getTransitionsForJiraIssue", ReadOnly = true)]
    [Description("List workflow transitions available for an issue (id, name, target status). Use expand \"transitions.fields\" to see fields required by each transition's screen.")]
    public static async Task<string> GetTransitionsForJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("e.g. \"transitions.fields\"")] string? expand = null,
        [Description("Only return this transition.")] string? transitionId = null,
        [Description("Include transitions whose conditions currently fail.")] bool? includeUnavailableTransitions = null,
        bool? skipRemoteOnlyCondition = null,
        bool? sortByOpsBarAndStatus = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var query = JiraShape.Query(
            ("expand", expand),
            ("transitionId", transitionId),
            ("includeUnavailableTransitions", includeUnavailableTransitions),
            ("skipRemoteOnlyCondition", skipRemoteOnlyCondition),
            ("sortByOpsBarAndStatus", sortByOpsBarAndStatus));
        var resp = await client.JiraGetAsync($"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/transitions{query}", ct);

        var list = new JsonArray();
        foreach (var t in resp?["transitions"] as JsonArray ?? [])
        {
            if (t is null)
                continue;
            var o = new JsonObject
            {
                ["id"] = t["id"]?.ToString(),
                ["name"] = t["name"]?.ToString(),
                ["to"] = t["to"]?["name"]?.ToString(),
                ["toCategory"] = t["to"]?["statusCategory"]?["name"]?.ToString(),
                ["hasScreen"] = t["hasScreen"]?.DeepClone(),
                ["isAvailable"] = t["isAvailable"]?.DeepClone(),
            };
            if (t["fields"] is JsonObject tf && tf.Count > 0)
            {
                o["fields"] = new JsonArray(tf.Select(kv => (JsonNode?)new JsonObject
                {
                    ["fieldId"] = kv.Key,
                    ["name"] = kv.Value?["name"]?.ToString(),
                    ["required"] = kv.Value?["required"]?.DeepClone(),
                    ["type"] = kv.Value?["schema"]?["type"]?.ToString(),
                    ["allowedValues"] = AllowedValues(kv.Value?["allowedValues"]),
                }).ToArray());
            }
            list.Add(o);
        }
        return Json.Out(new JsonObject { ["transitions"] = list });
    }

    [McpServerTool(Name = "transitionJiraIssue")]
    [Description("Move an issue through its workflow. transition.id may be a transition ID, or (convenience) a transition name or target status name like \"Done\". Use getTransitionsForJiraIssue to see options and required fields.")]
    public static async Task<string> TransitionJiraIssue(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("{\"id\": \"31\"} (a bare string is also accepted)")] JsonElement transition,
        [Description("Fields to set on the transition screen, e.g. {\"resolution\": {\"name\": \"Done\"}}")] JsonElement? fields = null,
        [Description("Field update operations, e.g. {\"comment\": [{\"add\": {\"body\": \"Markdown text\"}}]}")] JsonElement? update = null,
        [Description("Optional history metadata.")] JsonElement? historyMetadata = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var requested = TransitionObject(transition)?["id"]?.ToString()
            ?? throw new McpException("transition must be {\"id\": \"<transitionId>\"}.");

        var path = $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/transitions";
        var id = requested;
        if (!IsNumeric(requested))
        {
            var available = await client.JiraGetAsync(path, ct);
            var all = (available?["transitions"] as JsonArray ?? []).Where(t => t is not null).ToList();
            var match = all.FirstOrDefault(t => string.Equals(t!["name"]?.ToString(), requested, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(t => string.Equals(t!["to"]?["name"]?.ToString(), requested, StringComparison.OrdinalIgnoreCase));
            id = match?["id"]?.ToString() ?? throw new McpException(
                $"No transition named \"{requested}\" from the current status. Available: " +
                string.Join(", ", all.Select(t => $"{t!["id"]} \"{t["name"]}\" -> {t["to"]?["name"]}")));
        }

        var body = new JsonObject { ["transition"] = new JsonObject { ["id"] = id } };
        if (Json.ToObject(fields) is { } f && f.Count > 0)
            body["fields"] = await NormalizeFieldsAsync(client, f, null, ct);
        if (Json.ToObject(update) is { } u && u.Count > 0)
            body["update"] = NormalizeUpdate(u);
        if (Json.ToObject(historyMetadata) is { } h)
            body["historyMetadata"] = h;

        await client.JiraAsync(HttpMethod.Post, path, body.ToJsonString(), ct);

        var issue = await client.JiraGetAsync($"/rest/api/3/issue/{Json.Q(issueIdOrKey)}?fields=status,resolution", ct);
        return Json.Out(new JsonObject
        {
            ["key"] = issue?["key"]?.ToString(),
            ["transitionId"] = id,
            ["status"] = issue?["fields"]?["status"]?["name"]?.ToString(),
            ["resolution"] = issue?["fields"]?["resolution"]?["name"]?.ToString(),
        });
    }

    // ---------------------------------------------------------------- projects & metadata

    [McpServerTool(Name = "getVisibleJiraProjects", ReadOnly = true)]
    [Description("List Jira projects the user can access for the given action (default \"create\"), optionally filtered by name/key.")]
    public static async Task<string> GetVisibleJiraProjects(
        AtlassianClient client,
        [Description("Filter by project name or key (partial match).")] string? searchString = null,
        [Description("view | browse | edit | create (default create)")] string? action = null,
        [Description("Index of the first project (default 0).")] int? startAt = null,
        [Description("Max projects (default 50, max 50).")] int? maxResults = null,
        [Description("Include each project's issue types (default true).")] bool? expandIssueTypes = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var withTypes = expandIssueTypes ?? true;
        var query = JiraShape.Query(
            ("query", searchString),
            ("action", action ?? "create"),
            ("startAt", startAt ?? 0),
            ("maxResults", Math.Clamp(maxResults ?? 50, 1, 50)),
            ("expand", withTypes ? "issueTypes" : null));
        var page = await client.JiraGetAsync($"/rest/api/3/project/search{query}", ct);

        var projects = new JsonArray();
        foreach (var p in page?["values"] as JsonArray ?? [])
        {
            if (p is null)
                continue;
            var o = new JsonObject
            {
                ["key"] = p["key"]?.ToString(),
                ["id"] = p["id"]?.ToString(),
                ["name"] = p["name"]?.ToString(),
                ["projectTypeKey"] = p["projectTypeKey"]?.ToString(),
            };
            if (withTypes && p["issueTypes"] is JsonArray types)
                o["issueTypes"] = new JsonArray(types.Select(t => (JsonNode?)new JsonObject
                {
                    ["id"] = t?["id"]?.ToString(),
                    ["name"] = t?["name"]?.ToString(),
                    ["subtask"] = t?["subtask"]?.DeepClone(),
                }).ToArray());
            projects.Add(o);
        }
        return Json.Out(new JsonObject
        {
            ["projects"] = projects,
            ["startAt"] = page?["startAt"]?.DeepClone(),
            ["total"] = page?["total"]?.DeepClone(),
            ["isLast"] = page?["isLast"]?.DeepClone(),
        });
    }

    [McpServerTool(Name = "getJiraProjectIssueTypesMetadata", ReadOnly = true)]
    [Description("List the issue types that can be created in a project (id, name, subtask).")]
    public static async Task<string> GetJiraProjectIssueTypesMetadata(
        AtlassianClient client,
        [Description("Project ID or key")] string projectIdOrKey,
        [Description("Index of the first issue type (default 0).")] int? startAt = null,
        [Description("Max issue types (default 50, max 200).")] int? maxResults = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var query = JiraShape.Query(("startAt", startAt ?? 0), ("maxResults", Math.Clamp(maxResults ?? 50, 1, 200)));
        var page = await client.JiraGetAsync($"/rest/api/3/issue/createmeta/{Json.Q(projectIdOrKey)}/issuetypes{query}", ct);
        var types = page?["issueTypes"] as JsonArray ?? page?["values"] as JsonArray ?? [];
        return Json.Out(new JsonObject
        {
            ["issueTypes"] = new JsonArray(types.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t?["id"]?.ToString(),
                ["name"] = t?["name"]?.ToString(),
                ["subtask"] = t?["subtask"]?.DeepClone(),
                ["description"] = t?["description"]?.ToString(),
            }).ToArray()),
            ["total"] = page?["total"]?.DeepClone(),
        });
    }

    [McpServerTool(Name = "getJiraIssueTypeMetaWithFields", ReadOnly = true)]
    [Description("Get the create-screen fields for an issue type in a project: field ID, name, required, type and allowed values. Use before createJiraIssue to find required/custom fields.")]
    public static async Task<string> GetJiraIssueTypeMetaWithFields(
        AtlassianClient client,
        [Description("Project ID or key")] string projectIdOrKey,
        [Description("Issue type ID (from getJiraProjectIssueTypesMetadata)")] string issueTypeId,
        [Description("When true (default), only required fields are returned.")] bool? requiredFieldsOnly = null,
        int? startAt = null,
        int? maxResults = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var query = JiraShape.Query(("startAt", startAt ?? 0), ("maxResults", Math.Clamp(maxResults ?? 200, 1, 200)));
        var page = await client.JiraGetAsync(
            $"/rest/api/3/issue/createmeta/{Json.Q(projectIdOrKey)}/issuetypes/{Json.Q(issueTypeId)}{query}", ct);
        var raw = page?["fields"] as JsonArray ?? page?["results"] as JsonArray ?? page?["values"] as JsonArray ?? [];
        var requiredOnly = requiredFieldsOnly ?? true;

        var fields = new JsonArray();
        foreach (var f in raw)
        {
            if (f is null || (requiredOnly && f["required"]?.GetValue<bool>() != true))
                continue;
            fields.Add(new JsonObject
            {
                ["fieldId"] = f["fieldId"]?.ToString() ?? f["key"]?.ToString(),
                ["name"] = f["name"]?.ToString(),
                ["required"] = f["required"]?.DeepClone(),
                ["type"] = f["schema"]?["type"]?.ToString(),
                ["items"] = f["schema"]?["items"]?.ToString(),
                ["custom"] = f["schema"]?["custom"]?.ToString(),
                ["hasDefaultValue"] = f["hasDefaultValue"]?.DeepClone(),
                ["allowedValues"] = AllowedValues(f["allowedValues"]),
            });
        }
        return Json.Out(new JsonObject
        {
            ["fields"] = fields,
            ["total"] = page?["total"]?.DeepClone(),
            ["note"] = requiredOnly ? "Only required fields shown; pass requiredFieldsOnly=false for all." : null,
        });
    }

    [McpServerTool(Name = "lookupJiraAccountId", ReadOnly = true)]
    [Description("Find Jira users by name or email to get their account IDs (for assignee, mentions, JQL).")]
    public static async Task<string> LookupJiraAccountId(
        AtlassianClient client,
        [Description("Name, partial name or email to search for")] string searchString,
        [Description("Ignored.")] bool? showAvatar = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        [Description("Ignored.")] string? actionSource = null,
        CancellationToken ct = default)
    {
        var users = await client.JiraGetAsync($"/rest/api/3/user/search{JiraShape.Query(("query", searchString), ("maxResults", 25))}", ct);
        return Json.Out(new JsonObject
        {
            ["users"] = new JsonArray((users as JsonArray ?? []).Select(u => (JsonNode?)new JsonObject
            {
                ["accountId"] = u?["accountId"]?.ToString(),
                ["displayName"] = u?["displayName"]?.ToString(),
                ["emailAddress"] = u?["emailAddress"]?.ToString(),
                ["active"] = u?["active"]?.DeepClone(),
                ["accountType"] = u?["accountType"]?.ToString(),
            }).ToArray()),
        });
    }

    // ---------------------------------------------------------------- links

    [McpServerTool(Name = "getIssueLinkTypes", ReadOnly = true)]
    [Description("List issue link types (e.g. Blocks, Duplicate, Clones, Relates) with their inward/outward phrasing. For createIssueLink: inwardIssue = blocker, outwardIssue = blocked (\"A is blocked by B\" -> inwardIssue: B, outwardIssue: A).")]
    public static async Task<string> GetIssueLinkTypes(
        AtlassianClient client,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var resp = await client.JiraGetAsync("/rest/api/3/issueLinkType", ct);
        return Json.Out(new JsonObject
        {
            ["issueLinkTypes"] = new JsonArray((resp?["issueLinkTypes"] as JsonArray ?? []).Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t?["id"]?.ToString(),
                ["name"] = t?["name"]?.ToString(),
                ["inward"] = t?["inward"]?.ToString(),
                ["outward"] = t?["outward"]?.ToString(),
            }).ToArray()),
        });
    }

    [McpServerTool(Name = "createIssueLink")]
    [Description("Link two Jira issues. For directional types like Blocks: inwardIssue = the issue that blocks, outwardIssue = the issue that is blocked (\"A is blocked by B\" -> inwardIssue: B, outwardIssue: A). Use getIssueLinkTypes if unsure of the type name.")]
    public static async Task<string> CreateIssueLink(
        AtlassianClient client,
        [Description("Link type name (e.g. Duplicate, Blocks, Clones, Relates)")] string type,
        [Description("Inward issue key (e.g. HSP-1)")] string inwardIssue,
        [Description("Outward issue key (e.g. MKY-1)")] string outwardIssue,
        [Description("Optional comment added to the outward issue.")] string? comment = null,
        [Description(ContentFormatDesc)] string? contentFormat = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["type"] = IsNumeric(type) ? new JsonObject { ["id"] = type } : new JsonObject { ["name"] = type },
            ["inwardIssue"] = new JsonObject { ["key"] = inwardIssue },
            ["outwardIssue"] = new JsonObject { ["key"] = outwardIssue },
        };
        if (Adf.FromInput(comment, contentFormat) is { } c)
            body["comment"] = new JsonObject { ["body"] = c };

        await client.JiraAsync(HttpMethod.Post, "/rest/api/3/issueLink", body.ToJsonString(), ct);
        return Json.Out(new JsonObject
        {
            ["linked"] = true,
            ["type"] = type,
            ["inwardIssue"] = inwardIssue,
            ["outwardIssue"] = outwardIssue,
        });
    }

    [McpServerTool(Name = "getJiraIssueRemoteIssueLinks", ReadOnly = true)]
    [Description("List remote links (web links, Confluence pages, etc.) attached to a Jira issue.")]
    public static async Task<string> GetJiraIssueRemoteIssueLinks(
        AtlassianClient client,
        [Description(IssueDesc)] string issueIdOrKey,
        [Description("Only return the link with this global ID.")] string? globalId = null,
        [Description(CloudIdDesc)] string? cloudId = null,
        CancellationToken ct = default)
    {
        var resp = await client.JiraGetAsync(
            $"/rest/api/3/issue/{Json.Q(issueIdOrKey)}/remotelink{JiraShape.Query(("globalId", globalId))}", ct);
        var links = resp as JsonArray ?? (resp is JsonObject single ? new JsonArray(single.DeepClone()) : []);
        return Json.Out(new JsonObject
        {
            ["remoteLinks"] = new JsonArray(links.Select(l => (JsonNode?)new JsonObject
            {
                ["id"] = l?["id"]?.DeepClone(),
                ["globalId"] = l?["globalId"]?.ToString(),
                ["relationship"] = l?["relationship"]?.ToString(),
                ["title"] = l?["object"]?["title"]?.ToString(),
                ["url"] = l?["object"]?["url"]?.ToString(),
                ["summary"] = l?["object"]?["summary"]?.ToString(),
                ["application"] = l?["application"]?["name"]?.ToString() ?? l?["application"]?["type"]?.ToString(),
            }).ToArray()),
        });
    }

    // ---------------------------------------------------------------- identity

    [McpServerTool(Name = "atlassianUserInfo", ReadOnly = true)]
    [Description("Get the account this server is authenticated as.")]
    public static async Task<string> AtlassianUserInfo(AtlassianClient client, CancellationToken ct = default)
    {
        var me = await client.JiraGetAsync("/rest/api/3/myself", ct);
        return Json.Out(new JsonObject
        {
            ["accountId"] = me?["accountId"]?.ToString(),
            ["displayName"] = me?["displayName"]?.ToString(),
            ["emailAddress"] = me?["emailAddress"]?.ToString(),
            ["timeZone"] = me?["timeZone"]?.ToString(),
            ["locale"] = me?["locale"]?.ToString(),
            ["site"] = client.Options.SiteUrl,
        });
    }

    [McpServerTool(Name = "getAccessibleAtlassianResources", ReadOnly = true)]
    [Description("Return the configured Atlassian site and its cloud ID. Not needed to use the other tools (cloudId is ignored), but kept so existing workflows that look it up still work.")]
    public static async Task<string> GetAccessibleAtlassianResources(AtlassianClient client, CancellationToken ct = default)
    {
        var site = client.Options.SiteUrl;
        var id = await client.GetCloudIdAsync(ct);
        return Json.Out(new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["url"] = site,
            ["name"] = Uri.TryCreate(site, UriKind.Absolute, out var u) ? u.Host.Split('.')[0] : site,
            ["scopes"] = new JsonArray("jira", "confluence"),
        }));
    }

    // ---------------------------------------------------------------- helpers

    static async Task<JsonNode> FetchIssueAsync(AtlassianClient client, string issueIdOrKey, string[]? fields, string? expand,
        string[]? properties, CancellationToken ct)
    {
        var query = JiraShape.Query(
            ("fields", string.Join(",", FieldList(fields))),
            ("expand", expand),
            ("properties", properties is { Length: > 0 } ? string.Join(",", properties) : null));
        return await client.JiraGetAsync($"/rest/api/3/issue/{Json.Q(issueIdOrKey)}{query}", ct)
            ?? throw new McpException($"Issue {issueIdOrKey} returned an empty response.");
    }

    static IEnumerable<string> FieldList(string[]? fields) =>
        fields is { Length: > 0 } ? fields.Where(f => !string.IsNullOrWhiteSpace(f)) : JiraShape.DefaultFields;

    static bool IsNumeric(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    static JsonObject? TransitionObject(JsonElement? element)
    {
        var node = Json.ToNode(element);
        return node switch
        {
            JsonObject o when o["id"] is not null => new JsonObject { ["id"] = o["id"]!.ToString() },
            JsonObject o when o["name"] is not null => new JsonObject { ["id"] = o["name"]!.ToString() },
            JsonValue v when v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('{') => JsonNode.Parse(s) is JsonObject p && p["id"] is not null
                ? new JsonObject { ["id"] = p["id"]!.ToString() }
                : null,
            JsonValue v => new JsonObject { ["id"] = v.ToString() },
            _ => null,
        };
    }

    static JsonNode? AllowedValues(JsonNode? values)
    {
        if (values is not JsonArray arr || arr.Count == 0)
            return null;
        var list = new JsonArray(arr.Take(50).Select(v => (JsonNode?)new JsonObject
        {
            ["id"] = v?["id"]?.ToString(),
            ["name"] = v?["name"]?.ToString() ?? v?["value"]?.ToString() ?? v?["key"]?.ToString(),
        }).ToArray());
        if (arr.Count > 50)
            list.Add($"... {arr.Count - 50} more");
        return list;
    }

    /// <summary>Jira wants "2026-03-09T09:00:00.000+0000" exactly; ISO "Z"/"+00:00" forms are rejected.</summary>
    static string JiraTimestamp(string? input)
    {
        var when = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(input))
        {
            var s = input.Trim();
            // Accept Jira's own "+0000" form, which DateTimeOffset doesn't parse.
            if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-') && s[^4..].All(char.IsAsciiDigit))
                s = s[..^2] + ":" + s[^2..];
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out when))
                throw new McpException($"started \"{input}\" is not a valid ISO 8601 date-time.");
        }
        return when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+0000'", CultureInfo.InvariantCulture);
    }

    // Rich-text system fields; custom textarea fields are discovered via /field.
    static readonly HashSet<string> RichTextSystemFields = new(StringComparer.Ordinal) { "description", "environment" };
    const string TextareaCustomType = "com.atlassian.jira.plugin.system.customfieldtypes:textarea";
    static readonly SemaphoreSlim RichTextLock = new(1, 1);
    static HashSet<string>? _richTextCustomFields;

    static async Task<HashSet<string>> RichTextCustomFieldsAsync(AtlassianClient client, CancellationToken ct)
    {
        if (_richTextCustomFields is { } cached)
            return cached;
        await RichTextLock.WaitAsync(ct);
        try
        {
            if (_richTextCustomFields is null)
            {
                var all = await client.JiraGetAsync("/rest/api/3/field", ct) as JsonArray ?? [];
                _richTextCustomFields = all
                    .Where(f => f?["schema"]?["custom"]?.ToString() == TextareaCustomType)
                    .Select(f => f!["id"]!.ToString())
                    .ToHashSet(StringComparer.Ordinal);
            }
            return _richTextCustomFields;
        }
        finally
        {
            RichTextLock.Release();
        }
    }

    /// <summary>
    /// Makes model-supplied field values acceptable to Jira v3: Markdown strings become ADF for rich-text
    /// fields, and bare strings are wrapped for the common object-valued system fields.
    /// </summary>
    static async Task<JsonObject> NormalizeFieldsAsync(AtlassianClient client, JsonObject input, string? contentFormat, CancellationToken ct)
    {
        HashSet<string>? richCustom = null;
        if (input.Any(kv => kv.Key.StartsWith("customfield_", StringComparison.Ordinal) && kv.Value is JsonValue))
            richCustom = await RichTextCustomFieldsAsync(client, ct);

        var output = new JsonObject();
        foreach (var (key, value) in input)
        {
            output[key] = value switch
            {
                null => null,
                _ when RichTextSystemFields.Contains(key) || richCustom?.Contains(key) == true => RichText(value, contentFormat),
                JsonValue v when v.TryGetValue<string>(out var s) => key switch
                {
                    "priority" or "resolution" or "issuetype" or "security" => new JsonObject { [IsNumeric(s) ? "id" : "name"] = s },
                    "assignee" or "reporter" => new JsonObject { ["accountId"] = s },
                    "parent" => new JsonObject { [IsNumeric(s) ? "id" : "key"] = s },
                    "labels" => new JsonArray(s),
                    "components" or "fixVersions" or "versions" => new JsonArray(new JsonObject { ["name"] = s }),
                    _ => value.DeepClone(),
                },
                JsonArray a when key is "components" or "fixVersions" or "versions" => new JsonArray(a.Select(x =>
                    x is JsonValue xv && xv.TryGetValue<string>(out var name)
                        ? (JsonNode)new JsonObject { ["name"] = name }
                        : x?.DeepClone()).ToArray()),
                _ => value.DeepClone(),
            };
        }
        return output;
    }

    static JsonNode? RichText(JsonNode value, string? contentFormat) => value switch
    {
        JsonObject o when o["type"]?.ToString() == "doc" => o.DeepClone(),
        JsonValue v when v.TryGetValue<string>(out var s) => Adf.FromInput(s, contentFormat),
        _ => value.DeepClone(),
    };

    /// <summary>Converts Markdown comment bodies inside an "update" block ({"comment":[{"add":{"body":"..."}}]}) to ADF.</summary>
    static JsonObject NormalizeUpdate(JsonObject update)
    {
        var copy = (JsonObject)update.DeepClone();
        if (copy["comment"] is JsonArray ops)
        {
            foreach (var op in ops)
            {
                if (op?["add"] is JsonObject add && add["body"] is JsonValue body && body.TryGetValue<string>(out var text))
                    add["body"] = Adf.FromInput(text, null);
            }
        }
        return copy;
    }
}
