using System.Text.Json;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 4 (slot 0625-02): inline PR review-thread comments are mapped from the
/// REST <c>/pulls/{n}/comments</c> shape with a stable
/// <c>review_comment</c> identity.
/// </summary>
public class ReviewThreadCommentMapperTests
{
    [Fact]
    public void MapReviewThreadComment_MapsRestFields()
    {
        string json = """
        {
            "id": 123,
            "user": { "login": "reviewer" },
            "created_at": "2024-05-01T09:00:00Z",
            "body": "This line needs a null check"
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        GitHubCommentRecord record = GhCliIssueMapper.MapReviewThreadComment(
            doc.RootElement, issueDbId: 4, "HL7/fhir", issueNumber: 99);

        Assert.Equal(4, record.IssueId);
        Assert.Equal("HL7/fhir", record.RepoFullName);
        Assert.Equal(99, record.IssueNumber);
        Assert.Equal("reviewer", record.Author);
        Assert.Equal("This line needs a null check", record.Body);
        Assert.True(record.IsReviewComment);
        Assert.Equal("review_comment", record.CommentKind);
        Assert.Equal("123", record.ExternalId);
    }
}
