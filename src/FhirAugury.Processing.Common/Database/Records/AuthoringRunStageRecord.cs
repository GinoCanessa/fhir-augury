using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_run_stages")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(Status))]
public partial record class AuthoringRunStageRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string RunId { get; set; }
    public required string StageName { get; set; }
    public required string PartitionKey { get; set; }
    public required string InputFingerprint { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseId { get; set; }
    public DateTimeOffset? LeaseAcquiredAt { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
