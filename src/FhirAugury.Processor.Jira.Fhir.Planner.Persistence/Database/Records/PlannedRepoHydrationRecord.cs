using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_repo_hydration")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(RepoKey))]
public partial record class PlannedRepoHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string RepoKey { get; set; }
    public string? Description { get; set; }
    public string? WorkGroup { get; set; }
    public string? Specification { get; set; }
    public string? CategoryDetail { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
