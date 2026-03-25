using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A GitHub issue or pull request.</summary>
[LdgSQLiteTable("github_issues")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Number))]
[LdgSQLiteIndex(nameof(State))]
[LdgSQLiteIndex(nameof(Milestone))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
public partial record class GitHubIssueRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>Unique key in format "owner/repo#N".</summary>
    [LdgSQLiteUnique]
    public required string UniqueKey { get; set; }

    public required string RepoFullName { get; set; }
    public required int Number { get; set; }
    public required bool IsPullRequest { get; set; }
    public required string Title { get; set; }
    public required string? Body { get; set; }
    public required string State { get; set; }
    public required string? Author { get; set; }
    public required string? Labels { get; set; }
    public required string? Assignees { get; set; }
    public required string? Milestone { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public required DateTimeOffset? ClosedAt { get; set; }
    public required string? MergeState { get; set; }
    public required string? HeadBranch { get; set; }
    public required string? BaseBranch { get; set; }
}
