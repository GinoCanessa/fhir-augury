using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_related_zulip")]
[LdgSQLiteIndex(nameof(IssueKey))]
public partial record class PlannedTicketRelatedZulipRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string ZulipThreadId { get; set; }
}
