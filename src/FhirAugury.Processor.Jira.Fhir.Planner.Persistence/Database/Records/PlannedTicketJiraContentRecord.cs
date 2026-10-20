using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_jira_content")]
public partial record class PlannedTicketJiraContentRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string TicketKey { get; set; }

    public string? DescriptionHtml { get; set; }
    public string? ResolutionDescriptionHtml { get; set; }
}
