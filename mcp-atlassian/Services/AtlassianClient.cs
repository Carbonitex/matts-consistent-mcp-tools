using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace McpAtlassian.Services;

/// <summary>Thrown for any non-success Atlassian response. Derives from McpException so the
/// message is surfaced to the model as a tool error instead of a generic failure.</summary>
public sealed class AtlassianApiException(HttpStatusCode status, string message) : McpException(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Thin REST wrapper over Jira Cloud (REST v3) and Confluence Cloud (REST v2, with v1 for CQL search).
/// Paths are passed as they appear in Atlassian's docs, e.g. "/rest/api/3/issue/ABC-1" or
/// "/wiki/api/v2/pages/123". Absolute URLs (e.g. Confluence "_links.next") are also accepted.
/// </summary>
public sealed class AtlassianClient
{
    const int MaxRetries = 3;

    readonly HttpClient _http;

    public AtlassianOptions Options { get; }

    public AtlassianClient(AtlassianOptions options)
    {
        Options = options;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-atlassian/1.0");
        if (options.IsConfigured)
        {
            var raw = Encoding.UTF8.GetBytes($"{options.Email}:{options.ApiToken}");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }
    }

    public Task<JsonNode?> JiraAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        SendAsync(method, Combine(Options.JiraBase, path), body, ct);

    public Task<JsonNode?> ConfluenceAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        SendAsync(method, Combine(Options.ConfluenceBase, path), body, ct);

    public Task<JsonNode?> JiraGetAsync(string path, CancellationToken ct = default) => JiraAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonNode?> ConfluenceGetAsync(string path, CancellationToken ct = default) => ConfluenceAsync(HttpMethod.Get, path, null, ct);

    /// <summary>Unauthenticated-safe lookup of the site's cloud ID (used for gateway URLs and resource listing).</summary>
    public async Task<string?> GetCloudIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(Options.CloudId))
            return Options.CloudId;
        var node = await SendAsync(HttpMethod.Get, Options.SiteUrl + "/_edge/tenant_info", null, ct);
        return node?["cloudId"]?.GetValue<string>();
    }

    public async Task<JsonNode?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        EnsureConfigured();

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                var json = body as string ?? JsonSerializer.Serialize(body, Json.Options);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            // Required by Jira for some mutating endpoints that would otherwise be treated as XSRF.
            req.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode == 503) && attempt < MaxRetries)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new AtlassianApiException(resp.StatusCode, DescribeError(method, url, resp.StatusCode, text));

            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }
    }

    void EnsureConfigured()
    {
        if (!Options.IsConfigured)
            throw new McpException(
                "Atlassian is not configured. Set ATLASSIAN_SITE (https://yourco.atlassian.net), ATLASSIAN_EMAIL and " +
                "ATLASSIAN_API_TOKEN in the MCP server's env (create a token at https://id.atlassian.com/manage-profile/security/api-tokens).");
    }

    static string Combine(string baseUrl, string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    static string DescribeError(HttpMethod method, string url, HttpStatusCode status, string body)
    {
        var detail = ExtractErrorDetail(body);
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " (check ATLASSIAN_EMAIL / ATLASSIAN_API_TOKEN; scoped tokens also need ATLASSIAN_CLOUD_ID)",
            HttpStatusCode.Forbidden => " (the account lacks permission for this resource)",
            // Atlassian treats bad Basic credentials as anonymous, so a wrong token usually shows up as 404, not 401.
            HttpStatusCode.NotFound => " (not found, not visible to this account, or the credentials are invalid)",
            _ => "",
        };
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;
        return $"Atlassian API {(int)status} {status}{hint} on {method} {path}: {detail}";
    }

    /// <summary>Flattens the various Atlassian error shapes (Jira errorMessages/errors, Confluence errors[]/message) to one line.</summary>
    internal static string ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty response)";
        try
        {
            var node = JsonNode.Parse(body);
            var parts = new List<string>();
            if (node?["errorMessages"] is JsonArray msgs)
                parts.AddRange(msgs.Select(m => m?.ToString() ?? "").Where(s => s.Length > 0));
            if (node?["errors"] is JsonObject errs)
                parts.AddRange(errs.Select(kv => $"{kv.Key}: {kv.Value}"));
            if (node?["errors"] is JsonArray errArr)
                parts.AddRange(errArr.Select(e => e?["title"]?.ToString() ?? e?["detail"]?.ToString() ?? e?.ToJsonString() ?? ""));
            if (node?["message"] is JsonNode msg)
                parts.Add(msg.ToString());
            if (parts.Count > 0)
                return string.Join("; ", parts);
        }
        catch (JsonException)
        {
        }
        // Some endpoints (notably Confluence v1 on 401) return a full HTML page; don't dump it on the model.
        if (body.TrimStart().StartsWith('<'))
            return "(HTML error page returned)";
        return body.Length > 1000 ? body[..1000] + "…" : body;
    }
}
