namespace McpAtlassian.Services;

/// <summary>
/// Connection settings, read from environment variables:
///   ATLASSIAN_SITE        https://yourco.atlassian.net   (required)
///   ATLASSIAN_EMAIL       account email for the API token (required)
///   ATLASSIAN_API_TOKEN   token from id.atlassian.com/manage-profile/security/api-tokens (required)
///   ATLASSIAN_CLOUD_ID    optional; set this when using a *scoped* API token, which only works
///                         through the api.atlassian.com gateway rather than the site URL.
/// </summary>
public sealed class AtlassianOptions
{
    public string? Site { get; init; }
    public string? Email { get; init; }
    public string? ApiToken { get; init; }
    public string? CloudId { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Site) && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(ApiToken);

    public string SiteUrl => NormalizeSite(Site ?? "");

    public string JiraBase => string.IsNullOrWhiteSpace(CloudId)
        ? SiteUrl
        : $"https://api.atlassian.com/ex/jira/{CloudId}";

    public string ConfluenceBase => string.IsNullOrWhiteSpace(CloudId)
        ? SiteUrl
        : $"https://api.atlassian.com/ex/confluence/{CloudId}";

    public static AtlassianOptions FromEnvironment() => new()
    {
        Site = Env("ATLASSIAN_SITE") ?? Env("JIRA_URL") ?? Env("CONFLUENCE_URL"),
        Email = Env("ATLASSIAN_EMAIL") ?? Env("JIRA_USERNAME") ?? Env("CONFLUENCE_USERNAME"),
        ApiToken = Env("ATLASSIAN_API_TOKEN") ?? Env("JIRA_API_TOKEN") ?? Env("CONFLUENCE_API_TOKEN"),
        CloudId = Env("ATLASSIAN_CLOUD_ID"),
    };

    static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    static string NormalizeSite(string site)
    {
        site = site.Trim().TrimEnd('/');
        if (site.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase))
            site = site[..^5];
        if (!site.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !site.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            site = "https://" + site;
        return site;
    }
}
