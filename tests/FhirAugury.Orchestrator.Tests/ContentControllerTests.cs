using System.Net;
using System.Net.Http.Json;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

public class ContentControllerTests
{
    // ── Mock handler ────────────────────────────────────────────────────

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private Func<Task<HttpResponseMessage>>? _responseFactory;
        public List<HttpRequestMessage> SentRequests { get; } = [];

        public void RespondWith<T>(T body) =>
            _responseFactory = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body),
            });

        public void Throw(Exception ex) =>
            _responseFactory = () => Task.FromException<HttpResponseMessage>(ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            if (_responseFactory is not null)
                return _responseFactory();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // ── Setup helpers ───────────────────────────────────────────────────

    private static Dictionary<string, SourceServiceConfig> TwoEnabledSources() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SourceSystems.Jira] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5001" },
            [SourceSystems.Zulip] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://zulip:5002" },
        };

    private static ContentController CreateController(
        Dictionary<string, MockHttpMessageHandler> handlers,
        Dictionary<string, SourceServiceConfig> services,
        SearchOptions? searchOptions = null)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();

        foreach (KeyValuePair<string, MockHttpMessageHandler> kvp in handlers)
        {
            HttpClient httpClient = new(kvp.Value)
            {
                BaseAddress = new Uri(services[kvp.Key].HttpAddress),
            };
            factory.CreateClient($"source-{kvp.Key}").Returns(httpClient);
        }

        OrchestratorOptions options = new()
        {
            Services = services,
            Search = searchOptions ?? new SearchOptions(),
        };

        IOptions<OrchestratorOptions> opts = Options.Create(options);
        SourceHttpClient sourceHttpClient = new(factory, opts, NullLogger<SourceHttpClient>.Instance);
        FreshnessDecay freshnessDecay = new(opts);

        return new ContentController(sourceHttpClient, freshnessDecay, opts, NullLoggerFactory.Instance);
    }

    private static CrossReferenceHit MakeXRefHit(
        string sourceType, string sourceId, string targetType, string targetId,
        string linkType = "mentions", double score = 1.0, DateTimeOffset? updatedAt = null) => new()
    {
        SourceType = sourceType,
        SourceId = sourceId,
        TargetType = targetType,
        TargetId = targetId,
        LinkType = linkType,
        Score = score,
        UpdatedAt = updatedAt,
    };

    // ── Cross-reference fan-out tests ───────────────────────────────────

    [Fact]
    public async Task RefersTo_FansOutToAllEnabledSources()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123", Direction = "refers-to", Total = 0, Hits = [],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123", Direction = "refers-to", Total = 0, Hits = [],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        IActionResult result = await controller.RefersTo("FHIR-123", null, null, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Single(zulipHandler.SentRequests);
    }

    [Fact]
    public async Task RefersTo_MergesResultsFromMultipleSources()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Jira, "J-1", SourceSystems.Fhir, "FHIR-123")],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Zulip, "msg-42", SourceSystems.Fhir, "FHIR-123")],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.RefersTo("FHIR-123", null, null, null, CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Hits.Count);
        Assert.Contains(response.Hits, h => h.SourceType == SourceSystems.Jira);
        Assert.Contains(response.Hits, h => h.SourceType == SourceSystems.Zulip);
    }

    [Fact]
    public async Task RefersTo_DeduplicatesHits_KeepsHighestScore()
    {
        CrossReferenceHit lowScore = MakeXRefHit(SourceSystems.Jira, "J-1", SourceSystems.Fhir, "FHIR-123", score: 0.5);
        CrossReferenceHit highScore = MakeXRefHit(SourceSystems.Jira, "J-1", SourceSystems.Fhir, "FHIR-123", score: 2.0);

        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123", Direction = "refers-to", Total = 1, Hits = [lowScore],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123", Direction = "refers-to", Total = 1, Hits = [highScore],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.RefersTo("FHIR-123", null, null, null, CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        Assert.Equal(1, response.Total);
        Assert.Single(response.Hits);
    }

    [Fact]
    public async Task ReferredBy_PartialFailure_ReturnsResultsAndWarning()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "referred-by",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Fhir, "FHIR-123", SourceSystems.Jira, "J-1")],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.Throw(new HttpRequestException("Connection refused"));

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.ReferredBy("FHIR-123", null, null, null, CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        Assert.Equal(1, response.Total);
        Assert.Single(response.Hits);
        Assert.NotNull(response.Warnings);
        Assert.Contains(response.Warnings, w => w.Contains(SourceSystems.Zulip));
    }

    // ── Scoring pipeline tests ──────────────────────────────────────────

    [Fact]
    public async Task RefersTo_AppliesScoringPipeline()
    {
        DateTimeOffset recent = DateTimeOffset.UtcNow;
        DateTimeOffset old = DateTimeOffset.UtcNow.AddYears(-3);

        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Jira, "J-1", SourceSystems.Fhir, "FHIR-123", score: 1.0, updatedAt: recent)],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Zulip, "Z-1", SourceSystems.Fhir, "FHIR-123", score: 1.0, updatedAt: old)],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        SearchOptions searchOptions = new()
        {
            FreshnessWeights = new Dictionary<string, double>
            {
                [SourceSystems.Jira] = 1.0,
                [SourceSystems.Zulip] = 1.0,
            },
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources(), searchOptions);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.RefersTo("FHIR-123", null, null, null, CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        Assert.Equal(2, response.Hits.Count);

        CrossReferenceHit recentHit = response.Hits.Single(h => h.SourceId == "J-1");
        CrossReferenceHit oldHit = response.Hits.Single(h => h.SourceId == "Z-1");

        Assert.True(recentHit.Score > oldHit.Score,
            $"Recent item ({recentHit.Score:F4}) should score higher than old item ({oldHit.Score:F4})");
    }

    [Fact]
    public async Task RefersTo_SortByDate_OrdersByUpdatedAt()
    {
        DateTimeOffset older = DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset newer = DateTimeOffset.UtcNow.AddDays(-1);

        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Jira, "J-old", SourceSystems.Fhir, "FHIR-123", score: 5.0, updatedAt: older)],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Zulip, "Z-new", SourceSystems.Fhir, "FHIR-123", score: 1.0, updatedAt: newer)],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.RefersTo("FHIR-123", null, null, "date", CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        Assert.Equal(2, response.Hits.Count);
        Assert.Equal("Z-new", response.Hits[0].SourceId);
        Assert.Equal("J-old", response.Hits[1].SourceId);
    }

    [Fact]
    public async Task RefersTo_HitsIncludeScoreAndUpdatedAt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123",
            Direction = "refers-to",
            Total = 1,
            Hits = [MakeXRefHit(SourceSystems.Jira, "J-1", SourceSystems.Fhir, "FHIR-123", score: 1.0, updatedAt: now)],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new CrossReferenceQueryResponse
        {
            Value = "FHIR-123", Direction = "refers-to", Total = 0, Hits = [],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.RefersTo("FHIR-123", null, null, null, CancellationToken.None));
        CrossReferenceQueryResponse response = Assert.IsType<CrossReferenceQueryResponse>(ok.Value);

        CrossReferenceHit hit = Assert.Single(response.Hits);
        Assert.True(hit.Score > 0, "Score should be populated");
        Assert.NotNull(hit.UpdatedAt);
    }

    // ── Search fan-out tests ────────────────────────────────────────────

    [Fact]
    public async Task Search_FansOutToEnabledSources()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"], Total = 0, Hits = [],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"], Total = 0, Hits = [],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        IActionResult result = await controller.Search(["test"], null, null, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Single(zulipHandler.SentRequests);
    }

    [Fact]
    public async Task Search_AppliesNormalizationAndDecay()
    {
        // Two sources, one item each → each normalizes to 1.0.
        // Different ages + freshness weights produce different final scores.
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"],
            Total = 1,
            Hits =
            [
                new ContentSearchHit
                {
                    Source = SourceSystems.Jira, Id = "J-1", Title = "Recent",
                    Score = 50.0, UpdatedAt = DateTimeOffset.UtcNow,
                },
            ],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"],
            Total = 1,
            Hits =
            [
                new ContentSearchHit
                {
                    Source = SourceSystems.Zulip, Id = "Z-1", Title = "Old",
                    Score = 50.0, UpdatedAt = DateTimeOffset.UtcNow.AddYears(-3),
                },
            ],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        SearchOptions searchOptions = new()
        {
            FreshnessWeights = new Dictionary<string, double>
            {
                [SourceSystems.Jira] = 1.0,
                [SourceSystems.Zulip] = 1.0,
            },
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources(), searchOptions);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.Search(["test"], null, null, null, CancellationToken.None));
        ContentSearchResponse response = Assert.IsType<ContentSearchResponse>(ok.Value);

        Assert.Equal(2, response.Hits.Count);

        // Single-item-per-source normalizes to 1.0, then decay reduces the old item
        ContentSearchHit recentHit = response.Hits.Single(h => h.Id == "J-1");
        ContentSearchHit oldHit = response.Hits.Single(h => h.Id == "Z-1");

        Assert.True(recentHit.Score <= 1.0, "Scores should be normalized to ≤ 1.0");
        Assert.True(recentHit.Score > oldHit.Score,
            $"Recent item ({recentHit.Score:F4}) should score higher than old item ({oldHit.Score:F4}) after decay");
    }

    [Fact]
    public async Task Search_RespectsSourceFilter()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"], Total = 0, Hits = [],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"], Total = 0, Hits = [],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        IActionResult result = await controller.Search(
            ["test"], [SourceSystems.Jira], null, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Empty(zulipHandler.SentRequests);
    }

    [Fact]
    public async Task Search_SortByDate_OrdersByUpdatedAt()
    {
        DateTimeOffset older = DateTimeOffset.UtcNow.AddDays(-30);
        DateTimeOffset newer = DateTimeOffset.UtcNow.AddDays(-1);

        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"],
            Total = 1,
            Hits =
            [
                new ContentSearchHit
                {
                    Source = SourceSystems.Jira, Id = "J-old", Title = "Old Item",
                    Score = 50.0, UpdatedAt = older,
                },
            ],
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new ContentSearchResponse
        {
            Values = ["test"],
            Total = 1,
            Hits =
            [
                new ContentSearchHit
                {
                    Source = SourceSystems.Zulip, Id = "Z-new", Title = "New Item",
                    Score = 50.0, UpdatedAt = newer,
                },
            ],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.Search(["test"], null, null, "date", CancellationToken.None));
        ContentSearchResponse response = Assert.IsType<ContentSearchResponse>(ok.Value);

        Assert.Equal(2, response.Hits.Count);
        Assert.Equal("Z-new", response.Hits[0].Id);
        Assert.Equal("J-old", response.Hits[1].Id);
    }

    // ── Item routing tests ──────────────────────────────────────────────

    [Fact]
    public async Task GetItem_RoutesToCorrectSource()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentItemResponse
        {
            Source = SourceSystems.Jira, Id = "FHIR-123", Title = "Test Item",
        });

        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new ContentItemResponse
        {
            Source = SourceSystems.Zulip, Id = "Z-1", Title = "Other",
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, TwoEnabledSources());

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.GetItem(SourceSystems.Jira, "FHIR-123", ct: CancellationToken.None));
        ContentItemResponse item = Assert.IsType<ContentItemResponse>(ok.Value);

        Assert.Equal("FHIR-123", item.Id);
        Assert.Equal(SourceSystems.Jira, item.Source);
        Assert.Single(jiraHandler.SentRequests);
        Assert.Empty(zulipHandler.SentRequests);
    }

    [Fact]
    public async Task GetItem_DisabledSource_Returns404()
    {
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new ContentItemResponse
        {
            Source = SourceSystems.Jira, Id = "J-1", Title = "Item",
        });

        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            [SourceSystems.Jira] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5001" },
        };

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
        };

        ContentController controller = CreateController(handlers, services);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(
            await controller.GetItem("unknown-source", "ID-1", ct: CancellationToken.None));

        Assert.Empty(jiraHandler.SentRequests);
    }

    // ── Keyword tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetKeywords_RoutesToCorrectSource()
    {
        Dictionary<string, SourceServiceConfig> services = TwoEnabledSources();
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new KeywordListResponse
        {
            Source = "jira",
            SourceId = "FHIR-123",
            ContentType = "issue",
            Keywords =
            [
                new KeywordEntry { Keyword = "patient", KeywordType = "word", Count = 5, Bm25Score = 4.5 },
            ],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
        };

        ContentController controller = CreateController(handlers, services);
        IActionResult result = await controller.GetKeywords("jira", "FHIR-123", null, null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        KeywordListResponse response = Assert.IsType<KeywordListResponse>(ok.Value);
        Assert.Equal("jira", response.Source);
        Assert.Single(response.Keywords);
        Assert.Equal("patient", response.Keywords[0].Keyword);
    }

    [Fact]
    public async Task GetKeywords_DisabledSource_Returns404()
    {
        Dictionary<string, SourceServiceConfig> services = TwoEnabledSources();
        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = new MockHttpMessageHandler(),
        };

        ContentController controller = CreateController(handlers, services);
        IActionResult result = await controller.GetKeywords("github", "test", null, null);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RelatedByKeyword_FansOutToAllSources()
    {
        Dictionary<string, SourceServiceConfig> services = TwoEnabledSources();
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new RelatedByKeywordResponse
        {
            Source = "jira",
            SourceId = "FHIR-123",
            RelatedItems =
            [
                new RelatedByKeywordItem
                {
                    Source = "jira", SourceId = "FHIR-456", ContentType = "issue",
                    Title = "Related Jira", Score = 0.8, SharedKeywords = ["patient"],
                },
            ],
        });
        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.RespondWith(new RelatedByKeywordResponse
        {
            Source = "zulip",
            SourceId = "FHIR-123",
            RelatedItems =
            [
                new RelatedByKeywordItem
                {
                    Source = "zulip", SourceId = "456", ContentType = "message",
                    Title = "Related Zulip", Score = 0.5, SharedKeywords = ["patient"],
                },
            ],
        });

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, services);
        IActionResult result = await controller.RelatedByKeyword("jira", "FHIR-123", null, null, null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        RelatedByKeywordResponse response = Assert.IsType<RelatedByKeywordResponse>(ok.Value);
        Assert.Equal(2, response.RelatedItems.Count);
        Assert.Equal(0.8, response.RelatedItems[0].Score);
        Assert.Equal(0.5, response.RelatedItems[1].Score);
    }

    [Fact]
    public async Task RelatedByKeyword_PartialFailure_ReturnsWarning()
    {
        Dictionary<string, SourceServiceConfig> services = TwoEnabledSources();
        MockHttpMessageHandler jiraHandler = new();
        jiraHandler.RespondWith(new RelatedByKeywordResponse
        {
            Source = "jira",
            SourceId = "test",
            RelatedItems = [],
        });
        MockHttpMessageHandler zulipHandler = new();
        zulipHandler.Throw(new HttpRequestException("Connection refused"));

        Dictionary<string, MockHttpMessageHandler> handlers = new()
        {
            [SourceSystems.Jira] = jiraHandler,
            [SourceSystems.Zulip] = zulipHandler,
        };

        ContentController controller = CreateController(handlers, services);
        IActionResult result = await controller.RelatedByKeyword("jira", "test", null, null, null);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        RelatedByKeywordResponse response = Assert.IsType<RelatedByKeywordResponse>(ok.Value);
        Assert.NotNull(response.Warnings);
        Assert.Contains(response.Warnings, w => w.Contains("zulip"));
    }
}
