using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_github_hydration")]
[LdgSQLiteIndex(nameof(IssueKey))]
public partial record class PlannedGitHubHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string GitHubItemId { get; set; }
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public int? Number { get; set; }
    public string? Path { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public bool? IsPullRequest { get; set; }
    public string? Labels { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
