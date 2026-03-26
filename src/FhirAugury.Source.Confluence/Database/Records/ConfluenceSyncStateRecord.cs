using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>Tracks sync state and schedule per source or sub-source.</summary>
[LdgSQLiteTable("sync_state")]
[LdgSQLiteIndex(nameof(SourceName), nameof(SubSource))]
public partial record class ConfluenceSyncStateRecord : ISyncState
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceName { get; set; }
    public required string SubSource { get; set; }
    public required DateTimeOffset LastSyncAt { get; set; }
    public required string? LastCursor { get; set; }
    public required int ItemsIngested { get; set; }
    public required string? SyncSchedule { get; set; }
    public required DateTimeOffset? NextScheduledAt { get; set; }
    public required string Status { get; set; }
    public required string? LastError { get; set; }
}
