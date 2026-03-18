using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class SearchToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public SearchToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        var issue = McpTestHelper.CreateSampleIssue("FHIR-10001", "Patient resource validation error",
            description: "The Patient resource fails validation when birthDate is missing");
        JiraIssueRecord.Insert(conn, issue);

        var issue2 = McpTestHelper.CreateSampleIssue("FHIR-10002", "Observation search parameter fix",
            description: "Search by code returns incorrect results for Observation");
        JiraIssueRecord.Insert(conn, issue2);

        var stream = McpTestHelper.CreateSampleStream(1, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = McpTestHelper.CreateSampleMessage(100, stream.Id, "implementers", "Patient validation",
            "Alice", "Patient resource validation is broken when birthDate is null");
        ZulipMessageRecord.Insert(conn, msg);

        var page = McpTestHelper.CreateSamplePage("5001", "FHIR Patient Resource Guide", "FHIR",
            "Guide for implementing the Patient resource with validation rules");
        ConfluencePageRecord.Insert(conn, page);

        var repo = McpTestHelper.CreateSampleRepo("HL7/fhir");
        GitHubRepoRecord.Insert(conn, repo);
        var ghIssue = McpTestHelper.CreateSampleGitHubIssue("HL7/fhir", 42, "Fix Patient validation",
            body: "Patient validation needs to handle missing birthDate");
        GitHubIssueRecord.Insert(conn, ghIssue);

        FtsSetup.RebuildJiraFts(conn);
        FtsSetup.RebuildZulipFts(conn);
        FtsSetup.RebuildConfluenceFts(conn);
        FtsSetup.RebuildGitHubFts(conn);
    }

    [Fact]
    public void Search_ReturnsResultsFromAllSources()
    {
        var result = SearchTools.Search(_db, "Patient validation");
        Assert.Contains("Patient", result);
        Assert.Contains("results", result.ToLowerInvariant());
    }

    [Fact]
    public void Search_WithSourceFilter_FiltersCorrectly()
    {
        var result = SearchTools.Search(_db, "Patient", sources: "jira");
        Assert.Contains("FHIR-10001", result);
        Assert.DoesNotContain("HL7/fhir", result);
    }

    [Fact]
    public void Search_NoResults_ReturnsMessage()
    {
        var result = SearchTools.Search(_db, "xyznonexistent12345");
        Assert.Contains("No results", result);
    }

    [Fact]
    public void SearchJira_ReturnsJiraResults()
    {
        var result = SearchTools.SearchJira(_db, "validation");
        Assert.Contains("FHIR-10001", result);
    }

    [Fact]
    public void SearchZulip_ReturnsZulipResults()
    {
        var result = SearchTools.SearchZulip(_db, "Patient");
        Assert.Contains("implementers", result);
    }

    [Fact]
    public void SearchConfluence_ReturnsConfluenceResults()
    {
        var result = SearchTools.SearchConfluence(_db, "Patient");
        Assert.Contains("Patient Resource Guide", result);
    }

    [Fact]
    public void SearchGithub_ReturnsGitHubResults()
    {
        var result = SearchTools.SearchGithub(_db, "validation");
        Assert.Contains("HL7/fhir", result);
    }

    [Fact]
    public void SearchZulip_WithStreamFilter_FiltersCorrectly()
    {
        var result = SearchTools.SearchZulip(_db, "Patient", stream: "implementers");
        Assert.Contains("Patient", result);

        var result2 = SearchTools.SearchZulip(_db, "Patient", stream: "nonexistent");
        Assert.Contains("No results", result2);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
