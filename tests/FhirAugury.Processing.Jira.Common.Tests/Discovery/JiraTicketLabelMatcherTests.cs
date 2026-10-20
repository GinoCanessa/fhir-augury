using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Discovery;

public class JiraTicketLabelMatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchKeys_EmptyInputDoesNotSend(bool orchestrator)
    {
        SelectionHandler handler = new(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);

        Assert.Empty(await matcher.MatchKeysAsync([], ActiveFilters(), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 500)]
    [InlineData(true, 1)]
    [InlineData(true, 500)]
    public async Task MatchKeys_SendsCompleteScopedRequest(bool orchestrator, int keyCount)
    {
        string[] keys = CreateKeys(keyCount);
        JiraLocalProcessingListResponse page = CreatePage(keys.Select(CreateTicket).ToArray());
        page = page with { Provenance = keyCount == 1 ? null : page.Provenance! with { IsStable = false } };
        SelectionHandler handler = new(_ => JsonResponse(page));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);
        ResolvedJiraProcessingFilters filters = ActiveFilters();

        IReadOnlyList<string> matches = await matcher.MatchKeysAsync(keys, filters, CancellationToken.None);

        Assert.Equal(keys, matches);
        AssertSelectionRoute(handler, orchestrator);
        Assert.Single(handler.Requests);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.RequestJson));
        JsonElement root = document.RootElement;
        Assert.Equal(keys, ReadStrings(root.GetProperty("keys")));
        Assert.Equal(filters.LabelsToInclude, ReadStrings(root.GetProperty("labelText").GetProperty("includes")));
        Assert.Equal(filters.LabelsToExclude, ReadStrings(root.GetProperty("labelText").GetProperty("excludes")));
        Assert.Equal(500, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        foreach (string field in new[]
        {
            "statuses", "projects", "specifications", "workGroups", "types", "labels",
            "priorities", "changeCategories", "changeImpacts", "relatedArtifacts", "reporters", "processedLocally",
        })
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
        }
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchKeys_DoesNotReturnUnsubmittedKeys(bool orchestrator)
    {
        SelectionHandler handler = new(_ => JsonResponse(CreatePage(
            [CreateTicket("fhir-2"), CreateTicket("OTHER-1")]) with { Provenance = null }));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);

        IReadOnlyList<string> matches = await matcher.MatchKeysAsync(
            ["FHIR-1", "FHIR-2", "FHIR-3"],
            ActiveFilters(),
            CancellationToken.None);

        Assert.Equal(["fhir-2"], matches);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchKeys_ValidEmptyPageReturnsNoMatches(bool orchestrator)
    {
        SelectionHandler handler = new(_ => RawResponse("""{"results":[],"limit":500,"offset":0,"total":0}"""));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);

        Assert.Empty(await matcher.MatchKeysAsync(["FHIR-1"], ActiveFilters(), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false, "oversized")]
    [InlineData(false, "duplicate")]
    [InlineData(false, "case-duplicate")]
    [InlineData(false, "null")]
    [InlineData(false, "empty")]
    [InlineData(false, "blank")]
    [InlineData(true, "oversized")]
    [InlineData(true, "duplicate")]
    [InlineData(true, "case-duplicate")]
    [InlineData(true, "null")]
    [InlineData(true, "empty")]
    [InlineData(true, "blank")]
    public async Task MatchKeys_RejectsInvalidBatchWithoutSending(bool orchestrator, string kind)
    {
        string[] keys = kind switch
        {
            "oversized" => CreateKeys(501),
            "duplicate" => ["FHIR-1", "FHIR-1"],
            "case-duplicate" => ["FHIR-1", "fhir-1"],
            "null" => [null!],
            "empty" => [""],
            "blank" => [" \t"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        SelectionHandler handler = new(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => matcher.MatchKeysAsync(keys, ActiveFilters(), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, "results")]
    [InlineData(false, "limit")]
    [InlineData(false, "offset")]
    [InlineData(false, "total")]
    [InlineData(true, "results")]
    [InlineData(true, "limit")]
    [InlineData(true, "offset")]
    [InlineData(true, "total")]
    public async Task MatchKeys_RejectsMissingRequiredJsonMembers(bool orchestrator, string missingMember)
    {
        JsonObject envelope = JsonNode.Parse("""{"results":[],"limit":500,"offset":0,"total":0}""")!.AsObject();
        Assert.True(envelope.Remove(missingMember));
        SelectionHandler handler = new(_ => RawResponse(envelope.ToJsonString()));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => matcher.MatchKeysAsync(["FHIR-1"], ActiveFilters(), CancellationToken.None));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains("required response members", exception.Message);
        Assert.Single(handler.Requests);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    public static IEnumerable<object[]> MalformedMatchPages()
    {
        string[] cases =
        [
            "null", "invalid-json", "null-results", "non-array-results", "null-entry",
            "missing-key", "missing-title", "null-key", "empty-key", "blank-key", "invalid-key-type",
            "null-title", "null-project", "invalid-date", "duplicate-key", "case-duplicate-key",
            "negative-total", "wrong-offset", "negative-offset", "wrong-limit", "zero-limit",
            "negative-limit", "null-limit", "null-offset", "null-total", "invalid-total",
            "early-empty-page", "short-page", "truncated-total", "oversized-page", "too-many-results", "too-many-keys",
        ];
        foreach (bool orchestrator in new[] { false, true })
        {
            foreach (string kind in cases)
            {
                yield return [orchestrator, kind];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedMatchPages))]
    public async Task MatchKeys_RejectsIncompleteOrMalformedPayload(bool orchestrator, string kind)
    {
        object payload = kind switch
        {
            "null" => "null",
            "invalid-json" => "{not-json",
            "null-results" => """{"results":null,"limit":500,"offset":0,"total":0}""",
            "non-array-results" => """{"results":{},"limit":500,"offset":0,"total":0}""",
            "null-entry" => CreatePage([null!]),
            "missing-key" => """{"results":[{"title":"Title"}],"limit":500,"offset":0,"total":1}""",
            "missing-title" => """{"results":[{"key":"FHIR-1"}],"limit":500,"offset":0,"total":1}""",
            "null-key" => CreatePage([CreateTicket(null!)]),
            "empty-key" => CreatePage([CreateTicket("")]),
            "blank-key" => CreatePage([CreateTicket(" \t")]),
            "invalid-key-type" => """{"results":[{"key":1,"title":"Title"}],"limit":500,"offset":0,"total":1}""",
            "null-title" => CreatePage([CreateTicket("FHIR-1") with { Title = null! }]),
            "null-project" => CreatePage([CreateTicket("FHIR-1") with { ProjectKey = null! }]),
            "invalid-date" => """{"results":[{"key":"FHIR-1","title":"Title","updatedAt":"not-a-date"}],"limit":500,"offset":0,"total":1}""",
            "duplicate-key" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("FHIR-1")]),
            "case-duplicate-key" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("fhir-1")]),
            "negative-total" => CreatePage([]) with { Total = -1 },
            "wrong-offset" => CreatePage([]) with { Offset = 1 },
            "negative-offset" => CreatePage([]) with { Offset = -1 },
            "wrong-limit" => CreatePage([]) with { Limit = 499 },
            "zero-limit" => CreatePage([]) with { Limit = 0 },
            "negative-limit" => CreatePage([]) with { Limit = -1 },
            "null-limit" => """{"results":[],"limit":null,"offset":0,"total":0}""",
            "null-offset" => """{"results":[],"limit":500,"offset":null,"total":0}""",
            "null-total" => """{"results":[],"limit":500,"offset":0,"total":null}""",
            "invalid-total" => """{"results":[],"limit":500,"offset":0,"total":"invalid"}""",
            "early-empty-page" => CreatePage([]) with { Total = 1 },
            "short-page" => CreatePage([CreateTicket("FHIR-1")]) with { Total = 2 },
            "truncated-total" => CreatePage(CreateKeys(500).Select(CreateTicket).ToArray()) with { Total = 501 },
            "oversized-page" => CreatePage(CreateKeys(501).Select(CreateTicket).ToArray()),
            "too-many-results" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("FHIR-2")]) with { Total = 1 },
            "too-many-keys" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("FHIR-2"), CreateTicket("FHIR-3")]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        SelectionHandler handler = new(_ => payload is string raw ? RawResponse(raw) : JsonResponse(payload));
        IJiraTicketLabelMatcher matcher = CreateClient(orchestrator, handler);
        string[] keys = CreateKeys(kind is "truncated-total" or "oversized-page" ? 500 : 2);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => matcher.MatchKeysAsync(keys, ActiveFilters(), CancellationToken.None));

        Assert.True(exception.InnerException is JsonException or InvalidDataException);
        Assert.Single(handler.Requests);
        AssertSelectionRoute(handler, orchestrator);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectionRequest_EscapesShapeSeparatelyFromIssueType(bool orchestrator, bool discovery)
    {
        ResolvedJiraProcessingFilters filters = ActiveFilters() with { SourceTicketShape = "fhir &?type=pss" };
        SelectionHandler handler = new(_ => JsonResponse(CreatePage([])));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        if (discovery)
        {
            await client.ListTicketsAsync(filters, CancellationToken.None);
        }
        else
        {
            await client.MatchKeysAsync(["FHIR-1"], filters, CancellationToken.None);
        }

        string prefix = orchestrator ? "api/v1/jira" : "api/v1";
        Assert.Equal(
            $"{prefix}/local-processing/selection-tickets?type={Uri.EscapeDataString(filters.SourceTicketShape)}",
            Assert.Single(handler.Requests).RequestUri!.PathAndQuery.TrimStart('/'));
        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.RequestJson));
        Assert.Equal(
            discovery ? filters.TicketTypes : null,
            ReadStrings(document.RootElement.GetProperty("types")));
    }

    public static IEnumerable<object[]> TransientFailureCases()
    {
        foreach (bool orchestrator in new[] { false, true })
        {
            foreach (bool discovery in new[] { false, true })
            {
                foreach (int status in new[] { 429, 500, 502, 503, 504 })
                {
                    yield return [orchestrator, discovery, status];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TransientFailureCases))]
    public async Task SelectionRequest_RetriesTransientFailure(bool orchestrator, bool discovery, int status)
    {
        SelectionHandler handler = new(attempt => attempt <= HttpRetryHelper.DefaultMaxRetries
            ? StatusResponse((HttpStatusCode)status)
            : JsonResponse(CreatePage([CreateTicket("FHIR-1")])));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        await SelectAsync(client, discovery, CancellationToken.None);

        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Requests.Count);
        AssertSelectionRoute(handler, orchestrator);
        AssertFreshRequests(handler);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
        using JsonDocument document = JsonDocument.Parse(handler.RequestJson[0]);
        Assert.Equal(ActiveFilters().LabelsToInclude, ReadStrings(document.RootElement.GetProperty("labelText").GetProperty("includes")));
        Assert.Equal(discovery ? null : ["FHIR-1"], ReadStrings(document.RootElement.GetProperty("keys")));
    }

    [Theory]
    [MemberData(nameof(TransientFailureCases))]
    public async Task SelectionRequest_ExhaustedTransientFailureIsUnavailable(bool orchestrator, bool discovery, int status)
    {
        SelectionHandler handler = new(_ => StatusResponse((HttpStatusCode)status));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => SelectAsync(client, discovery, CancellationToken.None));

        HttpRequestException cause = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal((HttpStatusCode)status, cause.StatusCode);
        Assert.Contains($"HTTP {status}", exception.Message);
        Assert.Contains("after retries", exception.Message);
        Assert.DoesNotContain("private upstream diagnostic", exception.Message);
        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Requests.Count);
        AssertSelectionRoute(handler, orchestrator);
        AssertFreshRequests(handler);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectionRequest_UnsupportedRouteNeverFallsBack(bool orchestrator, bool discovery)
    {
        SelectionHandler handler = new(_ => StatusResponse(HttpStatusCode.NotFound));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => SelectAsync(client, discovery, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
        Assert.Contains("HTTP 404", exception.Message);
        Assert.Contains("not supported", exception.Message);
        Assert.DoesNotContain("private upstream diagnostic", exception.Message);
        Assert.Single(handler.Requests);
        AssertSelectionRoute(handler, orchestrator);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectionRequest_BadRequestHasSafeValidationDetail(bool orchestrator, bool discovery)
    {
        SelectionHandler handler = new(_ => StatusResponse(HttpStatusCode.BadRequest));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => SelectAsync(client, discovery, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, Assert.IsType<HttpRequestException>(exception.InnerException).StatusCode);
        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("validation", exception.Message);
        Assert.DoesNotContain("private upstream diagnostic", exception.Message);
        Assert.Single(handler.Requests);
        AssertSelectionRoute(handler, orchestrator);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    public async Task SelectionRequest_IdentifiesSourceOnAuthenticationFailure(bool orchestrator, int status)
    {
        SelectionHandler handler = new(_ => StatusResponse((HttpStatusCode)status));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.MatchKeysAsync(["FHIR-1"], ActiveFilters(), CancellationToken.None));

        HttpRequestException cause = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Contains("Jira ticket selection", cause.Message);
        Assert.Equal((HttpStatusCode)status, cause.StatusCode);
        Assert.Single(handler.Requests);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionRequest_ExhaustedTransportFailureIsUnavailable(bool orchestrator)
    {
        HttpRequestException cause = new("private transport diagnostic");
        SelectionHandler handler = new(_ => throw cause);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.MatchKeysAsync(["FHIR-1"], ActiveFilters(), CancellationToken.None));

        Assert.Same(cause, exception.InnerException);
        Assert.Contains("could not reach", exception.Message);
        Assert.DoesNotContain("private transport diagnostic", exception.Message);
        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Requests.Count);
        AssertSelectionRoute(handler, orchestrator);
        AssertFreshRequests(handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionRequest_NonCallerTimeoutIsUnavailable(bool orchestrator)
    {
        TaskCanceledException cause = new("private timeout diagnostic", new TimeoutException());
        SelectionHandler handler = new(_ => throw cause);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.MatchKeysAsync(["FHIR-1"], ActiveFilters(), CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Contains("timed out", exception.Message);
        Assert.DoesNotContain("private timeout diagnostic", exception.Message);
        Assert.Equal(HttpRetryHelper.DefaultMaxRetries + 1, handler.Requests.Count);
        AssertSelectionRoute(handler, orchestrator);
        AssertFreshRequests(handler);
    }

    public static IEnumerable<object[]> CancellationCases()
    {
        foreach (bool orchestrator in new[] { false, true })
        {
            foreach (bool discovery in new[] { false, true })
            {
                foreach (string phase in new[] { "before-send", "during-send", "retry-delay" })
                {
                    yield return [orchestrator, discovery, phase];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task SelectionRequest_PreservesCallerCancellation(bool orchestrator, bool discovery, string phase)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SelectionHandler handler = new(async (_, ct) =>
        {
            started.TrySetResult();
            if (phase == "retry-delay")
            {
                return StatusResponse(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30));
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse(CreatePage([]));
        });
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);
        if (phase == "before-send")
        {
            cancellation.Cancel();
        }

        Task selection = SelectAsync(client, discovery, cancellation.Token);
        if (phase != "before-send")
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);

        Assert.Equal(phase == "before-send" ? 0 : 1, handler.Requests.Count);
        Assert.All(handler.ResponseContents, content => Assert.True(content.IsDisposed));
    }

    private static ResolvedJiraProcessingFilters ActiveFilters() => new()
    {
        TicketStatuses = ["Triaged"],
        Projects = ["FHIR"],
        Specifications = ["fhir-core"],
        WorkGroups = ["FHIR-I"],
        TicketTypes = ["Change Request"],
        LabelsToInclude = [" inc_% ", "O'Reilly", @"back\slash", "MiXeD", "MiXeD"],
        LabelsToExclude = ["ex-01", "ex_%"],
    };

    private static Task SelectAsync(JiraTicketDiscoveryClientBase client, bool discovery, CancellationToken ct)
        => discovery
            ? client.ListTicketsAsync(ActiveFilters(), ct)
            : client.MatchKeysAsync(["FHIR-1"], ActiveFilters(), ct);

    private static JiraTicketDiscoveryClientBase CreateClient(bool orchestrator, HttpMessageHandler handler)
    {
        HttpClient httpClient = new(handler) { BaseAddress = new Uri("http://localhost/") };
        IOptions<JiraProcessingOptions> options = Options.Create(new JiraProcessingOptions
        {
            JiraSourceAddress = "http://source",
            OrchestratorAddress = "http://orchestrator",
            DiscoverySource = orchestrator ? JiraTicketDiscoverySource.Orchestrator : JiraTicketDiscoverySource.DirectJiraSource,
        });
        return orchestrator
            ? new OrchestratorJiraTicketDiscoveryClient(httpClient, options, new JiraLocalProcessingRequestFactory())
            : new DirectJiraTicketDiscoveryClient(httpClient, options, new JiraLocalProcessingRequestFactory());
    }

    private static void AssertSelectionRoute(SelectionHandler handler, bool orchestrator)
    {
        string prefix = orchestrator ? "api/v1/jira" : "api/v1";
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"{prefix}/local-processing/selection-tickets?type=fhir", request.RequestUri!.PathAndQuery.TrimStart('/'));
        });
    }

    private static void AssertFreshRequests(SelectionHandler handler)
    {
        for (int index = 1; index < handler.Requests.Count; index++)
        {
            Assert.NotSame(handler.Requests[index - 1], handler.Requests[index]);
            Assert.NotSame(handler.RequestContents[index - 1], handler.RequestContents[index]);
            Assert.Equal(handler.RequestJson[0], handler.RequestJson[index]);
        }
    }

    private static string[]? ReadStrings(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null
        : value.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string[] CreateKeys(int count)
        => Enumerable.Range(1, count).Select(index => $"FHIR-{index}").ToArray();

    private static JiraIssueSummaryEntry CreateTicket(string key) => new()
    {
        Key = key,
        Title = "Title",
        ProjectKey = "FHIR",
    };

    private static JiraLocalProcessingListResponse CreatePage(IReadOnlyList<JiraIssueSummaryEntry> tickets)
        => new(tickets, 500, 0, tickets.Count)
        {
            Provenance = new SourceReadProvenance
            {
                Source = "jira",
                ContentRevision = 7,
                IsStable = true,
                ProjectLastSuccessfulRefreshAt = new Dictionary<string, DateTimeOffset?> { ["FHIR"] = null },
            },
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => RawResponse(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));

    private static HttpResponseMessage RawResponse(string json)
        => new(HttpStatusCode.OK) { Content = new TrackingContent(json) };

    private static HttpResponseMessage StatusResponse(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        HttpResponseMessage response = new(status)
        {
            ReasonPhrase = "private upstream diagnostic",
            Content = new TrackingContent("private upstream diagnostic"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter ?? TimeSpan.Zero);
        return response;
    }

    private sealed class TrackingContent(string value) : StringContent(value, Encoding.UTF8, "application/json")
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class SelectionHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        public SelectionHandler(Func<int, HttpResponseMessage> respond)
            : this((attempt, _) => Task.FromResult(respond(attempt)))
        {
        }

        public SelectionHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<HttpContent> RequestContents { get; } = [];
        public List<string> RequestJson { get; } = [];
        public List<TrackingContent> ResponseContents { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            HttpContent content = request.Content ?? throw new InvalidOperationException("Missing POST body.");
            RequestContents.Add(content);
            RequestJson.Add(await content.ReadAsStringAsync(cancellationToken));
            HttpResponseMessage response = await _respond(Requests.Count, cancellationToken);
            ResponseContents.Add(Assert.IsType<TrackingContent>(response.Content));
            return response;
        }
    }
}
