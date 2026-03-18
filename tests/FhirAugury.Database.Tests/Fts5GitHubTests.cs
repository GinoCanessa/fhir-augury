using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class Fts5GitHubTests
{
    [Fact]
    public void Insert_AutoPopulatesFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1, "Patient validation bug");
        issue.Body = "The Patient resource validation fails for edge cases";
        GitHubIssueRecord.Insert(conn, issue);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM github_issues_fts WHERE github_issues_fts MATCH '\"Patient\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Update_UpdatesFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1, "Original Title");
        GitHubIssueRecord.Insert(conn, issue);

        issue.Title = "Updated Observation bug";
        issue.Body = "Observation resource has a validation issue";
        GitHubIssueRecord.Update(conn, issue);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM github_issues_fts WHERE github_issues_fts MATCH '\"Observation\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Delete_RemovesFromFts()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1, "To Delete");
        GitHubIssueRecord.Insert(conn, issue);

        using var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM github_issues WHERE Id = @id";
        delCmd.Parameters.AddWithValue("@id", issue.Id);
        delCmd.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM github_issues_fts WHERE github_issues_fts MATCH '\"Delete\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(0, count);
    }

    [Fact]
    public void CommentFts_InsertPopulates()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1, "Test Issue");
        GitHubIssueRecord.Insert(conn, issue);

        var comment = new GitHubCommentRecord
        {
            Id = GitHubCommentRecord.GetIndex(),
            IssueId = issue.Id,
            RepoFullName = "HL7/fhir",
            IssueNumber = 1,
            Author = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "This comment mentions Patient validation",
            IsReviewComment = false,
        };
        GitHubCommentRecord.Insert(conn, comment);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM github_comments_fts WHERE github_comments_fts MATCH '\"Patient\"'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1, count);
    }
}
