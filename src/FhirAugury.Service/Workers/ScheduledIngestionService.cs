using System.Collections.Concurrent;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Extensions.Options;

namespace FhirAugury.Service.Workers;

/// <summary>Background service that enqueues incremental syncs at configured intervals.</summary>
public class ScheduledIngestionService(
    IngestionQueue queue,
    DatabaseService dbService,
    IOptions<AuguryConfiguration> config,
    ILogger<ScheduledIngestionService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextRunTimes = [];

    /// <summary>Gets the next scheduled run time for each source.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> NextRunTimes => _nextRunTimes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduled ingestion service started");

        // Initialize schedule from config
        InitializeSchedule();

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var (sourceName, nextRun) in _nextRunTimes.ToArray())
            {
                if (stoppingToken.IsCancellationRequested) break;

                var sourceCfg = config.Value.Sources.GetValueOrDefault(sourceName);
                if (sourceCfg is null || !sourceCfg.Enabled)
                {
                    logger.LogDebug("Skipping disabled source: {Source}", sourceName);
                    continue;
                }

                if (now < nextRun) continue;

                try
                {
                    var request = new IngestionRequest
                    {
                        SourceName = sourceName,
                        Type = IngestionType.Incremental,
                    };

                    await queue.EnqueueAsync(request, stoppingToken);
                    logger.LogInformation("Enqueued scheduled sync for {Source}", sourceName);

                    // Advance next run time
                    var interval = GetSyncInterval(sourceName, sourceCfg);
                    _nextRunTimes[sourceName] = now + interval;

                    // Persist next scheduled time to sync_state
                    UpdateNextScheduledAt(sourceName, _nextRunTimes[sourceName], interval);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to enqueue scheduled sync for {Source}", sourceName);
                }
            }

            // Sleep until next source is due (max 30s to stay responsive)
            var sleepTime = CalculateSleepTime();
            await Task.Delay(sleepTime, stoppingToken);
        }

        logger.LogInformation("Scheduled ingestion service stopped");
    }

    private void InitializeSchedule()
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = config.Value;

        foreach (var (sourceName, sourceCfg) in cfg.Sources)
        {
            if (!sourceCfg.Enabled || sourceCfg.SyncSchedule is null)
                continue;

            // Check if we have a persisted next run time
            var nextRun = GetPersistedNextRun(sourceName);
            _nextRunTimes[sourceName] = nextRun ?? now + sourceCfg.SyncSchedule.Value;

            logger.LogInformation("Scheduled {Source} next sync at {NextRun} (interval: {Interval})",
                sourceName, _nextRunTimes[sourceName], sourceCfg.SyncSchedule.Value);
        }
    }

    private DateTimeOffset? GetPersistedNextRun(string sourceName)
    {
        try
        {
            using var conn = dbService.OpenConnection();
            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
            return syncState?.NextScheduledAt;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read persisted next run time for {Source}", sourceName);
            return null;
        }
    }

    private TimeSpan GetSyncInterval(string sourceName, SourceConfiguration sourceCfg)
    {
        // Check for runtime-updated schedule in sync_state
        try
        {
            using var conn = dbService.OpenConnection();
            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
            if (syncState?.SyncSchedule is not null && TimeSpan.TryParse(syncState.SyncSchedule, out var overridden))
            {
                return overridden;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read sync interval override for {Source}", sourceName);
        }

        return sourceCfg.SyncSchedule ?? TimeSpan.FromHours(1);
    }

    private void UpdateNextScheduledAt(string sourceName, DateTimeOffset nextRun, TimeSpan interval)
    {
        try
        {
            using var conn = dbService.OpenConnection();
            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
            if (syncState is not null)
            {
                syncState.NextScheduledAt = nextRun;
                syncState.SyncSchedule = interval.ToString();
                SyncStateRecord.Update(conn, syncState);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update next scheduled time for {Source}", sourceName);
        }
    }

    private TimeSpan CalculateSleepTime()
    {
        if (_nextRunTimes.Count == 0)
            return TimeSpan.FromSeconds(30);

        var now = DateTimeOffset.UtcNow;
        var nextDue = _nextRunTimes.Values.Min();
        var delay = nextDue - now;

        if (delay <= TimeSpan.Zero)
            return TimeSpan.FromSeconds(1);

        // Cap at 30 seconds to stay responsive
        return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    /// <summary>Updates the schedule for a specific source at runtime.</summary>
    public void UpdateSchedule(string sourceName, TimeSpan interval)
    {
        var now = DateTimeOffset.UtcNow;
        _nextRunTimes[sourceName] = now + interval;

        try
        {
            using var conn = dbService.OpenConnection();
            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
            if (syncState is not null)
            {
                syncState.SyncSchedule = interval.ToString();
                syncState.NextScheduledAt = _nextRunTimes[sourceName];
                SyncStateRecord.Update(conn, syncState);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist schedule update for {Source}", sourceName);
        }

        logger.LogInformation("Updated schedule for {Source}: interval={Interval}, next={NextRun}",
            sourceName, interval, _nextRunTimes[sourceName]);
    }
}
