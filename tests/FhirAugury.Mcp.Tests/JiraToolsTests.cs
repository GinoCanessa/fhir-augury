using Fhiraugury;
using FhirAugury.Mcp.Tools;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using NSubstitute;

namespace FhirAugury.Mcp.Tests;

public class JiraToolsTests
{
    [Fact]
    public async Task GetJiraIssue_ReturnsFormattedIssue()
    {
        var mockResponse = McpTestHelper.CreateItemResponse("jira", "FHIR-123", "Test Issue Title");

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.GetItemAsync(Arg.Any<GetItemRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await JiraTools.GetJiraIssue(client, "FHIR-123");

        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue Title", result);
        Assert.Contains("Test content", result);
    }

    [Fact]
    public async Task GetJiraComments_ReturnsFormattedComments()
    {
        var comments = new[]
        {
            new JiraComment
            {
                Id = "1", IssueKey = "FHIR-100", Author = "User1",
                Body = "First comment", CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            new JiraComment
            {
                Id = "2", IssueKey = "FHIR-100", Author = "User2",
                Body = "Second comment", CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var streamCall = McpTestHelper.CreateStreamingCall(comments);
        var client = Substitute.For<JiraService.JiraServiceClient>();
        client.GetIssueComments(Arg.Any<JiraGetCommentsRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await JiraTools.GetJiraComments(client, "FHIR-100");

        Assert.Contains("Comments on FHIR-100", result);
        Assert.Contains("User1", result);
        Assert.Contains("First comment", result);
        Assert.Contains("User2", result);
    }

    [Fact]
    public async Task GetJiraComments_NoComments_ReturnsMessage()
    {
        var streamCall = McpTestHelper.CreateStreamingCall<JiraComment>();
        var client = Substitute.For<JiraService.JiraServiceClient>();
        client.GetIssueComments(Arg.Any<JiraGetCommentsRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await JiraTools.GetJiraComments(client, "FHIR-999");

        Assert.Contains("No comments", result);
    }

    [Fact]
    public async Task SnapshotJiraIssue_ReturnsMarkdown()
    {
        var mockResponse = new SnapshotResponse
        {
            Id = "FHIR-123",
            Source = "jira",
            Markdown = "# FHIR-123: Test Issue\n\nFull snapshot content...",
        };

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.GetSnapshotAsync(Arg.Any<GetSnapshotRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await JiraTools.SnapshotJiraIssue(client, "FHIR-123");

        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
    }

    [Fact]
    public async Task SearchJira_ReturnsFormattedResults()
    {
        var mockResponse = McpTestHelper.CreateSearchResponse(
            ("jira", "FHIR-100", "Issue One", 0.9),
            ("jira", "FHIR-200", "Issue Two", 0.8));

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<SourceService.SourceServiceClient>();
        client.SearchAsync(Arg.Any<SearchRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await JiraTools.SearchJira(client, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("FHIR-200", result);
    }

    [Fact]
    public async Task QueryJiraIssues_ReturnsFormattedResults()
    {
        var issues = new[]
        {
            new JiraIssueSummary
            {
                Key = "FHIR-100", Title = "Test Issue",
                Status = "Open", Type = "Bug",
                WorkGroup = "FHIR-I",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var streamCall = McpTestHelper.CreateStreamingCall(issues);
        var client = Substitute.For<JiraService.JiraServiceClient>();
        client.QueryIssues(Arg.Any<JiraQueryRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await JiraTools.QueryJiraIssues(client, statuses: "Open");

        Assert.Contains("Jira Query Results", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("Open", result);
    }

    [Fact]
    public async Task ListJiraIssues_ReturnsFormattedList()
    {
        var items = new[]
        {
            new ItemSummary
            {
                Id = "FHIR-100", Title = "Test Issue",
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Url = "https://jira.hl7.org/browse/FHIR-100",
            },
        };

        var streamCall = McpTestHelper.CreateStreamingCall(items);
        var client = Substitute.For<SourceService.SourceServiceClient>();
        client.ListItems(Arg.Any<ListItemsRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await JiraTools.ListJiraIssues(client);

        Assert.Contains("Jira Issues", result);
        Assert.Contains("FHIR-100", result);
    }
}
