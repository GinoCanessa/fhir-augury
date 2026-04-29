namespace FhirAugury.Processing.Common.Queue;

public record ProcessingQueueStats(
    int ProcessedCount,
    int RemainingCount,
    int InFlightCount,
    int ErrorCount,
    double? AverageItemDurationMs,
    DateTimeOffset? LastItemCompletedAt);
