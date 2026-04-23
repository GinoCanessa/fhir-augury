using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// FHIR change-request style Jira ticket. Inherits the shared Jira base
/// columns from <see cref="JiraIssueBaseRecord"/> and adds the HL7
/// change-request-template custom-field columns. Houses tickets from
/// FHIR/GCR/HTA/TSC/UP/UPSM (i.e. <c>JiraProjectShape.FhirChangeRequest</c>);
/// PSS/BALDEF/BALLOT live in their own sibling tables.
/// </summary>
[LdgSQLiteTable("jira_issues")]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(ProcessedLocallyAt))]
[LdgSQLiteIndex(nameof(WorkGroup), nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Specification), nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Resolution))]
[LdgSQLiteIndex(nameof(SelectedBallot))]
public partial record class JiraIssueRecord : JiraIssueBaseRecord
{
    public required string? Resolution { get; set; }
    public required string? ResolutionDescription { get; set; }
    public string? ResolutionDescriptionPlain { get; set; }

    // HL7 custom fields
    public required string? WorkGroup { get; set; }
    public required string? Specification { get; set; }
    public required string? RaisedInVersion { get; set; }
    public required string? SelectedBallot { get; set; }
    public required string? RelatedArtifacts { get; set; }
    public required string? RelatedIssues { get; set; }
    public required string? DuplicateOf { get; set; }
    public required string? AppliedVersions { get; set; }
    public required string? ChangeType { get; set; }
    public required string? Impact { get; set; }
    public required string? Vote { get; set; }
    public string? VoteMover { get; set; }
    public string? VoteSeconder { get; set; }
    public int? VoteForCount { get; set; }
    public int? VoteAgainstCount { get; set; }
    public int? VoteAbstainCount { get; set; }

    public string? ChangeCategory { get; set; }
    public string? ChangeImpact { get; set; }

    public string? Realm { get; set; }
    public string? SponsoringWorkGroup { get; set; }
    public string? CoSponsoringWorkGroups { get; set; }
}
