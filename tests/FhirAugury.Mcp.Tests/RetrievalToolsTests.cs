using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class RetrievalToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public RetrievalToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        var issue = McpTestHelper.CreateSampleIssue("FHIR-20001", "Encounter resource needs status field",
            description: "The Encounter resource should require a status field", workGroup: "Patient Administration");
        JiraIssueRecord.Insert(conn, issue);

        var comment = McpTestHelper.CreateSampleComment(issue.Id, "FHIR-20001", "Brian", "I agree this is needed.");
        JiraCommentRecord.Insert(conn, comment);
        var comment2 = McpTestHelper.CreateSampleComment(issue.Id, "FHIR-20001", "Carol", "Implemented in R5.");
        JiraCommentRecord.Insert(conn, comment2);

        var stream = McpTestHelper.CreateSampleStream(10, "terminology");
        ZulipStreamRecord.Insert(conn, stream);
        var msg1 = McpTestHelper.CreateSampleMessage(200, stream.Id, "terminology", "SNOMED mapping",
            "Dave", "How do we map SNOMED codes to FHIR?");
        ZulipMessageRecord.Insert(conn, msg1);
        var msg2 = McpTestHelper.CreateSampleMessage(201, stream.Id, "terminology", "SNOMED mapping",
            "Eve", "You can use ConceptMap resources.");
        ZulipMessageRecord.Insert(conn, msg2);

        var space = McpTestHelper.CreateSampleSpace("FHIR", "FHIR Specification");
        ConfluenceSpaceRecord.Insert(conn, space);
        var page = McpTestHelper.CreateSamplePage("6001", "Terminology Binding Guide", "FHIR",
            "Guide to binding ValueSets to elements in FHIR resources.");
        ConfluencePageRecord.Insert(conn, page);

        var repo = McpTestHelper.CreateSampleRepo("HL7/fhir");
        GitHubRepoRecord.Insert(conn, repo);
        var ghIssue = McpTestHelper.CreateSampleGitHubIssue("HL7/fhir", 99, "Add Encounter status validation",
            body: "Encounter should validate status is present");
        GitHubIssueRecord.Insert(conn, ghIssue);
    }

    [Fact]
    public void GetJiraIssue_ReturnsIssueDetails()
    {
        var result = RetrievalTools.GetJiraIssue(_db, "FHIR-20001");
        Assert.Contains("Encounter resource needs status field", result);
        Assert.Contains("Patient Administration", result);
        Assert.Contains("Description", result);
    }

    [Fact]
    public void GetJiraIssue_NotFound_ReturnsMessage()
    {
        var result = RetrievalTools.GetJiraIssue(_db, "FHIR-99999");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void GetJiraComments_ReturnsComments()
    {
        var result = RetrievalTools.GetJiraComments(_db, "FHIR-20001");
        Assert.Contains("Brian", result);
        Assert.Contains("Carol", result);
        Assert.Contains("2", result);
    }

    [Fact]
    public void GetJiraComments_NotFound_ReturnsMessage()
    {
        var result = RetrievalTools.GetJiraComments(_db, "FHIR-99999");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void GetZulipThread_ReturnsMessages()
    {
        var result = RetrievalTools.GetZulipThread(_db, "terminology", "SNOMED mapping");
        Assert.Contains("Dave", result);
        Assert.Contains("Eve", result);
        Assert.Contains("ConceptMap", result);
        Assert.Contains("Participants", result);
    }

    [Fact]
    public void GetZulipThread_NotFound_ReturnsMessage()
    {
        var result = RetrievalTools.GetZulipThread(_db, "nonexistent", "nope");
        Assert.Contains("No messages found", result);
    }

    [Fact]
    public void GetConfluencePage_ById_ReturnsPage()
    {
        var result = RetrievalTools.GetConfluencePage(_db, pageId: "6001");
        Assert.Contains("Terminology Binding Guide", result);
        Assert.Contains("FHIR", result);
        Assert.Contains("Content", result);
    }

    [Fact]
    public void GetConfluencePage_ByTitle_ReturnsPage()
    {
        var result = RetrievalTools.GetConfluencePage(_db, title: "Terminology Binding Guide", space: "FHIR");
        Assert.Contains("Terminology Binding Guide", result);
    }

    [Fact]
    public void GetConfluencePage_NotFound_ReturnsMessage()
    {
        var result = RetrievalTools.GetConfluencePage(_db, pageId: "99999");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void GetGithubIssue_ReturnsIssue()
    {
        var result = RetrievalTools.GetGithubIssue(_db, "HL7/fhir", 99);
        Assert.Contains("Add Encounter status validation", result);
        Assert.Contains("Issue", result);
        Assert.Contains("open", result);
    }

    [Fact]
    public void GetGithubIssue_NotFound_ReturnsMessage()
    {
        var result = RetrievalTools.GetGithubIssue(_db, "HL7/fhir", 99999);
        Assert.Contains("not found", result);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
