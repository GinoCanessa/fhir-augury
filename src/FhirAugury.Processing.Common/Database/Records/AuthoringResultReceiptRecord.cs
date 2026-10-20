using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_result_receipts")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(RunItemId))]
[LdgSQLiteIndex(nameof(BusinessKey))]
public partial record class AuthoringResultReceiptRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    [LdgSQLiteUnique]
    public required string OperationId { get; set; }

    public required string RunId { get; set; }
    public required string RunItemId { get; set; }
    public required string BusinessKey { get; set; }
    public required string ContentHash { get; set; }
    public required string ExpectedSourceRevision { get; set; }
    public required string ObservedSourceRevision { get; set; }
    public long AuthoringEpoch { get; set; }
    public required DateTimeOffset PersistedAt { get; set; }
}
