using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class JiraCommentRecordTests
{
    [Fact]
    public void Insert_And_SelectByIssueKey()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-800", "Issue with comments");
        JiraIssueRecord.Insert(conn, issue);

        var comment1 = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = issue.Id,
            IssueKey = "FHIR-800",
            Author = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "First comment",
        };

        var comment2 = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = issue.Id,
            IssueKey = "FHIR-800",
            Author = "reviewer",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(1),
            Body = "Second comment",
        };

        JiraCommentRecord.Insert(conn, comment1);
        JiraCommentRecord.Insert(conn, comment2);

        var comments = JiraCommentRecord.SelectList(conn, IssueKey: "FHIR-800");
        Assert.Equal(2, comments.Count);
    }

    [Fact]
    public void Delete_RemovesComment()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var issue = TestHelper.CreateSampleIssue("FHIR-801", "Issue");
        JiraIssueRecord.Insert(conn, issue);

        var comment = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = issue.Id,
            IssueKey = "FHIR-801",
            Author = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "To be deleted",
        };

        JiraCommentRecord.Insert(conn, comment);
        Assert.Equal(1, JiraCommentRecord.SelectCount(conn, IssueKey: "FHIR-801"));

        // Use SQL delete directly
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM jira_comments WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", comment.Id);
        cmd.ExecuteNonQuery();

        Assert.Equal(0, JiraCommentRecord.SelectCount(conn, IssueKey: "FHIR-801"));
    }
}
