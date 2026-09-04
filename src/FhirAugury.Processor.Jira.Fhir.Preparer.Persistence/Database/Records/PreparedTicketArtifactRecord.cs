using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_artifacts")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(Value))]
public partial record class PreparedTicketArtifactRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string TicketKey { get; set; }
    public required string Value { get; set; }
}
