using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>A comment on a GitHub issue or pull request.</summary>
[LdgSQLiteTable("github_comments")]
[LdgSQLiteIndex(nameof(IssueId))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(IssueNumber))]
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

    /// <summary>Stable GitHub-native comment identity (GraphQL node id for gh-CLI issue comments / reviews, numeric REST id stringified for review-thread comments). Used with <see cref="CommentKind"/> for dedup. Null on legacy rows ingested before this column existed.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Comment kind discriminator: "issue", "review", or "review_comment". GitHub's three comment resources have independent id sequences, so this distinguishes them in the dedup natural key. Null on legacy rows.</summary>
    public string? CommentKind { get; set; }
}
