using System.ComponentModel;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Text;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class JiraTools
{
    [McpServerTool, Description("Search Jira issues using full-text search.")]
    public static async Task<string> SearchJira(
        [FromKeyedServices("jira")] SourceService.SourceServiceClient jiraSource,
        [Description("Search query")] string query,
        [Description("Filter by status (e.g., Open, Closed)")] string? status = null,
        [Description("Maximum results (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new SearchRequest { Query = query, Limit = limit };
            if (!string.IsNullOrEmpty(status))
                request.Filters.Add("status", status);

            var response = await jiraSource.SearchAsync(request, cancellationToken: cancellationToken);
            return UnifiedTools.FormatSearchResults(response, query);
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get full details of a Jira issue by its key.")]
    public static async Task<string> GetJiraIssue(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Issue key, e.g. FHIR-43499")] string key,
        [Description("Include comments")] bool includeComments = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await orchestrator.GetItemAsync(
                new GetItemRequest { Id = key, IncludeContent = true, IncludeComments = includeComments },
                cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine($"# {response.Id}: {response.Title}");
            sb.AppendLine();

            foreach (var (k, v) in response.Metadata)
                sb.AppendLine($"- **{FormatKey(k)}:** {v}");

            if (response.CreatedAt is not null)
                sb.AppendLine($"- **Created:** {response.CreatedAt.ToDateTimeOffset():yyyy-MM-dd}");
            if (response.UpdatedAt is not null)
                sb.AppendLine($"- **Updated:** {response.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd}");

            if (!string.IsNullOrEmpty(response.Content))
            {
                sb.AppendLine();
                sb.AppendLine("## Description");
                sb.AppendLine(response.Content);
            }

            if (response.Comments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## Comments ({response.Comments.Count})");
                sb.AppendLine();
                foreach (var c in response.Comments)
                {
                    sb.AppendLine($"### {c.Author} — {c.CreatedAt?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrEmpty(response.Url))
                sb.AppendLine($"**URL:** {response.Url}");

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get comments on a Jira issue.")]
    public static async Task<string> GetJiraComments(
        JiraService.JiraServiceClient jira,
        [Description("Issue key")] string key,
        [Description("Maximum comments (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var call = jira.GetIssueComments(new JiraGetCommentsRequest { IssueKey = key },
                cancellationToken: cancellationToken);

            var comments = new List<JiraComment>();
            await foreach (var comment in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                comments.Add(comment);
                if (comments.Count >= limit) break;
            }

            if (comments.Count == 0)
                return $"No comments on {key}.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Comments on {key} ({comments.Count})");
            sb.AppendLine();

            foreach (var c in comments)
            {
                sb.AppendLine($"### {c.Author} — {c.CreatedAt?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                sb.AppendLine(c.Body);
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Query Jira issues with structured filters (status, work group, specification, etc).")]
    public static async Task<string> QueryJiraIssues(
        JiraService.JiraServiceClient jira,
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
            var request = new JiraQueryRequest
            {
                Query = query ?? "",
                SortBy = sortBy,
                SortOrder = sortOrder,
                Limit = limit,
            };

            AddItems(request.Statuses, statuses);
            AddItems(request.WorkGroups, workGroups);
            AddItems(request.Specifications, specifications);
            AddItems(request.Types_, types);
            AddItems(request.Priorities, priorities);

            using var call = jira.QueryIssues(request, cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine("## Jira Query Results");
            sb.AppendLine();

            var count = 0;
            await foreach (var issue in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                sb.AppendLine($"- **{issue.Key}** [{issue.Status}] {issue.Title}");
                if (!string.IsNullOrEmpty(issue.WorkGroup))
                    sb.Append($"  WG: {issue.WorkGroup}");
                if (!string.IsNullOrEmpty(issue.Specification))
                    sb.Append($"  Spec: {issue.Specification}");
                if (issue.UpdatedAt is not null)
                    sb.Append($"  Updated: {issue.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd}");
                sb.AppendLine();
                count++;
            }

            if (count == 0)
                return "No Jira issues matched the query.";

            sb.Insert(sb.ToString().IndexOf('\n') + 1, $"({count} results)\n\n");
            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Jira issue including metadata, description, comments, and cross-references.")]
    public static async Task<string> SnapshotJiraIssue(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Issue key")] string key,
        [Description("Include comments (default true)")] bool includeComments = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await orchestrator.GetSnapshotAsync(
                new GetSnapshotRequest { Id = key, IncludeComments = includeComments, IncludeInternalRefs = true },
                cancellationToken: cancellationToken);
            return response.Markdown;
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List Jira issues with optional filters and sorting.")]
    public static async Task<string> ListJiraIssues(
        [FromKeyedServices("jira")] SourceService.SourceServiceClient jiraSource,
        [Description("Sort by field (default updated_at)")] string sortBy = "updated_at",
        [Description("Sort order: asc or desc (default desc)")] string sortOrder = "desc",
        [Description("Maximum results (default 20)")] int limit = 20,
        [Description("Filter by status")] string? status = null,
        [Description("Filter by work group")] string? workGroup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ListItemsRequest
            {
                Limit = limit,
                SortBy = sortBy,
                SortOrder = sortOrder,
            };
            if (!string.IsNullOrEmpty(status))
                request.Filters.Add("status", status);
            if (!string.IsNullOrEmpty(workGroup))
                request.Filters.Add("work_group", workGroup);

            using var call = jiraSource.ListItems(request, cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine("## Jira Issues");
            sb.AppendLine();

            var count = 0;
            await foreach (var item in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                sb.AppendLine($"- **{item.Id}** {item.Title}");
                if (item.UpdatedAt is not null)
                    sb.AppendLine($"  Updated: {item.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd}");
                if (!string.IsNullOrEmpty(item.Url))
                    sb.AppendLine($"  URL: {item.Url}");
                count++;
            }

            if (count == 0)
                return "No Jira issues found.";

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static void AddItems(Google.Protobuf.Collections.RepeatedField<string> field, string? csv) =>
        CsvParser.AddItemsToRepeatedField(field, csv);

    private static string FormatKey(string key) => FormatHelpers.FormatKey(key);
}
