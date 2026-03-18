using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class ListingToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public ListingToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        // Jira issues
        var issue1 = McpTestHelper.CreateSampleIssue("FHIR-40001", "Patient search issue",
            workGroup: "Patient Administration", specification: "Patient");
        JiraIssueRecord.Insert(conn, issue1);
        var issue2 = McpTestHelper.CreateSampleIssue("FHIR-40002", "Observation code search",
            status: "Applied", workGroup: "Orders and Observations");
        JiraIssueRecord.Insert(conn, issue2);

        // Zulip streams and messages
        var stream1 = McpTestHelper.CreateSampleStream(20, "implementers");
        ZulipStreamRecord.Insert(conn, stream1);
        var stream2 = McpTestHelper.CreateSampleStream(21, "terminology");
        ZulipStreamRecord.Insert(conn, stream2);
        var msg = McpTestHelper.CreateSampleMessage(300, stream1.Id, "implementers", "search-topic",
            "Frank", "How does search work?");
        ZulipMessageRecord.Insert(conn, msg);

        // Confluence spaces
        var space = McpTestHelper.CreateSampleSpace("FHIR", "FHIR Specification");
        ConfluenceSpaceRecord.Insert(conn, space);

        // GitHub repos
        var repo = McpTestHelper.CreateSampleRepo("HL7/fhir");
        GitHubRepoRecord.Insert(conn, repo);
    }

    [Fact]
    public void ListJiraIssues_ReturnsAll()
    {
        var result = ListingTools.ListJiraIssues(_db);
        Assert.Contains("FHIR-40001", result);
        Assert.Contains("FHIR-40002", result);
        Assert.Contains("2 results", result);
    }

    [Fact]
    public void ListJiraIssues_FilterByWorkGroup()
    {
        var result = ListingTools.ListJiraIssues(_db, workGroup: "Patient Administration");
        Assert.Contains("FHIR-40001", result);
        Assert.DoesNotContain("FHIR-40002", result);
    }

    [Fact]
    public void ListJiraIssues_FilterByStatus()
    {
        var result = ListingTools.ListJiraIssues(_db, status: "Applied");
        Assert.Contains("FHIR-40002", result);
        Assert.DoesNotContain("FHIR-40001", result);
    }

    [Fact]
    public void ListJiraIssues_Empty_ReturnsMessage()
    {
        var result = ListingTools.ListJiraIssues(_db, workGroup: "Nonexistent Group");
        Assert.Contains("No Jira issues found", result);
    }

    [Fact]
    public void ListZulipStreams_ReturnsStreams()
    {
        var result = ListingTools.ListZulipStreams(_db);
        Assert.Contains("implementers", result);
        Assert.Contains("terminology", result);
        Assert.Contains("2", result);
    }

    [Fact]
    public void ListZulipTopics_ReturnsTopics()
    {
        var result = ListingTools.ListZulipTopics(_db, "implementers");
        Assert.Contains("search-topic", result);
    }

    [Fact]
    public void ListZulipTopics_Empty_ReturnsMessage()
    {
        var result = ListingTools.ListZulipTopics(_db, "nonexistent");
        Assert.Contains("No topics found", result);
    }

    [Fact]
    public void ListConfluenceSpaces_ReturnsSpaces()
    {
        var result = ListingTools.ListConfluenceSpaces(_db);
        Assert.Contains("FHIR", result);
        Assert.Contains("FHIR Specification", result);
    }

    [Fact]
    public void ListGithubRepos_ReturnsRepos()
    {
        var result = ListingTools.ListGithubRepos(_db);
        Assert.Contains("HL7/fhir", result);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
