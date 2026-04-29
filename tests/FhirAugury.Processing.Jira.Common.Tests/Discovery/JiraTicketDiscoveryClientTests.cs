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
        CapturingHandler handler = new(new JiraLocalProcessingListResponse([CreateTicket("FHIR-1"), CreateTicket("FHIR-2")], 500, 0, 2));
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-sync-{Guid.NewGuid():N}.db");
        JiraProcessingSourceTicketStore store = new(path);
        JiraTicketSyncService service = new(client, store, new JiraProcessingFilterResolver(), Options(false), NullLogger<JiraTicketSyncService>.Instance);

        int count = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-2", "fhir", CancellationToken.None));
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

    private static IOptions<JiraProcessingOptions> Options(bool markProcessed) => Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
    {
        AgentCliCommand = "agent {ticketKey}",
        JiraSourceAddress = "http://source",
        MarkUpstreamProcessedOnSuccess = markProcessed,
    });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private sealed class CapturingHandler(object responsePayload, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            HttpResponseMessage response = new(statusCode) { Content = JsonContent.Create(responsePayload) };
            return Task.FromResult(response);
        }
    }
}
