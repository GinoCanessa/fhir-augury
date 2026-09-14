using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_publication_refresh_receipts")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(StageId))]
public partial record class PreparedTicketPublicationRefreshReceiptRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string RunId { get; set; }
    public required string StageId { get; set; }
    public required string InputFingerprint { get; set; }
    public required string CorpusFingerprint { get; set; }
    public required DateTimeOffset SourceLastSuccessfulRefreshAt { get; set; }
    public long SourceContentRevision { get; set; }
    public int PublicDisplayNamePolicyVersion { get; set; }
    public required DateTimeOffset AppliedAt { get; set; }
}
