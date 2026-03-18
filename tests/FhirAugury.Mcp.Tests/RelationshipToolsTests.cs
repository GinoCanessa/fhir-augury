using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class RelationshipToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public RelationshipToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        var issue1 = McpTestHelper.CreateSampleIssue("FHIR-30001", "Patient search by name");
        JiraIssueRecord.Insert(conn, issue1);
        var issue2 = McpTestHelper.CreateSampleIssue("FHIR-30002", "Patient search improvements",
            description: "See FHIR-30001 for original discussion");
        JiraIssueRecord.Insert(conn, issue2);

        // Create a cross-reference link
        var xref = new CrossRefLinkRecord
        {
            Id = CrossRefLinkRecord.GetIndex(),
            SourceType = "jira",
            SourceId = "FHIR-30002",
            TargetType = "jira",
            TargetId = "FHIR-30001",
            LinkType = "mention",
            Context = "See FHIR-30001 for original discussion",
        };
        CrossRefLinkRecord.Insert(conn, xref);
    }

    [Fact]
    public void GetCrossReferences_ReturnsReferences()
    {
        var result = RelationshipTools.GetCrossReferences(_db, "jira", "FHIR-30002");
        Assert.Contains("FHIR-30001", result);
        Assert.Contains("Outbound", result);
    }

    [Fact]
    public void GetCrossReferences_InboundReferences()
    {
        var result = RelationshipTools.GetCrossReferences(_db, "jira", "FHIR-30001");
        Assert.Contains("FHIR-30002", result);
        Assert.Contains("Inbound", result);
    }

    [Fact]
    public void GetCrossReferences_NoRefs_ReturnsMessage()
    {
        var result = RelationshipTools.GetCrossReferences(_db, "jira", "FHIR-99999");
        Assert.Contains("No cross-references", result);
    }

    [Fact]
    public void FindRelated_NoRelated_ReturnsMessage()
    {
        var result = RelationshipTools.FindRelated(_db, "jira", "FHIR-99999");
        Assert.Contains("No related items", result);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
