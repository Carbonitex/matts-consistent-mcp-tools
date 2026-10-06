using System.Text;
using System.Text.Json.Nodes;
using McpAtlassian.Formatting;

namespace McpAtlassian.Tools;

/// <summary>Turns raw Jira REST payloads into compact, model-friendly JSON.</summary>
internal static class JiraShape
{
    public static readonly string[] DefaultFields =
    [
        "summary", "description", "status", "issuetype", "priority", "labels", "components", "assignee",
        "reporter", "created", "updated", "resolution", "project", "parent", "subtasks", "issuelinks",
    ];

    public static string IssueUrl(string site, string key) => $"{site}/browse/{key}";

    public static JsonObject Issue(JsonNode issue, string site, string? fmt)
    {
        var key = issue["key"]?.ToString() ?? "";
        var result = new JsonObject
        {
            ["key"] = key,
            ["id"] = issue["id"]?.ToString(),
            ["url"] = IssueUrl(site, key),
        };

        var fields = new JsonObject();
        if (issue["fields"] is JsonObject raw)
        {
            foreach (var (name, value) in raw)
            {
                // "*all" pulls in dozens of empty custom fields; drop them to keep responses readable.
                if (value is null && name.StartsWith("customfield_", StringComparison.Ordinal))
                    continue;
                fields[name] = Field(name, value, fmt);
            }
        }
        result["fields"] = fields;

        if (issue["names"] is JsonObject names)
            result["names"] = names.DeepClone();
        if (issue["renderedFields"] is JsonObject rendered)
            result["renderedFields"] = rendered.DeepClone();
        if (issue["properties"] is JsonObject props && props.Count > 0)
            result["properties"] = props.DeepClone();
        if (issue["changelog"] is JsonObject changelog)
            result["changelog"] = changelog.DeepClone();
        return result;
    }

    static JsonNode? Field(string name, JsonNode? value, string? fmt)
    {
        if (value is null)
            return null;

        switch (name)
        {
            case "status":
                return value["name"]?.ToString();
            case "issuetype":
            case "priority":
            case "resolution":
                return value["name"]?.ToString();
            case "project":
                return new JsonObject { ["key"] = value["key"]?.ToString(), ["name"] = value["name"]?.ToString() };
            case "components":
            case "fixVersions":
            case "versions":
                return Names(value);
            case "parent":
                return IssueRef(value);
            case "subtasks":
                return value is JsonArray subs ? new JsonArray(subs.Select(IssueRef).ToArray()) : null;
            case "issuelinks":
                return value is JsonArray links ? new JsonArray(links.Select(IssueLink).ToArray()) : null;
            case "comment":
                return Comments(value, fmt);
            case "attachment":
                return value is JsonArray atts
                    ? new JsonArray(atts.Select(a => (JsonNode?)new JsonObject
                    {
                        ["id"] = a?["id"]?.ToString(),
                        ["filename"] = a?["filename"]?.ToString(),
                        ["mimeType"] = a?["mimeType"]?.ToString(),
                        ["size"] = a?["size"]?.DeepClone(),
                        ["created"] = a?["created"]?.ToString(),
                        ["author"] = User(a?["author"]),
                        ["content"] = a?["content"]?.ToString(),
                    }).ToArray())
                    : null;
        }

        return Generic(value, fmt);
    }

    static JsonNode? Generic(JsonNode? value, string? fmt)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject o when o["type"]?.ToString() == "doc":
                return Adf.ForOutput(o.DeepClone(), fmt);
            case JsonObject o when o["accountId"] is not null:
                return User(o);
            case JsonObject o when o["value"] is not null && o["self"] is not null:
                // Select-list option: {self, value, id[, child]}
                return o["child"] is JsonObject child
                    ? new JsonObject { ["value"] = o["value"]!.ToString(), ["child"] = child["value"]?.ToString() }
                    : o["value"]!.ToString();
            case JsonArray a:
                return new JsonArray(a.Select(x => Generic(x, fmt)).ToArray());
            default:
                return value.DeepClone();
        }
    }

    public static JsonNode? User(JsonNode? user) => user is null
        ? null
        : new JsonObject { ["displayName"] = user["displayName"]?.ToString(), ["accountId"] = user["accountId"]?.ToString() };

    static JsonNode? Names(JsonNode? value) => value is JsonArray a
        ? new JsonArray(a.Select(x => (JsonNode?)JsonValue.Create(x?["name"]?.ToString())).ToArray())
        : null;

    static JsonNode? IssueRef(JsonNode? issue) => issue is null
        ? null
        : new JsonObject
        {
            ["key"] = issue["key"]?.ToString(),
            ["summary"] = issue["fields"]?["summary"]?.ToString(),
            ["status"] = issue["fields"]?["status"]?["name"]?.ToString(),
            ["issuetype"] = issue["fields"]?["issuetype"]?["name"]?.ToString(),
        };

    static JsonNode? IssueLink(JsonNode? link)
    {
        if (link is null)
            return null;
        var outward = link["outwardIssue"];
        var other = outward ?? link["inwardIssue"];
        return new JsonObject
        {
            ["id"] = link["id"]?.ToString(),
            // Reads as "<this issue> <relationship> <key>", e.g. "blocks ABC-2" or "is blocked by ABC-3".
            ["relationship"] = outward is not null ? link["type"]?["outward"]?.ToString() : link["type"]?["inward"]?.ToString(),
            ["type"] = link["type"]?["name"]?.ToString(),
            ["direction"] = outward is not null ? "outward" : "inward",
            ["key"] = other?["key"]?.ToString(),
            ["summary"] = other?["fields"]?["summary"]?.ToString(),
            ["status"] = other?["fields"]?["status"]?["name"]?.ToString(),
        };
    }

    public static JsonNode Comments(JsonNode page, string? fmt) => new JsonObject
    {
        ["total"] = page["total"]?.DeepClone(),
        ["startAt"] = page["startAt"]?.DeepClone(),
        ["comments"] = page["comments"] is JsonArray cs
            ? new JsonArray(cs.Select(c => (JsonNode?)Comment(c, fmt)).ToArray())
            : new JsonArray(),
    };

    public static JsonObject Comment(JsonNode? c, string? fmt)
    {
        var o = new JsonObject
        {
            ["id"] = c?["id"]?.ToString(),
            ["author"] = User(c?["author"]),
            ["created"] = c?["created"]?.ToString(),
            ["updated"] = c?["updated"]?.ToString(),
            ["body"] = c?["body"] is JsonNode body ? Adf.ForOutput(body.DeepClone(), fmt) : null,
        };
        if (c?["visibility"] is JsonObject vis)
            o["visibility"] = vis.DeepClone();
        return o;
    }

    public static string Query(params (string Name, object? Value)[] parameters)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in parameters)
        {
            var s = value switch
            {
                null => null,
                bool b => b ? "true" : "false",
                string str when str.Length == 0 => null,
                _ => value.ToString(),
            };
            if (s is null)
                continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(s));
        }
        return sb.ToString();
    }
}
