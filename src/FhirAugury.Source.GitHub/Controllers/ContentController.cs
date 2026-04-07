using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1/content")]
public class ContentController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("refers-to")]
    public IActionResult RefersTo(
        [FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);
        using SqliteConnection connection = db.OpenConnection();

        List<CrossReferenceHit> hits = [];

        // Find the source item in GitHub
        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: value);
        if (issue is null)
            return Ok(new CrossReferenceQueryResponse
            {
                Value = value,
                SourceType = sourceType,
                Direction = "refers-to",
                Total = 0,
                Hits = [],
            });

        string sourceId = issue.UniqueKey;
        string sourceUrl = GitHubUrlHelper.BuildIssueUrl(sourceId);

        // Query outgoing xref tables where SourceId = value
        if (sourceType is null || string.Equals(sourceType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: sourceId))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = sourceId,
                    SourceTitle = issue.Title,
                    SourceUrl = sourceUrl,
                    TargetType = SourceSystems.Jira,
                    TargetId = r.JiraKey,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        if (hits.Count < maxResults &&
            (sourceType is null || string.Equals(sourceType, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: sourceId))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = sourceId,
                    SourceTitle = issue.Title,
                    SourceUrl = sourceUrl,
                    TargetType = SourceSystems.Zulip,
                    TargetId = r.TargetId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        if (hits.Count < maxResults &&
            (sourceType is null || string.Equals(sourceType, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: sourceId))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = sourceId,
                    SourceTitle = issue.Title,
                    SourceUrl = sourceUrl,
                    TargetType = SourceSystems.Confluence,
                    TargetId = r.PageId,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        if (hits.Count < maxResults &&
            (sourceType is null || string.Equals(sourceType, SourceSystems.Fhir, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: sourceId))
            {
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = sourceId,
                    SourceTitle = issue.Title,
                    SourceUrl = sourceUrl,
                    TargetType = SourceSystems.Fhir,
                    TargetId = r.ElementPath,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

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
    public IActionResult ReferredBy(
        [FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);
        using SqliteConnection connection = db.OpenConnection();

        List<CrossReferenceHit> hits = [];
        string detectedType = sourceType ?? ValueFormatDetector.DetectSourceType(value) ?? "";

        // Search xref tables where the value appears as a target
        if (string.Equals(detectedType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase)
            || ValueFormatDetector.IsJiraKey(value))
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, JiraKey: value))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = resolved?.Title,
                    SourceUrl = resolved?.Url,
                    TargetType = SourceSystems.Jira,
                    TargetId = r.JiraKey,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        if (hits.Count < maxResults && ValueFormatDetector.IsFhirElement(value))
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, ElementPath: value))
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = resolved?.Title,
                    SourceUrl = resolved?.Url,
                    TargetType = SourceSystems.Fhir,
                    TargetId = r.ElementPath,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

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
    public IActionResult CrossReferenced(
        [FromQuery] string? value, [FromQuery] string? sourceType, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        int maxResults = Math.Min(limit ?? 50, 200);
        using SqliteConnection connection = db.OpenConnection();

        List<CrossReferenceHit> hits = [];
        HashSet<string> seen = [];

        // Outgoing: refers-to
        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: value);
        if (issue is not null)
        {
            string sourceId = issue.UniqueKey;
            string sourceUrl = GitHubUrlHelper.BuildIssueUrl(sourceId);

            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: sourceId))
            {
                string dedupeKey = $"out:{r.TargetType}:{r.JiraKey}";
                if (!seen.Add(dedupeKey)) continue;
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = sourceId,
                    SourceTitle = issue.Title,
                    SourceUrl = sourceUrl,
                    TargetType = SourceSystems.Jira,
                    TargetId = r.JiraKey,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }

            if (hits.Count < maxResults)
            {
                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: sourceId))
                {
                    string dedupeKey = $"out:{r.TargetType}:{r.TargetId}";
                    if (!seen.Add(dedupeKey)) continue;
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.GitHub,
                        ContentType = r.ContentType,
                        SourceId = sourceId,
                        SourceTitle = issue.Title,
                        SourceUrl = sourceUrl,
                        TargetType = SourceSystems.Zulip,
                        TargetId = r.TargetId,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                    if (hits.Count >= maxResults) break;
                }
            }

            if (hits.Count < maxResults)
            {
                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: sourceId))
                {
                    string dedupeKey = $"out:{r.TargetType}:{r.PageId}";
                    if (!seen.Add(dedupeKey)) continue;
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.GitHub,
                        ContentType = r.ContentType,
                        SourceId = sourceId,
                        SourceTitle = issue.Title,
                        SourceUrl = sourceUrl,
                        TargetType = SourceSystems.Confluence,
                        TargetId = r.PageId,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                    if (hits.Count >= maxResults) break;
                }
            }

            if (hits.Count < maxResults)
            {
                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: sourceId))
                {
                    string dedupeKey = $"out:{r.TargetType}:{r.ElementPath}";
                    if (!seen.Add(dedupeKey)) continue;
                    hits.Add(new CrossReferenceHit
                    {
                        SourceType = SourceSystems.GitHub,
                        ContentType = r.ContentType,
                        SourceId = sourceId,
                        SourceTitle = issue.Title,
                        SourceUrl = sourceUrl,
                        TargetType = SourceSystems.Fhir,
                        TargetId = r.ElementPath,
                        LinkType = r.LinkType,
                        Context = r.Context,
                    });
                    if (hits.Count >= maxResults) break;
                }
            }
        }

        // Incoming: referred-by
        if (hits.Count < maxResults && ValueFormatDetector.IsJiraKey(value))
        {
            foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, JiraKey: value))
            {
                string dedupeKey = $"in:{SourceSystems.GitHub}:{r.SourceId}";
                if (!seen.Add(dedupeKey)) continue;
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = resolved?.Title,
                    SourceUrl = resolved?.Url,
                    TargetType = SourceSystems.Jira,
                    TargetId = r.JiraKey,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        if (hits.Count < maxResults && ValueFormatDetector.IsFhirElement(value))
        {
            foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, ElementPath: value))
            {
                string dedupeKey = $"in:{SourceSystems.GitHub}:{r.SourceId}";
                if (!seen.Add(dedupeKey)) continue;
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                hits.Add(new CrossReferenceHit
                {
                    SourceType = SourceSystems.GitHub,
                    ContentType = r.ContentType,
                    SourceId = r.SourceId,
                    SourceTitle = resolved?.Title,
                    SourceUrl = resolved?.Url,
                    TargetType = SourceSystems.Fhir,
                    TargetId = r.ElementPath,
                    LinkType = r.LinkType,
                    Context = r.Context,
                });
                if (hits.Count >= maxResults) break;
            }
        }

        // Filter by sourceType if specified
        if (sourceType is not null)
        {
            hits = hits.Where(h =>
                string.Equals(h.TargetType, sourceType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.SourceType, sourceType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "cross-referenced",
            Total = hits.Count,
            Hits = hits,
        });
    }

    [HttpGet("search")]
    public IActionResult Search(
        [FromQuery] string? values, [FromQuery] string? sources, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(values))
            return BadRequest(new { error = "Query parameter 'values' is required" });

        List<string> valueList = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (valueList.Count == 0)
            return BadRequest(new { error = "At least one search value is required" });

        List<string>? sourceFilter = sources?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // If sources filter is specified and doesn't include "github", return empty
        if (sourceFilter is not null &&
            !sourceFilter.Any(s => string.Equals(s, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase)))
        {
            return Ok(new ContentSearchResponse
            {
                Values = valueList,
                Total = 0,
                Hits = [],
            });
        }

        int maxResults = Math.Min(limit ?? 20, 200);
        using SqliteConnection connection = db.OpenConnection();

        List<ContentSearchHit> allHits = [];

        foreach (string searchValue in valueList)
        {
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(searchValue);
            if (string.IsNullOrEmpty(ftsQuery))
                continue;

            // Search issues FTS
            SearchIssuesFts(connection, ftsQuery, maxResults, searchValue, allHits);

            // Search file contents FTS
            SearchFileContentsFts(connection, ftsQuery, maxResults, searchValue, allHits);

            // Search commits FTS
            SearchCommitsFts(connection, ftsQuery, maxResults, searchValue, allHits);
        }

        // Sort by score descending and apply limit
        allHits = allHits.OrderByDescending(h => h.Score).Take(maxResults).ToList();

        return Ok(new ContentSearchResponse
        {
            Values = valueList,
            Total = allHits.Count,
            Hits = allHits,
        });
    }

    [HttpGet("item/{source}/{*id}")]
    public IActionResult GetItem(
        [FromRoute] string source, [FromRoute] string id,
        [FromQuery] bool? includeContent, [FromQuery] bool? includeComments, [FromQuery] bool? includeSnapshot)
    {
        if (!string.Equals(source, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase))
            return NotFound(new { error = $"Source '{source}' is not served by this endpoint" });

        using SqliteConnection connection = db.OpenConnection();

        // Check if this is a file content request
        if (GitHubUrlHelper.TryParseFileId(id, out string? fileRepo, out string? filePath))
        {
            GitHubFileContentRecord? file = GitHubUrlHelper.LookupFileRecord(connection, fileRepo!, filePath!);
            if (file is not null)
                return Ok(BuildFileItemResponse(file, id, includeContent ?? true, includeSnapshot ?? false));
        }

        // Try as issue/PR key
        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: id);
        if (issue is null)
            return NotFound(new { error = $"Item '{id}' not found" });

        return Ok(BuildIssueItemResponse(connection, issue, includeContent ?? true, includeComments ?? true, includeSnapshot ?? false));
    }

    private static ContentItemResponse BuildIssueItemResponse(
        SqliteConnection connection, GitHubIssueRecord issue,
        bool includeContent, bool includeComments, bool includeSnapshot)
    {
        List<CommentInfo>? comments = null;
        if (includeComments)
        {
            List<GitHubCommentRecord> commentRecords = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            comments = commentRecords.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body, c.CreatedAt,
                $"https://github.com/{c.RepoFullName}/issues/{c.IssueNumber}#issuecomment-{c.Id}")).ToList();
        }

        Dictionary<string, string> metadata = new()
        {
            ["state"] = issue.State,
            ["is_pull_request"] = issue.IsPullRequest.ToString(),
            ["repo"] = issue.RepoFullName,
            ["number"] = issue.Number.ToString(),
        };
        if (issue.Author is not null) metadata["author"] = issue.Author;
        if (issue.Labels is not null) metadata["labels"] = issue.Labels;
        if (issue.Assignees is not null) metadata["assignees"] = issue.Assignees;

        string? snapshot = includeSnapshot
            ? GitHubUrlHelper.BuildMarkdownSnapshot(connection, issue, includeComments, includeRefs: true)
            : null;

        return new ContentItemResponse
        {
            Source = SourceSystems.GitHub,
            ContentType = issue.IsPullRequest ? "pull_request" : ContentTypes.Issue,
            Id = issue.UniqueKey,
            Title = issue.Title,
            Content = includeContent ? issue.Body : null,
            Url = GitHubUrlHelper.BuildIssueUrl(issue.UniqueKey),
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            Metadata = metadata,
            Comments = comments,
            Snapshot = snapshot,
        };
    }

    private static ContentItemResponse BuildFileItemResponse(
        GitHubFileContentRecord file, string id, bool includeContent, bool includeSnapshot)
    {
        Dictionary<string, string> metadata = new()
        {
            ["repo"] = file.RepoFullName,
            ["file_path"] = file.FilePath,
            ["extension"] = file.FileExtension,
            ["parser_type"] = file.ParserType,
            ["content_length"] = file.ContentLength.ToString(),
            ["extracted_length"] = file.ExtractedLength.ToString(),
        };
        if (file.LastCommitSha is not null) metadata["last_commit_sha"] = file.LastCommitSha;
        if (file.LastModifiedAt is not null) metadata["last_modified_at"] = file.LastModifiedAt;

        string? snapshot = null;
        if (includeSnapshot)
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine($"# {file.FilePath}");
            sb.AppendLine();
            sb.AppendLine($"**Repository:** {file.RepoFullName}  ");
            sb.AppendLine($"**Extension:** {file.FileExtension}  ");
            sb.AppendLine($"**Parser:** {file.ParserType}  ");
            sb.AppendLine($"**Size:** {file.ContentLength:N0} bytes  ");
            if (file.LastCommitSha is not null) sb.AppendLine($"**Last Commit:** {file.LastCommitSha}  ");
            if (file.LastModifiedAt is not null) sb.AppendLine($"**Last Modified:** {file.LastModifiedAt}  ");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(file.ContentText))
            {
                sb.AppendLine("## Content");
                sb.AppendLine();
                sb.AppendLine(file.ContentText);
            }
            snapshot = sb.ToString();
        }

        return new ContentItemResponse
        {
            Source = SourceSystems.GitHub,
            ContentType = ContentTypes.File,
            Id = id,
            Title = file.FilePath,
            Content = includeContent ? (file.ContentText ?? "") : null,
            Url = $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
            Metadata = metadata,
            Snapshot = snapshot,
        };
    }

    private static void SearchIssuesFts(
        SqliteConnection connection, string ftsQuery, int maxResults, string matchedValue, List<ContentSearchHit> hits)
    {
        string sql = """
            SELECT gi.UniqueKey, gi.Title,
                   snippet(github_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                   github_issues_fts.rank, gi.State, gi.UpdatedAt
            FROM github_issues_fts
            JOIN github_issues gi ON gi.Id = github_issues_fts.rowid
            WHERE github_issues_fts MATCH @query
            ORDER BY github_issues_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uniqueKey = reader.GetString(0);
            hits.Add(new ContentSearchHit
            {
                Source = SourceSystems.GitHub,
                ContentType = ContentTypes.Issue,
                Id = uniqueKey,
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = GitHubUrlHelper.BuildIssueUrl(uniqueKey),
                UpdatedAt = GitHubUrlHelper.ParseTimestamp(reader, 5),
                MatchedValue = matchedValue,
            });
        }
    }

    private static void SearchFileContentsFts(
        SqliteConnection connection, string ftsQuery, int maxResults, string matchedValue, List<ContentSearchHit> hits)
    {
        string sql = """
            SELECT fc.RepoFullName, fc.FilePath,
                   snippet(github_file_contents_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   github_file_contents_fts.rank,
                   fc.FileExtension, fc.ParserType
            FROM github_file_contents_fts
            JOIN github_file_contents fc ON fc.Id = github_file_contents_fts.rowid
            WHERE github_file_contents_fts MATCH @query
            ORDER BY github_file_contents_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string repo = reader.GetString(0);
            string path = reader.GetString(1);
            string fileId = $"{repo}:{path}";

            hits.Add(new ContentSearchHit
            {
                Source = SourceSystems.GitHub,
                ContentType = ContentTypes.File,
                Id = fileId,
                Title = path,
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = $"https://github.com/{repo}/blob/main/{path}",
                MatchedValue = matchedValue,
            });
        }
    }

    private static void SearchCommitsFts(
        SqliteConnection connection, string ftsQuery, int maxResults, string matchedValue, List<ContentSearchHit> hits)
    {
        string sql = """
            SELECT gc.Sha, gc.Message, gc.Author, gc.Date, gc.Url,
                   github_commits_fts.rank
            FROM github_commits_fts
            JOIN github_commits gc ON gc.Id = github_commits_fts.rowid
            WHERE github_commits_fts MATCH @query
            ORDER BY github_commits_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new ContentSearchHit
            {
                Source = SourceSystems.GitHub,
                ContentType = ContentTypes.Commit,
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Score = -reader.GetDouble(5),
                Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedAt = GitHubUrlHelper.ParseTimestamp(reader, 3),
                MatchedValue = matchedValue,
            });
        }
    }
}
