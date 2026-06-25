using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 4 (slot 0625-02): the natural-key unique index over
/// (RepoFullName, CommentKind, ExternalId) makes <c>ignoreDuplicates</c>
/// comment inserts idempotent across (re-)ingestion. Without it, the
/// GetIndex() PK lets every comment re-duplicate on each incremental run.
/// </summary>
public class CommentDedupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public CommentDedupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_comment_dedup_{Guid.NewGuid():N}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void SameIdentityInsertedTwice_YieldsSingleRow()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubCommentRecord.Insert(conn, MakeComment(), ignoreDuplicates: true);
        GitHubCommentRecord.Insert(conn, MakeComment(), ignoreDuplicates: true);

        List<GitHubCommentRecord> rows = GitHubCommentRecord.SelectList(conn, RepoFullName: "HL7/fhir", IssueNumber: 99);
        Assert.Single(rows);
    }

    [Fact]
    public void DifferentCommentKindSameExternalId_AreDistinct()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubCommentRecord issueComment = MakeComment();
        issueComment.CommentKind = "issue";
        GitHubCommentRecord reviewComment = MakeComment();
        reviewComment.CommentKind = "review_comment";

        GitHubCommentRecord.Insert(conn, issueComment, ignoreDuplicates: true);
        GitHubCommentRecord.Insert(conn, reviewComment, ignoreDuplicates: true);

        List<GitHubCommentRecord> rows = GitHubCommentRecord.SelectList(conn, RepoFullName: "HL7/fhir", IssueNumber: 99);
        Assert.Equal(2, rows.Count);
    }

    private static GitHubCommentRecord MakeComment() => new()
    {
        Id = GitHubCommentRecord.GetIndex(),
        IssueId = 1,
        RepoFullName = "HL7/fhir",
        IssueNumber = 99,
        Author = "reviewer",
        CreatedAt = DateTimeOffset.UtcNow,
        Body = "comment body",
        IsReviewComment = true,
        ExternalId = "123",
        CommentKind = "review_comment",
    };
}
