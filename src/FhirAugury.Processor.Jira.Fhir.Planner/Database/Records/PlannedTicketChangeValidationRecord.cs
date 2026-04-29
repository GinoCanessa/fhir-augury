using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_ticket_change_validations")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(TicketRepoId))]
[LdgSQLiteIndex(nameof(RepoKey))]
public partial record class PlannedTicketChangeValidationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ValidationSequence { get; set; }
    public string Action { get; set; } = string.Empty;
}
