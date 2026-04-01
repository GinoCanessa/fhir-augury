using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Common.Text;
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
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Api;

/// <summary>HTTP Minimal API endpoints for standalone use and debugging.</summary>
public static class ConfluenceHttpApi
{
    public static IEndpointRouteBuilder MapConfluenceHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        api.MapGet("/search", (string? q, int? limit, ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) =>
        {
            ConfluenceServiceOptions options = optionsAccessor.Value;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new { query = q, results = Array.Empty<object>() });

            int maxResults = Math.Min(limit ?? 20, 200);

            string sql = """
                SELECT cp.ConfluenceId, cp.Title,
                       snippet(confluence_pages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       confluence_pages_fts.rank, cp.SpaceKey, cp.LastModifiedAt
                FROM confluence_pages_fts
                JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
                WHERE confluence_pages_fts MATCH @query
                ORDER BY confluence_pages_fts.rank
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            List<object> results = new List<object>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string pageId = reader.GetString(0);
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

        api.MapGet("/pages/{pageId}", (string pageId, ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) =>
        {
            ConfluenceServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: pageId);

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

        api.MapGet("/pages/{pageId}/related", (string pageId, int? limit, ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) =>
        {
            ConfluenceServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 10, 50);

            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: pageId);
            List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: pageId);

            List<string> relatedIds = outLinks.Select(l => l.TargetPageId)
                .Concat(inLinks.Select(l => l.SourcePageId))
                .Distinct()
                .Take(maxResults)
                .ToList();

            List<object> results = new List<object>();
            foreach (string relId in relatedIds)
            {
                ConfluencePageRecord? related = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: relId);
                if (related is null) continue;
                results.Add(new
                {
                    pageId = related.ConfluenceId,
                    title = related.Title,
                    spaceKey = related.SpaceKey,
                    url = related.Url ?? $"{options.BaseUrl}/pages/{related.ConfluenceId}",
                });
            }

            return Results.Ok(new { sourceKey = pageId, related = results });
        });

        api.MapGet("/pages/{pageId}/snapshot", (string pageId, ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) =>
        {
            ConfluenceServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            StringBuilder md = new StringBuilder();
            md.AppendLine($"# {page.Title}");
            md.AppendLine();
            md.AppendLine($"**Space:** {page.SpaceKey}  ");
            md.AppendLine($"**Version:** {page.VersionNumber}  ");
            if (page.LastModifiedBy is not null) md.AppendLine($"**Last Modified By:** {page.LastModifiedBy}  ");
            md.AppendLine($"**Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}  ");
            if (page.Labels is not null) md.AppendLine($"**Labels:** {page.Labels}  ");
            md.AppendLine();
            if (!string.IsNullOrEmpty(page.BodyPlain))
            {
                md.AppendLine("## Content");
                md.AppendLine();
                md.AppendLine(page.BodyPlain);
                md.AppendLine();
            }

            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
            if (comments.Count > 0)
            {
                md.AppendLine("## Comments");
                foreach (ConfluenceCommentRecord c in comments) { md.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}"); md.AppendLine(); }
            }

            return Results.Ok(new { key = pageId, markdown = md.ToString(), url = page.Url ?? $"{options.BaseUrl}/pages/{pageId}" });
        });

        api.MapGet("/pages/{pageId}/content", (string pageId, string? format, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            string content = format?.Equals("storage", StringComparison.OrdinalIgnoreCase) == true
                ? (page.BodyStorage ?? "")
                : (page.BodyPlain ?? "");

            return Results.Ok(new { key = pageId, content, format = format ?? "text" });
        });

        api.MapGet("/pages", (int? limit, int? offset, string? spaceKey, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = "SELECT ConfluenceId, Title, SpaceKey, LastModifiedAt FROM confluence_pages";
            List<SqliteParameter> parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(spaceKey))
            {
                sql += " WHERE SpaceKey = @spaceKey";
                parameters.Add(new SqliteParameter("@spaceKey", spaceKey));
            }

            sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
            parameters.Add(new SqliteParameter("@limit", maxResults));
            parameters.Add(new SqliteParameter("@offset", skip));

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

            List<object> items = new List<object>();
            using SqliteDataReader reader = cmd.ExecuteReader();
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
            using SqliteConnection connection = db.OpenConnection();
            List<ConfluenceSpaceRecord> spaces = ConfluenceSpaceRecord.SelectList(connection);

            return Results.Ok(new
            {
                total = spaces.Count,
                spaces = spaces.Select(s => new { s.Key, s.Name, s.Description, s.Url, s.LastFetchedAt }),
            });
        });

        api.MapPost("/ingest", async (HttpRequest req, ConfluenceIngestionPipeline pipeline) =>
        {
            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                IngestionResult result = type == "full"
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

        api.MapGet("/status", (ConfluenceIngestionPipeline pipeline, ConfluenceDatabase db, IIndexTracker indexTracker) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluenceSyncStateRecord? syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);

            return Results.Ok(new
            {
                isRunning = pipeline.IsRunning,
                currentStatus = pipeline.CurrentStatus,
                lastSyncAt = syncState?.LastSyncAt,
                itemsIngested = syncState?.ItemsIngested ?? 0,
                lastError = syncState?.LastError,
                indexes = indexTracker.GetAllStatuses().Select(i => new
                {
                    i.Name,
                    i.Description,
                    i.IsRebuilding,
                    i.LastRebuildStartedAt,
                    i.LastRebuildCompletedAt,
                    i.RecordCount,
                    i.LastError,
                }),
            });
        });

        api.MapPost("/rebuild", async (ConfluenceIngestionPipeline pipeline) =>
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

        api.MapPost("/rebuild-index", (
            HttpRequest req,
            IngestionWorkQueue workQueue,
            ConfluenceDatabase database,
            ConfluenceIndexer indexer,
            ConfluenceXRefRebuilder xrefRebuilder,
            ConfluenceLinkRebuilder linkRebuilder,
            IIndexTracker indexTracker) =>
        {
            string indexType = (req.Query["type"].FirstOrDefault() ?? "all").ToLowerInvariant();

            workQueue.Enqueue(ct =>
            {
                switch (indexType)
                {
                    case "bm25":
                        indexTracker.MarkStarted("bm25");
                        try { indexer.RebuildFullIndex(ct); indexTracker.MarkCompleted("bm25"); }
                        catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                        break;
                    case "cross-refs":
                        indexTracker.MarkStarted("cross-refs");
                        try { xrefRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("cross-refs"); }
                        catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                        break;
                    case "page-links":
                        indexTracker.MarkStarted("page-links");
                        try { linkRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("page-links"); }
                        catch (Exception ex) { indexTracker.MarkFailed("page-links", ex.Message); throw; }
                        break;
                    case "fts":
                        indexTracker.MarkStarted("fts");
                        try { database.RebuildFtsIndexes(); indexTracker.MarkCompleted("fts"); }
                        catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                        break;
                    case "all":
                        indexTracker.MarkStarted("cross-refs");
                        try { xrefRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("cross-refs"); }
                        catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                        indexTracker.MarkStarted("page-links");
                        try { linkRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("page-links"); }
                        catch (Exception ex) { indexTracker.MarkFailed("page-links", ex.Message); throw; }
                        indexTracker.MarkStarted("bm25");
                        try { indexer.RebuildFullIndex(ct); indexTracker.MarkCompleted("bm25"); }
                        catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                        indexTracker.MarkStarted("fts");
                        try { database.RebuildFtsIndexes(); indexTracker.MarkCompleted("fts"); }
                        catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                        break;
                    default:
                        return Task.CompletedTask;
                }
                return Task.CompletedTask;
            }, $"rebuild-index-{indexType}");

            return Results.Ok(new { success = true, actionTaken = $"queued {indexType} index rebuild" });
        });

        api.MapGet("/stats",(ConfluenceDatabase db, FhirAugury.Common.Caching.IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int pageCount = ConfluencePageRecord.SelectCount(connection);
            int commentCount = ConfluenceCommentRecord.SelectCount(connection);
            int spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
            int linkCount = ConfluencePageLinkRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

            return Results.Ok(new
            {
                source = SourceSystems.Confluence,
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
}
