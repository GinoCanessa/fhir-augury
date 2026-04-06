using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Api.Controllers;

[ApiController]
[Route("api/v1/items")]
public class ItemsController(GitHubDatabase db) : ControllerBase
{
    [HttpGet]
    public IActionResult ListItems([FromQuery] int? limit, [FromQuery] int? offset)
    {
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT UniqueKey, Title, State, IsPullRequest, UpdatedAt FROM github_issues ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<ItemSummary> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string uniqueKey = reader.GetString(0);
            items.Add(new ItemSummary
            {
                Id = uniqueKey,
                Title = reader.GetString(1),
                Url = GitHubUrlHelper.BuildIssueUrl(uniqueKey),
                UpdatedAt = GitHubUrlHelper.ParseTimestamp(reader, 4),
                Metadata = new Dictionary<string, string>
                {
                    ["state"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ["is_pull_request"] = reader.GetBoolean(3).ToString(),
                },
            });
        }

        return Ok(new ItemListResponse(items.Count, items));
    }

    [HttpGet("related/{*key}")]
    public IActionResult GetRelated([FromRoute] string key, [FromQuery] int? limit, [FromQuery] string? seedSource)
    {
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 10, 50);

        // Cross-source related: if seedSource is Jira, find GitHub items referencing that Jira key
        if (!string.IsNullOrEmpty(seedSource) &&
            string.Equals(seedSource, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(connection, JiraKey: key);
            List<RelatedItem> jiraRelated = [];
            HashSet<string> seen = [];

            foreach (JiraXRefRecord jiraRef in refs)
            {
                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, jiraRef);
                if (resolved is null || !seen.Add(resolved.Id)) continue;

                jiraRelated.Add(new RelatedItem
                {
                    Source = SourceSystems.GitHub,
                    Id = resolved.Id,
                    Title = resolved.Title,
                    Url = resolved.Url,
                    RelevanceScore = 1.0,
                });
                if (jiraRelated.Count >= maxResults) break;
            }

            return Ok(new FindRelatedResponse(seedSource, key, null, jiraRelated));
        }

        // Same-source related via shared Jira keys
        List<JiraXRefRecord> sourceRefs = JiraXRefRecord.SelectList(connection, SourceId: key);
        HashSet<string> relatedIds = [];
        List<RelatedItem> results = [];

        foreach (JiraXRefRecord jiraRef in sourceRefs)
        {
            List<JiraXRefRecord> sameKeyRefs = JiraXRefRecord.SelectList(connection, JiraKey: jiraRef.JiraKey);
            foreach (JiraXRefRecord r in sameKeyRefs)
            {
                if (r.SourceId == key) continue;

                GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(connection, r);
                if (resolved is null || !relatedIds.Add(resolved.Id)) continue;

                results.Add(new RelatedItem
                {
                    Source = SourceSystems.GitHub,
                    Id = resolved.Id,
                    Title = resolved.Title,
                    Url = resolved.Url,
                    RelevanceScore = 1.0,
                });
                if (results.Count >= maxResults) break;
            }
            if (results.Count >= maxResults) break;
        }

        return Ok(new FindRelatedResponse(SourceSystems.GitHub, key, null, results));
    }

    [HttpGet("snapshot/{*key}")]
    public IActionResult GetSnapshot([FromRoute] string key, [FromQuery] bool? includeComments, [FromQuery] bool? includeRefs)
    {
        using SqliteConnection connection = db.OpenConnection();

        // Check if this is a file content request
        if (GitHubUrlHelper.TryParseFileId(key, out string? fileRepo, out string? filePath))
        {
            GitHubFileContentRecord? file = GitHubUrlHelper.LookupFileRecord(connection, fileRepo!, filePath!);
            if (file is not null)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
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

                return Ok(new SnapshotResponse(
                    key, SourceSystems.GitHub, sb.ToString(),
                    $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
                    ContentTypes.File));
            }
        }

        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        string md = GitHubUrlHelper.BuildMarkdownSnapshot(connection, issue, includeComments ?? true, includeRefs ?? false);

        return Ok(new SnapshotResponse(
            issue.UniqueKey, SourceSystems.GitHub, md,
            GitHubUrlHelper.BuildIssueUrl(issue.UniqueKey), null));
    }

    [HttpGet("content/{*key}")]
    public IActionResult GetContent([FromRoute] string key, [FromQuery] string? format)
    {
        using SqliteConnection connection = db.OpenConnection();

        // Check if this is a file content request
        if (GitHubUrlHelper.TryParseFileId(key, out string? fileRepo, out string? filePath))
        {
            GitHubFileContentRecord? file = GitHubUrlHelper.LookupFileRecord(connection, fileRepo!, filePath!);
            if (file is not null)
            {
                return Ok(new ContentResponse(
                    key, SourceSystems.GitHub,
                    file.ContentText ?? "", "text",
                    $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
                    null, ContentTypes.File));
            }
        }

        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        return Ok(new ContentResponse(
            issue.UniqueKey, SourceSystems.GitHub,
            issue.Body ?? "", format ?? "markdown",
            GitHubUrlHelper.BuildIssueUrl(issue.UniqueKey), null, null));
    }

    // IMPORTANT: This catch-all route MUST be defined last or use Order attribute
    [HttpGet("{*key}"), ActionName("GetItem")]
    public IActionResult GetItem([FromRoute] string key, [FromQuery] bool? includeContent, [FromQuery] bool? includeComments)
    {
        using SqliteConnection connection = db.OpenConnection();

        // Check if this is a file content request (format: "owner/repo:path/to/file")
        if (GitHubUrlHelper.TryParseFileId(key, out string? fileRepo, out string? filePath))
        {
            GitHubFileContentRecord? file = GitHubUrlHelper.LookupFileRecord(connection, fileRepo!, filePath!);
            if (file is not null)
            {
                return Ok(new ItemResponse
                {
                    Source = SourceSystems.GitHub,
                    ContentType = ContentTypes.File,
                    Id = key,
                    Title = file.FilePath,
                    Content = includeContent != false ? (file.ContentText ?? "") : null,
                    Url = $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["repo"] = file.RepoFullName,
                        ["file_path"] = file.FilePath,
                        ["extension"] = file.FileExtension,
                        ["parser_type"] = file.ParserType,
                        ["content_length"] = file.ContentLength.ToString(),
                        ["extracted_length"] = file.ExtractedLength.ToString(),
                        ["last_commit_sha"] = file.LastCommitSha ?? "",
                        ["last_modified_at"] = file.LastModifiedAt ?? "",
                    },
                });
            }
        }

        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        List<CommentInfo>? comments = null;
        if (includeComments != false)
        {
            List<GitHubCommentRecord> commentRecords = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            comments = commentRecords.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body, c.CreatedAt,
                $"https://github.com/{c.RepoFullName}/issues/{c.IssueNumber}#issuecomment-{c.Id}")).ToList();
        }

        List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, SourceId: issue.UniqueKey);
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
        if (issue.Milestone is not null) metadata["milestone"] = issue.Milestone;
        if (issue.MergeState is not null) metadata["merge_state"] = issue.MergeState;
        if (issue.ClosedAt is not null) metadata["closed_at"] = issue.ClosedAt.Value.ToString("o");
        if (jiraRefs.Count > 0) metadata["jira_refs"] = string.Join(",", jiraRefs.Select(r => r.JiraKey));

        return Ok(new ItemResponse
        {
            Source = SourceSystems.GitHub,
            Id = issue.UniqueKey,
            Title = issue.Title,
            Content = includeContent != false ? issue.Body : null,
            Url = GitHubUrlHelper.BuildIssueUrl(issue.UniqueKey),
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            Metadata = metadata,
            Comments = comments,
        });
    }
}