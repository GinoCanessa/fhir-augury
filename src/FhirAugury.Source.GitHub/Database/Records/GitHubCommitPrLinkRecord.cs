using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Bidirectional mapping between commits and pull requests.</summary>
[LdgSQLiteTable("github_commit_pr_links")]
[LdgSQLiteIndex(nameof(CommitSha))]
[LdgSQLiteIndex(nameof(PrNumber), nameof(RepoFullName))]
public partial record class GitHubCommitPrLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string CommitSha { get; set; }
    public required int PrNumber { get; set; }
    public required string RepoFullName { get; set; }
}
