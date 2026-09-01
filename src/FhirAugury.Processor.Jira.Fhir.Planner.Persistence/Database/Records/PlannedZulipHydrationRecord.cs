using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_zulip_hydration")]
[LdgSQLiteIndex(nameof(IssueKey))]
public partial record class PlannedZulipHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string IssueKey { get; set; }
    public required string ZulipThreadId { get; set; }
    public int? StreamId { get; set; }
    public string? StreamName { get; set; }
    public string? Topic { get; set; }
    public int? MessageCount { get; set; }
    public DateTimeOffset? FirstMessageAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public string? FirstMessageExcerpt { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
