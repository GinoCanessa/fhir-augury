using System.Globalization;
using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Api;

/// <summary>HTTP Minimal API endpoints — the sole API surface for the GitHub source service.</summary>
public static class GitHubHttpApi
{
    public static IEndpointRouteBuilder MapGitHubHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        MapSearchEndpoints(api);
        MapItemEndpoints(api);
        MapCrossReferenceEndpoints(api);
        MapGitHubSpecificEndpoints(api);
        MapIngestionEndpoints(api);
        MapLifecycleEndpoints(api);

        return app;
    }

    // ── Search ───────────────────────────────────────────────────

    private static void MapSearchEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/search", (string? q, int? limit, int? offset, GitHubDatabase db) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new SearchResponse(q, 0, [], null));

            int maxResults = Math.Min(limit ?? 20, 200);
            int skip = Math.Max(offset ?? 0, 0);

            List<SearchResult> results = [];

            // Search issues
            string issueSql = """
                SELECT gi.UniqueKey, gi.Title,
                       snippet(github_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                       github_issues_fts.rank, gi.State, gi.UpdatedAt
                FROM github_issues_fts
                JOIN github_issues gi ON gi.Id = github_issues_fts.rowid
                WHERE github_issues_fts MATCH @query
                ORDER BY github_issues_fts.rank
                LIMIT @limit OFFSET @offset
                """;

            using (SqliteCommand cmd = new SqliteCommand(issueSql, connection))
            {
                cmd.Parameters.AddWithValue("@query", ftsQuery);
                cmd.Parameters.AddWithValue("@limit", maxResults);
                cmd.Parameters.AddWithValue("@offset", skip);

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string uniqueKey = reader.GetString(0);
                    results.Add(new SearchResult
                    {
                        Source = SourceSystems.GitHub,
                        Id = uniqueKey,
                        Title = reader.GetString(1),
                        Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Score = -reader.GetDouble(3),
                        Url = BuildIssueUrl(uniqueKey),
                        UpdatedAt = ParseTimestamp(reader, 5),
                    });
                }
            }

            // Search file contents
            string fileSql = """
                SELECT fc.RepoFullName, fc.FilePath,
                       snippet(github_file_contents_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       github_file_contents_fts.rank,
                       fc.FileExtension, fc.ParserType
                FROM github_file_contents_fts
                JOIN github_file_contents fc ON fc.Id = github_file_contents_fts.rowid
                WHERE github_file_contents_fts MATCH @query
                ORDER BY github_file_contents_fts.rank
                LIMIT @limit OFFSET @offset
                """;

            using (SqliteCommand cmd = new SqliteCommand(fileSql, connection))
            {
                cmd.Parameters.AddWithValue("@query", ftsQuery);
                cmd.Parameters.AddWithValue("@limit", maxResults);
                cmd.Parameters.AddWithValue("@offset", skip);

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string repo = reader.GetString(0);
                    string filePath = reader.GetString(1);
                    string fileId = $"{repo}:{filePath}";

                    results.Add(new SearchResult
                    {
                        Source = SourceSystems.GitHub,
                        ContentType = ContentTypes.File,
                        Id = fileId,
                        Title = filePath,
                        Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Score = -reader.GetDouble(3),
                        Url = $"https://github.com/{repo}/blob/main/{filePath}",
                    });
                }
            }

            return Results.Ok(new SearchResponse(q, results.Count, results, null));
        });

        api.MapGet("/commits/search", (string? q, int? limit, int? offset, GitHubDatabase db) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new SearchResponse(q, 0, [], null));

            int maxResults = Math.Min(limit ?? 20, 200);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = """
                SELECT gc.Sha, gc.Message, gc.Author, gc.Date, gc.Url,
                       github_commits_fts.rank
                FROM github_commits_fts
                JOIN github_commits gc ON gc.Id = github_commits_fts.rowid
                WHERE github_commits_fts MATCH @query
                ORDER BY github_commits_fts.rank
                LIMIT @limit OFFSET @offset
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<SearchResult> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new SearchResult
                {
                    Source = SourceSystems.GitHub,
                    ContentType = ContentTypes.Commit,
                    Id = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Score = -reader.GetDouble(5),
                    Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                    UpdatedAt = ParseTimestamp(reader, 3),
                });
            }

            return Results.Ok(new SearchResponse(q, results.Count, results, null));
        });
    }

    // ── Items ────────────────────────────────────────────────────

    private static void MapItemEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/items", (int? limit, int? offset, GitHubDatabase db) =>
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
                    Url = BuildIssueUrl(uniqueKey),
                    UpdatedAt = ParseTimestamp(reader, 4),
                    Metadata = new Dictionary<string, string>
                    {
                        ["state"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ["is_pull_request"] = reader.GetBoolean(3).ToString(),
                    },
                });
            }

            return Results.Ok(new ItemListResponse(items.Count, items));
        });

        api.MapGet("/items/{*key}", (string key, bool? includeContent, bool? includeComments, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();

            // Check if this is a file content request (format: "owner/repo:path/to/file")
            if (TryParseFileId(key, out string? fileRepo, out string? filePath))
            {
                GitHubFileContentRecord? file = LookupFileRecord(connection, fileRepo!, filePath!);
                if (file is not null)
                {
                    return Results.Ok(new ItemResponse
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
                return Results.NotFound(new { error = $"Issue {key} not found" });

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

            return Results.Ok(new ItemResponse
            {
                Source = SourceSystems.GitHub,
                Id = issue.UniqueKey,
                Title = issue.Title,
                Content = includeContent != false ? issue.Body : null,
                Url = BuildIssueUrl(issue.UniqueKey),
                CreatedAt = issue.CreatedAt,
                UpdatedAt = issue.UpdatedAt,
                Metadata = metadata,
                Comments = comments,
            });
        });

        // Catch-all {*key} must be the final segment in ASP.NET routing, so we use
        // /items/related/{*key} instead of /items/{*key}/related.
        api.MapGet("/items/related/{*key}", (string key, int? limit, string? seedSource, GitHubDatabase db) =>
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
                    ResolvedItem? resolved = ResolveXRef(connection, jiraRef);
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

                return Results.Ok(new FindRelatedResponse(seedSource, key, null, jiraRelated));
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

                    ResolvedItem? resolved = ResolveXRef(connection, r);
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

            return Results.Ok(new FindRelatedResponse(SourceSystems.GitHub, key, null, results));
        });

        api.MapGet("/items/snapshot/{*key}", (string key, bool? includeComments, bool? includeRefs, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();

            // Check if this is a file content request
            if (TryParseFileId(key, out string? fileRepo, out string? filePath))
            {
                GitHubFileContentRecord? file = LookupFileRecord(connection, fileRepo!, filePath!);
                if (file is not null)
                {
                    StringBuilder sb = new StringBuilder();
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

                    return Results.Ok(new SnapshotResponse(
                        key, SourceSystems.GitHub, sb.ToString(),
                        $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
                        ContentTypes.File));
                }
            }

            GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            string md = BuildMarkdownSnapshot(connection, issue, includeComments ?? true, includeRefs ?? false);

            return Results.Ok(new SnapshotResponse(
                issue.UniqueKey, SourceSystems.GitHub, md,
                BuildIssueUrl(issue.UniqueKey), null));
        });

        api.MapGet("/items/content/{*key}", (string key, string? format, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();

            // Check if this is a file content request
            if (TryParseFileId(key, out string? fileRepo, out string? filePath))
            {
                GitHubFileContentRecord? file = LookupFileRecord(connection, fileRepo!, filePath!);
                if (file is not null)
                {
                    return Results.Ok(new ContentResponse(
                        key, SourceSystems.GitHub,
                        file.ContentText ?? "", "text",
                        $"https://github.com/{file.RepoFullName}/blob/main/{file.FilePath}",
                        null, ContentTypes.File));
                }
            }

            GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            return Results.Ok(new ContentResponse(
                issue.UniqueKey, SourceSystems.GitHub,
                issue.Body ?? "", format ?? "markdown",
                BuildIssueUrl(issue.UniqueKey), null, null));
        });
    }

    // ── Cross-references ─────────────────────────────────────────

    private static void MapCrossReferenceEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/xref/{*key}", (string key, string? source, string? direction, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            string dir = direction?.ToLowerInvariant() ?? "both";
            string sourceType = source ?? SourceSystems.GitHub;
            List<SourceCrossReference> refs = [];

            if (string.Equals(sourceType, SourceSystems.GitHub, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
            {
                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: key))
                {
                    ResolvedItem? resolved = ResolveXRef(connection, r);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.GitHub, r.SourceId, SourceSystems.Jira, r.JiraKey,
                        "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
                }

                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: key))
                {
                    ResolvedItem? resolved = ResolveXRef(connection, r);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.GitHub, r.SourceId, SourceSystems.Zulip, r.TargetId,
                        "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
                }

                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: key))
                {
                    ResolvedItem? resolved = ResolveXRef(connection, r);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.GitHub, r.SourceId, SourceSystems.Confluence, r.TargetId,
                        "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: key))
                {
                    ResolvedItem? resolved = ResolveXRef(connection, r);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.GitHub, r.SourceId, SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
                }
            }

            if (string.Equals(sourceType, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
            {
                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, JiraKey: key))
                {
                    ResolvedItem? resolved = ResolveXRef(connection, r);
                    refs.Add(new SourceCrossReference(
                        SourceSystems.GitHub, r.SourceId, SourceSystems.Jira, r.JiraKey,
                        "mentions", r.Context, r.ContentType, resolved?.Title, resolved?.Url));
                }
            }

            return Results.Ok(new CrossReferenceResponse(sourceType, key, dir, refs));
        });
    }

    // ── GitHub-specific endpoints ────────────────────────────────

    private static void MapGitHubSpecificEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/items/comments/{*key}", (string key, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            (string repo, int number) = ParseIssueKey(key);

            List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: repo, IssueNumber: number);

            List<CommentInfo> result = comments.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body, c.CreatedAt,
                $"https://github.com/{c.RepoFullName}/issues/{c.IssueNumber}#issuecomment-{c.Id}")).ToList();

            return Results.Ok(new { key, comments = result });
        });

        api.MapGet("/items/commits/{*key}", (string key, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            (string repo, int number) = ParseIssueKey(key);

            // Find commits via PR links
            List<GitHubCommitPrLinkRecord> prLinks = GitHubCommitPrLinkRecord.SelectList(connection,
                PrNumber: number, RepoFullName: repo);

            HashSet<string> writtenShas = [];
            List<object> commits = [];

            foreach (GitHubCommitPrLinkRecord link in prLinks)
            {
                if (!writtenShas.Add(link.CommitSha)) continue;
                GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(connection, Sha: link.CommitSha);
                if (commit is not null)
                    commits.Add(MapCommitToJson(commit, connection));
            }

            // Also find commits mentioning this issue number in messages
            string issueRef = $"#{number}";
            using SqliteCommand cmd = new SqliteCommand(
                "SELECT Sha FROM github_commits WHERE RepoFullName = @repo AND Message LIKE @pattern LIMIT 100",
                connection);
            cmd.Parameters.AddWithValue("@repo", repo);
            cmd.Parameters.AddWithValue("@pattern", $"%{issueRef}%");

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string sha = reader.GetString(0);
                if (!writtenShas.Add(sha)) continue;
                GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(connection, Sha: sha);
                if (commit is not null)
                    commits.Add(MapCommitToJson(commit, connection));
            }

            return Results.Ok(new { key, commits });
        });

        api.MapGet("/items/pr/{*key}", (string key, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            (string repo, int number) = ParseIssueKey(key);

            string uniqueKey = $"{repo}#{number}";
            GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey);
            if (issue is null)
                return Results.NotFound(new { error = $"PR {uniqueKey} not found" });
            if (!issue.IsPullRequest)
                return Results.BadRequest(new { error = $"{uniqueKey} is not a pull request" });

            return Results.Ok(new
            {
                issue.UniqueKey,
                issue.RepoFullName,
                issue.Number,
                issue.Title,
                issue.Body,
                issue.State,
                issue.Author,
                issue.Labels,
                issue.Assignees,
                issue.Milestone,
                issue.CreatedAt,
                issue.UpdatedAt,
                issue.ClosedAt,
                issue.MergeState,
                issue.HeadBranch,
                issue.BaseBranch,
                merged = issue.MergeState == "merged",
                url = BuildIssueUrl(issue.UniqueKey),
            });
        });

        api.MapGet("/repos", (GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            List<GitHubRepoRecord> repos = GitHubRepoRecord.SelectList(connection);

            List<object> result = [];
            foreach (GitHubRepoRecord repo in repos)
            {
                int issueCount = 0, prCount = 0;
                using (SqliteCommand cmd = new SqliteCommand(
                    "SELECT IsPullRequest, COUNT(*) FROM github_issues WHERE RepoFullName = @repo GROUP BY IsPullRequest",
                    connection))
                {
                    cmd.Parameters.AddWithValue("@repo", repo.FullName);
                    using SqliteDataReader reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.GetBoolean(0))
                            prCount = reader.GetInt32(1);
                        else
                            issueCount = reader.GetInt32(1);
                    }
                }

                result.Add(new
                {
                    repo.FullName,
                    repo.Description,
                    issueCount,
                    prCount,
                    url = $"https://github.com/{repo.FullName}",
                    repo.HasIssues,
                });
            }

            return Results.Ok(new { repos = result });
        });

        api.MapGet("/items/jira-refs/{*key}", (string? key, string? repo, string? jiraKey, GitHubDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();

            string sql = "SELECT SourceType, SourceId, RepoFullName, JiraKey, Context FROM github_jira_refs WHERE 1=1";
            List<SqliteParameter> parameters = [];

            if (!string.IsNullOrEmpty(repo))
            {
                sql += " AND RepoFullName = @repo";
                parameters.Add(new SqliteParameter("@repo", repo));
            }

            if (!string.IsNullOrEmpty(jiraKey))
            {
                sql += " AND JiraKey = @jiraKey";
                parameters.Add(new SqliteParameter("@jiraKey", jiraKey));
            }

            // If key is provided as the route parameter, filter by SourceId
            if (!string.IsNullOrEmpty(key))
            {
                sql += " AND SourceId = @sourceId";
                parameters.Add(new SqliteParameter("@sourceId", key));
            }

            sql += " ORDER BY JiraKey";

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

            List<object> refs = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                refs.Add(new
                {
                    sourceType = reader.GetString(0),
                    sourceId = reader.GetString(1),
                    repoFullName = reader.GetString(2),
                    jiraKey = reader.GetString(3),
                    context = reader.IsDBNull(4) ? "" : reader.GetString(4),
                });
            }

            return Results.Ok(new { jiraRefs = refs });
        });
    }

    // ── Ingestion ────────────────────────────────────────────────

    private static void MapIngestionEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/ingest", async (HttpRequest req, GitHubIngestionPipeline pipeline) =>
        {
            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                IngestionResult result = type == "full"
                    ? await pipeline.RunFullIngestionAsync(ct: req.HttpContext.RequestAborted)
                    : await pipeline.RunIncrementalIngestionAsync(req.HttpContext.RequestAborted);

                return Results.Ok(new
                {
                    result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated, result.ItemsFailed,
                    errors = result.Errors,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPost("/rebuild", async (GitHubIngestionPipeline pipeline) =>
        {
            Common.Api.RebuildResponse result = await HttpServiceLifecycle.RebuildFromCacheAsync(
                async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
                CancellationToken.None);
            return result.Success ? Results.Ok(result) : Results.StatusCode(500);
        });

        api.MapPost("/rebuild-index", (
            HttpRequest req,
            IngestionWorkQueue workQueue,
            GitHubDatabase database,
            GitHubIndexer indexer,
            GitHubXRefRebuilder xrefRebuilder,
            GitHubRepoCloner cloner,
            GitHubCommitFileExtractor commitExtractor,
            GitHubFileContentIndexer fileContentIndexer,
            ArtifactFileMapper artifactFileMapper,
            IIndexTracker indexTracker,
            IOptions<GitHubServiceOptions> optionsAccessor) =>
        {
            string indexType = (req.Query["type"].FirstOrDefault() ?? "all").ToLowerInvariant();
            GitHubServiceOptions options = optionsAccessor.Value;

            workQueue.Enqueue(async ct =>
            {
                List<string> repos = [.. options.Repositories, .. options.AdditionalRepositories];
                switch (indexType)
                {
                    case "commits":
                        indexTracker.MarkStarted("commits");
                        try
                        {
                            foreach (string repo in repos)
                            {
                                string path = await cloner.EnsureCloneAsync(repo, ct);
                                await commitExtractor.ExtractAsync(path, repo, ct);
                            }
                            indexTracker.MarkCompleted("commits");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("commits", ex.Message); throw; }
                        break;
                    case "cross-refs":
                        indexTracker.MarkStarted("cross-refs");
                        try
                        {
                            foreach (string repo in repos)
                                xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
                            indexTracker.MarkCompleted("cross-refs");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                        break;
                    case "bm25":
                        indexTracker.MarkStarted("bm25");
                        try
                        {
                            indexer.RebuildFullIndex(ct);
                            indexTracker.MarkCompleted("bm25");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                        break;
                    case "artifact-map":
                        indexTracker.MarkStarted("artifact-map");
                        try
                        {
                            foreach (string repo in repos)
                            {
                                string path = await cloner.EnsureCloneAsync(repo, ct);
                                artifactFileMapper.BuildMappings(repo, path, ct);
                            }
                            indexTracker.MarkCompleted("artifact-map");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("artifact-map", ex.Message); throw; }
                        break;
                    case "file-contents":
                        indexTracker.MarkStarted("file-contents");
                        try
                        {
                            foreach (string repo in repos)
                            {
                                string path = await cloner.EnsureCloneAsync(repo, ct);
                                fileContentIndexer.IndexRepositoryFiles(repo, path, ct);
                            }
                            indexTracker.MarkCompleted("file-contents");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("file-contents", ex.Message); throw; }
                        break;
                    case "fts":
                        indexTracker.MarkStarted("fts");
                        try
                        {
                            database.RebuildFtsIndexes();
                            indexTracker.MarkCompleted("fts");
                        }
                        catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                        break;
                    case "all":
                        indexTracker.MarkStarted("commits");
                        indexTracker.MarkStarted("file-contents");
                        indexTracker.MarkStarted("cross-refs");
                        indexTracker.MarkStarted("artifact-map");
                        indexTracker.MarkStarted("bm25");
                        indexTracker.MarkStarted("fts");
                        try
                        {
                            foreach (string repo in repos)
                            {
                                string path = await cloner.EnsureCloneAsync(repo, ct);
                                await commitExtractor.ExtractAsync(path, repo, ct);
                                fileContentIndexer.IndexRepositoryFiles(repo, path, ct);
                                xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
                                artifactFileMapper.BuildMappings(repo, path, ct);
                            }
                            indexTracker.MarkCompleted("commits");
                            indexTracker.MarkCompleted("file-contents");
                            indexTracker.MarkCompleted("cross-refs");
                            indexTracker.MarkCompleted("artifact-map");
                            indexer.RebuildFullIndex(ct);
                            indexTracker.MarkCompleted("bm25");
                            database.RebuildFtsIndexes();
                            indexTracker.MarkCompleted("fts");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("commits", ex.Message);
                            indexTracker.MarkFailed("file-contents", ex.Message);
                            indexTracker.MarkFailed("cross-refs", ex.Message);
                            indexTracker.MarkFailed("artifact-map", ex.Message);
                            indexTracker.MarkFailed("bm25", ex.Message);
                            indexTracker.MarkFailed("fts", ex.Message);
                            throw;
                        }
                        break;
                }
            }, $"rebuild-index-{indexType}");

            return Results.Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
        });

        api.MapPost("/notify-peer", (
            Common.Api.PeerIngestionNotification notification,
            IngestionWorkQueue workQueue,
            GitHubXRefRebuilder xrefRebuilder,
            IOptions<GitHubServiceOptions> optionsAccessor) =>
        {
            GitHubServiceOptions options = optionsAccessor.Value;
            workQueue.Enqueue(async ct =>
            {
                List<string> repos = [.. options.Repositories, .. options.AdditionalRepositories];
                foreach (string repo in repos)
                    xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
            }, "rebuild-xrefs");

            return Results.Ok(new PeerIngestionAck(Acknowledged: true));
        });
    }

    // ── Lifecycle ────────────────────────────────────────────────

    private static void MapLifecycleEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/status", (GitHubIngestionPipeline pipeline, GitHubDatabase db, IIndexTracker indexTracker,
            IOptions<GitHubServiceOptions> optionsAccessor) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            GitHubSyncStateRecord? syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);
            GitHubServiceOptions options = optionsAccessor.Value;

            return Results.Ok(new IngestionStatusResponse(
                Source: SourceSystems.GitHub,
                Status: pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
                LastSyncAt: syncState?.LastSyncAt,
                ItemsTotal: syncState?.ItemsIngested ?? 0,
                ItemsProcessed: syncState?.ItemsIngested ?? 0,
                LastError: syncState?.LastError,
                SyncSchedule: options.SyncSchedule,
                Indexes: HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses())));
        });

        api.MapGet("/stats", (GitHubDatabase db, IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int issueCount = GitHubIssueRecord.SelectCount(connection);
            int commentCount = GitHubCommentRecord.SelectCount(connection);
            int commitCount = GitHubCommitRecord.SelectCount(connection);
            int repoCount = GitHubRepoRecord.SelectCount(connection);
            int jiraRefCount = JiraXRefRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats cacheStats = cache.GetStats(GitHubCacheLayout.SourceName);

            GitHubSyncStateRecord? syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);

            return Results.Ok(new StatsResponse
            {
                Source = SourceSystems.GitHub,
                TotalItems = issueCount,
                TotalComments = commentCount,
                DatabaseSizeBytes = dbSize,
                CacheSizeBytes = cacheStats.TotalBytes,
                CacheFiles = cacheStats.FileCount,
                LastSyncAt = syncState?.LastSyncAt,
                AdditionalCounts = new Dictionary<string, int>
                {
                    ["repos"] = repoCount,
                    ["commits"] = commitCount,
                    ["jira_refs"] = jiraRefCount,
                    ["spec_file_maps"] = GitHubSpecFileMapRecord.SelectCount(connection),
                    ["file_contents"] = GitHubFileContentRecord.SelectCount(connection),
                },
            });
        });

        api.MapGet("/health", (GitHubDatabase db, GitHubIngestionPipeline pipeline) =>
        {
            return Results.Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    internal static string BuildIssueUrl(string uniqueKey)
    {
        int hashIdx = uniqueKey.IndexOf('#');
        if (hashIdx < 0) return $"https://github.com/{uniqueKey}";
        string repo = uniqueKey[..hashIdx];
        string number = uniqueKey[(hashIdx + 1)..];
        return $"https://github.com/{repo}/issues/{number}";
    }

    /// <summary>Parses a file content ID in "owner/repo:path/to/file" format.</summary>
    private static bool TryParseFileId(string id, out string? repoFullName, out string? filePath)
    {
        int colonIdx = id.IndexOf(':');
        if (colonIdx > 0 && id.Contains('/'))
        {
            repoFullName = id[..colonIdx];
            filePath = id[(colonIdx + 1)..];
            return !string.IsNullOrEmpty(filePath);
        }

        repoFullName = null;
        filePath = null;
        return false;
    }

    private static GitHubFileContentRecord? LookupFileRecord(SqliteConnection connection, string repoFullName, string filePath)
    {
        List<GitHubFileContentRecord> results = GitHubFileContentRecord.SelectList(connection,
            RepoFullName: repoFullName, FilePath: filePath);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>Parses an issue key in "owner/repo#number" format.</summary>
    private static (string Repo, int Number) ParseIssueKey(string key)
    {
        int hashIdx = key.IndexOf('#');
        if (hashIdx < 0)
            throw new ArgumentException($"Invalid issue key format: {key}. Expected owner/repo#number.");
        string repo = key[..hashIdx];
        if (!int.TryParse(key[(hashIdx + 1)..], out int number))
            throw new ArgumentException($"Invalid issue number in key: {key}");
        return (repo, number);
    }

    internal record ResolvedItem(string Id, string Title, string Url, DateTimeOffset UpdatedAt);

    internal static ResolvedItem? ResolveXRef(SqliteConnection conn, ICrossReferenceRecord xref)
    {
        GitHubIssueRecord? issue = xref.ContentType switch
        {
            "issue" => GitHubIssueRecord.SelectSingle(conn, UniqueKey: xref.SourceId),
            "comment" => ResolveCommentToIssue(conn, xref.SourceId),
            "commit" => ResolveCommitToIssue(conn, xref.SourceId),
            _ => null,
        };

        if (issue is not null)
            return new(issue.UniqueKey, issue.Title, BuildIssueUrl(issue.UniqueKey), issue.UpdatedAt);

        if (xref.ContentType == "commit")
        {
            GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(conn, Sha: xref.SourceId);
            if (commit is not null)
                return new(commit.Sha, commit.Message, commit.Url, commit.Date);
        }

        return null;
    }

    private static GitHubIssueRecord? ResolveCommentToIssue(SqliteConnection conn, string sourceId)
    {
        int hashIdx = sourceId.IndexOf('#');
        int colonIdx = sourceId.LastIndexOf(':');
        if (hashIdx < 0 || colonIdx < 0 || colonIdx <= hashIdx) return null;

        string issueKey = sourceId[..colonIdx];
        return GitHubIssueRecord.SelectSingle(conn, UniqueKey: issueKey);
    }

    private static GitHubIssueRecord? ResolveCommitToIssue(SqliteConnection conn, string sourceId)
    {
        GitHubCommitPrLinkRecord? prLink = GitHubCommitPrLinkRecord.SelectSingle(conn, CommitSha: sourceId);
        if (prLink is null) return null;

        string uniqueKey = $"{prLink.RepoFullName}#{prLink.PrNumber}";
        return GitHubIssueRecord.SelectSingle(conn, UniqueKey: uniqueKey);
    }

    private static string BuildMarkdownSnapshot(
        SqliteConnection connection, GitHubIssueRecord issue, bool includeComments, bool includeRefs)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# {issue.UniqueKey}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**State:** {issue.State}  ");
        sb.AppendLine($"**Type:** {(issue.IsPullRequest ? "Pull Request" : "Issue")}  ");
        if (issue.Author is not null) sb.AppendLine($"**Author:** {issue.Author}  ");
        if (issue.Assignees is not null) sb.AppendLine($"**Assignees:** {issue.Assignees}  ");
        if (issue.Labels is not null) sb.AppendLine($"**Labels:** {issue.Labels}  ");
        if (issue.Milestone is not null) sb.AppendLine($"**Milestone:** {issue.Milestone}  ");
        if (issue.MergeState is not null) sb.AppendLine($"**Merge State:** {issue.MergeState}  ");
        if (issue.HeadBranch is not null) sb.AppendLine($"**Head Branch:** {issue.HeadBranch}  ");
        if (issue.BaseBranch is not null) sb.AppendLine($"**Base Branch:** {issue.BaseBranch}  ");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Updated:** {issue.UpdatedAt:yyyy-MM-dd}  ");
        if (issue.ClosedAt is not null) sb.AppendLine($"**Closed:** {issue.ClosedAt:yyyy-MM-dd}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Body))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Body);
            sb.AppendLine();
        }

        if (includeComments)
        {
            List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (GitHubCommentRecord c in comments)
                {
                    sb.AppendLine($"### {c.Author} ({c.CreatedAt:yyyy-MM-dd})");
                    sb.AppendLine();
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (includeRefs)
        {
            List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, SourceId: issue.UniqueKey);
            if (jiraRefs.Count > 0)
            {
                sb.AppendLine("## Jira References");
                sb.AppendLine();
                foreach (JiraXRefRecord r in jiraRefs)
                    sb.AppendLine($"- {r.JiraKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static object MapCommitToJson(GitHubCommitRecord commit, SqliteConnection connection)
    {
        List<GitHubCommitFileRecord> files = GitHubCommitFileRecord.SelectList(connection, CommitSha: commit.Sha);
        return new
        {
            commit.Sha,
            commit.Message,
            commit.Author,
            commit.AuthorEmail,
            commit.Date,
            commit.Url,
            committerName = commit.CommitterName,
            committerEmail = commit.CommitterEmail,
            commit.FilesChanged,
            commit.Insertions,
            commit.Deletions,
            impact = commit.Insertions - commit.Deletions,
            commit.Body,
            commit.Refs,
            changedFiles = files.Select(f => f.FilePath).ToList(),
        };
    }

    private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }
}
