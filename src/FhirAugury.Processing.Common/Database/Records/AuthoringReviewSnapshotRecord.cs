using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_review_snapshots")]
[LdgSQLiteIndex(nameof(ProcessorKind))]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(Sequence))]
public partial record class AuthoringReviewSnapshotRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string ProcessorKind { get; set; }
    public required string RunId { get; set; }
    public long AuthoringEpoch { get; set; }
    public long Sequence { get; set; }
    public int SchemaVersion { get; set; }
    public required string Status { get; set; }
    public required string TempPath { get; set; }
    public required string Path { get; set; }
    public string? ChecksumSha256 { get; set; }
    public long SizeBytes { get; set; }
    public int ItemCount { get; set; }
    public int ReceiptCount { get; set; }
    public required string TableCountsJson { get; set; }
    public string? PublicationProofJson { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public string? Error { get; set; }
}
