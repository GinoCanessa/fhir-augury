using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing;

/// <summary>
/// Combines BM25 keyword similarity with cross-references to find related items.
/// </summary>
public static class SimilaritySearchService
{
    /// <summary>Score boost multiplier for items that have explicit cross-references.</summary>
    private const double XrefBoost = 2.0;

    /// <summary>
    /// Finds items related to the given seed item using BM25 keyword similarity and cross-references.
    /// </summary>
    /// <param name="connection">An open SQLite connection.</param>
    /// <param name="sourceType">The seed item's source type.</param>
    /// <param name="sourceId">The seed item's source identifier.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A list of related items sorted by combined score descending.</returns>
    public static List<SearchResult> FindRelated(
        SqliteConnection connection,
        string sourceType,
        string sourceId,
        int limit = 20)
    {
        // Step 1: Get top 10 keywords of the seed item (highest BM25 scores)
        var seedKeywords = GetTopKeywords(connection, sourceType, sourceId, 10);

        if (seedKeywords.Count == 0)
        {
            return [];
        }

        // Step 2: Find other items sharing those keywords, sum their BM25 scores
        var candidateScores = new Dictionary<(string Type, string Id), double>();
        foreach (var keyword in seedKeywords)
        {
            var matches = GetItemsWithKeyword(connection, keyword);
            foreach (var (type, id, score) in matches)
            {
                // Skip the seed item
                if (type == sourceType && id == sourceId) continue;

                var key = (type, id);
                if (candidateScores.TryGetValue(key, out var existing))
                {
                    candidateScores[key] = existing + score;
                }
                else
                {
                    candidateScores[key] = score;
                }
            }
        }

        // Step 3: Get explicit cross-references for the seed item
        var xrefItems = new HashSet<(string, string)>();
        var xrefLinks = CrossRefQueryService.GetRelatedItems(connection, sourceType, sourceId);
        foreach (var link in xrefLinks)
        {
            xrefItems.Add((link.TargetType, link.TargetId));
            xrefItems.Add((link.SourceType, link.SourceId));
        }
        xrefItems.Remove((sourceType, sourceId));

        // Step 4: Merge — cross-referenced items get a score boost
        foreach (var xref in xrefItems)
        {
            if (candidateScores.TryGetValue(xref, out var existing))
            {
                candidateScores[xref] = existing * XrefBoost;
            }
            else
            {
                // Add xref-only items with a base score
                candidateScores[xref] = XrefBoost;
            }
        }

        // Step 5: Deduplicate and sort by combined score
        var sorted = candidateScores
            .OrderByDescending(kvp => kvp.Value)
            .Take(limit)
            .ToList();

        // Step 6: Enrich results with titles from source tables
        var results = new List<SearchResult>();
        foreach (var (key, score) in sorted)
        {
            var title = GetItemTitle(connection, key.Type, key.Id);
            var relationship = xrefItems.Contains(key) ? "xref+keyword" : "keyword";

            results.Add(new SearchResult
            {
                Source = key.Type,
                Id = key.Id,
                Title = title ?? key.Id,
                Score = score,
                NormalizedScore = score,
                Snippet = relationship,
            });
        }

        return results;
    }

    private static List<string> GetTopKeywords(
        SqliteConnection connection, string sourceType, string sourceId, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Keyword FROM index_keywords
            WHERE SourceType = @type AND SourceId = @id
            ORDER BY Bm25Score DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@type", sourceType);
        cmd.Parameters.AddWithValue("@id", sourceId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var keywords = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keywords.Add(reader.GetString(0));
        }
        return keywords;
    }

    private static List<(string SourceType, string SourceId, double Score)> GetItemsWithKeyword(
        SqliteConnection connection, string keyword)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT SourceType, SourceId, Bm25Score
            FROM index_keywords
            WHERE Keyword = @keyword
            """;
        cmd.Parameters.AddWithValue("@keyword", keyword);

        var results = new List<(string, string, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
        }
        return results;
    }

    private static string? GetItemTitle(SqliteConnection connection, string sourceType, string sourceId)
    {
        switch (sourceType)
        {
            case "jira":
            {
                var issue = JiraIssueRecord.SelectSingle(connection, Key: sourceId);
                return issue?.Title;
            }
            case "jira-comment":
            {
                var sepIdx = sourceId.IndexOf(':');
                if (sepIdx < 0) return sourceId;
                var issueKey = sourceId[..sepIdx];
                var issue = JiraIssueRecord.SelectSingle(connection, Key: issueKey);
                return issue is not null ? $"Comment on {issueKey}: {issue.Title}" : sourceId;
            }
            case "zulip":
            {
                var sepIdx = sourceId.LastIndexOf(':');
                if (sepIdx <= 0) return sourceId;
                var stream = sourceId[..sepIdx];
                var topic = sourceId[(sepIdx + 1)..];
                return $"{stream} > {topic}";
            }
            case "confluence":
            {
                var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: sourceId);
                return page?.Title;
            }
            case "github":
            {
                var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: sourceId);
                return issue?.Title;
            }
            case "github-comment":
            {
                var sepIdx = sourceId.IndexOf(':');
                if (sepIdx < 0) return sourceId;
                var issueRef = sourceId[..sepIdx];
                return $"Comment on {issueRef}";
            }
            default:
                return sourceId;
        }
    }
}
