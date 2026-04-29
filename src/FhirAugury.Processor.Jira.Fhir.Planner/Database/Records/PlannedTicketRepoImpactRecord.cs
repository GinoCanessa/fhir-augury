using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_ticket_repo_impacts")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(TicketRepoId))]
[LdgSQLiteIndex(nameof(RepoKey))]
[LdgSQLiteIndex(nameof(TicketRepoChangeId))]
public partial record class PlannedTicketRepoImpactRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string TicketRepoId { get; set; }
    public required string RepoKey { get; set; }
    public string? TicketRepoChangeId { get; set; }
    public required string AffectedFilePath { get; set; }
    public string HowAffected { get; set; } = string.Empty;
}
