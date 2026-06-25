using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 2 (slot 0625-02): <c>github_commit_pr_links</c> is populated on PR
/// ingestion. <see cref="GhCliIssueMapper.MapCommitPrLink"/> builds a link
/// record with a fresh PK, and the natural-key unique index makes
/// <c>ignoreDuplicates</c> inserts idempotent (the PK alone is a GetIndex()
/// value, so it cannot dedupe).
/// </summary>
public class GitHubCommitPrLinkMapperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubCommitPrLinkMapperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_pr_links_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void MapCommitPrLink_PopulatesFields()
    {
        GitHubCommitPrLinkRecord link = GhCliIssueMapper.MapCommitPrLink("abc123", 7, "HL7/fhir");

        Assert.Equal("abc123", link.CommitSha);
        Assert.Equal(7, link.PrNumber);
        Assert.Equal("HL7/fhir", link.RepoFullName);
        Assert.NotEqual(0, link.Id);
    }

    [Fact]
    public void InsertSameTriceTwice_YieldsSingleRow()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubCommitPrLinkRecord first = GhCliIssueMapper.MapCommitPrLink("sha1", 7, "HL7/fhir");
        GitHubCommitPrLinkRecord second = GhCliIssueMapper.MapCommitPrLink("sha1", 7, "HL7/fhir");

        GitHubCommitPrLinkRecord.Insert(conn, first, ignoreDuplicates: true);
        GitHubCommitPrLinkRecord.Insert(conn, second, ignoreDuplicates: true);

        List<GitHubCommitPrLinkRecord> rows = GitHubCommitPrLinkRecord.SelectList(conn, CommitSha: "sha1");
        Assert.Single(rows);
    }
}
