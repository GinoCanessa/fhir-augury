using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 2 (slot 0625-02): commit→PR link sync uses delete-then-insert
/// (replace) semantics so a force-push that rewrites a PR's commit set does
/// not leave stale links. This exercises the same DB operations
/// <c>ApplyCommitLinks</c> performs (which is otherwise driven by a live
/// <c>gh</c> subprocess): delete every link for the (repo, PR), then insert the
/// new commit set idempotently.
/// </summary>
public class SyncPrCommitLinksReplaceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public SyncPrCommitLinksReplaceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_pr_links_replace_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void Replace_RemovesStaleLinks_KeepsNewSet()
    {
        const string repo = "HL7/fhir";
        const int prNumber = 5;

        using SqliteConnection conn = _db.OpenConnection();

        // Seed an original (now stale) commit set for the PR.
        InsertLink(conn, "old1", prNumber, repo);
        InsertLink(conn, "old2", prNumber, repo);
        // A link for a different PR must survive the replace.
        InsertLink(conn, "other", 6, repo);

        ReplaceLinks(conn, repo, prNumber, ["new1", "new2", "new3"]);

        List<GitHubCommitPrLinkRecord> forPr = GitHubCommitPrLinkRecord.SelectList(conn, PrNumber: prNumber, RepoFullName: repo);
        Assert.Equal(3, forPr.Count);
        Assert.Equivalent(new[] { "new1", "new2", "new3" }, forPr.Select(l => l.CommitSha).OrderBy(s => s).ToArray());

        // Untouched PR link remains.
        Assert.Single(GitHubCommitPrLinkRecord.SelectList(conn, PrNumber: 6, RepoFullName: repo));
    }

    private static void InsertLink(SqliteConnection conn, string sha, int prNumber, string repo)
        => GitHubCommitPrLinkRecord.Insert(conn, GhCliIssueMapper.MapCommitPrLink(sha, prNumber, repo), ignoreDuplicates: true);

    private static void ReplaceLinks(SqliteConnection conn, string repo, int prNumber, IEnumerable<string> shas)
    {
        using (SqliteCommand del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM github_commit_pr_links WHERE RepoFullName = @repo AND PrNumber = @n";
            del.Parameters.AddWithValue("@repo", repo);
            del.Parameters.AddWithValue("@n", prNumber);
            del.ExecuteNonQuery();
        }

        foreach (string sha in shas)
            InsertLink(conn, sha, prNumber, repo);
    }
}
