using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Confluence.Api;

/// <summary>HTTP Minimal API endpoints for standalone use and debugging.</summary>
public static class ConfluenceHttpApi
{
    public static IEndpointRouteBuilder MapConfluenceHttpApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/search", (string? q, int? limit, ConfluenceDatabase db, ConfluenceServiceOptions options) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using var connection = db.OpenConnection();
            var ftsQuery = SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new { query = q, results = Array.Empty<object>() });

            var maxResults = Math.Min(limit ?? 20, 200);

            var sql = """
                SELECT cp.ConfluenceId, cp.Title,
                       snippet(confluence_pages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       confluence_pages_fts.rank, cp.SpaceKey, cp.LastModifiedAt
                FROM confluence_pages_fts
                JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
                WHERE confluence_pages_fts MATCH @query
                ORDER BY confluence_pages_fts.rank
                LIMIT @limit
                """;

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            var results = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pageId = reader.GetString(0);
                results.Add(new
                {
                    pageId,
                    title = reader.GetString(1),
                    snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    score = -reader.GetDouble(3),
                    spaceKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                    url = $"{options.BaseUrl}/pages/{pageId}",
                });
            }

            return Results.Ok(new { query = q, total = results.Count, results });
        });

        api.MapGet("/pages/{pageId}", (string pageId, ConfluenceDatabase db, ConfluenceServiceOptions options) =>
        {
            using var connection = db.OpenConnection();
            var page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            var comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            var outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: pageId);

            return Results.Ok(new
            {
                page.ConfluenceId,
                page.SpaceKey,
                page.Title,
                bodyPlain = page.BodyPlain,
                page.Labels,
                page.VersionNumber,
                page.LastModifiedBy,
                page.LastModifiedAt,
                page.ParentId,
                url = page.Url ?? $"{options.BaseUrl}/pages/{pageId}",
                comments = comments.Select(c => new { c.Author, c.Body, c.CreatedAt }),
                links = outLinks.Select(l => new { l.TargetPageId, l.LinkType }),
            });
        });

        api.MapGet("/pages", (int? limit, int? offset, string? spaceKey, ConfluenceDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var maxResults = Math.Min(limit ?? 50, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var sql = "SELECT ConfluenceId, Title, SpaceKey, LastModifiedAt FROM confluence_pages";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(spaceKey))
            {
                sql += " WHERE SpaceKey = @spaceKey";
                parameters.Add(new SqliteParameter("@spaceKey", spaceKey));
            }

            sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
            parameters.Add(new SqliteParameter("@limit", maxResults));
            parameters.Add(new SqliteParameter("@offset", skip));

            using var cmd = new SqliteCommand(sql, connection);
            foreach (var p in parameters) cmd.Parameters.Add(p);

            var items = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new
                {
                    pageId = reader.GetString(0),
                    title = reader.GetString(1),
                    spaceKey = reader.IsDBNull(2) ? null : reader.GetString(2),
                    lastModifiedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }

            return Results.Ok(new { total = items.Count, items });
        });

        api.MapGet("/spaces", (ConfluenceDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var spaces = ConfluenceSpaceRecord.SelectList(connection);

            return Results.Ok(new
            {
                total = spaces.Count,
                spaces = spaces.Select(s => new { s.Key, s.Name, s.Description, s.Url, s.LastFetchedAt }),
            });
        });

        api.MapPost("/ingest", async (HttpRequest req, ConfluenceIngestionPipeline pipeline) =>
        {
            var type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                var result = type == "full"
                    ? await pipeline.RunFullIngestionAsync(req.HttpContext.RequestAborted)
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

        api.MapGet("/status", (ConfluenceIngestionPipeline pipeline, ConfluenceDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);

            return Results.Ok(new
            {
                isRunning = pipeline.IsRunning,
                currentStatus = pipeline.CurrentStatus,
                lastSyncAt = syncState?.LastSyncAt,
                itemsIngested = syncState?.ItemsIngested ?? 0,
                lastError = syncState?.LastError,
            });
        });

        api.MapPost("/rebuild", async (ConfluenceIngestionPipeline pipeline) =>
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

        api.MapGet("/stats", (ConfluenceDatabase db, FhirAugury.Common.Caching.IResponseCache cache) =>
        {
            using var connection = db.OpenConnection();
            var pageCount = ConfluencePageRecord.SelectCount(connection);
            var commentCount = ConfluenceCommentRecord.SelectCount(connection);
            var spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
            var linkCount = ConfluencePageLinkRecord.SelectCount(connection);
            var dbSize = db.GetDatabaseSizeBytes();
            var cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

            return Results.Ok(new
            {
                source = "confluence",
                totalPages = pageCount,
                totalComments = commentCount,
                totalSpaces = spaceCount,
                totalLinks = linkCount,
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
