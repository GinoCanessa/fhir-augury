using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class JiraToolsTests
{
    [Fact]
    public async Task GetJiraComments_ReturnsFormattedComments()
    {
        string json = """
            {
                "comments": [
                    { "author": "User1", "body": "First comment", "createdAt": "2024-01-01T00:00:00Z" },
                    { "author": "User2", "body": "Second comment", "createdAt": "2024-01-02T00:00:00Z" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.GetJiraComments(factory, "FHIR-100");

        Assert.Contains("Comments on FHIR-100", result);
        Assert.Contains("User1", result);
        Assert.Contains("First comment", result);
        Assert.Contains("User2", result);
    }

    [Fact]
    public async Task GetJiraComments_NoComments_ReturnsMessage()
    {
        string json = """{ "comments": [] }""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.GetJiraComments(factory, "FHIR-999");

        Assert.Contains("No comments", result);
    }

    [Fact]
    public async Task QueryJiraIssues_ReturnsFormattedResults()
    {
        string json = """
            {
                "results": [
                    { "key": "FHIR-100", "title": "Test Issue", "status": "Open", "type": "Bug", "workGroup": "FHIR-I", "updatedAt": "2024-01-01T00:00:00Z" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.QueryJiraIssues(factory, statuses: "Open");

        Assert.Contains("Jira Query Results", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("Open", result);
    }

    [Fact]
    public async Task QueryJiraIssues_WithLabels_IncludesLabelsInBody()
    {
        string json = """{"results":[{"key":"PROJ-1","title":"Test","status":"Open"}]}""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.QueryJiraIssues(factory, labels: "bug,urgent");

        Assert.Contains("PROJ-1", result);
    }

    [Fact]
    public async Task ListJiraLabels_ReturnsMarkdownTable()
    {
        string json = """[{"name":"bug","issueCount":42},{"name":"feature","issueCount":10}]""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.ListJiraLabels(factory);

        Assert.Contains("| bug | 42 |", result);
        Assert.Contains("| feature | 10 |", result);
        Assert.Contains("Label", result);
    }

    [Fact]
    public async Task ListJiraLabels_Empty_ReturnsMessage()
    {
        string json = "[]";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.ListJiraLabels(factory);

        Assert.Equal("No labels found.", result);
    }

    // ── Specifications ─────────────────────────────────────────────────

    [Fact]
    public async Task ListJiraSpecifications_ReturnsMarkdownTable()
    {
        string json = """[{"name":"FHIR Core","issueCount":300},{"name":"US Core","issueCount":150}]""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.ListJiraSpecifications(factory);

        Assert.Contains("| FHIR Core | 300 |", result);
        Assert.Contains("| US Core | 150 |", result);
        Assert.Contains("Specification", result);
    }

    [Fact]
    public async Task ListJiraSpecifications_Empty_ReturnsMessage()
    {
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", "[]");

        string result = await JiraTools.ListJiraSpecifications(factory);

        Assert.Equal("No specifications found.", result);
    }

    // ── Statuses ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListJiraStatuses_ReturnsMarkdownTable()
    {
        string json = """[{"name":"Open","issueCount":1000},{"name":"Closed","issueCount":2000}]""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.ListJiraStatuses(factory);

        Assert.Contains("| Open | 1000 |", result);
        Assert.Contains("| Closed | 2000 |", result);
        Assert.Contains("Status", result);
    }

    [Fact]
    public async Task ListJiraStatuses_Empty_ReturnsMessage()
    {
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", "[]");

        string result = await JiraTools.ListJiraStatuses(factory);

        Assert.Equal("No statuses found.", result);
    }

    // ── Enhanced QueryJiraIssues ────────────────────────────────────────

    [Fact]
    public async Task QueryJiraIssues_WithAssigneesAndReporters_Succeeds()
    {
        string json = """{"results":[{"key":"FHIR-100","title":"Test","status":"Open","type":"Bug"}]}""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.QueryJiraIssues(factory,
            assignees: "user1,user2",
            reporters: "reporter1");

        Assert.Contains("FHIR-100", result);
    }

    [Fact]
    public async Task QueryJiraIssues_WithDateFilters_Succeeds()
    {
        string json = """{"results":[{"key":"FHIR-200","title":"Date test","status":"Open","type":"Task"}]}""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.QueryJiraIssues(factory,
            createdAfter: "2024-01-01",
            updatedBefore: "2024-12-31",
            offset: 10);

        Assert.Contains("FHIR-200", result);
    }

    // ── Orchestrator routing (path assertions) ──────────────────────────
    // Every JiraTools tool must reach Jira through the orchestrator at
    // /api/v1/jira/...; a wrong path would still return mocked JSON and pass
    // the formatting tests above, so these pin the outgoing request path.

    private static string PathAndQuery(MockHttpHandler handler)
        => handler.LastRequest!.RequestUri!.PathAndQuery;

    [Fact]
    public async Task GetJiraComments_HitsOrchestratorCommentsRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", """{ "comments": [] }""");

        await JiraTools.GetJiraComments(factory, "FHIR-100", limit: 7);

        Assert.Equal("/api/v1/jira/items/FHIR-100/comments?limit=7", PathAndQuery(handler));
    }

    [Fact]
    public async Task QueryJiraIssues_PostsToOrchestratorQueryRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", """{"results":[]}""");

        await JiraTools.QueryJiraIssues(factory, statuses: "Open");

        Assert.Equal("/api/v1/jira/query", PathAndQuery(handler));
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task ListJiraLabels_HitsOrchestratorLabelsRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", "[]");

        await JiraTools.ListJiraLabels(factory);

        Assert.Equal("/api/v1/jira/labels", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListJiraSpecifications_HitsOrchestratorSpecificationsRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", "[]");

        await JiraTools.ListJiraSpecifications(factory);

        Assert.Equal("/api/v1/jira/specifications", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListJiraStatuses_HitsOrchestratorStatusesRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", "[]");

        await JiraTools.ListJiraStatuses(factory);

        Assert.Equal("/api/v1/jira/statuses", PathAndQuery(handler));
    }
}
