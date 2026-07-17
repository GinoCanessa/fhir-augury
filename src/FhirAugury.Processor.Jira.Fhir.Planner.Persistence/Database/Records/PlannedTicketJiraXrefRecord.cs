using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_jira_xref")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(JiraKey))]
public partial record class PlannedTicketJiraXrefRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string JiraKey { get; set; }
    public required string Source { get; set; }
}
