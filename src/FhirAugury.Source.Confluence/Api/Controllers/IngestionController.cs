using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Confluence.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class IngestionController(
    ConfluenceIngestionPipeline pipeline,
    IngestionWorkQueue workQueue,
    ConfluenceDatabase database,
    ConfluenceIndexer indexer,
    ConfluenceXRefRebuilder xrefRebuilder,
    ConfluenceLinkRebuilder linkRebuilder,
    IIndexTracker indexTracker) : ControllerBase
{
    [HttpPost("ingest")]
    public async Task<IActionResult> TriggerIngestion([FromQuery] string? type, CancellationToken cancellationToken)
    {
        string ingestionType = type ?? "incremental";
        try
        {
            IngestionResult result = ingestionType == "full"
                ? await pipeline.RunFullIngestionAsync(cancellationToken)
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

    [HttpPost("rebuild")]
    public async Task<IActionResult> RebuildFromCache(CancellationToken cancellationToken)
    {
        try
        {
            IngestionResult result = await pipeline.RebuildFromCacheAsync(cancellationToken);
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
            RebuildIndexByType(indexType, database, indexer, xrefRebuilder, linkRebuilder, indexTracker, ct);
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

    private static void RebuildIndexByType(
        string indexType,
        ConfluenceDatabase database,
        ConfluenceIndexer indexer,
        ConfluenceXRefRebuilder xrefRebuilder,
        ConfluenceLinkRebuilder linkRebuilder,
        IIndexTracker indexTracker,
        CancellationToken ct)
    {
        switch (indexType)
        {
            case "bm25":
                indexTracker.MarkStarted("bm25");
                try { indexer.RebuildFullIndex(ct); indexTracker.MarkCompleted("bm25"); }
                catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                break;
            case "cross-refs":
                indexTracker.MarkStarted("cross-refs");
                try { xrefRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("cross-refs"); }
                catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                break;
            case "page-links":
                indexTracker.MarkStarted("page-links");
                try { linkRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("page-links"); }
                catch (Exception ex) { indexTracker.MarkFailed("page-links", ex.Message); throw; }
                break;
            case "fts":
                indexTracker.MarkStarted("fts");
                try { database.RebuildFtsIndexes(); indexTracker.MarkCompleted("fts"); }
                catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                break;
            case "all":
                indexTracker.MarkStarted("cross-refs");
                try { xrefRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("cross-refs"); }
                catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                indexTracker.MarkStarted("page-links");
                try { linkRebuilder.RebuildAll(ct); indexTracker.MarkCompleted("page-links"); }
                catch (Exception ex) { indexTracker.MarkFailed("page-links", ex.Message); throw; }
                indexTracker.MarkStarted("bm25");
                try { indexer.RebuildFullIndex(ct); indexTracker.MarkCompleted("bm25"); }
                catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                indexTracker.MarkStarted("fts");
                try { database.RebuildFtsIndexes(); indexTracker.MarkCompleted("fts"); }
                catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                break;
        }
    }
}