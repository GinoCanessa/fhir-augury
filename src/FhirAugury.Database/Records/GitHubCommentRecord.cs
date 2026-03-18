using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A comment on a GitHub issue or pull request.</summary>
[LdgSQLiteTable("github_comments")]
[LdgSQLiteIndex(nameof(IssueId))]
[LdgSQLiteIndex(nameof(RepoFullName))]
public partial record class GitHubCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(GitHubIssueRecord.Id))]
    public required int IssueId { get; set; }

    public required string RepoFullName { get; set; }
    public required int IssueNumber { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
    public required bool IsReviewComment { get; set; }
}
