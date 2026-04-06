using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class LifecycleController(
    ZulipIngestionPipeline pipeline,
    ZulipDatabase db,
    IResponseCache cache,
    IIndexTracker indexTracker,
    IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetIngestionStatus()
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

        return Ok(new IngestionStatusResponse(
            SourceSystems.Zulip,
            pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            syncState?.LastSyncAt,
            syncState?.ItemsIngested ?? 0,
            0,
            syncState?.LastError,
            options.SyncSchedule,
            HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses())));
    }

    [HttpGet("stats")]
    public IActionResult GetStatistics()
    {
        using SqliteConnection connection = db.OpenConnection();
        int messageCount = ZulipMessageRecord.SelectCount(connection);
        int streamCount = ZulipStreamRecord.SelectCount(connection);
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(ZulipCacheLayout.SourceName);

        ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

        return Ok(new StatsResponse
        {
            Source = SourceSystems.Zulip,
            TotalItems = messageCount,
            TotalComments = 0,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
            CacheFiles = cacheStats.FileCount,
            LastSyncAt = syncState?.LastSyncAt,
            AdditionalCounts = new Dictionary<string, int> { ["streams"] = streamCount },
        });
    }

    [HttpGet("health")]
    public IActionResult GetHealthCheck()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
    }
}