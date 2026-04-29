using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_related_jira")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(AssociatedTicketKey))]
[LdgSQLiteIndex(nameof(LinkType))]
public partial record class PreparedTicketRelatedJiraRecord
{
    public required string Id { get; set; }
    public required string TicketKey { get; set; }
    public required string AssociatedTicketKey { get; set; }
    public required string LinkType { get; set; }
    public required string Justification { get; set; }
}

