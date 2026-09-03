using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_run_items")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(BusinessKey))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(CurrentOperationId))]
[LdgSQLiteIndex(nameof(AcceptedReceiptId))]
public partial record class AuthoringRunItemRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string RunId { get; set; }
    public required string BusinessKey { get; set; }
    public required string ItemKind { get; set; }
    public required string ExpectedSourceRevision { get; set; }
    public required string Status { get; set; }
    public string? CurrentOperationId { get; set; }
    public string? AcceptedReceiptId { get; set; }
    public string? PostPersistenceLeaseId { get; set; }
    public DateTimeOffset? PostPersistenceLeaseAcquiredAt { get; set; }
    public int AttemptCount { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
