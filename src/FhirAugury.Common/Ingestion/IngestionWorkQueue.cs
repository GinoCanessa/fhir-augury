using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Ingestion;

/// <summary>
/// Queues ingestion requests onto a background executor using the application lifetime token
/// instead of the gRPC request token. This prevents client disconnects from cancelling
/// in-progress ingestion work.
/// </summary>
public class IngestionWorkQueue(
    IHostApplicationLifetime lifetime,
    ILogger<IngestionWorkQueue> logger)
{
    private Task? _currentWork;
    private readonly object _lock = new();

    /// <summary>
    /// Enqueues an ingestion function to run in the background using the application lifetime token.
    /// A linked token is created so the work stops if either the app shuts down or the original
    /// cancellation is explicitly requested by the caller (not the gRPC request).
    /// </summary>
    public void Enqueue(Func<CancellationToken, Task> work, string description)
    {
        CancellationToken appToken = lifetime.ApplicationStopping;

        lock (_lock)
        {
            if (_currentWork is not null && !_currentWork.IsCompleted)
            {
                logger.LogWarning("Ingestion work already in progress, skipping: {Description}", description);
                return;
            }

            _currentWork = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("Starting background ingestion: {Description}", description);
                    await work(appToken);
                    logger.LogInformation("Background ingestion completed: {Description}", description);
                }
                catch (OperationCanceledException) when (appToken.IsCancellationRequested)
                {
                    logger.LogInformation("Background ingestion cancelled due to app shutdown: {Description}", description);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background ingestion failed: {Description}", description);
                }
            }, CancellationToken.None);
        }
    }
}
