using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Api;

/// <summary>HTTP Minimal API endpoints for standalone use and debugging.</summary>
public static class GitHubHttpApi
{
    public static IEndpointRouteBuilder MapGitHubHttpApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/search", (string? q, int? limit, GitHubDatabase db) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using var connection = db.OpenConnection();
            var ftsQuery = SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new { query = q, results = Array.Empty<object>() });

            var maxResults = Math.Min(limit ?? 20, 200);

            var sql = """
                SELECT gi.UniqueKey, gi.Title,
                       snippet(github_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                       github_issues_fts.rank, gi.State, gi.UpdatedAt
                FROM github_issues_fts
                JOIN github_issues gi ON gi.Id = github_issues_fts.rowid
                WHERE github_issues_fts MATCH @query
                ORDER BY github_issues_fts.rank
                LIMIT @limit
                """;

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            var results = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uniqueKey = reader.GetString(0);
                results.Add(new
                {
                    key = uniqueKey,
                    title = reader.GetString(1),
                    snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    score = -reader.GetDouble(3),
                    state = reader.IsDBNull(4) ? null : reader.GetString(4),
                    url = GitHubGrpcService.BuildIssueUrl(uniqueKey),
                });
            }

            return Results.Ok(new { query = q, total = results.Count, results });
        });

        api.MapGet("/items/{*key}", (string key, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            var comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            var jiraRefs = GitHubJiraRefRecord.SelectList(connection, SourceId: issue.UniqueKey);

            return Results.Ok(new
            {
                issue.UniqueKey,
                issue.RepoFullName,
                issue.Number,
                issue.IsPullRequest,
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
                url = GitHubGrpcService.BuildIssueUrl(issue.UniqueKey),
                comments = comments.Select(c => new { c.Author, c.Body, c.CreatedAt }),
                jiraRefs = jiraRefs.Select(r => new { r.JiraKey, r.Context }),
            });
        });

        api.MapGet("/items", (int? limit, int? offset, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var maxResults = Math.Min(limit ?? 50, 500);
            var skip = Math.Max(offset ?? 0, 0);

            using var cmd = new SqliteCommand(
                "SELECT UniqueKey, Title, State, IsPullRequest, UpdatedAt FROM github_issues ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
                connection);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            var items = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new
                {
                    key = reader.GetString(0),
                    title = reader.GetString(1),
                    state = reader.IsDBNull(2) ? null : reader.GetString(2),
                    isPullRequest = reader.GetBoolean(3),
                    updatedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }

            return Results.Ok(new { total = items.Count, items });
        });

        api.MapGet("/related/{*key}", (string key, int? limit, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var maxResults = Math.Min(limit ?? 10, 50);

            var refs = GitHubJiraRefRecord.SelectList(connection, SourceId: key);
            var relatedIds = new HashSet<string>();

            foreach (var jiraRef in refs)
            {
                var sameKeyRefs = GitHubJiraRefRecord.SelectList(connection, JiraKey: jiraRef.JiraKey);
                foreach (var r in sameKeyRefs)
                {
                    if (r.SourceId != key)
                        relatedIds.Add(r.SourceId);
                }
            }

            var results = new List<object>();
            foreach (var relatedId in relatedIds.Take(maxResults))
            {
                var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: relatedId);
                if (issue is null) continue;
                results.Add(new
                {
                    key = issue.UniqueKey,
                    title = issue.Title,
                    state = issue.State,
                    url = GitHubGrpcService.BuildIssueUrl(issue.UniqueKey),
                });
            }

            return Results.Ok(new { sourceKey = key, related = results });
        });

        api.MapGet("/snapshot/{*key}", (string key, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            var comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);

            var md = new System.Text.StringBuilder();
            md.AppendLine($"# {issue.UniqueKey}: {issue.Title}");
            md.AppendLine();
            md.AppendLine($"**State:** {issue.State} | **Type:** {(issue.IsPullRequest ? "PR" : "Issue")}");
            if (issue.Author is not null) md.AppendLine($"**Author:** {issue.Author}");
            md.AppendLine();
            if (issue.Body is not null) { md.AppendLine("## Description"); md.AppendLine(issue.Body); md.AppendLine(); }
            if (comments.Count > 0)
            {
                md.AppendLine("## Comments");
                foreach (var c in comments) { md.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}"); md.AppendLine(); }
            }

            return Results.Ok(new { key, markdown = md.ToString(), url = GitHubGrpcService.BuildIssueUrl(key) });
        });

        api.MapGet("/content/{*key}", (string key, string? format, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            return Results.Ok(new { key, content = issue.Body ?? "", format = format ?? "markdown" });
        });

        api.MapPost("/ingest", async (HttpRequest req, GitHubIngestionPipeline pipeline) =>
        {
            var type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                var result = type == "full"
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

        api.MapGet("/status", (GitHubIngestionPipeline pipeline, GitHubDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: GitHubSource.SourceName);

            return Results.Ok(new
            {
                isRunning = pipeline.IsRunning,
                currentStatus = pipeline.CurrentStatus,
                lastSyncAt = syncState?.LastSyncAt,
                itemsIngested = syncState?.ItemsIngested ?? 0,
                lastError = syncState?.LastError,
            });
        });

        api.MapPost("/rebuild", async (GitHubIngestionPipeline pipeline) =>
        {
            try
            {
                var result = await pipeline.RebuildFromCacheAsync();
                return Results.Ok(new { result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapGet("/stats", (GitHubDatabase db, FhirAugury.Common.Caching.IResponseCache cache) =>
        {
            using var connection = db.OpenConnection();
            var issueCount = GitHubIssueRecord.SelectCount(connection);
            var commentCount = GitHubCommentRecord.SelectCount(connection);
            var commitCount = GitHubCommitRecord.SelectCount(connection);
            var repoCount = GitHubRepoRecord.SelectCount(connection);
            var jiraRefCount = GitHubJiraRefRecord.SelectCount(connection);
            var dbSize = db.GetDatabaseSizeBytes();
            var cacheStats = cache.GetStats(GitHubCacheLayout.SourceName);

            return Results.Ok(new
            {
                source = "github",
                totalIssues = issueCount,
                totalComments = commentCount,
                totalCommits = commitCount,
                totalRepos = repoCount,
                totalJiraRefs = jiraRefCount,
                databaseSizeBytes = dbSize,
                cacheSizeBytes = cacheStats.TotalBytes,
                cacheFiles = cacheStats.FileCount,
            });
        });

        return app;
    }

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }
}
