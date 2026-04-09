namespace FhirAugury.Common.Indexing;

/// <summary>
/// Tracks the rebuild state of indexes within a source service.
/// Registered as a singleton per service so all components share one tracker.
/// </summary>
public interface IIndexTracker
{
    /// <summary>Registers an index that this service manages.</summary>
    void RegisterIndex(string name, string description, Func<int> recordCountProvider);

    /// <summary>Marks an index rebuild as started.</summary>
    void MarkStarted(string indexName);

    /// <summary>Marks an index rebuild as completed.</summary>
    void MarkCompleted(string indexName, int? recordCount = null);

    /// <summary>Marks an index rebuild as failed.</summary>
    void MarkFailed(string indexName, string error);

    /// <summary>Returns the current status of all registered indexes.</summary>
    IReadOnlyList<IndexInfo> GetAllStatuses();

    /// <summary>Returns the status of a specific index.</summary>
    IndexInfo? GetStatus(string indexName);
}
