using FhirAugury.Common.Api;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class IngestionController(
    JiraIngestionPipeline pipeline,
    IngestionWorkQueue workQueue,
    JiraDatabase database,
    JiraIndexBuilder indexBuilder,
    JiraXRefRebuilder xrefRebuilder,
    JiraIndexer indexer,
    IOptions<JiraServiceOptions> optionsAccessor,
    IIndexTracker indexTracker) : ControllerBase
{
    private readonly JiraServiceOptions _options = optionsAccessor.Value;

    [HttpPost("ingest")]
    public async Task<IActionResult> TriggerIngestion([FromQuery] string? type, [FromQuery] string? project, CancellationToken ct)
    {
        if (project is not null)
        {
            List<JiraProjectConfig> configured = _options.GetEffectiveProjects();
            JiraProjectConfig? match = configured
                .FirstOrDefault(p => p.Key.Equals(project, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return BadRequest(new { error = $"Unknown project '{project}'" });

            project = match.Key;
        }

        string ingestionType = type ?? "incremental";
        try
        {
            IngestionResult result = ingestionType == "full"
                ? await pipeline.RunFullIngestionAsync(project: project, ct: ct)
                : await pipeline.RunIncrementalIngestionAsync(project: project, ct: ct);

            return Ok(new
            {
                result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated, result.ItemsFailed,
                errors = result.Errors,
                project = project ?? "all",
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("ingest/trigger")]
    public IActionResult QueueIngestion([FromQuery] string? type, [FromQuery] string? project)
    {
        if (project is not null)
        {
            List<JiraProjectConfig> configured = _options.GetEffectiveProjects();
            JiraProjectConfig? match = configured
                .FirstOrDefault(p => p.Key.Equals(project, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return BadRequest(new { error = $"Unknown project '{project}'" });

            project = match.Key;
        }

        string ingestionType = (type ?? "incremental").ToLowerInvariant();

        workQueue.Enqueue(ct => ingestionType switch
        {
            "full" => pipeline.RunFullIngestionAsync(project: project, ct: ct),
            _ => pipeline.RunIncrementalIngestionAsync(project: project, ct: ct),
        }, $"jira-{ingestionType}{(project is not null ? $"-{project}" : "")}");

        return Accepted(new { status = "queued", type = ingestionType, project = project ?? "all" });
    }

    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildFromCache()
    {
        try
        {
            IngestionResult result = await pipeline.RebuildFromCacheAsync();
            return Ok(new RebuildResponse(true, result.ItemsProcessed, 0, null));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex([FromQuery] string? type)
    {
        string indexType = (type ?? "all").ToLowerInvariant();

        workQueue.Enqueue(ct =>
        {
            RebuildIndexByType(indexType, ct);
            return Task.CompletedTask;
        }, $"rebuild-index-{indexType}");

        return Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
    }

    /// <summary>
    /// Acknowledges an ingestion-completion notification from a peer source so
    /// this service can rebuild its Jira-side cross-references.
    /// </summary>
    /// <remarks>
    /// Counterpart to the orchestrator-side
    /// <c>POST api/v1/notify-ingestion</c> endpoint
    /// (<c>FhirAugury.Orchestrator.Controllers.IngestionController.NotifyIngestion</c>).
    /// The orchestrator fans completion notifications from one source out to
    /// every other source via this endpoint.
    /// </remarks>
    [HttpPost("notify-peer")]
    [Tags("ingestion-notifications")]
    public IActionResult NotifyPeer([FromBody] PeerIngestionNotification notification)
    {
        workQueue.Enqueue(ct =>
        {
            xrefRebuilder.RebuildAll(ct);
            return Task.CompletedTask;
        }, "rebuild-xrefs");

        return Ok(new PeerIngestionAck(true));
    }

    private void RebuildIndexByType(string indexType, CancellationToken ct)
    {
        switch (indexType)
        {
            case "lookup-tables":
                RebuildSingleIndex("lookup-tables", () =>
                {
                    using SqliteConnection conn = database.OpenConnection();
                    indexBuilder.RebuildIndexTables(conn);
                });
                break;
            case "cross-refs":
                RebuildSingleIndex("cross-refs", () => xrefRebuilder.RebuildAll(ct));
                break;
            case "bm25":
                RebuildSingleIndex("bm25", () => indexer.RebuildFullIndex(ct));
                break;
            case "fts":
                RebuildSingleIndex("fts", () => database.RebuildFtsIndexes());
                break;
            case "all":
                RebuildSingleIndex("lookup-tables", () =>
                {
                    using SqliteConnection conn = database.OpenConnection();
                    indexBuilder.RebuildIndexTables(conn);
                });
                RebuildSingleIndex("cross-refs", () => xrefRebuilder.RebuildAll(ct));
                RebuildSingleIndex("bm25", () => indexer.RebuildFullIndex(ct));
                RebuildSingleIndex("fts", () => database.RebuildFtsIndexes());
                break;
        }
    }

    private void RebuildSingleIndex(string name, Action action)
    {
        indexTracker.MarkStarted(name);
        try
        {
            action();
            indexTracker.MarkCompleted(name);
        }
        catch (Exception ex)
        {
            indexTracker.MarkFailed(name, ex.Message);
            throw;
        }
    }
}