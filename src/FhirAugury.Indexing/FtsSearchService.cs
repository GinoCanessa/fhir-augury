using FhirAugury.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing;

/// <summary>
/// Provides full-text search capabilities over FTS5-indexed tables.
/// </summary>
public static class FtsSearchService
{
    /// <summary>
    /// Sanitizes a search query for use with FTS5 by escaping special characters
    /// and wrapping each term in double quotes.
    /// </summary>
    /// <param name="query">The raw search query.</param>
    /// <returns>A sanitized FTS5 query string.</returns>
    public static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sanitized = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"");

        return string.Join(" ", sanitized);
    }

    /// <summary>
    /// Searches the jira_issues_fts table for matching issues.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="query">The search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="statusFilter">Optional filter to restrict results to a specific issue status.</param>
    /// <returns>A list of search results from matching Jira issues.</returns>
    public static List<SearchResult> SearchJiraIssues(
        SqliteConnection connection,
        string query,
        int limit = 20,
        string? statusFilter = null)
    {
        var ftsQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
        {
            return [];
        }

        var sql = """
            SELECT ji.Key, ji.Title,
                   snippet(jira_issues_fts, 2, '<b>', '</b>', '...', 20) as Snippet,
                   jira_issues_fts.rank,
                   ji.Status, ji.UpdatedAt
            FROM jira_issues_fts
            JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
            WHERE jira_issues_fts MATCH @query
            """;

        if (!string.IsNullOrEmpty(statusFilter))
        {
            sql += " AND ji.Status = @status";
        }

        sql += " ORDER BY jira_issues_fts.rank LIMIT @limit";

        using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@query", ftsQuery);
        command.Parameters.AddWithValue("@limit", limit);

        if (!string.IsNullOrEmpty(statusFilter))
        {
            command.Parameters.AddWithValue("@status", statusFilter);
        }

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new SearchResult
            {
                Source = "jira",
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                UpdatedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return results;
    }

    /// <summary>
    /// Searches the jira_comments_fts table for matching comments.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="query">The search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A list of search results from matching Jira comments.</returns>
    public static List<SearchResult> SearchJiraComments(
        SqliteConnection connection,
        string query,
        int limit = 20)
    {
        var ftsQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
        {
            return [];
        }

        const string sql = """
            SELECT jc.IssueKey, ji.Title,
                   snippet(jira_comments_fts, 2, '<b>', '</b>', '...', 20) as Snippet,
                   jira_comments_fts.rank,
                   jc.CreatedAt
            FROM jira_comments_fts
            JOIN jira_comments jc ON jc.Id = jira_comments_fts.rowid
            JOIN jira_issues ji ON ji.Key = jc.IssueKey
            WHERE jira_comments_fts MATCH @query
            ORDER BY jira_comments_fts.rank
            LIMIT @limit
            """;

        using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@query", ftsQuery);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new SearchResult
            {
                Source = "jira-comment",
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                UpdatedAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            });
        }

        return results;
    }

    /// <summary>
    /// Searches the zulip_messages_fts table for matching messages.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="query">The search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="streamFilter">Optional filter to restrict results to a specific stream.</param>
    /// <returns>A list of search results from matching Zulip messages.</returns>
    public static List<SearchResult> SearchZulipMessages(
        SqliteConnection connection,
        string query,
        int limit = 20,
        string? streamFilter = null)
    {
        var ftsQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
        {
            return [];
        }

        var sql = """
            SELECT zm.StreamName, zm.Topic,
                   snippet(zulip_messages_fts, 3, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank,
                   zm.SenderName, zm.Timestamp, zm.Id
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            WHERE zulip_messages_fts MATCH @query
            """;

        if (!string.IsNullOrEmpty(streamFilter))
        {
            sql += " AND zm.StreamName = @stream";
        }

        sql += " ORDER BY zulip_messages_fts.rank LIMIT @limit";

        using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@query", ftsQuery);
        command.Parameters.AddWithValue("@limit", limit);

        if (!string.IsNullOrEmpty(streamFilter))
        {
            command.Parameters.AddWithValue("@stream", streamFilter);
        }

        var results = new List<SearchResult>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var streamName = reader.GetString(0);
            var topic = reader.GetString(1);

            results.Add(new SearchResult
            {
                Source = "zulip",
                Id = $"{streamName}:{topic}",
                Title = $"{streamName} > {topic}",
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = $"https://chat.fhir.org/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
                UpdatedAt = reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return results;
    }

    /// <summary>
    /// Searches all available FTS tables and returns normalized, sorted results.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="query">The search query text.</param>
    /// <param name="sources">Optional set of source names to search. When null, all sources are searched.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A combined and normalized list of search results sorted by score descending.</returns>
    public static List<SearchResult> SearchAll(
        SqliteConnection connection,
        string query,
        IReadOnlySet<string>? sources = null,
        int limit = 20)
    {
        var results = new List<SearchResult>();

        if (sources is null || sources.Contains("jira"))
        {
            results.AddRange(SearchJiraIssues(connection, query, limit));
        }

        if (sources is null || sources.Contains("jira-comment"))
        {
            results.AddRange(SearchJiraComments(connection, query, limit));
        }

        if (sources is null || sources.Contains("zulip"))
        {
            results.AddRange(SearchZulipMessages(connection, query, limit));
        }

        NormalizeScores(results);

        return results
            .OrderByDescending(r => r.NormalizedScore)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Applies min-max normalization to scores within each source group.
    /// When only one result exists for a source, its normalized score is set to 1.0.
    /// </summary>
    /// <param name="results">The list of search results to normalize in place.</param>
    public static void NormalizeScores(List<SearchResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        var sourceGroups = results
            .Select((result, index) => (Result: result, Index: index))
            .GroupBy(x => x.Result.Source);

        foreach (var group in sourceGroups)
        {
            var items = group.ToList();
            var min = items.Min(x => x.Result.Score);
            var max = items.Max(x => x.Result.Score);
            var range = max - min;

            foreach (var (result, index) in items)
            {
                var normalized = range == 0 ? 1.0 : (result.Score - min) / range;
                results[index] = result with { NormalizedScore = normalized };
            }
        }
    }
}
