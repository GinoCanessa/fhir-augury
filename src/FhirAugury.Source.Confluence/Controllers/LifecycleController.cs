using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
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
    ConfluenceDatabase db,
    IResponseCache cache,
    IIndexTracker indexTracker) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
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
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(ConfluenceCacheLayout.SourceName);

        return Ok(new StatsResponse
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
    }

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
    }
}