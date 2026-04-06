using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1")]
public class LifecycleController(
    GitHubIngestionPipeline pipeline,
    GitHubDatabase db,
    IResponseCache cache,
    IIndexTracker indexTracker,
    IOptions<GitHubServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        using SqliteConnection connection = db.OpenConnection();
        GitHubSyncStateRecord? syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);
        GitHubServiceOptions options = optionsAccessor.Value;

        return Ok(new IngestionStatusResponse(
            Source: SourceSystems.GitHub,
            Status: pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            LastSyncAt: syncState?.LastSyncAt,
            ItemsTotal: syncState?.ItemsIngested ?? 0,
            ItemsProcessed: syncState?.ItemsIngested ?? 0,
            LastError: syncState?.LastError,
            SyncSchedule: options.SyncSchedule,
            Indexes: HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses())));
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        using SqliteConnection connection = db.OpenConnection();
        int issueCount = GitHubIssueRecord.SelectCount(connection);
        int commentCount = GitHubCommentRecord.SelectCount(connection);
        int commitCount = GitHubCommitRecord.SelectCount(connection);
        int repoCount = GitHubRepoRecord.SelectCount(connection);
        int jiraRefCount = JiraXRefRecord.SelectCount(connection);
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(GitHubCacheLayout.SourceName);

        GitHubSyncStateRecord? syncState = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);

        return Ok(new StatsResponse
        {
            Source = SourceSystems.GitHub,
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
            CacheFiles = cacheStats.FileCount,
            LastSyncAt = syncState?.LastSyncAt,
            AdditionalCounts = new Dictionary<string, int>
            {
                ["repos"] = repoCount,
                ["commits"] = commitCount,
                ["jira_refs"] = jiraRefCount,
                ["spec_file_maps"] = GitHubSpecFileMapRecord.SelectCount(connection),
                ["file_contents"] = GitHubFileContentRecord.SelectCount(connection),
            },
        });
    }

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
    }
}