namespace FhirAugury.Common.Indexing;

/// <summary>Snapshot of an individual index's state.</summary>
public class IndexInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsRebuilding { get; set; }
    public DateTimeOffset? LastRebuildStartedAt { get; set; }
    public DateTimeOffset? LastRebuildCompletedAt { get; set; }
    public int RecordCount { get; set; }
    public string? LastError { get; set; }
}
