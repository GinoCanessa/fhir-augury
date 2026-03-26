using System.Text.Json;
using FhirAugury.Source.GitHub.Ingestion;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Tests for <see cref="GhCliIssueMapper"/> — verifies mapping from gh CLI JSON shapes
/// to database records, including field name differences and state normalization.
/// </summary>
public class GhCliIssueMapperTests
{
    [Fact]
    public void MapIssue_BasicFields()
    {
        string json = """
        {
            "number": 42,
            "title": "Test Issue",
            "body": "Issue body text",
            "state": "OPEN",
            "author": { "login": "testuser" },
            "labels": [{ "name": "bug" }, { "name": "P1" }],
            "assignees": [{ "login": "dev1" }],
            "milestone": { "title": "R6" },
            "createdAt": "2024-01-15T10:00:00Z",
            "updatedAt": "2024-06-15T12:00:00Z",
            "closedAt": null
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubIssueRecord record = GhCliIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.Equal("HL7/fhir#42", record.UniqueKey);
        Assert.Equal("HL7/fhir", record.RepoFullName);
        Assert.Equal(42, record.Number);
        Assert.False(record.IsPullRequest);
        Assert.Equal("Test Issue", record.Title);
        Assert.Equal("Issue body text", record.Body);
        Assert.Equal("open", record.State); // normalized to lowercase
        Assert.Equal("testuser", record.Author);
        Assert.Equal("bug,P1", record.Labels);
        Assert.Equal("dev1", record.Assignees);
        Assert.Equal("R6", record.Milestone);
        Assert.Null(record.MergeState);
        Assert.Null(record.HeadBranch);
        Assert.Null(record.BaseBranch);
    }

    [Fact]
    public void MapIssue_ClosedState_NormalizesToLowercase()
    {
        string json = """
        {
            "number": 1,
            "title": "Closed",
            "body": null,
            "state": "CLOSED",
            "author": { "login": "u" },
            "labels": [],
            "assignees": [],
            "milestone": null,
            "createdAt": "2024-01-01T00:00:00Z",
            "updatedAt": "2024-01-02T00:00:00Z",
            "closedAt": "2024-01-02T00:00:00Z"
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubIssueRecord record = GhCliIssueMapper.MapIssue(doc.RootElement, "owner/repo");

        Assert.Equal("closed", record.State);
        Assert.NotNull(record.ClosedAt);
    }

    [Fact]
    public void MapPullRequest_WithMergedAt()
    {
        string json = """
        {
            "number": 100,
            "title": "Fix bug",
            "body": "PR body",
            "state": "MERGED",
            "author": { "login": "contributor" },
            "labels": [{ "name": "enhancement" }],
            "assignees": [],
            "milestone": null,
            "createdAt": "2024-03-01T00:00:00Z",
            "updatedAt": "2024-03-15T00:00:00Z",
            "closedAt": "2024-03-15T00:00:00Z",
            "mergedAt": "2024-03-15T00:00:00Z",
            "headRefName": "fix-bug-branch",
            "baseRefName": "main",
            "isDraft": false
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubIssueRecord record = GhCliIssueMapper.MapPullRequest(doc.RootElement, "HL7/fhir");

        Assert.True(record.IsPullRequest);
        Assert.Equal("merged", record.MergeState);
        Assert.Equal("fix-bug-branch", record.HeadBranch);
        Assert.Equal("main", record.BaseBranch);
        Assert.Equal("merged", record.State); // normalized
        Assert.Equal("contributor", record.Author);
        Assert.Equal("enhancement", record.Labels);
    }

    [Fact]
    public void MapPullRequest_OpenPr_NoMergeState()
    {
        string json = """
        {
            "number": 200,
            "title": "Draft PR",
            "body": "",
            "state": "OPEN",
            "author": { "login": "dev" },
            "labels": [],
            "assignees": [{ "login": "reviewer1" }, { "login": "reviewer2" }],
            "milestone": null,
            "createdAt": "2024-05-01T00:00:00Z",
            "updatedAt": "2024-05-02T00:00:00Z",
            "closedAt": null,
            "mergedAt": null,
            "headRefName": "feature/draft",
            "baseRefName": "develop",
            "isDraft": true
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubIssueRecord record = GhCliIssueMapper.MapPullRequest(doc.RootElement, "owner/repo");

        Assert.True(record.IsPullRequest);
        Assert.Null(record.MergeState); // mergedAt is null
        Assert.Equal("feature/draft", record.HeadBranch);
        Assert.Equal("develop", record.BaseBranch);
        Assert.Equal("reviewer1,reviewer2", record.Assignees);
    }

    [Fact]
    public void MapRepo_FromGhCliJson()
    {
        string json = """
        {
            "name": "fhir",
            "nameWithOwner": "HL7/fhir",
            "description": "FHIR specification",
            "hasIssuesEnabled": true,
            "owner": { "login": "HL7" }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubRepoRecord record = GhCliIssueMapper.MapRepo(doc.RootElement);

        Assert.Equal("HL7/fhir", record.FullName);
        Assert.Equal("HL7", record.Owner);
        Assert.Equal("fhir", record.Name);
        Assert.Equal("FHIR specification", record.Description);
        Assert.True(record.HasIssues);
    }

    [Fact]
    public void MapComment_FromGhCliJson()
    {
        string json = """
        {
            "author": { "login": "commenter" },
            "body": "This is a comment",
            "createdAt": "2024-02-10T08:30:00Z",
            "id": "IC_abc123",
            "url": "https://github.com/HL7/fhir/issues/42#issuecomment-123"
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubCommentRecord record = GhCliIssueMapper.MapComment(
            doc.RootElement, issueDbId: 7, "HL7/fhir", issueNumber: 42);

        Assert.Equal(7, record.IssueId);
        Assert.Equal("HL7/fhir", record.RepoFullName);
        Assert.Equal(42, record.IssueNumber);
        Assert.Equal("commenter", record.Author);
        Assert.Equal("This is a comment", record.Body);
        Assert.False(record.IsReviewComment);
    }

    [Fact]
    public void MapReview_FromGhCliJson()
    {
        string json = """
        {
            "author": { "login": "reviewer" },
            "body": "Looks good, minor nit",
            "state": "APPROVED",
            "submittedAt": "2024-04-20T14:00:00Z"
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubCommentRecord record = GhCliIssueMapper.MapReview(
            doc.RootElement, issueDbId: 10, "HL7/fhir", issueNumber: 100);

        Assert.Equal(10, record.IssueId);
        Assert.Equal("HL7/fhir", record.RepoFullName);
        Assert.Equal(100, record.IssueNumber);
        Assert.Equal("reviewer", record.Author);
        Assert.Equal("Looks good, minor nit", record.Body);
        Assert.True(record.IsReviewComment);
    }

    [Fact]
    public void MapIssue_EmptyLabelsAndAssignees_ReturnsNull()
    {
        string json = """
        {
            "number": 5,
            "title": "No labels",
            "body": null,
            "state": "OPEN",
            "author": { "login": "u" },
            "labels": [],
            "assignees": [],
            "milestone": null,
            "createdAt": "2024-01-01T00:00:00Z",
            "updatedAt": "2024-01-01T00:00:00Z",
            "closedAt": null
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Database.Records.GitHubIssueRecord record = GhCliIssueMapper.MapIssue(doc.RootElement, "o/r");

        Assert.Null(record.Labels);
        Assert.Null(record.Assignees);
    }
}
