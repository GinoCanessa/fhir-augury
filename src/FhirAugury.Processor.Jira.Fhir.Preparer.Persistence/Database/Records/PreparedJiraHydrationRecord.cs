using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_jira_hydration")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(HydrationStatus))]
[LdgSQLiteIndex(nameof(WorkGroupClean))]
public partial record class PreparedJiraHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TicketKey { get; set; }
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
    public required DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
