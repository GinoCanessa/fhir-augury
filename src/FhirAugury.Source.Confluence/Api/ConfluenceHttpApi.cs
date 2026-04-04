using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
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

/// <summary>HTTP Minimal API endpoints for the Confluence source service.</summary>
public static class ConfluenceHttpApi
{
    public static IEndpointRouteBuilder MapConfluenceHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        MapSearchEndpoints(api);
        MapItemEndpoints(api);
        MapCrossReferenceEndpoints(api);
        MapIngestionEndpoints(api);
        MapLifecycleEndpoints(api);

        return app;
    }

    // ── Search ──────────────────────────────────────────────────────

    private static void MapSearchEndpoints(RouteGroupBuilder api)
    {
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

            List<object> results = [];
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
    }

    // ── Items (Pages) ───────────────────────────────────────────────

    private static void MapItemEndpoints(RouteGroupBuilder api)
    {
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

            List<object> results = [];
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

        api.MapGet("/pages/{pageId}/comments", (string pageId, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);

            return Results.Ok(comments.Select(c => new
            {
                id = c.Id.ToString(),
                pageId = c.PageId,
                author = c.Author,
                body = c.Body ?? "",
                createdAt = c.CreatedAt,
                url = page.Url ?? "",
            }));
        });

        api.MapGet("/pages/{pageId}/children", (string pageId, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? parentPage = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (parentPage is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE ParentId = @parentId";
            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@parentId", parentPage.ConfluenceId);

            List<object> children = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                children.Add(new
                {
                    id = reader.GetInt32(0),
                    confluenceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    spaceKey = reader.GetString(2),
                    title = reader.GetString(3),
                    url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    lastModifiedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            return Results.Ok(children);
        });

        api.MapGet("/pages/{pageId}/ancestors", (string pageId, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? current = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (current is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            List<object> ancestors = [];
            HashSet<string> visited = [];
            string? parentId = current.ParentId;

            while (!string.IsNullOrEmpty(parentId) && !visited.Contains(parentId))
            {
                visited.Add(parentId);
                ConfluencePageRecord? parent = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: parentId);
                if (parent is null) break;

                ancestors.Add(new
                {
                    id = parent.Id,
                    confluenceId = parent.ConfluenceId,
                    spaceKey = parent.SpaceKey,
                    title = parent.Title,
                    url = parent.Url ?? "",
                    lastModifiedAt = parent.LastModifiedAt,
                });

                parentId = parent.ParentId;
            }

            // Root-first order
            ancestors.Reverse();
            return Results.Ok(ancestors);
        });

        api.MapGet("/pages/{pageId}/linked", (string pageId, string? direction, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            if (page is null)
                return Results.NotFound(new { error = $"Page {pageId} not found" });

            List<string> linkedPageIds = [];
            string dir = direction?.ToLowerInvariant() ?? "both";

            if (dir is "outgoing" or "both")
            {
                List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
                linkedPageIds.AddRange(outLinks.Select(l => l.TargetPageId));
            }

            if (dir is "incoming" or "both")
            {
                List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);
                linkedPageIds.AddRange(inLinks.Select(l => l.SourcePageId));
            }

            List<object> results = [];
            foreach (string linkedId in linkedPageIds.Distinct())
            {
                ConfluencePageRecord? linked = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: linkedId);
                if (linked is null) continue;

                results.Add(new
                {
                    id = linked.Id,
                    confluenceId = linked.ConfluenceId,
                    spaceKey = linked.SpaceKey,
                    title = linked.Title,
                    url = linked.Url ?? "",
                    lastModifiedAt = linked.LastModifiedAt,
                });
            }

            return Results.Ok(results);
        });

        api.MapGet("/pages/by-label/{label}", (string label, string? spaceKey, int? limit, int? offset, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE Labels LIKE @label";
            List<SqliteParameter> parameters = [new("@label", $"%{label}%")];

            if (!string.IsNullOrEmpty(spaceKey))
            {
                sql += " AND SpaceKey = @spaceKey";
                parameters.Add(new SqliteParameter("@spaceKey", spaceKey));
            }

            sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
            parameters.Add(new SqliteParameter("@limit", maxResults));
            parameters.Add(new SqliteParameter("@offset", skip));

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

            List<object> items = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new
                {
                    id = reader.GetInt32(0),
                    confluenceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    spaceKey = reader.IsDBNull(2) ? null : reader.GetString(2),
                    title = reader.GetString(3),
                    url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    lastModifiedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            return Results.Ok(new { label, total = items.Count, items });
        });

        api.MapGet("/pages", (int? limit, int? offset, string? spaceKey, ConfluenceDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = "SELECT ConfluenceId, Title, SpaceKey, LastModifiedAt FROM confluence_pages";
            List<SqliteParameter> parameters = [];

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

            List<object> items = [];
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
    }

    // ── Cross-References ─────────────────────────────────────────────

    private static void MapCrossReferenceEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/xref/{id}", (string id, string? source, string? direction, ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) =>
        {
            ConfluenceServiceOptions options = optionsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            List<SourceCrossReference> refs = [];
            string dir = direction?.ToLowerInvariant() ?? "both";
            string src = source ?? SourceSystems.Confluence;

            if (string.Equals(src, SourceSystems.Confluence, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
            {
                ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
                string sourceTitle = page?.Title ?? "";
                string sourceUrl = page?.Url ?? "";

                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Confluence, id,
                        SourceSystems.Jira, r.JiraKey,
                        "mentions", r.Context ?? "",
                        null, sourceTitle, sourceUrl));
                }

                foreach (ZulipXRefRecord r in ZulipXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Confluence, id,
                        SourceSystems.Zulip, r.TargetId,
                        "mentions", r.Context ?? "",
                        null, sourceTitle, sourceUrl));
                }

                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Confluence, id,
                        SourceSystems.GitHub, r.TargetId,
                        "mentions", r.Context ?? "",
                        null, sourceTitle, sourceUrl));
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: id))
                {
                    refs.Add(new SourceCrossReference(
                        SourceSystems.Confluence, id,
                        SourceSystems.Fhir, r.TargetId,
                        "mentions", r.Context ?? "",
                        null, sourceTitle, sourceUrl));
                }
            }

            if (string.Equals(src, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
            {
                List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, JiraKey: id);
                HashSet<string> seen = [];
                foreach (JiraXRefRecord jiraRef in jiraRefs)
                {
                    if (!seen.Add(jiraRef.SourceId)) continue;
                    ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: jiraRef.SourceId);
                    if (page is null) continue;

                    refs.Add(new SourceCrossReference(
                        SourceSystems.Confluence, jiraRef.SourceId,
                        SourceSystems.Jira, id,
                        "mentions", jiraRef.Context ?? "",
                        null, page.Title, page.Url ?? ""));
                }
            }

            return Results.Ok(new CrossReferenceResponse(src, id, dir, refs));
        });
    }

    // ── Ingestion ────────────────────────────────────────────────────

    private static void MapIngestionEndpoints(RouteGroupBuilder api)
    {
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

        api.MapPost("/rebuild", async (ConfluenceIngestionPipeline pipeline) =>
        {
            try
            {
                IngestionResult result = await pipeline.RebuildFromCacheAsync();
                return Results.Ok(new RebuildResponse(true, result.ItemsProcessed, 0, null));
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
                RebuildIndexByType(indexType, database, indexer, xrefRebuilder, linkRebuilder, indexTracker, ct);
                return Task.CompletedTask;
            }, $"rebuild-index-{indexType}");

            return Results.Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
        });

        api.MapPost("/notify-peer", (PeerIngestionNotification notification, IngestionWorkQueue workQueue, ConfluenceXRefRebuilder xrefRebuilder) =>
        {
            workQueue.Enqueue(ct =>
            {
                xrefRebuilder.RebuildAll(ct);
                return Task.CompletedTask;
            }, "rebuild-xrefs");

            return Results.Ok(new PeerIngestionAck(Acknowledged: true));
        });
    }

    // ── Lifecycle / Status ───────────────────────────────────────────

    private static void MapLifecycleEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/status", (ConfluenceIngestionPipeline pipeline, ConfluenceDatabase db, IIndexTracker indexTracker) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            ConfluenceSyncStateRecord? syncState = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);

            IngestionStatusResponse status = new IngestionStatusResponse(
                SourceSystems.Confluence,
                pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
                syncState?.LastSyncAt,
                syncState?.ItemsIngested ?? 0,
                0,
                syncState?.LastError,
                pipeline.IsRunning ? pipeline.CurrentStatus : null,
                HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()));

            return Results.Ok(status);
        });

        api.MapGet("/stats", (ConfluenceDatabase db, IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int pageCount = ConfluencePageRecord.SelectCount(connection);
            int commentCount = ConfluenceCommentRecord.SelectCount(connection);
            int spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
            int linkCount = ConfluencePageLinkRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

            return Results.Ok(new StatsResponse
            {
                Source = SourceSystems.Confluence,
                TotalItems = pageCount,
                TotalComments = commentCount,
                DatabaseSizeBytes = dbSize,
                CacheSizeBytes = cacheStats.TotalBytes,
                CacheFiles = cacheStats.FileCount,
                AdditionalCounts = new Dictionary<string, int>
                {
                    ["spaces"] = spaceCount,
                    ["page_links"] = linkCount,
                },
            });
        });

        api.MapGet("/health", (ConfluenceDatabase db, ConfluenceIngestionPipeline pipeline) =>
        {
            return Results.Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void RebuildIndexByType(
        string indexType,
        ConfluenceDatabase database,
        ConfluenceIndexer indexer,
        ConfluenceXRefRebuilder xrefRebuilder,
        ConfluenceLinkRebuilder linkRebuilder,
        IIndexTracker indexTracker,
        CancellationToken ct)
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
        }
    }
}
