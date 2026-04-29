using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_ticket_testing_considerations")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(TicketRepoId))]
[LdgSQLiteIndex(nameof(RepoKey))]
public partial record class PlannedTicketTestingConsiderationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public int ConsiderationSequence { get; set; }
    public string Consideration { get; set; } = string.Empty;
}
