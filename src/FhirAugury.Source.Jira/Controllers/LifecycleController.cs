using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Hosting;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class LifecycleController(JiraIngestionPipeline pipeline, JiraDatabase db, IResponseCache cache, IIndexTracker indexTracker, IStartupRebuildStatus? startupRebuild = null) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        using SqliteConnection connection = db.OpenConnection();

        // Gather all sync state records for Jira
        List<JiraSyncStateRecord> allStates = JiraSyncStateRecord.SelectList(connection)
            .Where(s => s.SourceName == JiraSource.SourceName)
            .ToList();

        // Build per-project status list
        List<JiraProjectStatus> projectStatuses = allStates
            .Select(s =>
            {
                (string project, string _) = JiraSyncStateHelper.ParseSyncKey(s.SubSource);
                return new JiraProjectStatus(
                    Project: project,
                    LastSyncAt: s.LastSyncAt,
                    ItemsIngested: s.ItemsIngested,
                    Status: s.Status);
            })
            .GroupBy(p => p.Project)
            .Select(g => g.OrderByDescending(p => p.LastSyncAt).First())
            .ToList();

        // Overall status: most recent sync across all projects
        JiraSyncStateRecord? latestState = allStates
            .OrderByDescending(s => s.LastSyncAt)
            .FirstOrDefault();

        IngestionStatusResponse status = new IngestionStatusResponse(
            SourceSystems.Jira,
            pipeline.IsRunning ? pipeline.CurrentStatus : (latestState?.Status ?? "unknown"),
            latestState?.LastSyncAt,
            latestState?.ItemsIngested ?? 0,
            0,
            latestState?.LastError,
            pipeline.IsRunning ? pipeline.CurrentStatus : null,
            HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()),
            ["bm25", "cross-refs", "fts", "lookup-tables", "all"])
        {
            AdditionalData = projectStatuses.Count > 0
                ? new Dictionary<string, JsonElement> { ["projects"] = JsonSerializer.SerializeToElement(projectStatuses) }
                : null
        };

        return Ok(status);
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        using SqliteConnection connection = db.OpenConnection();
        int issueCount = JiraIssueRecord.SelectCount(connection);
        int commentCount = JiraCommentRecord.SelectCount(connection);
        int linkCount = JiraIssueLinkRecord.SelectCount(connection);
        long dbSize = db.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(JiraCacheLayout.SourceName);

        JiraSyncStateRecord? syncState = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

        // Per-project issue counts
        Dictionary<string, int> additionalCounts = new()
        {
            ["issue_links"] = linkCount,
        };

        using SqliteCommand projectCmd = connection.CreateCommand();
        projectCmd.CommandText = "SELECT ProjectKey, COUNT(*) FROM jira_issues GROUP BY ProjectKey";
        using SqliteDataReader reader = projectCmd.ExecuteReader();
        while (reader.Read())
        {
            string projectKey = reader.GetString(0);
            int count = reader.GetInt32(1);
            additionalCounts[$"project_{projectKey}"] = count;
        }

        return Ok(new StatsResponse
        {
            Source = SourceSystems.Jira,
            TotalItems = issueCount,
            TotalComments = commentCount,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
            CacheFiles = cacheStats.FileCount,
            LastSyncAt = syncState?.LastSyncAt,
            AdditionalCounts = additionalCounts,
        });
    }

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline, startupRebuild));
    }
}