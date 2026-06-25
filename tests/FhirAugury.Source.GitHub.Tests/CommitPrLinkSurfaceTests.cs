using FhirAugury.Source.GitHub.Controllers;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 3 (slot 0625-02): a commit's applying PR(s) are surfaced via
/// <see cref="GitHubUrlHelper.BuildCommitPrLinks"/> with a deterministic
/// <c>primaryPr</c> selection rule (merged → base-is-default-branch →
/// lowest number).
/// </summary>
public class CommitPrLinkSurfaceTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public CommitPrLinkSurfaceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_commit_surface_{Guid.NewGuid():N}.db");
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
    public void MergedPrToDefaultBranch_IsPrimary_AmongTwo()
    {
        using SqliteConnection conn = _db.OpenConnection();
        SeedPr(conn, number: 10, merged: true, baseBranch: "master");
        SeedPr(conn, number: 11, merged: false, baseBranch: "feature/x");
        Link(conn, "sha-merged", 10);
        Link(conn, "sha-merged", 11);

        (List<object> prs, object? primary, GitHubIssueRecord? primaryRec) =
            GitHubUrlHelper.BuildCommitPrLinks(conn, "sha-merged");

        Assert.Equal(2, prs.Count);
        Assert.NotNull(primary);
        Assert.Equal(10, primaryRec!.Number);
    }

    [Fact]
    public void TwoOpenFeaturePrs_PrimaryIsLowestNumber()
    {
        using SqliteConnection conn = _db.OpenConnection();
        SeedPr(conn, number: 21, merged: false, baseBranch: "feature/a");
        SeedPr(conn, number: 20, merged: false, baseBranch: "feature/b");
        Link(conn, "sha-open", 21);
        Link(conn, "sha-open", 20);

        (_, _, GitHubIssueRecord? primaryRec) = GitHubUrlHelper.BuildCommitPrLinks(conn, "sha-open");

        Assert.Equal(20, primaryRec!.Number);
    }

    [Fact]
    public void NoLinks_EmptyPrsAndNullPrimary()
    {
        using SqliteConnection conn = _db.OpenConnection();
        (List<object> prs, object? primary, GitHubIssueRecord? primaryRec) =
            GitHubUrlHelper.BuildCommitPrLinks(conn, "sha-unlinked");

        Assert.Empty(prs);
        Assert.Null(primary);
        Assert.Null(primaryRec);
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
