using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Processing.Common.Authoring;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_runs")]
[LdgSQLiteIndex(nameof(ProcessorKind))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(Purpose))]
[LdgSQLiteIndex(nameof(SourceRunId))]
public partial record class AuthoringRunRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string ProcessorKind { get; set; }
    public long AuthoringEpoch { get; set; }
    public required string Status { get; set; }
    public string Purpose { get; set; } = AuthoringRunPurposeValues.Authoring;
    public string? SourceRunId { get; set; }
    public bool DatabaseOnly { get; set; }
    public int TotalItems { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? SnapshotId { get; set; }
    public string? RequestJson { get; set; }
}
