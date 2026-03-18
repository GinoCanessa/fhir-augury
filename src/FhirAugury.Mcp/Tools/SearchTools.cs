using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Indexing;
using FhirAugury.Models;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class SearchTools
{
    [McpServerTool, Description("Search across all FHIR community sources (Zulip, Jira, Confluence, GitHub) using full-text search.")]
    public static string Search(
        DatabaseService db,
        [Description("Search query text")] string query,
        [Description("Comma-separated source filter: zulip,jira,confluence,github")] string? sources = null,
        [Description("Maximum results to return (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();

        IReadOnlySet<string>? sourceSet = null;
        if (!string.IsNullOrWhiteSpace(sources))
        {
            sourceSet = sources
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
        }

        var results = FtsSearchService.SearchAll(conn, query, sourceSet, limit);
        return FormatResults(results, query);
    }

    [McpServerTool, Description("Search Zulip chat messages.")]
    public static string SearchZulip(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific stream name")] string? stream = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = FtsSearchService.SearchZulipMessages(conn, query, limit, stream);
        FtsSearchService.NormalizeScores(results);
        return FormatResults(results, query);
    }

    [McpServerTool, Description("Search Jira issues.")]
    public static string SearchJira(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter by status (e.g., Open, Closed)")] string? status = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = FtsSearchService.SearchJiraIssues(conn, query, limit, status);
        FtsSearchService.NormalizeScores(results);
        return FormatResults(results, query);
    }

    [McpServerTool, Description("Search Confluence wiki pages.")]
    public static string SearchConfluence(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific Confluence space key")] string? space = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = FtsSearchService.SearchConfluencePages(conn, query, limit, space);
        FtsSearchService.NormalizeScores(results);
        return FormatResults(results, query);
    }

    [McpServerTool, Description("Search GitHub issues and pull requests.")]
    public static string SearchGithub(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific repository (e.g., HL7/fhir)")] string? repo = null,
        [Description("Filter by state: open, closed")] string? state = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = FtsSearchService.SearchGitHubIssues(conn, query, limit, repo, state);
        FtsSearchService.NormalizeScores(results);
        return FormatResults(results, query);
    }

    internal static string FormatResults(List<SearchResult> results, string query)
    {
        if (results.Count == 0)
            return $"No results found for \"{query}\".";

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results ({results.Count} matches)");
        sb.AppendLine();

        foreach (var r in results)
        {
            var score = r.NormalizedScore.HasValue ? $"{r.NormalizedScore:F2}" : $"{r.Score:F1}";
            sb.AppendLine($"### [{r.Source}] {r.Id} — {r.Title}");
            sb.AppendLine($"- **Score:** {score}");
            if (r.UpdatedAt.HasValue)
                sb.AppendLine($"- **Updated:** {r.UpdatedAt:yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(r.Url))
                sb.AppendLine($"- **URL:** {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.AppendLine($"- **Snippet:** {r.Snippet}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
