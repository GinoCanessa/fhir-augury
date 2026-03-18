using System.Text.Json;
using FhirAugury.Sources.GitHub;

namespace FhirAugury.Sources.Tests;

public class GitHubIssueMapperTests
{
    [Fact]
    public void MapIssue_ExtractsBasicFields()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-issue.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.Equal("HL7/fhir#1234", record.UniqueKey);
        Assert.Equal("HL7/fhir", record.RepoFullName);
        Assert.Equal(1234, record.Number);
        Assert.Equal("Fix Patient resource validation for R5", record.Title);
        Assert.Equal("open", record.State);
        Assert.Equal("test-user", record.Author);
        Assert.False(record.IsPullRequest);
    }

    [Fact]
    public void MapIssue_ExtractsLabels()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-issue.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.NotNull(record.Labels);
        Assert.Contains("bug", record.Labels);
        Assert.Contains("R5", record.Labels);
    }

    [Fact]
    public void MapIssue_ExtractsAssignees()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-issue.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.NotNull(record.Assignees);
        Assert.Contains("assignee1", record.Assignees);
        Assert.Contains("assignee2", record.Assignees);
    }

    [Fact]
    public void MapIssue_ExtractsMilestone()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-issue.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.Equal("R5 Release", record.Milestone);
    }

    [Fact]
    public void MapIssue_DetectsPullRequest()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-pr.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.True(record.IsPullRequest);
        Assert.Equal("feature/patient-contact-period", record.HeadBranch);
        Assert.Equal("main", record.BaseBranch);
        Assert.Equal("merged", record.MergeState);
        Assert.Equal("closed", record.State);
        Assert.NotNull(record.ClosedAt);
    }

    [Fact]
    public void MapIssue_ParsesDates()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-github-issue.json"));
        using var doc = JsonDocument.Parse(json);

        var record = GitHubIssueMapper.MapIssue(doc.RootElement, "HL7/fhir");

        Assert.Equal(2025, record.CreatedAt.Year);
        Assert.Equal(6, record.CreatedAt.Month);
        Assert.Null(record.ClosedAt);
    }
}
