using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>A Jira issue with core fields and HL7-specific custom fields.</summary>
[LdgSQLiteTable("jira_issues")]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(WorkGroup), nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Specification), nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Resolution))]
[LdgSQLiteIndex(nameof(SelectedBallot))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(AssigneeId))]
[LdgSQLiteIndex(nameof(ReporterId))]
[LdgSQLiteIndex(nameof(VoteMoverId))]
[LdgSQLiteIndex(nameof(VoteSeconderId))]
public partial record class JiraIssueRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Key { get; set; }

    public required string ProjectKey { get; set; }
    public required string Title { get; set; }
    public required string? Description { get; set; }
    public string? DescriptionPlain { get; set; }
    public required string? Summary { get; set; }
    public required string Type { get; set; }
    public required string Priority { get; set; }
    public required string Status { get; set; }
    public required string? Resolution { get; set; }
    public required string? ResolutionDescription { get; set; }
    public string? ResolutionDescriptionPlain { get; set; }
    public required string? Assignee { get; set; }
    public required string? Reporter { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required DateTimeOffset? ResolvedAt { get; set; }

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

    // User FK columns referencing jira_users.Id
    public int? AssigneeId { get; set; }
    public int? ReporterId { get; set; }
    public int? VoteMoverId { get; set; }
    public int? VoteSeconderId { get; set; }

    public required string? Labels { get; set; }
    public required int CommentCount { get; set; }
    public required string? ChangeCategory { get; set; }
    public required string? ChangeImpact { get; set; }
}
