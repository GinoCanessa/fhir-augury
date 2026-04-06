using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class LifecycleController(JiraIngestionPipeline pipeline, JiraDatabase db, IResponseCache cache, IIndexTracker indexTracker) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        using SqliteConnection connection = db.OpenConnection();
        JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

        IngestionStatusResponse status = new IngestionStatusResponse(
            SourceSystems.Jira,
            pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            syncState?.LastSyncAt,
            syncState?.ItemsIngested ?? 0,
            0,
            syncState?.LastError,
            pipeline.IsRunning ? pipeline.CurrentStatus : null,
            HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()));

        return Ok(status);
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        using SqliteConnection connection = db.OpenConnection();
        int issueCount = JiraIssueRecord.SelectCount(connection);
        int commentCount = JiraCommentRecord.SelectCount(connection);
        int linkCount = JiraIssueLinkRecord.SelectCount(connection);
        int specCount = JiraSpecArtifactRecord.SelectCount(connection);
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(JiraCacheLayout.SourceName);

        JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

        return Ok(new StatsResponse
        {
            Source = SourceSystems.Jira,
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
            CacheFiles = cacheStats.FileCount,
            LastSyncAt = syncState?.LastSyncAt,
            AdditionalCounts = new Dictionary<string, int>
            {
                ["issue_links"] = linkCount,
                ["spec_artifacts"] = specCount,
            },
        });
    }

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
    }
}