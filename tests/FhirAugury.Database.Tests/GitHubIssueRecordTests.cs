using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class GitHubIssueRecordTests
{
    [Fact]
    public void InsertAndSelectSingle_Roundtrip()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1234, "Test Issue");
        GitHubIssueRecord.Insert(conn, issue);

        var loaded = GitHubIssueRecord.SelectSingle(conn, UniqueKey: "HL7/fhir#1234");
        Assert.NotNull(loaded);
        Assert.Equal("Test Issue", loaded.Title);
        Assert.Equal("HL7/fhir", loaded.RepoFullName);
        Assert.Equal(1234, loaded.Number);
        Assert.False(loaded.IsPullRequest);
    }

    [Fact]
    public void Update_ModifiesRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1234, "Original");
        GitHubIssueRecord.Insert(conn, issue);

        issue.Title = "Updated";
        issue.State = "closed";
        issue.ClosedAt = DateTimeOffset.UtcNow;
        GitHubIssueRecord.Update(conn, issue);

        var loaded = GitHubIssueRecord.SelectSingle(conn, UniqueKey: "HL7/fhir#1234");
        Assert.NotNull(loaded);
        Assert.Equal("Updated", loaded.Title);
        Assert.Equal("closed", loaded.State);
        Assert.NotNull(loaded.ClosedAt);
    }

    [Fact]
    public void PullRequest_StoresCorrectly()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var pr = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 5678, "Test PR", isPullRequest: true);
        pr.HeadBranch = "feature/test";
        pr.BaseBranch = "main";
        pr.MergeState = "merged";
        GitHubIssueRecord.Insert(conn, pr);

        var loaded = GitHubIssueRecord.SelectSingle(conn, UniqueKey: "HL7/fhir#5678");
        Assert.NotNull(loaded);
        Assert.True(loaded.IsPullRequest);
        Assert.Equal("feature/test", loaded.HeadBranch);
        Assert.Equal("main", loaded.BaseBranch);
        Assert.Equal("merged", loaded.MergeState);
    }

    [Fact]
    public void SelectList_FiltersByRepo()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        GitHubIssueRecord.Insert(conn, TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1, "Issue 1"));
        GitHubIssueRecord.Insert(conn, TestHelper.CreateSampleGitHubIssue("HL7/fhir-ig-publisher", 2, "Issue 2"));

        var hl7Issues = GitHubIssueRecord.SelectList(conn, RepoFullName: "HL7/fhir");
        Assert.Single(hl7Issues);
    }

    [Fact]
    public void InsertIgnoreDuplicates_DoesNotThrow()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var issue = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1234, "Test");
        GitHubIssueRecord.Insert(conn, issue);

        var duplicate = TestHelper.CreateSampleGitHubIssue("HL7/fhir", 1234, "Duplicate");
        GitHubIssueRecord.Insert(conn, duplicate, ignoreDuplicates: true);

        var count = GitHubIssueRecord.SelectCount(conn);
        Assert.Equal(1, count);
    }
}
