using System.Net;
using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Discovery;

public class JiraTicketDiscoveryClientTests
{
    [Fact]
    public async Task DirectClient_ListTickets_PostsLocalProcessingRequestWithShape()
    {
        CapturingHandler handler = new(new JiraLocalProcessingListResponse([CreateTicket("FHIR-1")], 500, 0, 1));
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Single(tickets);
        Assert.Equal("api/v1/local-processing/tickets?type=fhir", handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task OrchestratorClient_ListTickets_UsesJiraProxyRoute()
    {
        CapturingHandler handler = new(new JiraLocalProcessingListResponse([], 500, 0, 0));
        OrchestratorJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal("api/v1/jira/local-processing/tickets?type=fhir", handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task GetTicket_Fhir_MapsItemResponseToSummaryEntry()
    {
        ItemResponse item = new()
        {
            Source = "jira",
            Id = "FHIR-1",
            Title = "Title",
            Url = "https://jira/browse/FHIR-1",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["status"] = "Triaged", ["type"] = "Change Request", ["work_group"] = "FHIR-I" },
        };
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(new CapturingHandler(item)), Options(false), new JiraLocalProcessingRequestFactory());

        JiraIssueSummaryEntry? ticket = await client.GetTicketAsync("FHIR-1", "fhir", CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal("FHIR", ticket.ProjectKey);
        Assert.Equal("Triaged", ticket.Status);
        Assert.Equal("FHIR-I", ticket.WorkGroup);
    }

    [Fact]
    public async Task MarkProcessed_PostsSetProcessedOnlyWhenEnabled()
    {
        CapturingHandler disabledHandler = new(new JiraLocalProcessingSetResponse("FHIR-1", false, true));
        DirectJiraTicketDiscoveryClient disabledClient = new(CreateHttpClient(disabledHandler), Options(false), new JiraLocalProcessingRequestFactory());
        await disabledClient.MarkProcessedAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.Empty(disabledHandler.Requests);

        CapturingHandler enabledHandler = new(new JiraLocalProcessingSetResponse("FHIR-1", false, true));
        DirectJiraTicketDiscoveryClient enabledClient = new(CreateHttpClient(enabledHandler), Options(true), new JiraLocalProcessingRequestFactory());
        await enabledClient.MarkProcessedAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.Single(enabledHandler.Requests);
        Assert.Equal("api/v1/local-processing/set-processed?type=fhir", enabledHandler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task GetTicket_NonFhirShapeReturnsUnsupportedForV1()
    {
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(new CapturingHandler(new object())), Options(false), new JiraLocalProcessingRequestFactory());

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetTicketAsync("PSS-1", "pss", CancellationToken.None));
    }

    [Fact]
    public async Task SyncService_UpsertsAllReturnedTickets()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 2);
        CapturingHandler handler = new(
        [
            new JiraLocalProcessingListResponse(page1, 500, 0, 502),
            new JiraLocalProcessingListResponse(page2, 500, 500, 502),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-sync-{Guid.NewGuid():N}.db");
        JiraProcessingSourceTicketStore store = new(path);
        JiraTicketSyncService service = new(client, store, new JiraProcessingFilterResolver(), Options(false), NullLogger<JiraTicketSyncService>.Instance);

        int count = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(502, count);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-500", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-501", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-502", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task DirectClient_ListTickets_PaginatesUntilShortPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 500);
        JiraIssueSummaryEntry[] page3 = CreateTickets(1001, 213);
        CapturingHandler handler = new(
        [
            new JiraLocalProcessingListResponse(page1, 500, 0, 1213),
            new JiraLocalProcessingListResponse(page2, 500, 500, 1213),
            new JiraLocalProcessingListResponse(page3, 500, 1000, 1213),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(1213, tickets.Count);
        for (int i = 0; i < tickets.Count; i++)
        {
            Assert.Equal($"FHIR-{i + 1}", tickets[i].Key);
        }
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(500, handler.RequestBodies[0]!.Limit);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Limit);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
        Assert.Equal(500, handler.RequestBodies[2]!.Limit);
        Assert.Equal(1000, handler.RequestBodies[2]!.Offset);
    }

    [Fact]
    public async Task DirectClient_ListTickets_ExactlyOneFullPage_IssuesTrailingEmptyPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        CapturingHandler handler = new(
        [
            new JiraLocalProcessingListResponse(page1, 500, 0, 500),
            new JiraLocalProcessingListResponse([], 500, 500, 500),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(500, tickets.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
    }

    [Fact]
    public async Task DirectClient_ListTickets_SinglePartialPage_IssuesOneRequest()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 7);
        CapturingHandler handler = new(
        [
            new JiraLocalProcessingListResponse(page1, 500, 0, 7),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(7, tickets.Count);
        Assert.Single(handler.Requests);
        Assert.Equal(500, handler.RequestBodies[0]!.Limit);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
    }

    [Fact]
    public async Task OrchestratorClient_ListTickets_PaginatesUntilShortPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 500);
        JiraIssueSummaryEntry[] page3 = CreateTickets(1001, 213);
        CapturingHandler handler = new(
        [
            new JiraLocalProcessingListResponse(page1, 500, 0, 1213),
            new JiraLocalProcessingListResponse(page2, 500, 500, 1213),
            new JiraLocalProcessingListResponse(page3, 500, 1000, 1213),
        ]);
        OrchestratorJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(1213, tickets.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
        Assert.Equal(1000, handler.RequestBodies[2]!.Offset);
        Assert.All(handler.Requests, r => Assert.Equal("api/v1/jira/local-processing/tickets?type=fhir", r.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    private static JiraIssueSummaryEntry CreateTicket(string key) => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = "Title",
        Type = "Change Request",
        Status = "Triaged",
        WorkGroup = "FHIR-I",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static JiraIssueSummaryEntry[] CreateTickets(int startKey, int count)
    {
        JiraIssueSummaryEntry[] tickets = new JiraIssueSummaryEntry[count];
        for (int i = 0; i < count; i++)
        {
            tickets[i] = CreateTicket($"FHIR-{startKey + i}");
        }
        return tickets;
    }

    private static IOptions<JiraProcessingOptions> Options(bool markProcessed) => Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
    {
        AgentCliCommand = "agent {ticketKey}",
        JiraSourceAddress = "http://source",
        MarkUpstreamProcessedOnSuccess = markProcessed,
    });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<object> _scripted;
        private readonly object? _staticPayload;
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(object responsePayload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _staticPayload = responsePayload;
            _scripted = new Queue<object>();
            _statusCode = statusCode;
        }

        public CapturingHandler(IEnumerable<object> scriptedPayloads, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _staticPayload = null;
            _scripted = new Queue<object>(scriptedPayloads);
            _statusCode = statusCode;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<JiraLocalProcessingListRequest?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            JiraLocalProcessingListRequest? body = null;
            if (request.Content is not null)
            {
                string raw = await request.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(raw))
                {
                    try
                    {
                        body = System.Text.Json.JsonSerializer.Deserialize<JiraLocalProcessingListRequest>(
                            raw,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        body = null;
                    }
                }
            }
            RequestBodies.Add(body);

            object payload = _scripted.Count > 0 ? _scripted.Dequeue() : _staticPayload!;
            HttpResponseMessage response = new(_statusCode) { Content = JsonContent.Create(payload) };
            return response;
        }
    }
}
