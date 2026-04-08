using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1")]
public class IngestionController(
    GitHubIngestionPipeline pipeline,
    IngestionWorkQueue workQueue,
    GitHubDatabase database,
    GitHubIndexer indexer,
    GitHubXRefRebuilder xrefRebuilder,
    GitHubRepoCloner cloner,
    GitHubCommitFileExtractor commitExtractor,
    GitHubFileContentIndexer fileContentIndexer,
    ArtifactFileMapper artifactFileMapper,
    IIndexTracker indexTracker,
    IOptions<GitHubServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest([FromQuery] string? type, CancellationToken cancellationToken)
    {
        string ingestType = type ?? "incremental";
        try
        {
            IngestionResult result = ingestType == "full"
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
        string ingestionType = (type ?? "incremental").ToLowerInvariant();

        workQueue.Enqueue(ct => ingestionType switch
        {
            "full" => pipeline.RunFullIngestionAsync(ct: ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"github-{ingestionType}");

        return Accepted(new { status = "queued", type = ingestionType });
    }

    [HttpPost("rebuild")]
    public async Task<IActionResult> Rebuild(CancellationToken cancellationToken)
    {
        RebuildResponse result = await HttpServiceLifecycle.RebuildFromCacheAsync(
            async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
            cancellationToken);
        return result.Success ? Ok(result) : StatusCode(500);
    }

    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex([FromQuery] string? type)
    {
        string indexType = (type ?? "all").ToLowerInvariant();
        GitHubServiceOptions options = optionsAccessor.Value;

        workQueue.Enqueue(async ct =>
        {
            List<string> repos = options.GetAllRepositoryNames();
            switch (indexType)
            {
                case "commits":
                    indexTracker.MarkStarted("commits");
                    try
                    {
                        foreach (string repo in repos)
                        {
                            string path = await cloner.EnsureCloneAsync(repo, ct);
                            await commitExtractor.ExtractAsync(path, repo, ct);
                        }
                        indexTracker.MarkCompleted("commits");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("commits", ex.Message); throw; }
                    break;
                case "cross-refs":
                    indexTracker.MarkStarted("cross-refs");
                    try
                    {
                        foreach (string repo in repos)
                            xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
                        indexTracker.MarkCompleted("cross-refs");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("cross-refs", ex.Message); throw; }
                    break;
                case "bm25":
                    indexTracker.MarkStarted("bm25");
                    try
                    {
                        indexer.RebuildFullIndex(ct);
                        indexTracker.MarkCompleted("bm25");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("bm25", ex.Message); throw; }
                    break;
                case "artifact-map":
                    indexTracker.MarkStarted("artifact-map");
                    try
                    {
                        foreach (string repo in repos)
                        {
                            string path = await cloner.EnsureCloneAsync(repo, ct);
                            artifactFileMapper.BuildMappings(repo, path, ct);
                        }
                        indexTracker.MarkCompleted("artifact-map");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("artifact-map", ex.Message); throw; }
                    break;
                case "file-contents":
                    indexTracker.MarkStarted("file-contents");
                    try
                    {
                        foreach (string repo in repos)
                        {
                            string path = await cloner.EnsureCloneAsync(repo, ct);
                            fileContentIndexer.IndexRepositoryFiles(repo, path, ct);
                        }
                        indexTracker.MarkCompleted("file-contents");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("file-contents", ex.Message); throw; }
                    break;
                case "fts":
                    indexTracker.MarkStarted("fts");
                    try
                    {
                        database.RebuildFtsIndexes();
                        indexTracker.MarkCompleted("fts");
                    }
                    catch (Exception ex) { indexTracker.MarkFailed("fts", ex.Message); throw; }
                    break;
                case "all":
                    indexTracker.MarkStarted("commits");
                    indexTracker.MarkStarted("file-contents");
                    indexTracker.MarkStarted("cross-refs");
                    indexTracker.MarkStarted("artifact-map");
                    indexTracker.MarkStarted("bm25");
                    indexTracker.MarkStarted("fts");
                    try
                    {
                        foreach (string repo in repos)
                        {
                            string path = await cloner.EnsureCloneAsync(repo, ct);
                            await commitExtractor.ExtractAsync(path, repo, ct);
                            fileContentIndexer.IndexRepositoryFiles(repo, path, ct);
                            xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
                            artifactFileMapper.BuildMappings(repo, path, ct);
                        }
                        indexTracker.MarkCompleted("commits");
                        indexTracker.MarkCompleted("file-contents");
                        indexTracker.MarkCompleted("cross-refs");
                        indexTracker.MarkCompleted("artifact-map");
                        indexer.RebuildFullIndex(ct);
                        indexTracker.MarkCompleted("bm25");
                        database.RebuildFtsIndexes();
                        indexTracker.MarkCompleted("fts");
                    }
                    catch (Exception ex)
                    {
                        indexTracker.MarkFailed("commits", ex.Message);
                        indexTracker.MarkFailed("file-contents", ex.Message);
                        indexTracker.MarkFailed("cross-refs", ex.Message);
                        indexTracker.MarkFailed("artifact-map", ex.Message);
                        indexTracker.MarkFailed("bm25", ex.Message);
                        indexTracker.MarkFailed("fts", ex.Message);
                        throw;
                    }
                    break;
            }
        }, $"rebuild-index-{indexType}");

        return Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
    }

    [HttpPost("notify-peer")]
    public IActionResult NotifyPeer([FromBody] Common.Api.PeerIngestionNotification notification)
    {
        GitHubServiceOptions options = optionsAccessor.Value;
        workQueue.Enqueue(async ct =>
        {
            List<string> repos = options.GetAllRepositoryNames();
            foreach (string repo in repos)
                xrefRebuilder.RebuildAll(repo, validJiraNumbers: null, ct);
        }, "rebuild-xrefs");

        return Ok(new PeerIngestionAck(Acknowledged: true));
    }
}