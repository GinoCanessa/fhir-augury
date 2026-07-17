using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_jira_hydration")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(WorkGroupClean))]
public partial record class PlannedJiraHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string JiraKey { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public string? Type { get; set; }
    public string? Priority { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionDescriptionPlain { get; set; }
    public string? WorkGroup { get; set; }
    public string? WorkGroupClean { get; set; }
    public string? Specification { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
