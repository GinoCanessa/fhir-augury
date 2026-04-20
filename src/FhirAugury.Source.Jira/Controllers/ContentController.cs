using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class ContentController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("content/refers-to")]
    public IActionResult RefersTo([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> hits = CollectRefersTo(connection, options, value, sourceType, maxResults);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "refers-to",
            Total = hits.Count,
            Hits = hits,
        });
    }

    [HttpGet("content/referred-by")]
    public IActionResult ReferredBy([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> hits = CollectReferredBy(connection, options, value, sourceType, maxResults);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "referred-by",
            Total = hits.Count,
            Hits = hits,
        });
    }

    [HttpGet("content/cross-referenced")]
    public IActionResult CrossReferenced([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> refersTo = CollectRefersTo(connection, options, value, sourceType, maxResults);
        List<CrossReferenceHit> referredBy = CollectReferredBy(connection, options, value, sourceType, maxResults);

        HashSet<string> seen = [];
        List<CrossReferenceHit> combined = [];
        foreach (CrossReferenceHit hit in refersTo.Concat(referredBy))
        {
            string dedupeKey = $"{hit.SourceType}:{hit.SourceId}:{hit.TargetType}:{hit.TargetId}";
            if (!seen.Add(dedupeKey))
                continue;
            combined.Add(hit);
            if (combined.Count >= maxResults)
                break;
        }

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "cross-referenced",
            Total = combined.Count,
            Hits = combined,
        });
    }

    [HttpGet("content/search")]
    public IActionResult Search([FromQuery] string? values, [FromQuery] string? sources, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(values))
            return BadRequest(new { error = "Query parameter 'values' is required" });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<string> valueList = [.. values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        List<string>? sourceList = sources is not null
            ? [.. sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        // If sources filter is specified and doesn't include "jira", return empty
        if (sourceList is not null &&
            !sourceList.Any(s => string.Equals(s, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase)))
        {
            return Ok(new ContentSearchResponse
            {
                Values = valueList,
                Total = 0,
                Hits = [],
            });
        }

        List<ContentSearchHit> allHits = [];
        foreach (string searchValue in valueList)
        {
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(searchValue);
            if (string.IsNullOrEmpty(ftsQuery))
                continue;

            int remaining = maxResults - allHits.Count;
            if (remaining <= 0)
                break;

            string sql = """
                SELECT ji.Key, ji.Title,
                       snippet(jira_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                       jira_issues_fts.rank * COALESCE(jp.BaselineValue, 5) / 5.0 as ScaledRank,
                       ji.Status, ji.UpdatedAt,
                       COALESCE(jp.BaselineValue, 5) as BaselineValue
                FROM jira_issues_fts
                JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
                LEFT JOIN jira_projects jp ON jp.Key = ji.ProjectKey
                WHERE jira_issues_fts MATCH @query
                  AND COALESCE(jp.BaselineValue, 5) > 0
                ORDER BY ScaledRank
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", remaining);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                allHits.Add(new ContentSearchHit
                {
                    Source = SourceSystems.Jira,
                    ContentType = "issue",
                    Id = key,
                    Title = reader.GetString(1),
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Score = -reader.GetDouble(3),
                    Url = $"{options.BaseUrl}/browse/{key}",
                    UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 5),
                    Metadata = new Dictionary<string, string>
                    {
                        ["status"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    },
                    MatchedValue = searchValue,
                });
            }
        }

        return Ok(new ContentSearchResponse
        {
            Values = valueList,
            Total = allHits.Count,
            Hits = allHits,
        });
    }

    [HttpGet("content/item/{source}/{*id}")]
    public IActionResult GetItem(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] bool? includeContent,
        [FromQuery] bool? includeComments,
        [FromQuery] bool? includeSnapshot)
    {
        if (!string.Equals(source, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not served by this service" });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: id);
        if (issue is null)
            return NotFound(new { error = $"Issue {id} not found" });

        Dictionary<string, string> metadata = new()
        {
            ["status"] = issue.Status,
            ["type"] = issue.Type,
            ["priority"] = issue.Priority,
        };
        if (issue.WorkGroup is not null) metadata["work_group"] = issue.WorkGroup;
        if (issue.Specification is not null) metadata["specification"] = issue.Specification;
        if (issue.Resolution is not null) metadata["resolution"] = issue.Resolution;
        if (issue.Assignee is not null) metadata["assignee"] = issue.Assignee;
        if (issue.Reporter is not null) metadata["reporter"] = issue.Reporter;
        if (issue.Labels is not null) metadata["labels"] = issue.Labels;

        List<CommentInfo>? comments = null;
        if (includeComments == true)
        {
            List<JiraCommentRecord> commentRecords = JiraCommentRecord.SelectList(connection, IssueKey: id);
            comments = commentRecords.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body, c.CreatedAt, null)).ToList();
        }

        string? snapshot = null;
        if (includeSnapshot == true)
        {
            snapshot = JiraUrlHelper.BuildMarkdownSnapshot(connection, issue, includeComments ?? false, true);
        }

        return Ok(new ContentItemResponse
        {
            Source = SourceSystems.Jira,
            ContentType = "issue",
            Id = issue.Key,
            Title = issue.Title,
            Content = includeContent == true ? issue.Description : null,
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            Metadata = metadata,
            Comments = comments,
            Snapshot = snapshot,
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static List<CrossReferenceHit> CollectRefersTo(
        SqliteConnection connection, JiraServiceOptions options, string value, string? sourceType, int maxResults)
    {
        List<CrossReferenceHit> hits = [];

        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: value);
        if (issue is null)
            return hits;

        string issueUrl = $"{options.BaseUrl}/browse/{issue.Key}";
        DateTimeOffset? issueUpdatedAt = issue.UpdatedAt;

        // Jira-to-Jira links via issue_links table
        if (sourceType is null || string.Equals(sourceType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            foreach (JiraIssueLinkRecord link in links)
            {
                if (hits.Count >= maxResults) break;
                JiraIssueRecord? target = JiraIssueRecord.SelectSingle(connection, Key: link.TargetKey);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = "issue",
                    SourceId = issue.Key,
                    SourceTitle = issue.Title,
                    SourceUrl = issueUrl,
                    TargetType = SourceSystems.Jira,
                    TargetId = link.TargetKey,
                    TargetTitle = target?.Title,
                    TargetUrl = $"{options.BaseUrl}/browse/{link.TargetKey}",
                    LinkType = link.LinkType,
                    UpdatedAt = issueUpdatedAt,
                });
            }
        }

        // Outgoing Zulip references
        if (sourceType is null || string.Equals(sourceType, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: issue.Key))
            {
                if (hits.Count >= maxResults) break;
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = issue.Key,
                    SourceTitle = issue.Title,
                    SourceUrl = issueUrl,
                    TargetType = SourceSystems.Zulip,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = issueUpdatedAt,
                });
            }
        }

        // Outgoing GitHub references
        if (sourceType is null || string.Equals(sourceType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: issue.Key))
            {
                if (hits.Count >= maxResults) break;
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = issue.Key,
                    SourceTitle = issue.Title,
                    SourceUrl = issueUrl,
                    TargetType = SourceSystems.GitHub,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = issueUpdatedAt,
                });
            }
        }

        // Outgoing Confluence references
        if (sourceType is null || string.Equals(sourceType, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: issue.Key))
            {
                if (hits.Count >= maxResults) break;
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = issue.Key,
                    SourceTitle = issue.Title,
                    SourceUrl = issueUrl,
                    TargetType = SourceSystems.Confluence,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = issueUpdatedAt,
                });
            }
        }

        // Outgoing FHIR element references
        if (sourceType is null || string.Equals(sourceType, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: issue.Key))
            {
                if (hits.Count >= maxResults) break;
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = issue.Key,
                    SourceTitle = issue.Title,
                    SourceUrl = issueUrl,
                    TargetType = SourceSystems.Fhir,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = issueUpdatedAt,
                });
            }
        }

        return hits;
    }

    private static List<CrossReferenceHit> CollectReferredBy(
        SqliteConnection connection, JiraServiceOptions options, string value, string? sourceType, int maxResults)
    {
        List<CrossReferenceHit> hits = [];

        string? detectedType = sourceType ?? ValueFormatDetector.DetectSourceType(value);

        // Jira-to-Jira: find Jira issues that link TO this value via issue_links
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            List<JiraIssueLinkRecord> incomingLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: value);
            foreach (JiraIssueLinkRecord link in incomingLinks)
            {
                if (hits.Count >= maxResults) break;
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: link.SourceKey);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = "issue",
                    SourceId = link.SourceKey,
                    SourceTitle = sourceIssue?.Title,
                    SourceUrl = $"{options.BaseUrl}/browse/{link.SourceKey}",
                    TargetType = SourceSystems.Jira,
                    TargetId = value,
                    TargetTitle = null,
                    TargetUrl = $"{options.BaseUrl}/browse/{value}",
                    LinkType = link.LinkType,
                    UpdatedAt = sourceIssue is not null ? sourceIssue.UpdatedAt : null,
                });
            }
        }

        // Zulip: find Jira issues whose xref_zulip records match the value
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
        {
            List<ZulipXRefRecord> zulipRefs = [];
            string[] parts = value.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out int streamId))
            {
                zulipRefs = ZulipXRefRecord.SelectList(connection, StreamId: streamId, TopicName: parts[1]);
            }
            else if (int.TryParse(value, out int streamOnly))
            {
                zulipRefs = ZulipXRefRecord.SelectList(connection, StreamId: streamOnly);
            }

            foreach (ZulipXRefRecord r in zulipRefs)
            {
                if (hits.Count >= maxResults) break;
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: r.SourceId);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = sourceIssue?.Title,
                    SourceUrl = sourceIssue is not null ? $"{options.BaseUrl}/browse/{r.SourceId}" : null,
                    TargetType = SourceSystems.Zulip,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = sourceIssue is not null ? sourceIssue.UpdatedAt : null,
                });
            }
        }

        // GitHub: find Jira issues whose xref_github records match the value
        if (detectedType is null || string.Equals(detectedType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            if (ValueFormatDetector.TryParseGitHubIssue(value, out string repoFullName, out int issueNumber))
            {
                List<GitHubXRefRecord> ghRefs = GitHubXRefRecord.SelectList(connection,
                    RepoFullName: repoFullName, IssueNumber: issueNumber);
                foreach (GitHubXRefRecord r in ghRefs)
                {
                    if (hits.Count >= maxResults) break;
                    JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: r.SourceId);
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.Jira,
                        ContentType = r.ContentType,
                        SourceId = r.SourceId,
                        SourceTitle = sourceIssue?.Title,
                        SourceUrl = sourceIssue is not null ? $"{options.BaseUrl}/browse/{r.SourceId}" : null,
                        TargetType = SourceSystems.GitHub,
                        TargetId = r.TargetId,
                        LinkType = r.LinkType,
                        Context = r.Context,
                        UpdatedAt = sourceIssue is not null ? sourceIssue.UpdatedAt : null,
                    });
                }
            }
        }

        // Confluence: find Jira issues whose xref_confluence records match the value
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            string pageId = value.StartsWith("page:", StringComparison.OrdinalIgnoreCase)
                ? value[5..]
                : value;
            List<ConfluenceXRefRecord> confRefs = ConfluenceXRefRecord.SelectList(connection, PageId: pageId);
            foreach (ConfluenceXRefRecord r in confRefs)
            {
                if (hits.Count >= maxResults) break;
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: r.SourceId);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = sourceIssue?.Title,
                    SourceUrl = sourceIssue is not null ? $"{options.BaseUrl}/browse/{r.SourceId}" : null,
                    TargetType = SourceSystems.Confluence,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = sourceIssue is not null ? sourceIssue.UpdatedAt : null,
                });
            }
        }

        // FHIR element: find Jira issues whose xref_fhir_element records match the value
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
        {
            List<FhirElementXRefRecord> fhirRefs = FhirElementXRefRecord.SelectList(connection, ElementPath: value);
            foreach (FhirElementXRefRecord r in fhirRefs)
            {
                if (hits.Count >= maxResults) break;
                JiraIssueRecord? sourceIssue = JiraIssueRecord.SelectSingle(connection, Key: r.SourceId);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Jira,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = sourceIssue?.Title,
                    SourceUrl = sourceIssue is not null ? $"{options.BaseUrl}/browse/{r.SourceId}" : null,
                    TargetType = SourceSystems.Fhir,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    UpdatedAt = sourceIssue is not null ? sourceIssue.UpdatedAt : null,
                });
            }
        }

        // Phase 2: Keyword relevance scoring — score source items against the query value via FTS5
        ApplyKeywordScores(connection, hits, value);

        return hits;
    }

    private static void ApplyKeywordScores(SqliteConnection connection, List<CrossReferenceHit> hits, string queryValue)
    {
        if (hits.Count == 0) return;

        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(queryValue);
        if (string.IsNullOrEmpty(ftsQuery)) return;

        // Batch-query FTS5 for all source items matching the query value
        using SqliteCommand cmd = new("""
            SELECT ji.Key, -(jira_issues_fts.rank) * COALESCE(jp.BaselineValue, 5) / 5.0 as Score
            FROM jira_issues_fts
            JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
            LEFT JOIN jira_projects jp ON jp.Key = ji.ProjectKey
            WHERE jira_issues_fts MATCH @query
              AND COALESCE(jp.BaselineValue, 5) > 0
            """, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);

        Dictionary<string, double> scores = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            double score = reader.GetDouble(1);
            scores[key] = score;
        }

        if (scores.Count == 0) return;

        for (int i = 0; i < hits.Count; i++)
        {
            if (scores.TryGetValue(hits[i].SourceId, out double keywordScore))
                hits[i] = hits[i] with { Score = hits[i].Score * keywordScore };
        }
    }

    // ── Keyword endpoints ────────────────────────────────────────

    [HttpGet("content/keywords/{source}/{**id}")]
    public IActionResult GetKeywords(
        [FromRoute] string source, [FromRoute] string id,
        [FromQuery] string? keywordType, [FromQuery] int? limit)
    {
        if (!string.Equals(source, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not handled by this service" });

        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 200);

        List<KeywordEntry> keywords = SourceDatabase.GetKeywordsForItem(connection, id, keywordType, maxResults);
        string contentType = keywords.Count > 0
            ? SourceDatabase.GetContentTypeForItem(connection, id)
            : "";

        return Ok(new KeywordListResponse
        {
            Source = SourceSystems.Jira,
            SourceId = id,
            ContentType = contentType,
            Keywords = keywords,
        });
    }

    [HttpGet("content/related-by-keyword/{source}/{**id}")]
    public IActionResult RelatedByKeyword(
        [FromRoute] string source, [FromRoute] string id,
        [FromQuery] double? minScore, [FromQuery] string? keywordType, [FromQuery] int? limit)
    {
        if (!string.Equals(source, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not handled by this service" });

        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);
        double threshold = minScore ?? 0.1;

        var rawResults = SourceDatabase.GetRelatedByKeyword(connection, id, threshold, keywordType, maxResults);

        List<RelatedByKeywordItem> items = rawResults.Select(r => new RelatedByKeywordItem
        {
            Source = SourceSystems.Jira,
            SourceId = r.SourceId,
            ContentType = r.ContentType,
            Title = ResolveTitle(connection, r.ContentType, r.SourceId),
            Score = r.Score,
            SharedKeywords = [.. r.SharedKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries)],
        }).ToList();

        return Ok(new RelatedByKeywordResponse
        {
            Source = SourceSystems.Jira,
            SourceId = id,
            RelatedItems = items,
        });
    }

    private static string ResolveTitle(SqliteConnection connection, string contentType, string sourceId)
    {
        if (contentType == "issue")
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Title FROM jira_issues WHERE Key = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", sourceId);
            return cmd.ExecuteScalar()?.ToString() ?? sourceId;
        }

        return sourceId;
    }
}
