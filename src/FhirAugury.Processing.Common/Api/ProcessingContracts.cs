namespace FhirAugury.Processing.Common.Api;

public record ProcessingStatusResponse(
    string Status,
    bool IsRunning,
    bool IsPaused,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    DateTimeOffset? LastPollAt,
    string SyncSchedule,
    int MaxConcurrentProcessingThreads,
    bool StartProcessingOnStartup);

public record ProcessingQueueStatsResponse(
    int ProcessedCount,
    int RemainingCount,
    int InFlightCount,
    int ErrorCount,
    double? AverageItemDurationMs,
    DateTimeOffset? LastItemCompletedAt);

public record ProcessingLifecycleResponse(
    string Status,
    bool IsRunning,
    string? Message);
