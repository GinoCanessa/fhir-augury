using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class JiraToolsTests
{
    [Fact]
    public async Task GetJiraIssue_ReturnsFormattedIssue()
    {
        string json = """
            {
                "id": "FHIR-123",
                "title": "Test Issue Title",
                "content": "Test content",
                "url": "https://jira.hl7.org/browse/FHIR-123",
                "metadata": { "status": "Open", "type": "Bug" }
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.GetJiraIssue(factory, "FHIR-123");

        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue Title", result);
        Assert.Contains("Test content", result);
    }

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
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

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
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.GetJiraComments(factory, "FHIR-999");

        Assert.Contains("No comments", result);
    }

    [Fact]
    public async Task SnapshotJiraIssue_ReturnsMarkdown()
    {
        string json = """
            {
                "markdown": "# FHIR-123: Test Issue\n\nFull snapshot content..."
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await JiraTools.SnapshotJiraIssue(factory, "FHIR-123");

        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
    }

    [Fact]
    public async Task SearchJira_ReturnsFormattedResults()
    {
        string json = """
            {
                "results": [
                    { "source": "jira", "id": "FHIR-100", "title": "Issue One", "score": 0.9 },
                    { "source": "jira", "id": "FHIR-200", "title": "Issue Two", "score": 0.8 }
                ],
                "total": 2
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.SearchJira(factory, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("FHIR-200", result);
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
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.QueryJiraIssues(factory, statuses: "Open");

        Assert.Contains("Jira Query Results", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("Open", result);
    }

    [Fact]
    public async Task ListJiraIssues_ReturnsFormattedList()
    {
        string json = """
            {
                "items": [
                    { "key": "FHIR-100", "title": "Test Issue", "updatedAt": "2024-01-01T00:00:00Z", "url": "https://jira.hl7.org/browse/FHIR-100" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.ListJiraIssues(factory);

        Assert.Contains("Jira Issues", result);
        Assert.Contains("FHIR-100", result);
    }
}
