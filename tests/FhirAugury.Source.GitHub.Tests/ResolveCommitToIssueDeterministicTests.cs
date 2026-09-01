using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 3 (slot 0625-02): a commit linked to multiple PRs resolves
/// deterministically (via <see cref="GitHubUrlHelper.ResolveXRef"/>) to the
/// same primary PR that <see cref="GitHubUrlHelper.SelectPrimaryPr"/> picks —
/// not an arbitrary link row.
/// </summary>
public class ResolveCommitToIssueDeterministicTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public ResolveCommitToIssueDeterministicTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_resolve_det_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();

        using SqliteConnection conn = _db.OpenConnection();
        GitHubRepoRecord.Insert(conn, new GitHubRepoRecord
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = Repo,
            Owner = "HL7",
            Name = "fhir",
            Description = null,
            HasIssues = true,
            LastFetchedAt = DateTimeOffset.UtcNow,
            Category = "FhirCore",
            DefaultBranch = "master",
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void MultiPrCommit_ResolvesToSamePrimaryAsSelectPrimaryPr()
    {
        using SqliteConnection conn = _db.OpenConnection();
        SeedPr(conn, number: 30, merged: false, baseBranch: "feature/a");
        SeedPr(conn, number: 31, merged: true, baseBranch: "master");
        Link(conn, "sha-multi", 30);
        Link(conn, "sha-multi", 31);

        List<GitHubIssueRecord> prs =
        [
            GitHubIssueRecord.SelectSingle(conn, UniqueKey: $"{Repo}#30")!,
            GitHubIssueRecord.SelectSingle(conn, UniqueKey: $"{Repo}#31")!,
        ];
        GitHubIssueRecord? expected = GitHubUrlHelper.SelectPrimaryPr(prs, conn);

        JiraXRefRecord xref = new()
        {
            Id = JiraXRefRecord.GetIndex(),
            ContentType = ContentTypes.Commit,
            SourceId = "sha-multi",
            LinkType = "mentions",
            JiraKey = "FHIR-1",
            OriginalLiteral = "FHIR-1",
            Context = "ctx",
        };

        GitHubUrlHelper.ResolvedItem? resolved = GitHubUrlHelper.ResolveXRef(conn, xref);

        Assert.NotNull(resolved);
        Assert.Equal(expected!.UniqueKey, resolved!.Id);
        Assert.Equal($"{Repo}#31", resolved.Id);
    }

    private static void SeedPr(SqliteConnection conn, int number, bool merged, string baseBranch)
        => GitHubIssueRecord.Insert(conn, new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{Repo}#{number}",
            RepoFullName = Repo,
            Number = number,
            IsPullRequest = true,
            Title = $"PR {number}",
            Body = "body",
            State = merged ? "merged" : "open",
            Author = "octocat",
            Labels = null,
            Assignees = null,
            Milestone = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ClosedAt = null,
            MergeState = merged ? "merged" : null,
            HeadBranch = "head",
            BaseBranch = baseBranch,
        });

    private static void Link(SqliteConnection conn, string sha, int prNumber)
        => GitHubCommitPrLinkRecord.Insert(conn, new GitHubCommitPrLinkRecord
        {
            Id = GitHubCommitPrLinkRecord.GetIndex(),
            CommitSha = sha,
            PrNumber = prNumber,
            RepoFullName = Repo,
        }, ignoreDuplicates: true);
}
