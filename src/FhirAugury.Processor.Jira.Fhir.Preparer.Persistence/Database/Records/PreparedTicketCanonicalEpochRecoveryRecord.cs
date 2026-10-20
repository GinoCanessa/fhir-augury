using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_canonical_epoch_recoveries")]
public partial record class PreparedTicketCanonicalEpochRecoveryRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string RunId { get; set; }

    [LdgSQLiteUnique]
    public required string SourceRunId { get; set; }

    public required long AuthoringEpoch { get; set; }
    public required string CorpusFingerprint { get; set; }
    public required string GroupingFingerprint { get; set; }
    public required string RecipeFingerprint { get; set; }
    public required string RecipeJson { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
}

[LdgSQLiteTable("prepared_ticket_canonical_epoch_recovery_journal")]
public partial record class PreparedTicketCanonicalEpochRecoveryJournalRecord
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

[LdgSQLiteTable("prepared_ticket_canonical_epoch_recovery_resolutions")]
public partial record class PreparedTicketCanonicalEpochRecoveryResolutionRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string RunId { get; set; }

    [LdgSQLiteUnique]
    public required string SourceRunId { get; set; }

    [LdgSQLiteUnique]
    public required string SnapshotId { get; set; }

    public required long AuthoringEpoch { get; set; }
    public required string SnapshotSha256 { get; set; }
    public required string CorpusFingerprint { get; set; }
    public required string GroupingFingerprint { get; set; }
    public required string ProofJson { get; set; }
    public required DateTimeOffset ResolvedAt { get; set; }
}
