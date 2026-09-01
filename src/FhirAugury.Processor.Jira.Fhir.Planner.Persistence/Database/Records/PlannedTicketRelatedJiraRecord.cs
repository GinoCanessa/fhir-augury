using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_related_jira")]
[LdgSQLiteIndex(nameof(IssueKey))]
public partial record class PlannedTicketRelatedJiraRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string JiraKey { get; set; }
    public string Source { get; set; } = string.Empty;
}
