using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_partition_certifications")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(StageId))]
[LdgSQLiteIndex(nameof(PartitionKey))]
[LdgSQLiteIndex(nameof(SourceRunId))]
public partial record class PreparedTicketPartitionCertificationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string RunId { get; set; }
    public required string StageId { get; set; }
    public required string PartitionKey { get; set; }
    public required string InputFingerprint { get; set; }
    public required string SourceRunId { get; set; }
    public required string SourceStageId { get; set; }
    public required string SourceInputFingerprint { get; set; }
    public required string OutputFingerprint { get; set; }
    public required DateTimeOffset CertifiedAt { get; set; }
}
