using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Common.Filtering;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1/content")]
public class ContentController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("refers-to")]
    public IActionResult RefersTo([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);
        ZulipServiceOptions options = optsAccessor.Value;

        if (!int.TryParse(value, out int msgId))
        {
            return Ok(new CrossReferenceQueryResponse
            {
                Value = value,
                SourceType = sourceType,
                Direction = "refers-to",
                Total = 0,
                Hits = [],
                Warnings = [$"Value '{value}' is not a valid Zulip message ID"],
            });
        }

        using SqliteConnection connection = db.OpenConnection();
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        string sourceTitle = message is not null ? $"[{message.StreamName}] {message.Topic}" : "";
        string sourceUrl = message is not null
            ? ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId)
            : "";

        // Compute per-source score factor from stream baseline
        double score = 1.0;
        DateTimeOffset? messageUpdatedAt = message?.Timestamp;
        if (message is not null)
        {
            ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(connection, Id: message.StreamId);
            int baseline = stream?.BaselineValue ?? 5;
            score = baseline / 5.0;
        }

        List<CrossReferenceHit> hits = [];

        if (sourceType is null or SourceSystems.Jira)
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Zulip,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    Score = score,
                    UpdatedAt = messageUpdatedAt,
                });
            }
        }

        if (sourceType is null or SourceSystems.GitHub)
        {
            foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Zulip,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    Score = score,
                    UpdatedAt = messageUpdatedAt,
                });
            }
        }

        if (sourceType is null or SourceSystems.Confluence)
        {
            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Zulip,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    Score = score,
                    UpdatedAt = messageUpdatedAt,
                });
            }
        }

        if (sourceType is null or SourceSystems.Fhir)
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Zulip,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                    Score = score,
                    UpdatedAt = messageUpdatedAt,
                });
            }
        }

        if (hits.Count > maxResults)
            hits = hits.GetRange(0, maxResults);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "refers-to",
            Total = hits.Count,
            Hits = hits,
        });
    }

    [HttpGet("referred-by")]
    public IActionResult ReferredBy([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);
        ZulipServiceOptions options = optsAccessor.Value;

        using SqliteConnection connection = db.OpenConnection();
        List<CrossReferenceHit> hits = [];

        string? detectedType = sourceType ?? ValueFormatDetector.DetectSourceType(value);

        if (detectedType is null or SourceSystems.Jira)
        {
            if (ValueFormatDetector.IsJiraKey(value))
            {
                AddZulipHits(
                    connection,
                    JiraXRefRecord.SelectList(connection, JiraKey: value),
                    options,
                    maxResults,
                    hits);
            }
        }

        if (detectedType is null or SourceSystems.GitHub)
        {
            if (ValueFormatDetector.TryParseGitHubIssue(value, out string repoFullName, out int issueNumber))
            {
                AddZulipHits(
                    connection,
                    GitHubXRefRecord.SelectList(connection, RepoFullName: repoFullName, IssueNumber: issueNumber),
                    options,
                    maxResults,
                    hits);
            }
        }

        if (detectedType is null or SourceSystems.Fhir)
        {
            if (ValueFormatDetector.IsFhirElement(value))
            {
                AddZulipHits(
                    connection,
                    FhirElementXRefRecord.SelectList(connection, ElementPath: value),
                    options,
                    maxResults,
                    hits);
            }
        }

        if (hits.Count > maxResults)
            hits = hits.GetRange(0, maxResults);

        // Phase 2: Keyword relevance scoring
        ApplyKeywordScores(connection, hits, value);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "referred-by",
            Total = hits.Count,
            Hits = hits,
        });
    }

    [HttpGet("cross-referenced")]
    public IActionResult CrossReferenced([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);

        // Gather both directions
        CrossReferenceQueryResponse refersToResponse = (CrossReferenceQueryResponse)((OkObjectResult)RefersTo(value, sourceType, maxResults)).Value!;
        CrossReferenceQueryResponse referredByResponse = (CrossReferenceQueryResponse)((OkObjectResult)ReferredBy(value, sourceType, maxResults)).Value!;

        // Merge and deduplicate
        HashSet<string> seen = [];
        List<CrossReferenceHit> merged = [];

        foreach (CrossReferenceHit hit in refersToResponse.Hits)
        {
            string key = $"{hit.SourceType}|{hit.SourceId}|{hit.TargetType}|{hit.TargetId}";
            if (seen.Add(key))
                merged.Add(hit);
        }

        foreach (CrossReferenceHit hit in referredByResponse.Hits)
        {
            string key = $"{hit.SourceType}|{hit.SourceId}|{hit.TargetType}|{hit.TargetId}";
            if (seen.Add(key))
                merged.Add(hit);
        }

        if (merged.Count > maxResults)
            merged = merged.GetRange(0, maxResults);

        List<string>? warnings = CombineWarnings(refersToResponse.Warnings, referredByResponse.Warnings);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "cross-referenced",
            Total = merged.Count,
            Hits = merged,
            Warnings = warnings,
        });
    }

    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? values, [FromQuery] string? sources, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(values))
            return BadRequest(new { error = "Query parameter 'values' is required" });

        List<string> valueList = [.. values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        List<string>? sourceList = sources is not null
            ? [.. sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        // If source filter is provided and doesn't include zulip, return empty
        if (sourceList.HasExplicitRestriction() && !sourceList!.Any(s => s.Equals(SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase)))
        {
            return Ok(new ContentSearchResponse
            {
                Values = valueList,
                Total = 0,
                Hits = [],
            });
        }

        int maxResults = Math.Min(limit ?? 20, 200);
        ZulipServiceOptions options = optsAccessor.Value;

        using SqliteConnection connection = db.OpenConnection();
        List<ContentSearchHit> allHits = [];

        foreach (string searchValue in valueList)
        {
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(searchValue);
            if (string.IsNullOrEmpty(ftsQuery))
                continue;

            string sql = """
                SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                       snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       zulip_messages_fts.rank, zm.SenderName, zm.Timestamp,
                       COALESCE(zs.BaselineValue, 5) as BaselineValue
                FROM zulip_messages_fts
                JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
                LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
                WHERE zulip_messages_fts MATCH @query
                ORDER BY (zulip_messages_fts.rank * COALESCE(zs.BaselineValue, 5) / 5.0)
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int msgId = reader.GetInt32(0);
                string streamName = reader.GetString(1);
                string topic = reader.GetString(2);
                double rawRank = reader.GetDouble(4);
                int baselineValue = reader.GetInt32(7);

                allHits.Add(new ContentSearchHit
                {
                    Source = SourceSystems.Zulip,
                    ContentType = ContentTypes.Message,
                    Id = msgId.ToString(),
                    Title = $"[{streamName}] {topic}",
                    Snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Score = -rawRank * (baselineValue / 5.0),
                    Url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, msgId),
                    UpdatedAt = ZulipUrlHelper.ParseTimestamp(reader, 6),
                    Metadata = new Dictionary<string, string>
                    {
                        ["sender_name"] = reader.GetString(5),
                        ["stream_name"] = streamName,
                        ["topic"] = topic,
                    },
                    MatchedValue = searchValue,
                });
            }
        }

        // Deduplicate by message ID, keeping highest score
        List<ContentSearchHit> deduped = [.. allHits
            .GroupBy(h => h.Id)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(maxResults)];

        return Ok(new ContentSearchResponse
        {
            Values = valueList,
            Total = deduped.Count,
            Hits = deduped,
        });
    }

    [HttpGet("item/{source}/{**id}")]
    public IActionResult GetItem(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] bool? includeContent,
        [FromQuery] bool? includeComments,
        [FromQuery] bool? includeSnapshot)
    {
        if (!source.Equals(SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not served by this service" });

        if (!int.TryParse(id, out int msgId))
            return BadRequest(new { error = "ID must be a Zulip message ID integer" });

        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();

        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return NotFound(new { error = $"Message {id} not found" });

        Dictionary<string, string> metadata = new()
        {
            ["stream_name"] = message.StreamName,
            ["topic"] = message.Topic,
            ["sender_name"] = message.SenderName,
            ["sender_id"] = message.SenderId.ToString(),
        };
        if (message.SenderEmail is not null) metadata["sender_email"] = message.SenderEmail;

        string? content = null;
        if (includeContent ?? false)
            content = message.ContentHtml ?? message.ContentPlain ?? "";

        // Thread messages as comments (other messages in the same topic)
        List<CommentInfo>? comments = null;
        if (includeComments ?? false)
        {
            comments = [];
            string commentSql = """
                SELECT ZulipMessageId, SenderName, ContentPlain, Timestamp
                FROM zulip_messages
                WHERE StreamName = @streamName AND Topic = @topic AND ZulipMessageId != @msgId
                ORDER BY Timestamp ASC
                """;

            using SqliteCommand cmd = new SqliteCommand(commentSql, connection);
            cmd.Parameters.AddWithValue("@streamName", message.StreamName);
            cmd.Parameters.AddWithValue("@topic", message.Topic);
            cmd.Parameters.AddWithValue("@msgId", msgId);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int commentMsgId = reader.GetInt32(0);
                comments.Add(new CommentInfo(
                    Id: commentMsgId.ToString(),
                    Author: reader.GetString(1),
                    Body: reader.IsDBNull(2) ? "" : reader.GetString(2),
                    CreatedAt: ZulipUrlHelper.ParseTimestamp(reader, 3),
                    Url: ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, commentMsgId)));
            }
        }

        string? snapshot = null;
        if (includeSnapshot ?? false)
            snapshot = ZulipUrlHelper.BuildThreadMarkdownSnapshot(connection, message.StreamName, message.Topic);

        return Ok(new ContentItemResponse
        {
            Source = SourceSystems.Zulip,
            ContentType = ContentTypes.Message,
            Id = message.ZulipMessageId.ToString(),
            Title = $"[{message.StreamName}] {message.Topic}",
            Content = content,
            Url = ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, message.ZulipMessageId),
            CreatedAt = message.Timestamp,
            UpdatedAt = message.Timestamp,
            Metadata = metadata,
            Comments = comments,
            Snapshot = snapshot,
        });
    }

    private static List<string>? CombineWarnings(List<string>? a, List<string>? b)
    {
        if (a is null && b is null) return null;
        List<string> combined = [];
        if (a is not null) combined.AddRange(a);
        if (b is not null) combined.AddRange(b);
        return combined.Count > 0 ? combined : null;
    }

    private static void ApplyKeywordScores(SqliteConnection connection, List<CrossReferenceHit> hits, string queryValue)
    {
        if (hits.Count == 0) return;

        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(queryValue);
        if (string.IsNullOrEmpty(ftsQuery)) return;

        // Score messages via FTS5, applying BaselineValue multiplier
        using SqliteCommand cmd = new("""
            SELECT zm.ZulipMessageId, -(zulip_messages_fts.rank) * COALESCE(zs.BaselineValue, 5) / 5.0 as Score
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH @query
            """, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);

        Dictionary<string, double> scores = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string messageId = reader.GetInt32(0).ToString();
            double score = reader.GetDouble(1);
            scores[messageId] = score;
        }

        if (scores.Count == 0) return;

        for (int i = 0; i < hits.Count; i++)
        {
            if (scores.TryGetValue(hits[i].SourceId, out double keywordScore))
                hits[i] = hits[i] with { Score = keywordScore };
        }
    }

    // ── Keyword endpoints ────────────────────────────────────────

    [HttpGet("keywords/{source}/{**id}")]
    public IActionResult GetKeywords(
        [FromRoute] string source, [FromRoute] string id,
        [FromQuery] string? keywordType, [FromQuery] int? limit)
    {
        if (!string.Equals(source, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not handled by this service" });

        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 200);

        List<KeywordEntry> keywords = SourceDatabase.GetKeywordsForItem(connection, id, keywordType, maxResults);
        string contentType = keywords.Count > 0
            ? SourceDatabase.GetContentTypeForItem(connection, id)
            : "";

        return Ok(new KeywordListResponse
        {
            Source = SourceSystems.Zulip,
            SourceId = id,
            ContentType = contentType,
            Keywords = keywords,
        });
    }

    [HttpGet("related-by-keyword/{source}/{**id}")]
    public IActionResult RelatedByKeyword(
        [FromRoute] string source, [FromRoute] string id,
        [FromQuery] double? minScore, [FromQuery] string? keywordType, [FromQuery] int? limit)
    {
        if (!string.Equals(source, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not handled by this service" });

        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);
        double threshold = minScore ?? 0.1;

        var rawResults = SourceDatabase.GetRelatedByKeyword(connection, id, threshold, keywordType, maxResults);

        List<RelatedByKeywordItem> items = rawResults.Select(r => new RelatedByKeywordItem
        {
            Source = SourceSystems.Zulip,
            SourceId = r.SourceId,
            ContentType = r.ContentType,
            Title = ResolveTitle(connection, r.ContentType, r.SourceId),
            Score = r.Score,
            SharedKeywords = [.. r.SharedKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries)],
        }).ToList();

        return Ok(new RelatedByKeywordResponse
        {
            Source = SourceSystems.Zulip,
            SourceId = id,
            RelatedItems = items,
        });
    }

    private static string ResolveTitle(SqliteConnection connection, string contentType, string sourceId)
    {
        if (contentType != "message")
            return sourceId;

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Topic FROM zulip_messages WHERE MessageId = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", int.TryParse(sourceId, out int msgId) ? msgId : 0);
        return cmd.ExecuteScalar()?.ToString() ?? sourceId;
    }

    /// <summary>
    /// Resolves xref records to Zulip message hits using batched message+stream lookups.
    /// Dedupes by <c>SourceId</c>, caps at <paramref name="maxResults"/>, and appends to <paramref name="hits"/>.
    /// </summary>
    private static void AddZulipHits<TXref>(
        SqliteConnection connection,
        IEnumerable<TXref> refs,
        ZulipServiceOptions options,
        int maxResults,
        List<CrossReferenceHit> hits)
        where TXref : ICrossReferenceRecord
    {
        HashSet<string> seen = [];
        List<(TXref Ref, int MsgId)> parsed = [];
        foreach (TXref r in refs)
        {
            if (!seen.Add(r.SourceId)) continue;
            if (!int.TryParse(r.SourceId, out int msgId)) continue;
            parsed.Add((r, msgId));
            if (parsed.Count >= maxResults) break;
        }

        if (parsed.Count == 0) return;

        int[] msgIds = [.. parsed.Select(p => p.MsgId).Distinct()];
        Dictionary<int, ZulipMessageRecord> msgs = msgIds.Length == 0
            ? []
            : ZulipMessageRecord
                .SelectList(connection, ZulipMessageIdValues: msgIds)
                .ToDictionary(m => m.ZulipMessageId);

        int[] streamIds = [.. msgs.Values.Select(m => m.StreamId).Distinct()];
        Dictionary<int, ZulipStreamRecord> streams = streamIds.Length == 0
            ? []
            : ZulipStreamRecord
                .SelectList(connection, IdValues: streamIds)
                .ToDictionary(s => s.Id);

        foreach ((TXref r, int msgId) in parsed)
        {
            msgs.TryGetValue(msgId, out ZulipMessageRecord? message);
            double score = 1.0;
            if (message is not null
                && streams.TryGetValue(message.StreamId, out ZulipStreamRecord? stream))
            {
                score = (stream?.BaselineValue ?? 5) / 5.0;
            }
            hits.Add(new CrossReferenceHit
            {
                SourceType = SourceSystems.Zulip,
                ContentType = r.ContentType,
                SourceId = r.SourceId,
                SourceTitle = message is not null ? $"[{message.StreamName}] {message.Topic}" : null,
                SourceUrl = message is not null ? ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId) : null,
                TargetType = r.TargetType,
                TargetId = r.TargetId,
                LinkType = r.LinkType,
                Context = r.Context,
                Score = score,
                UpdatedAt = message?.Timestamp,
            });
        }
    }
}
