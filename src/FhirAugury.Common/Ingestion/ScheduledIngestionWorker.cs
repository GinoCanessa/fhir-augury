using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Ingestion;

/// <summary>
/// Generic background service that triggers incremental ingestion at a configured interval.
/// </summary>
public class ScheduledIngestionWorker<TPipeline>(
    TPipeline pipeline,
    Func<string> syncScheduleProvider,
    ILogger logger)
    : BackgroundService where TPipeline : IIngestionPipeline
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = syncScheduleProvider();
        if (!TimeSpan.TryParse(schedule, out var interval) || interval <= TimeSpan.Zero)
        {
            logger.LogWarning("Invalid or disabled sync schedule: {Schedule}", schedule);
            return;
        }

        logger.LogInformation("Scheduled ingestion worker started. Interval: {Interval}", interval);

        // Wait for initial startup to complete
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting scheduled incremental ingestion");
                await pipeline.RunIncrementalIngestionAsync(stoppingToken);
                logger.LogInformation("Scheduled ingestion completed successfully");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled ingestion failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Scheduled ingestion worker stopped");
    }
}
