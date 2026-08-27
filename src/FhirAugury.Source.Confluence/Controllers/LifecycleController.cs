using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Hosting;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class LifecycleController(
    ConfluenceIngestionPipeline pipeline,
    ConfluenceSource source,
    ConfluenceDatabase db,
    IResponseCache cache,
    IIndexTracker indexTracker,
    IStartupRebuildStatus? startupRebuild = null) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluenceSyncStateRecord? syncState = ConfluenceSyncStateRecord.SelectSingle(
            connection,
            SourceName: ConfluenceSource.SourceName,
            SubSource: ConfluenceSource.SchedulingSubSource);

        IngestionStatusResponse status = new IngestionStatusResponse(
            SourceSystems.Confluence,
            pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            syncState?.LastSyncAt,
            syncState?.ItemsIngested ?? 0,
            0,
            syncState?.LastError,
            pipeline.IsRunning ? pipeline.CurrentStatus : null,
            HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()),
            ["bm25", "cross-refs", "page-links", "fts", "all"]);

        return Ok(status);
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        using SqliteConnection connection = db.OpenConnection();
        int pageCount = ConfluencePageRecord.SelectCount(connection);
        int commentCount = ConfluenceCommentRecord.SelectCount(connection);
        int spaceCount = ConfluenceSpaceRecord.SelectCount(connection);
        int linkCount = ConfluencePageLinkRecord.SelectCount(connection);
        int attachmentCount = ConfluenceAttachmentRecord.SelectCount(connection);
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

        Dictionary<string, int> counts = new()
        {
            ["spaces"] = spaceCount,
            ["page_links"] = linkCount,
            ["attachments"] = attachmentCount,
        };

        // Reconciliation counts reach the CLI and orchestrator through plumbing
        // that already exists. skippedBytes deliberately does NOT go here:
        // AdditionalCounts is Dictionary<string, int> and a byte total would
        // overflow it. It lives in the reconcile report only, which is why the
        // verdict itself has to carry complete_with_skips.
        foreach (ConfluenceReconcilePlan plan in source.ReconcileReport(source.BuildPolicy()))
        {
            Add(counts, "manifest_items", plan.ManifestItemCount);
            Add(counts, "cached", plan.CachedCount);
            Add(counts, "stale", plan.StaleCount);
            Add(counts, "missing", plan.MissingCount);
            Add(counts, "vanished", plan.VanishedCount);
            Add(counts, "skipped_by_policy", plan.SkippedByPolicyCount);
        }

        return Ok(new StatsResponse
        {
            Source = SourceSystems.Confluence,
            TotalItems = pageCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
            CacheFiles = cacheStats.FileCount,
            AdditionalCounts = counts,
        });
    }

    private static void Add(Dictionary<string, int> counts, string key, int value) =>
        counts[key] = counts.TryGetValue(key, out int existing) ? existing + value : value;

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline, startupRebuild));
    }
}