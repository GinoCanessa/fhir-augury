namespace FhirAugury.Common.Ingestion;

/// <summary>
/// Common interface for ingestion pipelines across all source services.
/// </summary>
public interface IIngestionPipeline
{
    /// <summary>Runs an incremental ingestion.</summary>
    Task RunIncrementalIngestionAsync(CancellationToken ct = default);

    /// <summary>Whether an ingestion is currently in progress.</summary>
    bool IsRunning { get; }

    /// <summary>Current status description.</summary>
    string CurrentStatus { get; }

    /// <summary>
    /// Returns the timestamp of the last completed sync, or null if no sync has ever completed.
    /// Used by the scheduled worker to skip startup syncs when data is still fresh.
    /// </summary>
    DateTimeOffset? GetLastSyncCompletedAt();
}
