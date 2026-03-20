using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.CrossRef;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FhirAugury.Orchestrator.Api;

/// <summary>Minimal API HTTP endpoints for the orchestrator.</summary>
public static class OrchestratorHttpApi
{
    public static IEndpointRouteBuilder MapOrchestratorHttpApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

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

            var sourceList = string.IsNullOrEmpty(sources)
                ? null
                : sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var (results, warnings) = await searchService.SearchAsync(q, sourceList, limit ?? 0, ct);

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
            RelatedItemFinder finder,
            CancellationToken ct) =>
        {
            var response = await finder.FindRelatedAsync(source, id, limit ?? 0, null, ct);

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
        api.MapGet("/xref/{source}/{id}", (
            string source,
            string id,
            string? direction,
            OrchestratorDatabase database) =>
        {
            using var connection = database.OpenConnection();
            var dir = direction?.ToLowerInvariant() ?? "both";

            var results = new List<object>();

            if (dir is "outgoing" or "both")
            {
                var outgoing = CrossRefLinkRecord.SelectList(connection,
                    SourceType: source, SourceId: id);
                results.AddRange(outgoing.Select(l => (object)new
                {
                    l.SourceType, l.SourceId, l.TargetType, l.TargetId, l.LinkType, l.Context,
                }));
            }

            if (dir is "incoming" or "both")
            {
                var incoming = CrossRefLinkRecord.SelectList(connection,
                    TargetType: source, TargetId: id);
                results.AddRange(incoming.Select(l => (object)new
                {
                    l.SourceType, l.SourceId, l.TargetType, l.TargetId, l.LinkType, l.Context,
                }));
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
            var client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                var item = await client.GetItemAsync(
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
            var client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                var snapshot = await client.GetSnapshotAsync(
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
            var client = router.GetSourceClient(source);
            if (client is null)
                return Results.NotFound(new { error = $"Source '{source}' not found or disabled" });

            try
            {
                var content = await client.GetContentAsync(
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
            var type = req.Query["type"].FirstOrDefault() ?? "incremental";
            var sourceCsv = req.Query["sources"].FirstOrDefault();
            var targetSources = string.IsNullOrEmpty(sourceCsv)
                ? router.GetEnabledSources().ToList()
                : sourceCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var statuses = new List<object>();
            foreach (var sourceName in targetSources)
            {
                var client = router.GetSourceClient(sourceName);
                if (client is null)
                {
                    statuses.Add(new { source = sourceName, status = "error", message = "Source not configured" });
                    continue;
                }

                try
                {
                    var result = await client.TriggerIngestionAsync(
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

        // ── Services health ──────────────────────────────────────────
        api.MapGet("/services", async (
            ServiceHealthMonitor monitor,
            OrchestratorDatabase database,
            CancellationToken ct) =>
        {
            await monitor.CheckAllAsync(ct);
            var status = monitor.GetCurrentStatus();

            using var connection = database.OpenConnection();
            var linkCount = CrossRefLinkRecord.SelectCount(connection);

            return Results.Ok(new
            {
                services = status.Values.Select(s => new
                {
                    s.Name, s.Status, s.GrpcAddress, s.UptimeSeconds,
                    s.Version, s.ItemCount, s.DbSizeBytes, s.LastSyncAt, s.LastError,
                }),
                crossRefLinks = linkCount,
            });
        });

        // ── Aggregate stats ──────────────────────────────────────────
        api.MapGet("/stats", async (
            SourceRouter router,
            OrchestratorDatabase database,
            CancellationToken ct) =>
        {
            using var connection = database.OpenConnection();
            var linkCount = CrossRefLinkRecord.SelectCount(connection);
            var dbSize = database.GetDatabaseSizeBytes();

            var sourceStats = new List<object>();
            foreach (var sourceName in router.GetEnabledSources())
            {
                var client = router.GetSourceClient(sourceName);
                if (client is null) continue;

                try
                {
                    var stats = await client.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
                    sourceStats.Add(new
                    {
                        source = stats.Source,
                        totalItems = stats.TotalItems,
                        totalComments = stats.TotalComments,
                        databaseSizeBytes = stats.DatabaseSizeBytes,
                        cacheSizeBytes = stats.CacheSizeBytes,
                    });
                }
                catch
                {
                    sourceStats.Add(new
                    {
                        source = sourceName,
                        totalItems = 0,
                        totalComments = 0,
                        databaseSizeBytes = 0L,
                        cacheSizeBytes = 0L,
                    });
                }
            }

            return Results.Ok(new
            {
                orchestrator = new { crossRefLinks = linkCount, databaseSizeBytes = dbSize },
                sources = sourceStats,
            });
        });

        // ── Jira query (proxied) ─────────────────────────────────────
        api.MapPost("/jira/query", async (
            HttpRequest req,
            SourceRouter router,
            CancellationToken ct) =>
        {
            var jiraClient = router.GetJiraClient();
            if (jiraClient is null)
                return Results.NotFound(new { error = "Jira service not configured or disabled" });

            var queryRequest = new JiraQueryRequest
            {
                Limit = int.TryParse(req.Query["limit"], out var l) ? l : 50,
            };

            if (req.Query.TryGetValue("status", out var statuses))
                queryRequest.Statuses.AddRange(statuses.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (req.Query.TryGetValue("work_group", out var wgs))
                queryRequest.WorkGroups.AddRange(wgs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (req.Query.TryGetValue("specification", out var specs))
                queryRequest.Specifications.AddRange(specs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));

            var results = new List<object>();
            using var stream = jiraClient.QueryIssues(queryRequest, cancellationToken: ct);
            while (await stream.ResponseStream.MoveNext(ct))
            {
                var issue = stream.ResponseStream.Current;
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
            var zulipClient = router.GetSourceClient("zulip");
            if (zulipClient is null)
                return Results.NotFound(new { error = "Zulip service not configured or disabled" });

            var q = req.Query["q"].FirstOrDefault() ?? "";
            var limit = int.TryParse(req.Query["limit"], out var l2) ? l2 : 20;

            var response = await zulipClient.SearchAsync(
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
