using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_run_attempts")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(RunItemId))]
[LdgSQLiteIndex(nameof(Status))]
public partial record class AuthoringRunAttemptRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string OperationId { get; set; }

    public required string RunId { get; set; }
    public required string RunItemId { get; set; }
    public int AttemptNumber { get; set; }
    public required string TokenVerifier { get; set; }
    public required string Status { get; set; }
    public string? ContentHash { get; set; }
    public string? ObservedSourceRevision { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
