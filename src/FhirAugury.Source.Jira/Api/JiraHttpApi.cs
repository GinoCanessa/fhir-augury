using FhirAugury.Common.Caching;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text;

namespace FhirAugury.Source.Jira.Api;

/// <summary>HTTP Minimal API endpoints for standalone use and debugging.</summary>
public static class JiraHttpApi
{
    public static IEndpointRouteBuilder MapJiraHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        api.MapGet("/search", (string? q, int? limit, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new { query = q, results = Array.Empty<object>() });

            int maxResults = Math.Min(limit ?? 20, 200);

            string sql = """
                SELECT ji.Key, ji.Title,
                       snippet(jira_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                       jira_issues_fts.rank, ji.Status, ji.UpdatedAt
                FROM jira_issues_fts
                JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
                WHERE jira_issues_fts MATCH @query
                ORDER BY jira_issues_fts.rank
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            List<object> results = new List<object>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                results.Add(new
                {
                    key,
                    title = reader.GetString(1),
                    snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    score = -reader.GetDouble(3),
                    status = reader.IsDBNull(4) ? null : reader.GetString(4),
                    url = $"{options.BaseUrl}/browse/{key}",
                });
            }

            return Results.Ok(new { query = q, total = results.Count, results });
        });

        api.MapGet("/items/{key}", (string key, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
            List<JiraIssueLinkRecord> targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

            return Results.Ok(new
            {
                issue.Key,
                issue.ProjectKey,
                issue.Title,
                issue.Description,
                issue.Type,
                issue.Priority,
                issue.Status,
                issue.Resolution,
                issue.ResolutionDescription,
                issue.Assignee,
                issue.Reporter,
                issue.CreatedAt,
                issue.UpdatedAt,
                issue.ResolvedAt,
                issue.WorkGroup,
                issue.Specification,
                issue.Labels,
                issue.CommentCount,
                url = $"{options.BaseUrl}/browse/{key}",
                comments = comments.Select(c => new { c.Author, c.Body, c.CreatedAt }),
                links = links.Select(l => new { l.TargetKey, l.LinkType })
                    .Concat(targetLinks.Select(l => new { TargetKey = l.SourceKey, l.LinkType })),
            });
        });

        api.MapGet("/items", (int? limit, int? offset, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            using SqliteCommand cmd = new SqliteCommand(
                "SELECT Key, Title, Status, Type, UpdatedAt FROM jira_issues ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
                connection);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<object> items = new List<object>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new
                {
                    key = reader.GetString(0),
                    title = reader.GetString(1),
                    status = reader.IsDBNull(2) ? null : reader.GetString(2),
                    type = reader.IsDBNull(3) ? null : reader.GetString(3),
                    updatedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }

            return Results.Ok(new { total = items.Count, items });
        });

        api.MapGet("/items/{key}/related", (string key, int? limit, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 10, 50);

            List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
            List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

            IEnumerable<(string Key, string LinkType)> relatedKeys = outLinks.Select(l => (Key: l.TargetKey, l.LinkType))
                .Concat(inLinks.Select(l => (Key: l.SourceKey, l.LinkType)))
                .DistinctBy(x => x.Key)
                .Take(maxResults);

            List<object> results = new List<object>();
            foreach ((string? relKey, string? linkType) in relatedKeys)
            {
                JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: relKey);
                if (issue is null) continue;
                results.Add(new
                {
                    key = issue.Key,
                    title = issue.Title,
                    status = issue.Status,
                    linkType,
                    url = $"{options.BaseUrl}/browse/{issue.Key}",
                });
            }

            return Results.Ok(new { sourceKey = key, related = results });
        });

        api.MapGet("/items/{key}/snapshot", (string key, JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) =>
        {
            JiraServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);

            StringBuilder md = new System.Text.StringBuilder();
            md.AppendLine($"# {issue.Key}: {issue.Title}");
            md.AppendLine();
            md.AppendLine($"**Status:** {issue.Status} | **Type:** {issue.Type} | **Priority:** {issue.Priority}");
            if (issue.WorkGroup is not null) md.AppendLine($"**Work Group:** {issue.WorkGroup}");
            if (issue.Specification is not null) md.AppendLine($"**Specification:** {issue.Specification}");
            md.AppendLine();
            if (issue.Description is not null) { md.AppendLine("## Description"); md.AppendLine(issue.Description); md.AppendLine(); }
            if (comments.Count > 0)
            {
                md.AppendLine("## Comments");
                foreach (JiraCommentRecord c in comments) { md.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}"); md.AppendLine(); }
            }

            return Results.Ok(new { key, markdown = md.ToString(), url = $"{options.BaseUrl}/browse/{key}" });
        });

        api.MapGet("/items/{key}/content", (string key, string? format, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (issue is null)
                return Results.NotFound(new { error = $"Issue {key} not found" });

            return Results.Ok(new { key, content = issue.Description ?? "", format = format ?? "text" });
        });

        api.MapPost("/ingest", async (HttpRequest req, JiraIngestionPipeline pipeline) =>
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

        api.MapGet("/status", (JiraIngestionPipeline pipeline, JiraDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

            return Results.Ok(new
            {
                isRunning = pipeline.IsRunning,
                currentStatus = pipeline.CurrentStatus,
                lastSyncAt = syncState?.LastSyncAt,
                itemsIngested = syncState?.ItemsIngested ?? 0,
                lastError = syncState?.LastError,
            });
        });

        api.MapPost("/rebuild", async (JiraIngestionPipeline pipeline) =>
        {
            try
            {
                IngestionResult result = await pipeline.RebuildFromCacheAsync();
                return Results.Ok(new { result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapGet("/stats", (JiraDatabase db, FhirAugury.Common.Caching.IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int issueCount = JiraIssueRecord.SelectCount(connection);
            int commentCount = JiraCommentRecord.SelectCount(connection);
            int linkCount = JiraIssueLinkRecord.SelectCount(connection);
            int specCount = JiraSpecArtifactRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats xmlStats = cache.GetStats(JiraCacheLayout.XmlSource);
            CacheStats jsonStats = cache.GetStats(JiraCacheLayout.JsonSource);

            return Results.Ok(new
            {
                source = "jira",
                totalIssues = issueCount,
                totalComments = commentCount,
                totalLinks = linkCount,
                totalSpecArtifacts = specCount,
                databaseSizeBytes = dbSize,
                cacheSizeBytes = xmlStats.TotalBytes + jsonStats.TotalBytes,
                cacheFiles = xmlStats.FileCount + jsonStats.FileCount,
                cacheXmlFiles = xmlStats.FileCount,
                cacheJsonFiles = jsonStats.FileCount,
            });
        });

        return app;
    }
}
