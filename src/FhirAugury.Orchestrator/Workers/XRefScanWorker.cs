using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.CrossRef;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Workers;

/// <summary>
/// Background worker that periodically scans source services for cross-references.
/// </summary>
public class XRefScanWorker(
    CrossRefLinker linker,
    StructuralLinker structuralLinker,
    OrchestratorOptions options,
    ILogger<XRefScanWorker> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private bool _scanRequested;

    /// <summary>
    /// Requests an immediate cross-reference scan (triggered by ingestion callbacks).
    /// </summary>
    public void RequestScan()
    {
        _scanRequested = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.CrossRef.ScanIntervalMinutes);
        if (interval <= TimeSpan.Zero)
        {
            logger.LogWarning("Cross-reference scanning disabled (interval <= 0)");
            return;
        }

        logger.LogInformation("XRef scan worker started. Interval: {Interval}", interval);

        // Wait for services to start up
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _scanLock.WaitAsync(0, stoppingToken))
                {
                    try
                    {
                        var fullRescan = _scanRequested;
                        _scanRequested = false;

                        logger.LogInformation("Starting cross-reference scan (fullRescan={FullRescan})", fullRescan);

                        var textLinks = await linker.ScanAllSourcesAsync(fullRescan, stoppingToken);
                        var structLinks = await structuralLinker.LinkSpecArtifactsAsync(stoppingToken);

                        logger.LogInformation(
                            "Cross-reference scan complete: {TextLinks} text links, {StructLinks} structural links",
                            textLinks, structLinks);
                    }
                    finally
                    {
                        _scanLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cross-reference scan failed");
            }

            try
            {
                // Wait for interval or until a scan is requested
                var waitTime = _scanRequested ? TimeSpan.Zero : interval;
                if (waitTime > TimeSpan.Zero)
                    await Task.Delay(waitTime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("XRef scan worker stopped");
    }
}
