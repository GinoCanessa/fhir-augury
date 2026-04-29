using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_related_zulip")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(ZulipThreadId))]
public partial record class PreparedTicketRelatedZulipRecord
{
    public required string Id { get; set; }
    public required string TicketKey { get; set; }
    public required string ZulipThreadId { get; set; }
    public required string Justification { get; set; }
}

