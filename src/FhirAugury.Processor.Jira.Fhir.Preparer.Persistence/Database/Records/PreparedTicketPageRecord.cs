using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_pages")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(Value))]
public partial record class PreparedTicketPageRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string TicketKey { get; set; }
    public required string Value { get; set; }
}
