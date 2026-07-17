using System.Net;
using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Api;

public class JiraProcessingTicketsEndpointTests
{
    [Fact]
    public async Task PostTicket_UnknownKeyReturns404()
    {
        using HttpClient client = CreateClient(new FakeDiscovery(null), out _);
        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-404", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostTicket_ExistingKeyUpsertsAndResetsRow()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient client = CreateClient(discovery, out JiraProcessingSourceTicketStore store);
        await client.PostAsync("/processing/tickets/FHIR-1", null);
        await store.MarkErrorAsync((await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None))!, "old", 1, DateTimeOffset.UtcNow, CancellationToken.None);

        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        FhirAugury.Processing.Jira.Common.Database.Records.JiraProcessingSourceTicketRecord? row = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.NotNull(row);
        Assert.Null(row.ProcessingStatus);
        Assert.Null(row.ErrorMessage);
    }

    [Fact]
    public async Task PostTicket_BypassesFiltersForNonMatchingStatus()
    {
        using HttpClient client = CreateClient(new FakeDiscovery(CreateTicket("FHIR-1", "Submitted")), out _);
        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostTicket_UsesFhirShapeQueryWhenProvided()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient client = CreateClient(discovery, out _);
        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1?shape=fhir", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("fhir", discovery.RequestedShapes[0]);
    }

    [Fact]
    public async Task PostTicket_NonFhirShapeReturnsClientErrorForV1()
    {
        using HttpClient client = CreateClient(new FakeDiscovery(CreateTicket("PSS-1", "Triaged")), out _);
        HttpResponseMessage response = await client.PostAsync("/processing/tickets/PSS-1?shape=pss", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTicket_InvalidKeyReturns400()
    {
        using HttpClient client = CreateClient(new FakeDiscovery(CreateTicket("FHIR-1", "Triaged")), out _);
        HttpResponseMessage response = await client.PostAsync("/processing/tickets/not-valid", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpClient CreateClient(FakeDiscovery discovery, out JiraProcessingSourceTicketStore store)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        string dbPath = Path.Combine(AppContext.BaseDirectory, $"jira-endpoint-{Guid.NewGuid():N}.db");
        store = new JiraProcessingSourceTicketStore(dbPath, new ResolvedJiraProcessingFilters { TicketStatuses = ["Triaged"], SourceTicketShape = "fhir" });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IJiraTicketDiscoveryClient>(discovery);
        builder.Services.AddSingleton(Options.Create(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source", SourceTicketShape = "fhir" }));
        WebApplication app = builder.Build();
        app.MapJiraProcessingTicketEndpoints();
        app.StartAsync().GetAwaiter().GetResult();
        return app.GetTestClient();
    }

    private static JiraIssueSummaryEntry CreateTicket(string key, string status) => new()
    {
        Key = key,
        ProjectKey = key.Split('-', 2)[0],
        Title = "Title",
        Type = "Change Request",
        Status = status,
        WorkGroup = "FHIR-I",
    };

    private sealed class FakeDiscovery(JiraIssueSummaryEntry? ticket) : IJiraTicketDiscoveryClient
    {
        public List<string> RequestedShapes { get; } = [];
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);
        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct)
        {
            RequestedShapes.Add(sourceTicketShape);
            return Task.FromResult(ticket?.Key == key ? ticket : null);
        }
        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.CompletedTask;
    }
}
