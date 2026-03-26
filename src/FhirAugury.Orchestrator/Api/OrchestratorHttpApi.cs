using Fhiraugury;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

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
                    r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url, r.UpdatedAt, r.Metadata,
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
            SourceRouter router,
            CancellationToken ct) =>
        {
            string dir = direction?.ToLowerInvariant() ?? "both";
            List<object> results = [];

            List<Task<GetItemXRefResponse>> tasks = [];
            foreach (string srcName in router.GetEnabledSources())
            {
                SourceService.SourceServiceClient? client = router.GetSourceClient(srcName);
                if (client is null) continue;

                tasks.Add(client.GetItemCrossReferencesAsync(new GetItemXRefRequest
                {
                    Source = source,
                    Id = id,
                    Direction = dir,
                }, cancellationToken: ct).ResponseAsync);
            }

            foreach (Task<GetItemXRefResponse> task in tasks)
            {
                try
                {
                    GetItemXRefResponse result = await task;
                    results.AddRange(result.References.Select(r => (object)new
                    {
                        r.SourceType, r.SourceId, r.TargetType, r.TargetId,
                        r.LinkType, r.Context,
                        targetTitle = r.SourceTitle,
                        targetUrl = r.SourceUrl,
                    }));
                }
                catch { /* ignore partial failures */ }
            }

            return Results.Ok(new { source, id, direction = dir, references = results });
        });

        // ── Get item (proxied) ───────────────────────────────────────
        api.MapGet("/items/{source}/{id}", async (
            string source,
            string id,
            SourceRouter router,
            CancellationToken ct) =>
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                ItemResponse item = await client.GetItemAsync(
                    new GetItemRequest { Id = id, IncludeContent = true, IncludeComments = true },
                    cancellationToken: ct);
                return Results.Ok(new
                {
                    item.Source, item.Id, item.Title, item.Content, item.Url,
                    createdAt = item.CreatedAt?.ToDateTimeOffset(),
                    updatedAt = item.UpdatedAt?.ToDateTimeOffset(),
                    metadata = new Dictionary<string, string>(item.Metadata),
                    comments = item.Comments.Select(c => new
                    {
                        c.Id, c.Author, c.Body, c.Url,
                        createdAt = c.CreatedAt?.ToDateTimeOffset(),
                    }),
                });
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Status.Detail });
            }
        });

        // ── Snapshot (proxied) ───────────────────────────────────────
        api.MapGet("/items/{source}/{id}/snapshot", async (
            string source,
            string id,
            SourceRouter router,
            CancellationToken ct) =>
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                SnapshotResponse snapshot = await client.GetSnapshotAsync(
                    new GetSnapshotRequest { Id = id, IncludeComments = true },
                    cancellationToken: ct);
                return Results.Ok(new { snapshot.Id, snapshot.Source, snapshot.Markdown, snapshot.Url });
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Status.Detail });
            }
        });

        // ── Content (proxied) ────────────────────────────────────────
        api.MapGet("/items/{source}/{id}/content", async (
            string source,
            string id,
            string? format,
            SourceRouter router,
            CancellationToken ct) =>
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                ContentResponse content = await client.GetContentAsync(
                    new GetContentRequest { Id = id, Format = format ?? "text" },
                    cancellationToken: ct);
                return Results.Ok(new
                {
                    content.Id, content.Source, content.Content, content.Format, content.Url,
                    metadata = new Dictionary<string, string>(content.Metadata),
                });
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return Results.NotFound(new { error = ex.Status.Detail });
            }
        });

        // ── Trigger sync ─────────────────────────────────────────────
        api.MapPost("/ingest/trigger", async (
            HttpRequest req,
            SourceRouter router,
            CancellationToken ct) =>
        {
            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            string? sourceCsv = req.Query["sources"].FirstOrDefault();
            List<string> targetSources = string.IsNullOrEmpty(sourceCsv)
                ? router.GetEnabledSources().ToList()
                : CsvParser.ParseSourceList(sourceCsv) ?? [];

            List<object> statuses = new List<object>();
            foreach (string sourceName in targetSources)
            {
                SourceService.SourceServiceClient? client = router.GetSourceClient(sourceName);
                if (client is null)
                {
                    statuses.Add(new { source = sourceName, status = "error", message = "Source not configured" });
                    continue;
                }

                try
                {
                    IngestionStatusResponse result = await client.TriggerIngestionAsync(
                        new TriggerIngestionRequest { Type = type }, cancellationToken: ct);
                    statuses.Add(new { source = sourceName, status = result.Status, itemsTotal = result.ItemsTotal });
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
            SourceRouter router,
            CancellationToken ct) =>
        {
            string indexType = req.Query["type"].FirstOrDefault() ?? "all";
            string? sourceCsv = req.Query["sources"].FirstOrDefault();
            List<string> targets = string.IsNullOrEmpty(sourceCsv)
                ? router.GetEnabledSources().ToList()
                : CsvParser.ParseSourceList(sourceCsv) ?? [];

            RebuildIndexRequest sourceRequest = new() { IndexType = indexType };
            List<object> results = [];

            await Task.WhenAll(targets.Select(async source =>
            {
                SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                if (client is null)
                {
                    lock (results)
                        results.Add(new { source, success = false, actionTaken = (string?)null, error = "Source not configured" });
                    return;
                }

                try
                {
                    RebuildIndexResponse resp = await client.RebuildIndexAsync(sourceRequest, cancellationToken: ct);
                    lock (results)
                        results.Add(new { source, success = resp.Success, actionTaken = resp.ActionTaken, error = resp.Error });
                }
                catch (Exception ex)
                {
                    lock (results)
                        results.Add(new { source, success = false, actionTaken = (string?)null, error = ex.Message });
                }
            }));

            return Results.Ok(new { indexType, results });
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
                    s.Name, s.Status, s.GrpcAddress, s.UptimeSeconds,
                    s.Version, s.ItemCount, s.DbSizeBytes, s.LastSyncAt, s.LastError,
                }),
            });
        });

        // ── Aggregate stats ──────────────────────────────────────────
        api.MapGet("/stats", async (
            SourceRouter router,
            OrchestratorDatabase database,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            ILogger logger = loggerFactory.CreateLogger("OrchestratorHttpApi");
            long dbSize = database.GetDatabaseSizeBytes();

            List<object> sourceStats = new List<object>();
            List<string> warnings = new List<string>();
            foreach (string sourceName in router.GetEnabledSources())
            {
                SourceService.SourceServiceClient? client = router.GetSourceClient(sourceName);
                if (client is null) continue;

                try
                {
                    StatsResponse stats = await client.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
                    sourceStats.Add(new
                    {
                        source = stats.Source,
                        totalItems = stats.TotalItems,
                        totalComments = stats.TotalComments,
                        databaseSizeBytes = stats.DatabaseSizeBytes,
                        cacheSizeBytes = stats.CacheSizeBytes,
                        status = "ok",
                    });
                }
                catch (Exception ex)
                {
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
            SourceRouter router,
            CancellationToken ct) =>
        {
            JiraService.JiraServiceClient? jiraClient = router.GetJiraClient();
            if (jiraClient is null)
                return Results.NotFound(new { error = "Jira service not configured or disabled" });

            JiraQueryRequest queryRequest = new JiraQueryRequest
            {
                Limit = int.TryParse(req.Query["limit"], out int l) ? l : 50,
            };

            if (req.Query.TryGetValue("status", out StringValues statuses))
                queryRequest.Statuses.AddRange(statuses.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (req.Query.TryGetValue("work_group", out StringValues wgs))
                queryRequest.WorkGroups.AddRange(wgs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (req.Query.TryGetValue("specification", out StringValues specs))
                queryRequest.Specifications.AddRange(specs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));

            List<object> results = new List<object>();
            using Grpc.Core.AsyncServerStreamingCall<JiraIssueSummary> stream = jiraClient.QueryIssues(queryRequest, cancellationToken: ct);
            while (await stream.ResponseStream.MoveNext(ct))
            {
                JiraIssueSummary issue = stream.ResponseStream.Current;
                results.Add(new
                {
                    issue.Key, issue.Title, issue.Type, issue.Status, issue.Priority,
                    issue.WorkGroup, issue.Specification,
                });
            }

            return Results.Ok(new { total = results.Count, results });
        });

        // ── Zulip query (proxied) ────────────────────────────────────
        api.MapPost("/zulip/query", async (
            HttpRequest req,
            SourceRouter router,
            CancellationToken ct) =>
        {
            SourceService.SourceServiceClient? zulipClient = router.GetSourceClient("zulip");
            if (zulipClient is null)
                return Results.NotFound(new { error = "Zulip service not configured or disabled" });

            string q = req.Query["q"].FirstOrDefault() ?? "";
            int limit = int.TryParse(req.Query["limit"], out int l2) ? l2 : 20;

            SearchResponse response = await zulipClient.SearchAsync(
                new SearchRequest { Query = q, Limit = limit },
                cancellationToken: ct);

            return Results.Ok(new
            {
                query = q,
                total = response.TotalResults,
                results = response.Results.Select(r => new
                {
                    r.Source, r.Id, r.Title, r.Snippet, r.Score, r.Url,
                }),
            });
        });

        return app;
    }
}
