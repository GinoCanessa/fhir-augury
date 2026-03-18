using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>Log of every ingestion run.</summary>
[LdgSQLiteTable("ingestion_log")]
[LdgSQLiteIndex(nameof(SourceName), nameof(StartedAt))]
public partial record class IngestionLogRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceName { get; set; }
    public required string RunType { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public required DateTimeOffset? CompletedAt { get; set; }
    public required int ItemsProcessed { get; set; }
    public required int ItemsNew { get; set; }
    public required int ItemsUpdated { get; set; }
    public required string? ErrorMessage { get; set; }
}
