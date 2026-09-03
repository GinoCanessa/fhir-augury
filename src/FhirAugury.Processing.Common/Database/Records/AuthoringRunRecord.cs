using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_runs")]
[LdgSQLiteIndex(nameof(ProcessorKind))]
[LdgSQLiteIndex(nameof(Status))]
public partial record class AuthoringRunRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string ProcessorKind { get; set; }
    public long AuthoringEpoch { get; set; }
    public required string Status { get; set; }
    public bool DatabaseOnly { get; set; }
    public int TotalItems { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? SnapshotId { get; set; }
}
