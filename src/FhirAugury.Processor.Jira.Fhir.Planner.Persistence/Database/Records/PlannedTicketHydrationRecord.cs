using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_hydration")]
[LdgSQLiteIndex(nameof(IssueKey))]
public partial record class PlannedTicketHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string IssueKey { get; set; }

    public string? Priority { get; set; }
    public string? Resolution { get; set; }
    public string? ResolutionDescriptionPlain { get; set; }
    public string? Specification { get; set; }
    public string? RaisedInVersion { get; set; }
    public string? SelectedBallot { get; set; }
    public string? ChangeCategory { get; set; }
    public string? Impact { get; set; }
    public string? Labels { get; set; }
    public int? CommentCount { get; set; }
    public string? DescriptionPlain { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? ResolutionDescriptionHtml { get; set; }
    public string? Reporter { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? RelatedArtifactsRaw { get; set; }
    public string? RelatedPagesRaw { get; set; }
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
