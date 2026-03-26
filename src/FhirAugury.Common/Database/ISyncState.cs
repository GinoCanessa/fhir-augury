namespace FhirAugury.Common.Database;

/// <summary>
/// Common interface for sync state records across all source projects.
/// Each source defines its own concrete record type with CsLightDbGen attributes,
/// but all share this schema contract.
/// </summary>
public interface ISyncState
{
    int Id { get; set; }
    string SourceName { get; set; }
    string SubSource { get; set; }
    DateTimeOffset LastSyncAt { get; set; }
    string? LastCursor { get; set; }
    int ItemsIngested { get; set; }
    string? SyncSchedule { get; set; }
    DateTimeOffset? NextScheduledAt { get; set; }
    string Status { get; set; }
    string? LastError { get; set; }
}
