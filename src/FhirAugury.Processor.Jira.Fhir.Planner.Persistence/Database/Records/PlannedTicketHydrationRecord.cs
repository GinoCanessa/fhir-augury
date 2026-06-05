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
    public DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
