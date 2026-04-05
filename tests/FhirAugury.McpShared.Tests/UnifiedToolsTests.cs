using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class UnifiedToolsTests
{
    [Fact]
    public async Task Search_ReturnsFormattedResults()
    {
        string json = """
            {
                "results": [
                    { "source": "jira", "id": "FHIR-123", "title": "Test Issue", "score": 0.95, "url": "https://example.com/jira/FHIR-123" },
                    { "source": "zulip", "id": "general:topic1", "title": "Test Topic", "score": 0.85 }
                ],
                "total": 2
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.Search(factory, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
        Assert.Contains("Test Topic", result);
    }

    [Fact]
    public async Task Search_WithNoResults_ReturnsMessage()
    {
        string json = """{ "results": [], "total": 0 }""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.Search(factory, "nonexistent");

        Assert.Contains("No results found", result);
    }

    [Fact]
    public async Task GetCrossReferences_ReturnsFormattedRefs()
    {
        string json = """
            {
                "references": [
                    { "sourceType": "jira", "sourceId": "FHIR-100", "targetType": "zulip", "targetId": "stream:topic", "linkType": "mentions" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.GetCrossReferences(factory, "jira", "FHIR-100");

        Assert.Contains("Cross-References", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("mentions", result);
    }

    [Fact]
    public async Task GetCrossReferences_NoRefs_ReturnsMessage()
    {
        string json = """{ "references": [] }""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.GetCrossReferences(factory, "jira", "FHIR-999");

        Assert.Contains("No cross-references found", result);
    }

    [Fact]
    public async Task GetStats_ReturnsFormattedStatus()
    {
        string json = """
            {
                "services": [
                    { "name": "jira", "status": "healthy", "itemCount": 1000, "dbSizeBytes": 10000000 },
                    { "name": "zulip", "status": "healthy", "itemCount": 5000, "dbSizeBytes": 50000000 }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.GetStats(factory);

        Assert.Contains("Services Status", result);
        Assert.Contains("jira", result);
        Assert.Contains("zulip", result);
    }

    [Fact]
    public async Task TriggerSync_ReturnsFormattedStatus()
    {
        string json = """
            {
                "statuses": [
                    { "source": "jira", "status": "started", "message": "Incremental sync started" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.TriggerSync(factory);

        Assert.Contains("Sync Triggered", result);
        Assert.Contains("jira", result);
        Assert.Contains("started", result);
    }

    [Fact]
    public async Task FindRelated_ReturnsFormattedRelated()
    {
        string json = """
            {
                "seedSource": "jira",
                "seedId": "FHIR-100",
                "seedTitle": "Test Jira Issue",
                "items": [
                    { "source": "zulip", "id": "stream:topic", "title": "Related Thread", "relevanceScore": 8.5, "relationship": "explicit_xref", "url": "https://example.com/zulip/stream:topic" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.FindRelated(factory, "jira", "FHIR-100");

        Assert.Contains("Related Items", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("Related Thread", result);
        Assert.Contains("explicit_xref", result);
    }
}
