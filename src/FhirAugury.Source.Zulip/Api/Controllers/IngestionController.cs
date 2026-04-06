using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class IngestionController(
    ZulipIngestionPipeline pipeline,
    IngestionWorkQueue workQueue,
    ZulipDatabase database,
    ZulipIndexer indexer,
    ZulipXRefRebuilder xrefRebuilder,
    IIndexTracker indexTracker,
    IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpPost("ingest")]
    public async Task<IActionResult> TriggerIngestion([FromQuery] string? type, CancellationToken cancellationToken)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (options.IngestionPaused)
            return StatusCode(StatusCodes.Status412PreconditionFailed);

        string ingestionType = type ?? "incremental";
        try
        {
            IngestionResult result = ingestionType == "full"
                ? await pipeline.RunFullIngestionAsync(ct: cancellationToken)
                : await pipeline.RunIncrementalIngestionAsync(cancellationToken);

            return Ok(new
            {
                result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated, result.ItemsFailed,
                errors = result.Errors,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("ingest/trigger")]
    public IActionResult QueueIngestion([FromQuery] string? type)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (options.IngestionPaused)
            return StatusCode(StatusCodes.Status412PreconditionFailed);

        string ingestionType = (type ?? "incremental").ToLowerInvariant();

        workQueue.Enqueue(ct => ingestionType switch
        {
            "full" => pipeline.RunFullIngestionAsync(ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"zulip-{ingestionType}");

        return Accepted(new { status = "queued", type = ingestionType });
    }

    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildFromCache()
    {
        RebuildResponse result = await HttpServiceLifecycle.RebuildFromCacheAsync(
            async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
            CancellationToken.None);
        return Ok(result);
    }

    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex([FromQuery] string? type)
    {
        string indexType = (type ?? "all").ToLowerInvariant();

        workQueue.Enqueue(ct =>
        {
            switch (indexType)
            {
                case "bm25":
                    indexTracker.MarkStarted("bm25");
                    try
                    {
                        indexer.RebuildFullIndex(ct);
                        indexTracker.MarkCompleted("bm25");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("bm25", ex.Message);
                        throw;
                    }
                    break;
                case "cross-refs":
                    indexTracker.MarkStarted("cross-refs");
                    try
                    {
                        xrefRebuilder.RebuildAll(ct);
                        indexTracker.MarkCompleted("cross-refs");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("cross-refs", ex.Message);
                        throw;
                    }
                    break;
                case "fts":
                    indexTracker.MarkStarted("fts");
                    try
                    {
                        database.RebuildFtsIndexes();
                        indexTracker.MarkCompleted("fts");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("fts", ex.Message);
                        throw;
                    }
                    break;
                case "all":
                    indexTracker.MarkStarted("bm25");
                    try
                    {
                        indexer.RebuildFullIndex(ct);
                        indexTracker.MarkCompleted("bm25");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("bm25", ex.Message);
                        throw;
                    }
                    indexTracker.MarkStarted("cross-refs");
                    try
                    {
                        xrefRebuilder.RebuildAll(ct);
                        indexTracker.MarkCompleted("cross-refs");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("cross-refs", ex.Message);
                        throw;
                    }
                    indexTracker.MarkStarted("fts");
                    try
                    {
                        database.RebuildFtsIndexes();
                        indexTracker.MarkCompleted("fts");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("fts", ex.Message);
                        throw;
                    }
                    break;
                default:
                    return Task.CompletedTask;
            }
            return Task.CompletedTask;
        }, $"rebuild-index-{indexType}");

        return Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
    }

    [HttpPost("notify-peer")]
    public IActionResult NotifyPeer([FromBody] PeerIngestionNotification notification)
    {
        workQueue.Enqueue(ct =>
        {
            xrefRebuilder.RebuildAll(ct);
            return Task.CompletedTask;
        }, "rebuild-xrefs");

        return Ok(new PeerIngestionAck(Acknowledged: true));
    }
}