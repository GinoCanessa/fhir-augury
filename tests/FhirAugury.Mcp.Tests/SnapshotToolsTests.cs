using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class SnapshotToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public SnapshotToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        // Jira issue with comments
        var issue = McpTestHelper.CreateSampleIssue("FHIR-50001", "Bundle entry ordering",
            description: "Bundle entries should be ordered by resource type", workGroup: "FHIR Infrastructure");
        JiraIssueRecord.Insert(conn, issue);
        var c1 = McpTestHelper.CreateSampleComment(issue.Id, "FHIR-50001", "Grace", "This is important for processors.");
        JiraCommentRecord.Insert(conn, c1);

        // Zulip thread
        var stream = McpTestHelper.CreateSampleStream(30, "infrastructure");
        ZulipStreamRecord.Insert(conn, stream);
        var msg = McpTestHelper.CreateSampleMessage(400, stream.Id, "infrastructure", "Bundle ordering",
            "Hank", "Discussion about FHIR-50001 bundle ordering rules");
        ZulipMessageRecord.Insert(conn, msg);

        // Confluence page
        var space = McpTestHelper.CreateSampleSpace("FHIR", "FHIR Specification");
        ConfluenceSpaceRecord.Insert(conn, space);
        var page = McpTestHelper.CreateSamplePage("7001", "Bundle Processing Rules", "FHIR",
            "Rules for processing Bundle resources including ordering constraints.");
        ConfluencePageRecord.Insert(conn, page);

        // Cross-reference: zulip message references jira issue
        var xref = new CrossRefLinkRecord
        {
            Id = CrossRefLinkRecord.GetIndex(),
            SourceType = "zulip",
            SourceId = "infrastructure:Bundle ordering",
            TargetType = "jira",
            TargetId = "FHIR-50001",
            LinkType = "mention",
            Context = "Discussion about FHIR-50001 bundle ordering rules",
        };
        CrossRefLinkRecord.Insert(conn, xref);
    }

    [Fact]
    public void SnapshotJiraIssue_IncludesAllSections()
    {
        var result = SnapshotTools.SnapshotJiraIssue(_db, "FHIR-50001");
        Assert.Contains("Bundle entry ordering", result);
        Assert.Contains("Description", result);
        Assert.Contains("Grace", result);
        Assert.Contains("Comments", result);
        Assert.Contains("Cross-References", result);
        Assert.Contains("jira.hl7.org", result);
    }

    [Fact]
    public void SnapshotJiraIssue_WithoutComments()
    {
        var result = SnapshotTools.SnapshotJiraIssue(_db, "FHIR-50001", includeComments: false);
        Assert.Contains("Bundle entry ordering", result);
        Assert.DoesNotContain("Grace", result);
    }

    [Fact]
    public void SnapshotJiraIssue_WithoutXrefs()
    {
        var result = SnapshotTools.SnapshotJiraIssue(_db, "FHIR-50001", includeXrefs: false);
        Assert.Contains("Bundle entry ordering", result);
        Assert.DoesNotContain("Cross-References", result);
    }

    [Fact]
    public void SnapshotJiraIssue_NotFound()
    {
        var result = SnapshotTools.SnapshotJiraIssue(_db, "FHIR-99999");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void SnapshotZulipThread_IncludesAllSections()
    {
        var result = SnapshotTools.SnapshotZulipThread(_db, "infrastructure", "Bundle ordering");
        Assert.Contains("infrastructure", result);
        Assert.Contains("Bundle ordering", result);
        Assert.Contains("Hank", result);
        Assert.Contains("Messages", result);
        Assert.Contains("Cross-References", result);
        Assert.Contains("FHIR-50001", result);
    }

    [Fact]
    public void SnapshotZulipThread_NotFound()
    {
        var result = SnapshotTools.SnapshotZulipThread(_db, "nonexistent", "nope");
        Assert.Contains("No messages found", result);
    }

    [Fact]
    public void SnapshotConfluencePage_IncludesAllSections()
    {
        var result = SnapshotTools.SnapshotConfluencePage(_db, "7001");
        Assert.Contains("Bundle Processing Rules", result);
        Assert.Contains("Content", result);
        Assert.Contains("FHIR", result);
    }

    [Fact]
    public void SnapshotConfluencePage_NotFound()
    {
        var result = SnapshotTools.SnapshotConfluencePage(_db, "99999");
        Assert.Contains("not found", result);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
