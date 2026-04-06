using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

public class RelatedItemFinderTests
{
    private static OrchestratorOptions DefaultOptions(params string[] sourceNames)
    {
        OrchestratorOptions opts = new()
        {
            Related = new RelatedOptions
            {
                CrossSourceWeight = 10.0,
                Bm25SimilarityWeight = 3.0,
                SharedMetadataWeight = 2.0,
                DefaultLimit = 20,
                MaxKeyTerms = 15,
                PerSourceTimeoutSeconds = 5,
            },
        };
        foreach (string name in sourceNames)
        {
            opts.Services[name] = new SourceServiceConfig
            {
                HttpAddress = $"http://localhost:5000",
                Enabled = true,
            };
        }
        return opts;
    }

    private static IHttpClientFactory CreateFactory(params (string Name, string ResponseJson)[] clients)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        foreach ((string name, string json) in clients)
        {
            MockHttpHandler handler = new(json);
            HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
            factory.CreateClient(name).Returns(client);
        }
        return factory;
    }

    private static IHttpClientFactory CreateFactoryWithHandler(string clientName, MockHttpHandler handler)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient(clientName).Returns(client);
        return factory;
    }

    // ── Test 1: GetRelatedAsync deserializes FindRelatedResponse ──

    [Fact]
    public async Task GetRelatedAsync_DeserializesFindRelatedResponse()
    {
        FindRelatedResponse expected = new(
            SeedSource: "jira",
            SeedId: "FHIR-100",
            SeedTitle: "Test Issue",
            Items:
            [
                new RelatedItem
                {
                    Source = "github",
                    Id = "HL7/fhir#42",
                    Title = "PR title",
                    RelevanceScore = 0.85,
                    Relationship = "cross_reference",
                },
            ]);

        string json = JsonSerializer.Serialize(expected, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        IHttpClientFactory factory = CreateFactory(("source-jira", json));
        OrchestratorOptions opts = DefaultOptions("jira");
        SourceHttpClient httpClient = new(factory, Options.Create(opts), NullLogger<SourceHttpClient>.Instance);

        FindRelatedResponse? result = await httpClient.GetRelatedAsync("jira", "jira", "FHIR-100", 20, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("jira", result.SeedSource);
        Assert.Equal("FHIR-100", result.SeedId);
        Assert.Single(result.Items);
        Assert.Equal("github", result.Items[0].Source);
        Assert.Equal("HL7/fhir#42", result.Items[0].Id);
        Assert.Equal(0.85, result.Items[0].RelevanceScore, 2);
    }

    // ── Test 2: GetRelatedAsync includes seedSource and seedId in URL ──

    [Fact]
    public async Task GetRelatedAsync_IncludesSeedSourceInUrl()
    {
        string json = JsonSerializer.Serialize(
            new FindRelatedResponse("jira", "FHIR-100", null, []),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        MockHttpHandler handler = new(json);
        IHttpClientFactory factory = CreateFactoryWithHandler("source-github", handler);
        OrchestratorOptions opts = DefaultOptions("github");
        SourceHttpClient httpClient = new(factory, Options.Create(opts), NullLogger<SourceHttpClient>.Instance);

        await httpClient.GetRelatedAsync("github", "jira", "FHIR-100", 10, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        string requestUrl = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("seedSource=jira", requestUrl);
        Assert.Contains("seedId=FHIR-100", requestUrl);
        Assert.Contains("limit=10", requestUrl);
    }

    // ── Test 3: Signal A produces related candidates ──

    [Fact]
    public async Task SignalA_ProducesRelatedCandidates()
    {
        // Seed item response (for FetchSeedItem)
        ItemResponse seedItem = new()
        {
            Source = "jira",
            Id = "FHIR-100",
            Title = "Seed Issue",
            Content = "",
        };
        string seedJson = JsonSerializer.Serialize(seedItem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // FindRelated response from github source
        FindRelatedResponse relatedResp = new(
            SeedSource: "jira",
            SeedId: "FHIR-100",
            SeedTitle: "Seed Issue",
            Items:
            [
                new RelatedItem
                {
                    Source = "github",
                    Id = "HL7/fhir#42",
                    Title = "Related PR",
                    Url = "https://github.com/HL7/fhir/pull/42",
                    RelevanceScore = 0.9,
                    Relationship = "cross_reference",
                    Context = "Referenced in PR",
                },
            ]);
        string relatedJson = JsonSerializer.Serialize(relatedResp, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Empty search response (for Signal B)
        string emptySearchJson = JsonSerializer.Serialize(
            new SearchResponse("", 0, [], null),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Jira source: returns seed item + empty related + empty search
        // GitHub source: returns related items + empty search
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        // Jira client: serves GetItem (seed) and GetRelated (empty) and Search (empty)
        MockHttpHandler jiraHandler = new(seedJson);
        HttpClient jiraClient = new(jiraHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-jira").Returns(jiraClient);

        // GitHub client: serves GetRelated (with items) and Search (empty)
        MultiResponseHandler githubHandler = new([relatedJson, emptySearchJson]);
        HttpClient githubClient = new(githubHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-github").Returns(githubClient);

        OrchestratorOptions opts = DefaultOptions("jira", "github");
        SourceHttpClient httpClient = new(factory, Options.Create(opts), NullLogger<SourceHttpClient>.Instance);
        RelatedItemFinder finder = new(httpClient, Options.Create(opts), NullLogger<RelatedItemFinder>.Instance);

        FindRelatedResponse result = await finder.FindRelatedAsync("jira", "FHIR-100", 20, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("jira", result.SeedSource);
        // Should have at least the github item from Signal A
        RelatedItem? githubItem = result.Items.FirstOrDefault(i => i.Source == "github" && i.Id == "HL7/fhir#42");
        Assert.NotNull(githubItem);
        Assert.True(githubItem.RelevanceScore >= 9.0, $"Expected score >= 9.0 (CrossSourceWeight * 0.9), got {githubItem.RelevanceScore}");
        Assert.Equal("cross_reference", githubItem.Relationship);
    }

    // ── Test: Per-source limiting ensures each source contributes up to limit ──

    [Fact]
    public async Task FindRelatedAsync_LimitsPerSource_NotGlobally()
    {
        // Seed item
        ItemResponse seedItem = new()
        {
            Source = "jira",
            Id = "FHIR-100",
            Title = "Seed Issue",
            Content = "",
        };
        string seedJson = JsonSerializer.Serialize(seedItem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // GitHub returns 5 cross-ref items with high scores
        FindRelatedResponse githubRelated = new(
            SeedSource: "jira",
            SeedId: "FHIR-100",
            SeedTitle: "Seed Issue",
            Items: Enumerable.Range(1, 5).Select(i => new RelatedItem
            {
                Source = "github",
                Id = $"HL7/fhir#{i}",
                Title = $"GitHub PR {i}",
                Url = $"https://github.com/HL7/fhir/pull/{i}",
                RelevanceScore = 1.0 - (i * 0.01),
                Relationship = "cross_reference",
            }).ToList());
        string githubRelatedJson = JsonSerializer.Serialize(githubRelated, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Zulip returns 5 cross-ref items with very high scores (would dominate without per-source limit)
        FindRelatedResponse zulipRelated = new(
            SeedSource: "jira",
            SeedId: "FHIR-100",
            SeedTitle: "Seed Issue",
            Items: Enumerable.Range(1, 5).Select(i => new RelatedItem
            {
                Source = "zulip",
                Id = $"zulip-msg-{i}",
                Title = $"Zulip Message {i}",
                Url = $"https://chat.example.com/{i}",
                RelevanceScore = 2.0 - (i * 0.01),
                Relationship = "cross_reference",
            }).ToList());
        string zulipRelatedJson = JsonSerializer.Serialize(zulipRelated, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        string emptySearchJson = JsonSerializer.Serialize(
            new SearchResponse("", 0, [], null),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        // Jira: seed item + empty related + empty search
        MockHttpHandler jiraHandler = new(seedJson);
        HttpClient jiraClient = new(jiraHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-jira").Returns(jiraClient);

        // GitHub: related response + empty search
        MultiResponseHandler githubHandler = new([githubRelatedJson, emptySearchJson]);
        HttpClient githubClient = new(githubHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-github").Returns(githubClient);

        // Zulip: related response + empty search
        MultiResponseHandler zulipHandler = new([zulipRelatedJson, emptySearchJson]);
        HttpClient zulipClient = new(zulipHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-zulip").Returns(zulipClient);

        OrchestratorOptions opts = DefaultOptions("jira", "github", "zulip");
        opts.Related.DefaultLimit = 3; // limit 3 per source
        SourceHttpClient httpClient = new(factory, Options.Create(opts), NullLogger<SourceHttpClient>.Instance);
        RelatedItemFinder finder = new(httpClient, Options.Create(opts), NullLogger<RelatedItemFinder>.Instance);

        FindRelatedResponse result = await finder.FindRelatedAsync("jira", "FHIR-100", 3, null, CancellationToken.None);

        // Each source should have at most 3 items despite Zulip having higher scores
        int githubCount = result.Items.Count(i => i.Source == "github");
        int zulipCount = result.Items.Count(i => i.Source == "zulip");

        Assert.True(githubCount <= 3, $"Expected at most 3 github items, got {githubCount}");
        Assert.True(zulipCount <= 3, $"Expected at most 3 zulip items, got {zulipCount}");
        // Both sources should be represented
        Assert.True(githubCount > 0, "Expected at least 1 github item");
        Assert.True(zulipCount > 0, "Expected at least 1 zulip item");
        // Total should be more than 3 (i.e., we're not capping globally at 3)
        Assert.True(result.Items.Count > 3, $"Expected more than 3 total items (per-source limit), got {result.Items.Count}");
    }

    // ── Test 4: Signal A skips null responses without crashing ──

    [Fact]
    public async Task SignalA_SkipsNullResponse()
    {
        // Seed item
        ItemResponse seedItem = new()
        {
            Source = "jira",
            Id = "FHIR-100",
            Title = "Seed Issue",
            Content = "",
        };
        string seedJson = JsonSerializer.Serialize(seedItem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // Empty search
        string emptySearchJson = JsonSerializer.Serialize(
            new SearchResponse("", 0, [], null),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        // Jira client returns seed item for GetItem + empty for GetRelated/Search
        MockHttpHandler jiraHandler = new(seedJson);
        HttpClient jiraClient = new(jiraHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-jira").Returns(jiraClient);

        // GitHub source returns 500 (will be caught and produce null)
        MockHttpHandler githubHandler = new("{}", HttpStatusCode.InternalServerError);
        HttpClient githubClient = new(githubHandler) { BaseAddress = new Uri("http://localhost") };
        factory.CreateClient("source-github").Returns(githubClient);

        OrchestratorOptions opts = DefaultOptions("jira", "github");
        SourceHttpClient httpClient = new(factory, Options.Create(opts), NullLogger<SourceHttpClient>.Instance);
        RelatedItemFinder finder = new(httpClient, Options.Create(opts), NullLogger<RelatedItemFinder>.Instance);

        // Should not throw
        FindRelatedResponse result = await finder.FindRelatedAsync("jira", "FHIR-100", 20, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("jira", result.SeedSource);
        // No github items should appear since the source returned an error
        Assert.DoesNotContain(result.Items, i => i.Source == "github");
    }
}

/// <summary>
/// A mock handler that returns a preconfigured response and records requests.
/// </summary>
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly string _responseJson;
    private readonly HttpStatusCode _statusCode;

    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> AllRequests { get; } = [];

    public MockHttpHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        AllRequests.Add(request);

        HttpResponseMessage response = new(_statusCode)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// A mock handler that returns different responses for sequential requests.
/// Falls back to the last response for any extra requests.
/// </summary>
internal class MultiResponseHandler : HttpMessageHandler
{
    private readonly List<string> _responses;
    private int _callIndex;

    public List<HttpRequestMessage> AllRequests { get; } = [];

    public MultiResponseHandler(List<string> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AllRequests.Add(request);
        int idx = Math.Min(_callIndex, _responses.Count - 1);
        _callIndex++;

        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses[idx], Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
