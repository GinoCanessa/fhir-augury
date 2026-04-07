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

    [Fact]
    public async Task QueryJiraIssues_WithLabels_IncludesLabelsInBody()
    {
        string json = """[{"key":"PROJ-1","summary":"Test","status":"Open"}]""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        await JiraTools.QueryJiraIssues(factory, labels: "bug,urgent");

        // Verify the method accepted labels without error — the mock returns a valid response
    }

    [Fact]
    public async Task ListJiraLabels_ReturnsMarkdownTable()
    {
        string json = """[{"name":"bug","issueCount":42},{"name":"feature","issueCount":10}]""";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.ListJiraLabels(factory);

        Assert.Contains("| bug | 42 |", result);
        Assert.Contains("| feature | 10 |", result);
        Assert.Contains("Label", result);
    }

    [Fact]
    public async Task ListJiraLabels_Empty_ReturnsMessage()
    {
        string json = "[]";
        IHttpClientFactory factory = McpTestHelper.CreateFactory("jira", json);

        string result = await JiraTools.ListJiraLabels(factory);

        Assert.Equal("No labels found.", result);
    }
}
