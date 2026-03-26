using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Ingestion;

/// <summary>
/// Generic background service that triggers incremental ingestion at a configured interval.
/// </summary>
public class ScheduledIngestionWorker<TPipeline>(
    TPipeline pipeline,
    Func<string> syncScheduleProvider,
    Func<string> minSyncAgeProvider,
    Func<bool> ingestionPausedProvider,
    ILogger logger)
    : BackgroundService where TPipeline : IIngestionPipeline
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string schedule = syncScheduleProvider();
        if (!TimeSpan.TryParse(schedule, out TimeSpan interval) || interval <= TimeSpan.Zero)
        {
            logger.LogWarning("Invalid or disabled sync schedule: {Schedule}", schedule);
            return;
        }

        TimeSpan minAge = TimeSpan.Zero;
        string minSyncAgeStr = minSyncAgeProvider();
        if (!string.IsNullOrEmpty(minSyncAgeStr) && TimeSpan.TryParse(minSyncAgeStr, out TimeSpan parsedMinAge))
        {
            minAge = parsedMinAge;
        }

        logger.LogInformation(
            "Scheduled ingestion worker started. Interval: {Interval}, MinSyncAge: {MinSyncAge}",
            interval, minAge);

        // Wait for initial startup to complete
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        // Check if the last sync is fresh enough to skip the initial run
        if (minAge > TimeSpan.Zero)
        {
            DateTimeOffset? lastSync = pipeline.GetLastSyncCompletedAt();
            if (lastSync.HasValue)
            {
                TimeSpan age = DateTimeOffset.UtcNow - lastSync.Value;
                if (age < minAge)
                {
                    TimeSpan remaining = minAge - age;
                    logger.LogInformation(
                        "Last sync was {Age} ago (threshold: {MinSyncAge}). Skipping startup sync, waiting {Remaining}",
                        age, minAge, remaining);

                    try
                    {
                        await Task.Delay(remaining, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (ingestionPausedProvider())
            {
                logger.LogInformation("Ingestion is paused, skipping scheduled run");
            }
            else
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
