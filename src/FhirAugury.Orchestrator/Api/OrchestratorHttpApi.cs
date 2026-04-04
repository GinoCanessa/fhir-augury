using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Api;

/// <summary>Minimal API HTTP endpoints for the orchestrator.</summary>
public static class OrchestratorHttpApi
{
    public static IEndpointRouteBuilder MapOrchestratorHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        // ── Unified search ───────────────────────────────────────────
        api.MapGet("/search", async (
            string? q,
            string? sources,
            int? limit,
            UnifiedSearchService searchService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            List<string>? sourceList = CsvParser.ParseSourceList(sources);

            (List<ScoredItem>? results, List<string>? warnings) = await searchService.SearchAsync(q, sourceList, limit ?? 0, ct);

            return Results.Ok(new
            {
                query = q,
                total = results.Count,
                warnings,
                results = results.Select(r => new
                {
                    r.Source, r.ContentType, r.Id, r.Title, r.Snippet, r.Score, r.Url, r.UpdatedAt, r.Metadata,
                }),
            });
        });

        // ── Related items ────────────────────────────────────────────
        api.MapGet("/related/{source}/{id}", async (
            string source,
            string id,
            int? limit,
            string? targetSources,
            RelatedItemFinder finder,
            CancellationToken ct) =>
        {
            List<string>? targetSourceList = CsvParser.ParseSourceList(targetSources);
            FindRelatedResponse response = await finder.FindRelatedAsync(source, id, limit ?? 0, targetSourceList, ct);

            return Results.Ok(new
            {
                seedSource = response.SeedSource,
                seedId = response.SeedId,
                seedTitle = response.SeedTitle,
                items = response.Items.Select(i => new
                {
                    i.Source, i.Id, i.Title, i.Snippet, i.Url,
                    relevanceScore = i.RelevanceScore,
                    i.Relationship, i.Context,
                }),
            });
        });

        // ── Cross-references ─────────────────────────────────────────
        api.MapGet("/xref/{source}/{id}", async (
            string source,
            string id,
            string? direction,
            SourceHttpClient httpClient,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            ILogger logger = loggerFactory.CreateLogger("OrchestratorHttpApi");
            string dir = direction?.ToLowerInvariant() ?? "both";
            List<object> results = [];

            List<Task<CrossReferenceResponse?>> tasks = [];
            foreach (string srcName in httpClient.GetEnabledSourceNames())
            {
                tasks.Add(httpClient.GetCrossReferencesAsync(srcName, id, dir, ct));
            }

            foreach (Task<CrossReferenceResponse?> task in tasks)
            {
                try
                {
                    CrossReferenceResponse? result = await task;
                    if (result is null) continue;
                    results.AddRange(result.References.Select(r => (object)new
                    {
                        r.SourceType, r.SourceId, r.TargetType, r.TargetId,
                        r.LinkType, r.Context, r.SourceContentType,
                        r.TargetTitle, r.TargetUrl,
                    }));
                }
                catch (Exception ex)
                {
                    if (ex.IsTransientHttpError(out string statusDescription))
                        logger.LogWarning("GetCrossReferences failed for a source ({HttpStatus})", statusDescription);
                    else
                        logger.LogDebug(ex, "GetCrossReferences failed for a source");
                }
            }

            return Results.Ok(new { source, id, direction = dir, references = results });
        });

        // ── Get item (proxied) ───────────────────────────────────────
        api.MapGet("/items/{source}/{id}", async (
            string source,
            string id,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            if (!httpClient.IsSourceEnabled(source))
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                ItemResponse? item = await httpClient.GetItemAsync(source, id, ct);
                if (item is null)
                    return Results.NotFound(new { error = $"Item '{id}' not found in source '{source}'" });

                return Results.Ok(new
                {
                    item.Source, item.Id, item.Title, item.Content, item.Url,
                    item.CreatedAt, item.UpdatedAt, item.Metadata, item.Comments,
                });
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // ── Snapshot (proxied) ───────────────────────────────────────
        api.MapGet("/items/{source}/{id}/snapshot", async (
            string source,
            string id,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            if (!httpClient.IsSourceEnabled(source))
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                SnapshotResponse? snapshot = await httpClient.GetSnapshotAsync(source, id, ct);
                if (snapshot is null)
                    return Results.NotFound(new { error = $"Snapshot for '{id}' not found in source '{source}'" });

                return Results.Ok(new { snapshot.Id, snapshot.Source, snapshot.Markdown, snapshot.Url });
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // ── Content (proxied) ────────────────────────────────────────
        api.MapGet("/items/{source}/{id}/content", async (
            string source,
            string id,
            string? format,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            if (!httpClient.IsSourceEnabled(source))
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                ContentResponse? content = await httpClient.GetContentAsync(source, id, format ?? "text", ct);
                if (content is null)
                    return Results.NotFound(new { error = $"Content for '{id}' not found in source '{source}'" });

                return Results.Ok(new
                {
                    content.Id, content.Source, content.Content, content.Format, content.Url, content.Metadata,
                });
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // ── Trigger sync ─────────────────────────────────────────────
        api.MapPost("/ingest/trigger", async (
            HttpRequest req,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            string? sourceCsv = req.Query["sources"].FirstOrDefault();
            List<string> targetSources = string.IsNullOrEmpty(sourceCsv)
                ? httpClient.GetEnabledSourceNames().ToList()
                : CsvParser.ParseSourceList(sourceCsv) ?? [];

            List<object> statuses = new List<object>();
            foreach (string sourceName in targetSources)
            {
                if (!httpClient.IsSourceEnabled(sourceName))
                {
                    statuses.Add(new { source = sourceName, status = "error", message = "Source not configured" });
                    continue;
                }

                try
                {
                    IngestionStatusResponse? result = await httpClient.TriggerIngestionAsync(sourceName, type, ct);
                    statuses.Add(new { source = sourceName, status = result?.Status ?? "unknown", itemsTotal = result?.ItemsTotal ?? 0 });
                }
                catch (Exception ex)
                {
                    statuses.Add(new { source = sourceName, status = "error", message = ex.Message });
                }
            }

            return Results.Ok(new { type, statuses });
        });

        // ── Rebuild index (fan-out) ──────────────────────────────────
        api.MapPost("/rebuild-index", async (
            HttpRequest req,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            string indexType = req.Query["type"].FirstOrDefault() ?? "all";
            string? sourceCsv = req.Query["sources"].FirstOrDefault();
            List<string> targets = string.IsNullOrEmpty(sourceCsv)
                ? httpClient.GetEnabledSourceNames().ToList()
                : CsvParser.ParseSourceList(sourceCsv) ?? [];

            List<object> results = [];

            await Task.WhenAll(targets.Select(async source =>
            {
                if (!httpClient.IsSourceEnabled(source))
                {
                    lock (results)
                        results.Add(new { source, success = false, actionTaken = (string?)null, error = "Source not configured" });
                    return;
                }

                try
                {
                    RebuildIndexResponse? resp = await httpClient.RebuildIndexAsync(source, indexType, ct);
                    lock (results)
                        results.Add(new { source, success = resp?.Success ?? false, actionTaken = resp?.ActionTaken, error = resp?.Error });
                }
                catch (Exception ex)
                {
                    lock (results)
                        results.Add(new { source, success = false, actionTaken = (string?)null, error = ex.Message });
                }
            }));

            return Results.Ok(new { indexType, results });
        });

        // ── Notify ingestion complete ────────────────────────────────
        api.MapPost("/notify-ingestion", async (
            HttpRequest req,
            SourceHttpClient httpClient,
            OrchestratorDatabase database,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            ILogger logger = loggerFactory.CreateLogger("OrchestratorHttpApi");
            PeerIngestionNotification? notification = await req.ReadFromJsonAsync<PeerIngestionNotification>(ct);
            if (notification is null)
                return Results.BadRequest(new { error = "Invalid notification body" });

            logger.LogInformation("Ingestion complete from {Source}", notification.Source);

            using SqliteConnection connection = database.OpenConnection();
            XrefScanStateRecord? existing = XrefScanStateRecord.SelectSingle(
                connection, SourceName: notification.Source);

            DateTimeOffset completedAt = notification.CompletedAt is not null
                ? DateTimeOffset.Parse(notification.CompletedAt)
                : DateTimeOffset.UtcNow;

            XrefScanStateRecord record = new XrefScanStateRecord
            {
                Id = existing?.Id ?? XrefScanStateRecord.GetIndex(),
                SourceName = notification.Source,
                LastCursor = null,
                LastScanAt = completedAt,
            };

            if (existing is not null)
                XrefScanStateRecord.Update(connection, record);
            else
                XrefScanStateRecord.Insert(connection, record);

            // Fan out to all OTHER sources
            PeerIngestionNotification peerNotification = new(
                Source: notification.Source,
                CompletedAt: notification.CompletedAt);

            List<string> fanOutTargets = httpClient.GetEnabledSourceNames()
                .Where(s => !s.Equals(notification.Source, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Task[] fanOutTasks = fanOutTargets.Select(async targetSource =>
            {
                try
                {
                    await httpClient.NotifyPeerAsync(targetSource, peerNotification, ct);
                }
                catch (Exception ex)
                {
                    if (ex.IsTransientHttpError(out string statusDescription))
                        logger.LogWarning("Failed to notify {Target} of {Source} ingestion ({HttpStatus})",
                            targetSource, notification.Source, statusDescription);
                    else
                        logger.LogWarning(ex, "Failed to notify {Target} of {Source} ingestion",
                            targetSource, notification.Source);
                }
            }).ToArray();

            await Task.WhenAll(fanOutTasks);

            return Results.Ok(new { acknowledged = true });
        });

        // ── Services health ──────────────────────────────────────────
        api.MapGet("/services", async (
            ServiceHealthMonitor monitor,
            CancellationToken ct) =>
        {
            await monitor.CheckAllAsync(ct);
            Dictionary<string, ServiceHealthInfo> status = monitor.GetCurrentStatus();

            return Results.Ok(new
            {
                services = status.Values.Select(s => new
                {
                    s.Name, s.Status, s.HttpAddress, s.UptimeSeconds,
                    s.Version, s.ItemCount, s.DbSizeBytes, s.LastSyncAt, s.LastError,
                    indexes = s.Indexes.Select(i => new
                    {
                        i.Name, i.Description, i.IsRebuilding,
                        i.LastRebuildStartedAt, i.LastRebuildCompletedAt,
                        i.RecordCount, i.LastError,
                    }),
                }),
            });
        });

        // ── Service endpoints ────────────────────────────────────────
        api.MapGet("/endpoints", (SourceHttpClient httpClient) =>
        {
            List<ServiceEndpointInfo> endpoints = [];
            foreach (string sourceName in httpClient.GetEnabledSourceNames())
            {
                SourceServiceConfig? config = httpClient.GetSourceConfig(sourceName);
                if (config is null) continue;

                endpoints.Add(new ServiceEndpointInfo(
                    Name: sourceName,
                    HttpAddress: config.HttpAddress,
                    Enabled: config.Enabled));
            }

            return Results.Ok(new ServiceEndpointsResponse(endpoints));
        });

        // ── Aggregate stats ──────────────────────────────────────────
        api.MapGet("/stats", async (
            SourceHttpClient httpClient,
            OrchestratorDatabase database,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            ILogger logger = loggerFactory.CreateLogger("OrchestratorHttpApi");
            long dbSize = database.GetDatabaseSizeBytes();

            List<object> sourceStats = new List<object>();
            List<string> warnings = new List<string>();
            foreach (string sourceName in httpClient.GetEnabledSourceNames())
            {
                try
                {
                    StatsResponse? stats = await httpClient.GetStatsAsync(sourceName, ct);
                    sourceStats.Add(new
                    {
                        source = stats?.Source ?? sourceName,
                        totalItems = stats?.TotalItems ?? 0,
                        totalComments = stats?.TotalComments ?? 0,
                        databaseSizeBytes = stats?.DatabaseSizeBytes ?? 0L,
                        cacheSizeBytes = stats?.CacheSizeBytes ?? 0L,
                        status = "ok",
                    });
                }
                catch (Exception ex)
                {
                    if (ex.IsTransientHttpError(out string statusDescription))
                        logger.LogWarning("Failed to get stats for source {Source} ({HttpStatus})", sourceName, statusDescription);
                    else
                        logger.LogWarning(ex, "Failed to get stats for source {Source}", sourceName);
                    warnings.Add($"Stats unavailable for '{sourceName}': {ex.Message}");
                    sourceStats.Add(new
                    {
                        source = sourceName,
                        totalItems = 0,
                        totalComments = 0,
                        databaseSizeBytes = 0L,
                        cacheSizeBytes = 0L,
                        status = "unavailable",
                    });
                }
            }

            return Results.Ok(new
            {
                orchestrator = new { databaseSizeBytes = dbSize },
                sources = sourceStats,
                warnings,
            });
        });

        // ── Jira query (proxied) ─────────────────────────────────────
        api.MapPost("/jira/query", async (
            HttpRequest req,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            if (!httpClient.IsSourceEnabled("jira"))
                return Results.NotFound(new { error = "Jira service not configured or disabled" });

            string q = req.Query["q"].FirstOrDefault() ?? "";
            int limit = int.TryParse(req.Query["limit"], out int l) ? l : 50;

            SearchResponse? response = await httpClient.SearchAsync("jira", q, limit, ct);
            return Results.Ok(new
            {
                query = q,
                total = response?.Total ?? 0,
                results = (response?.Results ?? []).Select(r => new
                {
                    r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url,
                }),
            });
        });

        // ── Zulip query (proxied) ────────────────────────────────────
        api.MapPost("/zulip/query", async (
            HttpRequest req,
            SourceHttpClient httpClient,
            CancellationToken ct) =>
        {
            if (!httpClient.IsSourceEnabled("zulip"))
                return Results.NotFound(new { error = "Zulip service not configured or disabled" });

            string q = req.Query["q"].FirstOrDefault() ?? "";
            int limit = int.TryParse(req.Query["limit"], out int l2) ? l2 : 20;

            SearchResponse? response = await httpClient.SearchAsync("zulip", q, limit, ct);
            return Results.Ok(new
            {
                query = q,
                total = response?.Total ?? 0,
                results = (response?.Results ?? []).Select(r => new
                {
                    r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url,
                }),
            });
        });

        return app;
    }
}
