using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_hydration")]
[LdgSQLiteIndex(nameof(Specification))]
[LdgSQLiteIndex(nameof(Resolution))]
[LdgSQLiteIndex(nameof(HydrationStatus))]
public partial record class PreparedTicketHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [LdgSQLiteUnique]
    public required string TicketKey { get; set; }
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
    public string? Assignee { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? RelatedArtifactsRaw { get; set; }
    public string? RelatedPagesRaw { get; set; }
    public string? SourceProject { get; set; }
    public DateTimeOffset? SourceLastSuccessfulRefreshAt { get; set; }
    public long? SourceContentRevision { get; set; }
    public required DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
    public int? PublicDisplayNamePolicyVersion { get; set; }
}
