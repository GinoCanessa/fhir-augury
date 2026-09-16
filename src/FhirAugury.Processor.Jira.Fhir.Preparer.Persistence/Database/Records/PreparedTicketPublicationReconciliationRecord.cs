using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_publication_reconciliations")]
public partial record class PreparedTicketPublicationReconciliationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string RunId { get; set; }

    public required string SourceRunId { get; set; }
    public required string SourceSnapshotId { get; set; }
    public required string SourceSnapshotSha256 { get; set; }
    public required string StableJiraGeneration { get; set; }
    public required string CorpusFingerprint { get; set; }
    public required string ComparisonJson { get; set; }
    public required string PromotionState { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset? AbandonedAt { get; set; }
    public string? AbandonmentReason { get; set; }
}

[LdgSQLiteTable("prepared_ticket_publication_reconciliation_journal")]
public partial record class PreparedTicketPublicationReconciliationJournalRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string RunId { get; set; }

    public required string State { get; set; }
    public string? SnapshotDescriptorJson { get; set; }
    public DateTimeOffset? LastRecoveryAttemptAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
