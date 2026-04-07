using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Text;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraTools
{
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
        [Description("Filter by labels (comma-separated, exact match, AND logic)")] string? labels = null,
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
                labels = ParseCsv(labels),
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

    [McpServerTool, Description("List all available Jira labels with issue counts.")]
    public static async Task<string> ListJiraLabels(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("jira");
            JsonElement root = await UnifiedTools.GetJsonAsync(
                client, "/api/v1/labels", cancellationToken);

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return "No labels found.";
            }

            StringBuilder sb = new();
            sb.AppendLine("| Label | Issues |");
            sb.AppendLine("|-------|--------|");
            foreach (JsonElement item in root.EnumerateArray())
            {
                string name = UnifiedTools.GetString(item, "name");
                string count = item.TryGetProperty("issueCount", out JsonElement countEl)
                    ? countEl.ToString()
                    : "";
                sb.AppendLine($"| {name} | {count} |");
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
