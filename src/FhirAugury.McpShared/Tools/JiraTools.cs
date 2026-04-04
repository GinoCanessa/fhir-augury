using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Text;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraTools
{
    [McpServerTool, Description("Search Jira issues using full-text search.")]
    public static async Task<string> SearchJira(
        IHttpClientFactory httpClientFactory,
        [Description("Search query")] string query,
        [Description("Filter by status (e.g., Open, Closed)")] string? status = null,
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("jira");
            StringBuilder url = new($"/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}");
            if (!string.IsNullOrEmpty(status))
                url.Append($"&status={Uri.EscapeDataString(status)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return UnifiedTools.FormatSearchResults(root, query);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get full details of a Jira issue by its key.")]
    public static async Task<string> GetJiraIssue(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key, e.g. FHIR-43499")] string key,
        [Description("Include comments")] bool includeComments = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/items/jira/{Uri.EscapeDataString(key)}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

            StringBuilder sb = new();
            string id = UnifiedTools.GetNullableString(root, "id") ?? UnifiedTools.GetNullableString(root, "key") ?? key;
            string title = UnifiedTools.GetString(root, "title");
            sb.AppendLine($"# {id}: {title}");
            sb.AppendLine();

            if (root.TryGetProperty("metadata", out JsonElement metaEl) && metaEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty kv in metaEl.EnumerateObject())
                    sb.AppendLine($"- **{FormatKey(kv.Name)}:** {kv.Value.GetString()}");
            }

            // Inline fields when metadata is absent
            AppendField(sb, root, "status", "Status");
            AppendField(sb, root, "type", "Type");
            AppendField(sb, root, "priority", "Priority");
            AppendField(sb, root, "workGroup", "Work Group");
            AppendField(sb, root, "specification", "Specification");
            AppendField(sb, root, "assignee", "Assignee");
            AppendField(sb, root, "reporter", "Reporter");

            AppendField(sb, root, "createdAt", "Created");
            AppendField(sb, root, "updatedAt", "Updated");

            string? content = UnifiedTools.GetNullableString(root, "content")
                              ?? UnifiedTools.GetNullableString(root, "description");
            if (!string.IsNullOrEmpty(content))
            {
                sb.AppendLine();
                sb.AppendLine("## Description");
                sb.AppendLine(content);
            }

            if (includeComments && root.TryGetProperty("comments", out JsonElement commentsEl)
                && commentsEl.ValueKind == JsonValueKind.Array && commentsEl.GetArrayLength() > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## Comments ({commentsEl.GetArrayLength()})");
                sb.AppendLine();
                foreach (JsonElement c in commentsEl.EnumerateArray())
                {
                    string author = UnifiedTools.GetString(c, "author");
                    string? date = UnifiedTools.GetNullableString(c, "createdAt");
                    sb.AppendLine($"### {author} — {date}");
                    sb.AppendLine(UnifiedTools.GetString(c, "body"));
                    sb.AppendLine();
                }
            }

            string? itemUrl = UnifiedTools.GetNullableString(root, "url");
            if (!string.IsNullOrEmpty(itemUrl))
                sb.AppendLine($"**URL:** {itemUrl}");

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get comments on a Jira issue.")]
    public static async Task<string> GetJiraComments(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        [Description("Maximum comments (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("jira");
            string url = $"/api/v1/items/{Uri.EscapeDataString(key)}/comments?limit={limit}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

            JsonElement comments = root.TryGetProperty("comments", out JsonElement cEl)
                ? cEl
                : root;

            if (comments.ValueKind != JsonValueKind.Array || comments.GetArrayLength() == 0)
                return $"No comments on {key}.";

            StringBuilder sb = new();
            sb.AppendLine($"## Comments on {key} ({comments.GetArrayLength()})");
            sb.AppendLine();

            int count = 0;
            foreach (JsonElement c in comments.EnumerateArray())
            {
                if (count >= limit) break;
                string author = UnifiedTools.GetString(c, "author");
                string? date = UnifiedTools.GetNullableString(c, "createdAt");
                sb.AppendLine($"### {author} — {date}");
                sb.AppendLine(UnifiedTools.GetString(c, "body"));
                sb.AppendLine();
                count++;
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Query Jira issues with structured filters (status, work group, specification, etc).")]
    public static async Task<string> QueryJiraIssues(
        IHttpClientFactory httpClientFactory,
        [Description("Filter by statuses (comma-separated)")] string? statuses = null,
        [Description("Filter by work groups (comma-separated)")] string? workGroups = null,
        [Description("Filter by specifications (comma-separated)")] string? specifications = null,
        [Description("Filter by types (comma-separated)")] string? types = null,
        [Description("Filter by priorities (comma-separated)")] string? priorities = null,
        [Description("Text query for additional filtering")] string? query = null,
        [Description("Sort by field (default updated_at)")] string sortBy = "updated_at",
        [Description("Sort order: asc or desc (default desc)")] string sortOrder = "desc",
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("jira");
            object body = new
            {
                statuses = ParseCsv(statuses),
                workGroups = ParseCsv(workGroups),
                specifications = ParseCsv(specifications),
                types = ParseCsv(types),
                priorities = ParseCsv(priorities),
                query = query ?? "",
                sortBy,
                sortOrder,
                limit,
            };

            JsonElement root = await UnifiedTools.PostJsonBodyAsync(client, "/api/v1/query", body, cancellationToken);

            JsonElement results = root.TryGetProperty("results", out JsonElement rEl) ? rEl : root;
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return "No Jira issues matched the query.";

            StringBuilder sb = new();
            sb.AppendLine("## Jira Query Results");
            sb.AppendLine($"({results.GetArrayLength()} results)");
            sb.AppendLine();

            foreach (JsonElement issue in results.EnumerateArray())
            {
                string issueKey = UnifiedTools.GetNullableString(issue, "key") ?? UnifiedTools.GetString(issue, "id");
                string issueStatus = UnifiedTools.GetString(issue, "status");
                string issueTitle = UnifiedTools.GetString(issue, "title");
                sb.Append($"- **{issueKey}** [{issueStatus}] {issueTitle}");

                string? wg = UnifiedTools.GetNullableString(issue, "workGroup");
                if (!string.IsNullOrEmpty(wg))
                    sb.Append($"  WG: {wg}");
                string? spec = UnifiedTools.GetNullableString(issue, "specification");
                if (!string.IsNullOrEmpty(spec))
                    sb.Append($"  Spec: {spec}");
                string? updated = UnifiedTools.GetNullableString(issue, "updatedAt");
                if (!string.IsNullOrEmpty(updated))
                    sb.Append($"  Updated: {updated}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Jira issue including metadata, description, comments, and cross-references.")]
    public static async Task<string> SnapshotJiraIssue(
        IHttpClientFactory httpClientFactory,
        [Description("Issue key")] string key,
        [Description("Include comments (default true)")] bool includeComments = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/items/jira/{Uri.EscapeDataString(key)}/snapshot";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return UnifiedTools.GetString(root, "markdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List Jira issues with optional filters and sorting.")]
    public static async Task<string> ListJiraIssues(
        IHttpClientFactory httpClientFactory,
        [Description("Sort by field (default updated_at)")] string sortBy = "updated_at",
        [Description("Sort order: asc or desc (default desc)")] string sortOrder = "desc",
        [Description("Maximum results (default 20)")] int limit = 20,
        [Description("Filter by status")] string? status = null,
        [Description("Filter by work group")] string? workGroup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("jira");
            StringBuilder url = new($"/api/v1/items?limit={limit}");
            if (!string.IsNullOrEmpty(status))
                url.Append($"&status={Uri.EscapeDataString(status)}");
            if (!string.IsNullOrEmpty(workGroup))
                url.Append($"&work_group={Uri.EscapeDataString(workGroup)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);

            JsonElement items = root.TryGetProperty("items", out JsonElement iEl) ? iEl : root;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                return "No Jira issues found.";

            StringBuilder sb = new();
            sb.AppendLine("## Jira Issues");
            sb.AppendLine();

            foreach (JsonElement item in items.EnumerateArray())
            {
                string id = UnifiedTools.GetNullableString(item, "key") ?? UnifiedTools.GetString(item, "id");
                sb.AppendLine($"- **{id}** {UnifiedTools.GetString(item, "title")}");
                string? updated = UnifiedTools.GetNullableString(item, "updatedAt");
                if (!string.IsNullOrEmpty(updated))
                    sb.AppendLine($"  Updated: {updated}");
                string? itemUrl = UnifiedTools.GetNullableString(item, "url");
                if (!string.IsNullOrEmpty(itemUrl))
                    sb.AppendLine($"  URL: {itemUrl}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static List<string> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static void AppendField(StringBuilder sb, JsonElement el, string prop, string label)
    {
        string? value = UnifiedTools.GetNullableString(el, prop);
        if (!string.IsNullOrEmpty(value))
            sb.AppendLine($"- **{label}:** {value}");
    }

    private static string FormatKey(string key) => FormatHelpers.FormatKey(key);
}
