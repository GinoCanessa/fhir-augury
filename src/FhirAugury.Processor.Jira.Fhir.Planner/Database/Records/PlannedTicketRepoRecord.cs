using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_ticket_repos")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(RepoKey))]
public partial record class PlannedTicketRepoRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string IssueKey { get; set; }
    public required string RepoKey { get; set; }
    public string? RepoRevision { get; set; }
    public string Justification { get; set; } = string.Empty;
}
