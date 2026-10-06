# mcp-atlassian

Use this if you want a consistent experience with Jira that doesn't change anytime they wanna push some sort of new dogshit experience.

## Setup

### 1. Create an API token

Go to <https://id.atlassian.com/manage-profile/security/api-tokens> and choose **Create API token**. Use the classic (unscoped) kind; it works directly against your site URL.

If you use a **scoped** token instead, also set `ATLASSIAN_CLOUD_ID` (see below), because scoped tokens only work through the `api.atlassian.com` gateway.

### 2. Build

Requires the .NET 10 SDK.

```sh
cd mcp-atlassian
dotnet publish -c Release -o ~/.local/share/mcp-atlassian
```

### 3. Register it with Claude Code

```sh
claude mcp add atlassian --scope user \
  -e ATLASSIAN_SITE=https://yourco.atlassian.net \
  -e ATLASSIAN_EMAIL=you@yourco.com \
  -e ATLASSIAN_API_TOKEN=your-token \
  -- ~/.local/share/mcp-atlassian/mcp-atlassian
```

Then turn off the claude.ai Atlassian connector in `/mcp` so the two don't fight over the same tool names.

### Environment variables

| Variable | Required | Notes |
|---|---|---|
| `ATLASSIAN_SITE` | yes | `https://yourco.atlassian.net` |
| `ATLASSIAN_EMAIL` | yes | The account the token belongs to |
| `ATLASSIAN_API_TOKEN` | yes | API token from step 1 |
| `ATLASSIAN_CLOUD_ID` | no | Only needed for scoped tokens. Find it at `https://yourco.atlassian.net/_edge/tenant_info` |

`JIRA_URL` / `JIRA_USERNAME` / `JIRA_API_TOKEN` (and the `CONFLUENCE_*` equivalents) are accepted as fallbacks.

## How it behaves

- Tool names and parameters match the old Atlassian MCP, so existing prompts and skills keep working. `cloudId` is still accepted but ignored, because the site comes from your env.
- It calls the Jira Cloud REST v3 and Confluence Cloud REST v2 APIs directly. CQL search uses Confluence v1, since v2 doesn't have search.
- Bodies default to **Markdown** in both directions. It converts to and from Atlassian Document Format for you. Pass `contentFormat: "adf"` for raw ADF, or `"html"` on Confluence for storage-format XHTML.
- 429 and 503 responses are retried, honoring `Retry-After`.
- Wrong credentials usually come back as **404**, not 401, because Atlassian quietly treats a bad token as an anonymous user. If everything says "not found", check your token. `atlassianUserInfo` is the quickest test.

## Tools

**Jira:** `getJiraIssue`, `searchJiraIssuesUsingJql`, `createJiraIssue`, `editJiraIssue`, `assignJiraIssue`, `addCommentToJiraIssue`, `getJiraIssueComments`, `addWorklogToJiraIssue`, `getTransitionsForJiraIssue`, `transitionJiraIssue` (also accepts a transition or status name such as "Done"), `getVisibleJiraProjects`, `getJiraProjectIssueTypesMetadata`, `getJiraIssueTypeMetaWithFields`, `lookupJiraAccountId`, `getIssueLinkTypes`, `createIssueLink`, `getJiraIssueRemoteIssueLinks`

**Confluence:** `getConfluencePage`, `getConfluencePageByTitle`, `getConfluenceSpaces`, `getPagesInConfluenceSpace`, `getConfluencePageChildren`, `getConfluencePageDescendants`, `createConfluencePage`, `updateConfluencePage`, `searchConfluenceUsingCql`, `getConfluencePageFooterComments`, `getConfluencePageInlineComments`, `getConfluenceCommentChildren`, `createConfluenceFooterComment`, `createConfluenceInlineComment`

**Account:** `atlassianUserInfo`, `getAccessibleAtlassianResources`

Not included on purpose: Rovo `search`/`fetch`, Teamwork Graph and Compass.

## Development

```sh
cd mcp-atlassian/McpAtlassian.Tests && dotnet test   # Markdown <-> ADF converter tests
```

## Known limitations

- Images in Markdown become links. Embedding needs an attachment upload first.
- `@Name` in Markdown stays plain text. A real mention needs an account ID, which you can find with `lookupJiraAccountId`.
- Underline, colours, merged cells and panel colours are lost when content is read back as Markdown. Use `responseContentFormat: "adf"` when you need the exact content.
- On round-trip, expand blocks and decision items flatten to plain text, and task items get new IDs.
