using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Ingestion;

/// <summary>
/// Generic background service that triggers incremental ingestion at a configured interval.
/// </summary>
/// <remarks>
/// When <paramref name="startupOnlyProvider"/> returns <c>true</c>, the worker runs a
/// single ingestion pass after the initial startup delay (still honoring
/// <c>MinSyncAge</c> and <c>IngestionPaused</c>) and then exits. The hosting service
/// remains running so HTTP endpoints and manual ingestion controllers are unaffected.
/// When it returns <c>false</c>, the original recurring-loop behavior is preserved.
/// </remarks>
public class ScheduledIngestionWorker<TPipeline>(
    TPipeline pipeline,
    Func<string> syncScheduleProvider,
    Func<string> minSyncAgeProvider,
    Func<bool> ingestionPausedProvider,
    Func<bool> startupOnlyProvider,
    ILogger logger)
    : BackgroundService where TPipeline : IIngestionPipeline
{
    /// <summary>
    /// Delay between service start and the first ingestion attempt. Exposed as
    /// a protected virtual so tests can override it to avoid a 30 s real wait.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(30);

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

        bool startupOnly = startupOnlyProvider();

        logger.LogInformation(
            "Scheduled ingestion worker started. Interval: {Interval}, MinSyncAge: {MinSyncAge}, StartupOnly: {StartupOnly}",
            interval, minAge, startupOnly);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Check if the last sync is fresh enough to skip the initial run.
        if (minAge > TimeSpan.Zero)
        {
            DateTimeOffset? lastSync = pipeline.GetLastSyncCompletedAt();
            if (lastSync.HasValue)
            {
                TimeSpan age = DateTimeOffset.UtcNow - lastSync.Value;
                if (age < minAge)
                {
                    TimeSpan remaining = minAge - age;

                    if (startupOnly)
                    {
                        logger.LogInformation(
                            "Last sync was {Age} ago (threshold: {MinSyncAge}). Startup-only ingestion mode: worker exiting without running",
                            age, minAge);
                        return;
                    }

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

        if (startupOnly)
        {
            await RunSinglePassAsync(stoppingToken);
            logger.LogInformation("Startup-only ingestion mode: worker exiting after initial run");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await RunSinglePassAsync(stoppingToken))
            {
                break;
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

    /// <summary>
    /// Runs a single scheduled ingestion pass, honoring <c>IngestionPaused</c> and
    /// swallowing pipeline exceptions. Returns <c>false</c> only when cancellation
    /// has been requested so the caller can break out of its loop.
    /// </summary>
    private async Task<bool> RunSinglePassAsync(CancellationToken stoppingToken)
    {
        if (ingestionPausedProvider())
        {
            logger.LogInformation("Ingestion is paused, skipping scheduled run");
            return true;
        }

        try
        {
            logger.LogInformation("Starting scheduled incremental ingestion");
            await pipeline.RunIncrementalIngestionAsync(stoppingToken);
            logger.LogInformation("Scheduled ingestion completed successfully");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled ingestion failed");
        }

        return true;
    }
}
