using FhirAugury.McpShared.Tools;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

public class ContentToolsTests
{
    [Fact]
    public async Task ContentSearch_ReturnsFormattedResults()
    {
        string json = """
            {
                "values": ["test"],
                "total": 2,
                "hits": [
                    { "source": "jira", "id": "FHIR-123", "title": "Test Issue", "score": 0.95, "matchedValue": "test" },
                    { "source": "zulip", "id": "179:topic", "title": "Related Topic", "score": 0.80, "matchedValue": "test" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.ContentSearch(factory, "test");

        Assert.Contains("Search Results", result);
        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
        Assert.Contains("179:topic", result);
        Assert.Contains("0.95", result);
        Assert.Contains("test", result);
    }

    [Fact]
    public async Task ContentSearch_WithNoResults_ReturnsMessage()
    {
        string json = """{ "values": ["nonexistent"], "total": 0, "hits": [] }""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.ContentSearch(factory, "nonexistent");

        Assert.Contains("No results found", result);
    }

    [Fact]
    public async Task RefersTo_ReturnsFormattedResults()
    {
        string json = """
            {
                "value": "FHIR-123",
                "direction": "refers-to",
                "total": 1,
                "hits": [
                    { "sourceType": "jira", "sourceId": "FHIR-123", "targetType": "zulip", "targetId": "179:topic", "linkType": "mentions" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.RefersTo(factory, "FHIR-123");

        Assert.Contains("Refers To", result);
        Assert.Contains("FHIR-123", result);
        Assert.Contains("179:topic", result);
        Assert.Contains("mentions", result);
    }

    [Fact]
    public async Task RefersTo_NoResults_ReturnsMessage()
    {
        string json = """
            {
                "value": "FHIR-999",
                "direction": "refers-to",
                "total": 0,
                "hits": []
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.RefersTo(factory, "FHIR-999");

        Assert.Contains("No refers-to references found", result);
    }

    [Fact]
    public async Task ReferredBy_ReturnsFormattedResults()
    {
        string json = """
            {
                "value": "Patient.birthDate",
                "direction": "referred-by",
                "total": 1,
                "hits": [
                    { "sourceType": "zulip", "sourceId": "179:topic", "targetType": "fhir", "targetId": "Patient.birthDate", "linkType": "mentions", "sourceTitle": "Discussion Thread" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.ReferredBy(factory, "Patient.birthDate");

        Assert.Contains("Referred By", result);
        Assert.Contains("Patient.birthDate", result);
        Assert.Contains("179:topic", result);
        Assert.Contains("Discussion Thread", result);
    }

    [Fact]
    public async Task CrossReferenced_ReturnsFormattedResults()
    {
        string json = """
            {
                "value": "FHIR-456",
                "direction": "cross-referenced",
                "total": 2,
                "hits": [
                    { "sourceType": "jira", "sourceId": "FHIR-456", "targetType": "github", "targetId": "HL7/fhir#100", "linkType": "linked_issue" },
                    { "sourceType": "zulip", "sourceId": "200:review", "targetType": "jira", "targetId": "FHIR-456", "linkType": "mentions" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.CrossReferenced(factory, "FHIR-456");

        Assert.Contains("Cross-References", result);
        Assert.Contains("FHIR-456", result);
        Assert.Contains("HL7/fhir#100", result);
        Assert.Contains("linked_issue", result);
    }

    [Fact]
    public async Task GetItem_ReturnsFormattedItem()
    {
        string json = """
            {
                "source": "jira",
                "id": "FHIR-123",
                "title": "Test Issue",
                "url": "https://jira.example.com/FHIR-123",
                "metadata": { "status": "Open", "priority": "High" }
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.GetItem(factory, "jira", "FHIR-123");

        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
        Assert.Contains("status", result);
        Assert.Contains("Open", result);
        Assert.Contains("https://jira.example.com/FHIR-123", result);
    }

    [Fact]
    public async Task GetItem_WithSnapshot_IncludesSnapshot()
    {
        string json = """
            {
                "source": "jira",
                "id": "FHIR-789",
                "title": "Snapshot Item",
                "snapshot": "# FHIR-789\n\nFull markdown content here.",
                "metadata": { "status": "In Progress" }
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await ContentTools.GetItem(factory, "jira", "FHIR-789", includeSnapshot: true);

        Assert.Contains("FHIR-789", result);
        Assert.Contains("Snapshot", result);
        Assert.Contains("Full markdown content here.", result);
    }

    [Fact]
    public async Task ContentSearch_CallsOrchestratorClient()
    {
        string json = """{ "values": ["test"], "total": 0, "hits": [] }""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        await ContentTools.ContentSearch(factory, "test");

        // The factory mock is configured for "orchestrator" — if a different
        // client name were requested, NSubstitute would return a default
        // HttpClient without a BaseAddress and the call would throw.
        // Reaching this point confirms "orchestrator" was used.
        factory.Received().CreateClient("orchestrator");
    }
}
