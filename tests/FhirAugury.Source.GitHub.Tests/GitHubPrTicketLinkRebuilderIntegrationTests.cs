using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 2 (slot 0626-02): the PR↔ticket edge rebuilder projects xref_jira rows
/// into github_pr_ticket_links, unioning the three provenance sources
/// (description / comment / commit) into one logical edge per (repo, pr, ticket).
/// </summary>
public class GitHubPrTicketLinkRebuilderIntegrationTests : IDisposable
{
    private const string Repo = "HL7/fhir";

    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly GitHubXRefRebuilder _xrefRebuilder;
    private readonly GitHubPrTicketLinkRebuilder _rebuilder;

    public GitHubPrTicketLinkRebuilderIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pr_ticket_rebuild_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _xrefRebuilder = new GitHubXRefRebuilder(
            _db,
            Options.Create(new GitHubServiceOptions()),
            NullLogger<GitHubXRefRebuilder>.Instance);
        _rebuilder = new GitHubPrTicketLinkRebuilder(_db, NullLogger<GitHubPrTicketLinkRebuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private static GitHubIssueRecord MakeIssue(int number, bool isPr, string title, string? body = null) => new()
    {
        Id = GitHubIssueRecord.GetIndex(),
        UniqueKey = $"{Repo}#{number}",
        RepoFullName = Repo,
        Number = number,
        IsPullRequest = isPr,
        Title = title,
        Body = body,
        State = "open",
        Author = "dev",
        Labels = null,
        Assignees = null,
        Milestone = null,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
        UpdatedAt = DateTimeOffset.UtcNow,
        ClosedAt = null,
        MergeState = null,
        HeadBranch = null,
        BaseBranch = null,
    };

    private void Seed()
    {
        using SqliteConnection connection = _db.OpenConnection();

        // PR #10 — body names FHIR-1 (description provenance).
        GitHubIssueRecord.Insert(connection, MakeIssue(10, isPr: true, "A PR", body: "Implements FHIR-1"));
        // Non-PR issue #99 — names FHIR-99 (must NOT create a PR edge).
        GitHubIssueRecord.Insert(connection, MakeIssue(99, isPr: false, "An issue", body: "Tracks FHIR-99"));

        // Comment on PR #10 — names FHIR-2 (comment provenance).
        GitHubCommentRecord.Insert(connection, new GitHubCommentRecord
        {
            Id = GitHubCommentRecord.GetIndex(),
            IssueId = 10,
            RepoFullName = Repo,
            IssueNumber = 10,
            Author = "reviewer",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "This addresses FHIR-2",
            IsReviewComment = false,
            ExternalId = "555",
            CommentKind = "issue_comment",
        }, ignoreDuplicates: true);

        // Commit linked to PR #10 — message names FHIR-1 and FHIR-3 (commit provenance).
        GitHubCommitRecord.Insert(connection, new GitHubCommitRecord
        {
            Id = GitHubCommitRecord.GetIndex(),
            Sha = "sha-pr10",
            RepoFullName = Repo,
            Message = "FHIR-1 FHIR-3 implement and fix",
            Body = null,
            Author = "dev",
            Date = DateTimeOffset.UtcNow,
            Url = $"https://github.com/{Repo}/commit/sha-pr10",
        });
        GitHubCommitPrLinkRecord.Insert(connection, new GitHubCommitPrLinkRecord
        {
            Id = GitHubCommitPrLinkRecord.GetIndex(),
            CommitSha = "sha-pr10",
            PrNumber = 10,
            RepoFullName = Repo,
        }, ignoreDuplicates: true);

        // Populate xref_jira from the seeded prose.
        _xrefRebuilder.RebuildAll(Repo);
    }

    private static Dictionary<string, string> EdgesFor(SqliteConnection connection, int prNumber)
    {
        Dictionary<string, string> result = [];
        foreach (GitHubPrTicketLinkRecord row in GitHubPrTicketLinkRecord.SelectList(connection, RepoFullName: Repo, PrNumber: prNumber))
        {
            result[row.JiraKey] = row.Provenance;
        }
        return result;
    }

    [Fact]
    public void Rebuild_ProjectsAllProvenanceSources_DedupedAndMerged()
    {
        Seed();

        _rebuilder.RebuildAllRepos([Repo]);

        using SqliteConnection connection = _db.OpenConnection();
        Dictionary<string, string> edges = EdgesFor(connection, 10);

        // FHIR-1 from both the PR description and a PR commit.
        Assert.Equal("commit,description", edges["FHIR-1"]);
        // FHIR-2 from a PR comment only.
        Assert.Equal("comment", edges["FHIR-2"]);
        // FHIR-3 from a PR commit only.
        Assert.Equal("commit", edges["FHIR-3"]);
        // Exactly three edges for the PR; FHIR-99 (non-PR issue) excluded.
        Assert.Equal(3, edges.Count);
        Assert.DoesNotContain("FHIR-99", edges.Keys);
    }

    [Fact]
    public void Rebuild_NonPrIssue_CreatesNoEdge()
    {
        Seed();

        _rebuilder.RebuildAllRepos([Repo]);

        using SqliteConnection connection = _db.OpenConnection();
        Assert.Empty(GitHubPrTicketLinkRecord.SelectList(connection, JiraKey: "FHIR-99"));
    }

    [Fact]
    public void Rebuild_IsIdempotent_OneRowPerNaturalKey()
    {
        Seed();

        _rebuilder.RebuildAllRepos([Repo]);
        int firstCount;
        using (SqliteConnection connection = _db.OpenConnection())
        {
            firstCount = GitHubPrTicketLinkRecord.SelectList(connection).Count;
        }

        _rebuilder.RebuildAllRepos([Repo]);
        using SqliteConnection conn2 = _db.OpenConnection();
        List<GitHubPrTicketLinkRecord> rows = GitHubPrTicketLinkRecord.SelectList(conn2);

        Assert.Equal(firstCount, rows.Count);
        Assert.Equal(3, rows.Count);
        // No (repo, pr, ticket) duplicates.
        int distinct = rows.Select(r => (r.RepoFullName, r.PrNumber, r.JiraKey)).Distinct().Count();
        Assert.Equal(rows.Count, distinct);
    }

    [Fact]
    public void CommitNamingThreeTickets_YieldsThreeCommitRowsAndThreeEdges()
    {
        // Commit-multiplicity regression guard: one commit naming three tickets
        // must produce three xref_jira commit rows and, when linked to a PR,
        // three commit-provenance edges.
        using (SqliteConnection connection = _db.OpenConnection())
        {
            GitHubIssueRecord.Insert(connection, MakeIssue(20, isPr: true, "PR twenty", body: "no tickets in this body"));
            GitHubCommitRecord.Insert(connection, new GitHubCommitRecord
            {
                Id = GitHubCommitRecord.GetIndex(),
                Sha = "sha-multi",
                RepoFullName = Repo,
                Message = "FHIR-10 FHIR-20 FHIR-30 across the board",
                Body = null,
                Author = "dev",
                Date = DateTimeOffset.UtcNow,
                Url = $"https://github.com/{Repo}/commit/sha-multi",
            });
            GitHubCommitPrLinkRecord.Insert(connection, new GitHubCommitPrLinkRecord
            {
                Id = GitHubCommitPrLinkRecord.GetIndex(),
                CommitSha = "sha-multi",
                PrNumber = 20,
                RepoFullName = Repo,
            }, ignoreDuplicates: true);

            _xrefRebuilder.RebuildAll(Repo);

            List<JiraXRefRecord> commitRefs = JiraXRefRecord.SelectList(connection, ContentType: "commit", SourceId: "sha-multi");
            Assert.Equal(3, commitRefs.Count);
        }

        _rebuilder.RebuildAllRepos([Repo]);

        using SqliteConnection conn = _db.OpenConnection();
        Dictionary<string, string> edges = EdgesFor(conn, 20);
        Assert.Equal("commit", edges["FHIR-10"]);
        Assert.Equal("commit", edges["FHIR-20"]);
        Assert.Equal("commit", edges["FHIR-30"]);
        Assert.Equal(3, edges.Count);
    }
}
