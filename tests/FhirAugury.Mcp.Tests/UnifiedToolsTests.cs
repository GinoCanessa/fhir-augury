using Fhiraugury;
using FhirAugury.Mcp.Tools;
using Grpc.Core;
using Grpc.Core.Testing;
using NSubstitute;

namespace FhirAugury.Mcp.Tests;

public class UnifiedToolsTests
{
    [Fact]
    public async Task Search_ReturnsFormattedResults()
    {
        var mockResponse = McpTestHelper.CreateSearchResponse(
            ("jira", "FHIR-123", "Test Issue", 0.95),
            ("zulip", "general:topic1", "Test Topic", 0.85));

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.UnifiedSearchAsync(Arg.Any<UnifiedSearchRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.Search(client, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("FHIR-123", result);
        Assert.Contains("Test Issue", result);
        Assert.Contains("Test Topic", result);
    }

    [Fact]
    public async Task Search_WithNoResults_ReturnsMessage()
    {
        var emptyResponse = new SearchResponse { TotalResults = 0 };

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(emptyResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.UnifiedSearchAsync(Arg.Any<UnifiedSearchRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.Search(client, "nonexistent");

        Assert.Contains("No results found", result);
    }

    [Fact]
    public async Task GetCrossReferences_ReturnsFormattedRefs()
    {
        var mockResponse = McpTestHelper.CreateXRefResponse(
            ("jira", "FHIR-100", "zulip", "stream:topic", "mentions"));

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.GetCrossReferencesAsync(Arg.Any<GetXRefRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.GetCrossReferences(client, "jira", "FHIR-100");

        Assert.Contains("Cross-References", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("mentions", result);
    }

    [Fact]
    public async Task GetCrossReferences_NoRefs_ReturnsMessage()
    {
        var emptyResponse = new GetXRefResponse();

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(emptyResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.GetCrossReferencesAsync(Arg.Any<GetXRefRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.GetCrossReferences(client, "jira", "FHIR-999");

        Assert.Contains("No cross-references found", result);
    }

    [Fact]
    public async Task GetStats_ReturnsFormattedStatus()
    {
        var mockResponse = McpTestHelper.CreateServicesStatus();

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.GetServicesStatusAsync(Arg.Any<ServicesStatusRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.GetStats(client);

        Assert.Contains("Services Status", result);
        Assert.Contains("jira", result);
        Assert.Contains("zulip", result);
        Assert.Contains("42", result); // cross-ref count
    }

    [Fact]
    public async Task TriggerSync_ReturnsFormattedStatus()
    {
        var mockResponse = new TriggerSyncResponse();
        mockResponse.Statuses.Add(new SourceSyncStatus { Source = "jira", Status = "started", Message = "Incremental sync started" });

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.TriggerSyncAsync(Arg.Any<TriggerSyncRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.TriggerSync(client);

        Assert.Contains("Sync Triggered", result);
        Assert.Contains("jira", result);
        Assert.Contains("started", result);
    }

    [Fact]
    public async Task FindRelated_ReturnsFormattedRelated()
    {
        var mockResponse = McpTestHelper.CreateRelatedResponse(
            "jira", "FHIR-100", "Test Jira Issue",
            ("zulip", "stream:topic", "Related Thread", 8.5, "explicit_xref"));

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<OrchestratorService.OrchestratorServiceClient>();
        client.FindRelatedAsync(Arg.Any<FindRelatedRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await UnifiedTools.FindRelated(client, "jira", "FHIR-100");

        Assert.Contains("Related Items", result);
        Assert.Contains("FHIR-100", result);
        Assert.Contains("Related Thread", result);
        Assert.Contains("explicit_xref", result);
    }
}
