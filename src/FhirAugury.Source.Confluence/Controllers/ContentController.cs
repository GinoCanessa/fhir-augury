using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class ContentController(ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("content/refers-to")]
    public IActionResult RefersTo([FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> hits = GetOutgoingReferences(connection, options, value, sourceType, maxResults);

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

        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> hits = GetIncomingReferences(connection, options, value, sourceType, maxResults);

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

        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<CrossReferenceHit> outgoing = GetOutgoingReferences(connection, options, value, sourceType, maxResults);
        List<CrossReferenceHit> incoming = GetIncomingReferences(connection, options, value, sourceType, maxResults);

        // Deduplicate by (SourceType+SourceId+TargetType+TargetId)
        HashSet<string> seen = [];
        List<CrossReferenceHit> combined = [];
        foreach (CrossReferenceHit hit in outgoing.Concat(incoming))
        {
            string key = $"{hit.SourceType}:{hit.SourceId}:{hit.TargetType}:{hit.TargetId}";
            if (seen.Add(key))
                combined.Add(hit);
        }

        List<CrossReferenceHit> trimmed = combined.Take(maxResults).ToList();

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "cross-referenced",
            Total = trimmed.Count,
            Hits = trimmed,
        });
    }

    [HttpGet("content/search")]
    public IActionResult Search([FromQuery] string? values, [FromQuery] string? sources, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(values))
            return BadRequest(new { error = "Query parameter 'values' is required" });

        List<string> valueList = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        List<string>? sourceList = string.IsNullOrWhiteSpace(sources)
            ? null
            : sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // If sources filter is specified and doesn't include confluence, return empty
        if (sourceList is not null && !sourceList.Any(s => string.Equals(s, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase)))
        {
            return Ok(new ContentSearchResponse
            {
                Values = valueList,
                Total = 0,
                Hits = [],
            });
        }

        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 20, 200);

        List<ContentSearchHit> allHits = [];
        foreach (string searchValue in valueList)
        {
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(searchValue);
            if (string.IsNullOrEmpty(ftsQuery))
                continue;

            string sql = """
                SELECT cp.ConfluenceId, cp.Title,
                       snippet(confluence_pages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       confluence_pages_fts.rank, cp.SpaceKey, cp.LastModifiedAt
                FROM confluence_pages_fts
                JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
                WHERE confluence_pages_fts MATCH @query
                ORDER BY confluence_pages_fts.rank
                LIMIT @limit OFFSET @offset
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", 0);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string pageId = reader.GetString(0);
                Dictionary<string, string> metadata = new()
                {
                    ["space_key"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                };

                allHits.Add(new ContentSearchHit
                {
                    Source = SourceSystems.Confluence,
                    ContentType = "page",
                    Id = pageId,
                    Title = reader.GetString(1),
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Score = -reader.GetDouble(3),
                    Url = ConfluenceUrlHelper.BuildPageUrl(options, pageId, null),
                    UpdatedAt = ConfluenceUrlHelper.ParseTimestamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    Metadata = metadata,
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

    [HttpGet("content/item/{source}/{**id}")]
    public IActionResult GetItem(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] bool includeContent = false,
        [FromQuery] bool includeComments = false,
        [FromQuery] bool includeSnapshot = false)
    {
        if (!string.Equals(source, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not handled by this service" });

        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();

        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
        if (page is null)
            return NotFound(new { error = $"Page {id} not found" });

        Dictionary<string, string> metadata = new()
        {
            ["space_key"] = page.SpaceKey,
            ["version"] = page.VersionNumber.ToString(),
        };
        if (page.Labels is not null) metadata["labels"] = page.Labels;
        if (page.ParentId is not null) metadata["parent_id"] = page.ParentId;
        if (page.LastModifiedBy is not null) metadata["last_modified_by"] = page.LastModifiedBy;

        List<CommentInfo>? comments = null;
        if (includeComments)
        {
            List<ConfluenceCommentRecord> commentRecords = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            comments = commentRecords.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body ?? "", c.CreatedAt, null)).ToList();
        }

        string? snapshot = null;
        if (includeSnapshot)
        {
            List<ConfluenceCommentRecord> snapshotComments = includeComments
                ? ConfluenceCommentRecord.SelectList(connection, PageId: page.Id)
                : ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            snapshot = ConfluenceUrlHelper.BuildMarkdownSnapshot(page, snapshotComments);
        }

        return Ok(new ContentItemResponse
        {
            Source = SourceSystems.Confluence,
            ContentType = "page",
            Id = page.ConfluenceId,
            Title = page.Title,
            Content = includeContent ? page.BodyPlain : null,
            Url = ConfluenceUrlHelper.BuildPageUrl(options, page.ConfluenceId, page.Url),
            UpdatedAt = page.LastModifiedAt,
            Metadata = metadata,
            Comments = comments,
            Snapshot = snapshot,
        });
    }

    private static List<CrossReferenceHit> GetOutgoingReferences(
        SqliteConnection connection, ConfluenceServiceOptions options, string value, string? sourceType, int maxResults)
    {
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: value);
        string sourceTitle = page?.Title ?? "";
        string sourceUrl = page is not null
            ? ConfluenceUrlHelper.BuildPageUrl(options, value, page.Url)
            : "";

        List<CrossReferenceHit> hits = [];

        if (sourceType is null || string.Equals(sourceType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        if (sourceType is null || string.Equals(sourceType, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        if (sourceType is null || string.Equals(sourceType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        if (sourceType is null || string.Equals(sourceType, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        if (sourceType is null || string.Equals(sourceType, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: value))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = value,
                    SourceTitle = sourceTitle,
                    SourceUrl = sourceUrl,
                    TargetType = r.TargetType,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        return hits.Take(maxResults).ToList();
    }

    private static List<CrossReferenceHit> GetIncomingReferences(
        SqliteConnection connection, ConfluenceServiceOptions options, string value, string? sourceType, int maxResults)
    {
        List<CrossReferenceHit> hits = [];
        string? detectedType = sourceType ?? ValueFormatDetector.DetectSourceType(value);

        // Jira key → find Confluence pages that mention this Jira key
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            if (ValueFormatDetector.IsJiraKey(value))
            {
                HashSet<string> seen = [];
                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, JiraKey: value))
                {
                    if (!seen.Add(r.SourceId)) continue;
                    ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: r.SourceId);
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.Confluence,
                        ContentType = r.ContentType,
                        SourceId = r.SourceId,
                        SourceTitle = page?.Title,
                        SourceUrl = page is not null ? ConfluenceUrlHelper.BuildPageUrl(options, r.SourceId, page.Url) : null,
                        TargetType = SourceSystems.Jira,
                        TargetId = value,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                }
            }
        }

        // GitHub issue → find Confluence pages that mention this GitHub issue
        if (detectedType is null || string.Equals(detectedType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            if (ValueFormatDetector.TryParseGitHubIssue(value, out string repoFullName, out int issueNumber))
            {
                HashSet<string> seen = [];
                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, RepoFullName: repoFullName, IssueNumber: issueNumber))
                {
                    if (!seen.Add(r.SourceId)) continue;
                    ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: r.SourceId);
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.Confluence,
                        ContentType = r.ContentType,
                        SourceId = r.SourceId,
                        SourceTitle = page?.Title,
                        SourceUrl = page is not null ? ConfluenceUrlHelper.BuildPageUrl(options, r.SourceId, page.Url) : null,
                        TargetType = SourceSystems.GitHub,
                        TargetId = value,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                }
            }
        }

        // FHIR element → find Confluence pages that mention this FHIR element
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase))
        {
            if (ValueFormatDetector.IsFhirElement(value))
            {
                HashSet<string> seen = [];
                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, ElementPath: value))
                {
                    if (!seen.Add(r.SourceId)) continue;
                    ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: r.SourceId);
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.Confluence,
                        ContentType = r.ContentType,
                        SourceId = r.SourceId,
                        SourceTitle = page?.Title,
                        SourceUrl = page is not null ? ConfluenceUrlHelper.BuildPageUrl(options, r.SourceId, page.Url) : null,
                        TargetType = SourceSystems.Fhir,
                        TargetId = value,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                }
            }
        }

        // Confluence page → find Confluence pages that reference this page via xref_confluence
        if (detectedType is null || string.Equals(detectedType, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            HashSet<string> seen = [];
            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, PageId: value))
            {
                if (!seen.Add(r.SourceId)) continue;
                ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: r.SourceId);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.Confluence,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = page?.Title,
                    SourceUrl = page is not null ? ConfluenceUrlHelper.BuildPageUrl(options, r.SourceId, page.Url) : null,
                    TargetType = SourceSystems.Confluence,
                    TargetId = value,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
            }
        }

        return hits.Take(maxResults).ToList();
    }
}
