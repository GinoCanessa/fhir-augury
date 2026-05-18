using System.Net;
using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparerServiceSmokeTests
{
    [Fact]
    public async Task InheritedProcessingEndpointsAreMapped()
    {
        using TestApp app = new();
        HttpClient client = app.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/processing/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/processing/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/processing/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/processing/tickets/FHIR-123", null)).StatusCode);
    }

    [Fact]
    public async Task PreparedTicketsApi_CanQueryPersistedRows()
    {
        using TestApp app = new();
        using PreparerDatabase database = new(app.DatabasePath, Microsoft.Extensions.Logging.Abstractions.NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        PreparedTicketPayload first = SamplePayload("FHIR-123");
        first.Recommendation = "A";
        first.ProposalAImpact = "Non-substantive";
        PreparedTicketPayload second = SamplePayload("FHIR-124");
        second.Recommendation = "B";
        second.ProposalAImpact = "Compatible, substantive";
        await database.SavePreparedTicketAsync(first);
        await database.SavePreparedTicketAsync(second);
        HttpClient client = app.Factory.CreateClient();

        PreparedTicketList? response = await client.GetFromJsonAsync<PreparedTicketList>("/api/v1/prepared-tickets?recommendation=A&impact=Non-substantive");

        Assert.NotNull(response);
        PreparedTicketSummary row = Assert.Single(response.Items);
        Assert.Equal("FHIR-123", row.Key);
    }

    [Fact]
    public async Task Startup_RunsHydrationSweep_AgainstUnreachableUpstream_LeavesUnresolvedRows()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "service-smoke-sweep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        // Seed one prepared_tickets row before the host starts so the startup
        // sweep finds an eligible ticket. No source-ticket rows are seeded, so
        // the Specification backfill short-circuits and the per-ticket sweep
        // is the only pass that runs.
        using (PreparerDatabase seedDb = new(dbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<PreparerDatabase>.Instance))
        {
            seedDb.Initialize();
            await seedDb.SavePreparedTicketAsync(SamplePayload("FHIR-9001"));
        }

        Dictionary<string, string?> config = new()
        {
            ["Processing:DatabasePath"] = dbPath,
            ["Processing:StartProcessingOnStartup"] = "false",
            ["Processing:Hydration:BackfillOnStartup"] = "true",
            ["Processing:OrchestratorAddress"] = $"http://127.0.0.1:{FindFreePort()}",
            ["Processing:Jira:JiraSourceAddress"] = $"http://127.0.0.1:{FindFreePort()}",
            ["Processing:Jira:AgentCliCommand"] = "fake --ticket {ticketKey} --db {dbPath}",
        };

        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJiraTicketDiscoveryClient>();
                services.AddSingleton<IJiraTicketDiscoveryClient, FakeDiscoveryClient>();
            });
        });

        // Trigger startup. If the sweeper failed, this would throw because
        // ValidateOnStart + HydrationSweeperUnavailableException both abort.
        _ = factory.CreateClient();

        using PreparerDatabase verify = new(dbPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<PreparerDatabase>.Instance);
        verify.Initialize();
        FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models.PreparedTicketHydrationReadModel? read = await verify.GetHydrationAsync("FHIR-9001");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);

        factory.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int FindFreePort()
    {
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static PreparedTicketPayload SamplePayload(string key) => new()
    {
        Key = key,
        RequestSummary = "request",
        ProposalA = "proposal a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        Recommendation = "A",
        RecommendationJustification = "because",
    };

    private sealed class TestApp : IDisposable
    {
        private readonly string _directory;

        public TestApp()
        {
            _directory = Path.Combine(Environment.CurrentDirectory, "temp", "service-smoke", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "preparer.db");
            Dictionary<string, string?> config = new()
            {
                ["Processing:DatabasePath"] = DatabasePath,
                ["Processing:StartProcessingOnStartup"] = "false",
                ["Processing:Hydration:BackfillOnStartup"] = "false",
                ["Processing:Jira:JiraSourceAddress"] = "http://localhost:5160",
                ["Processing:Jira:AgentCliCommand"] = "fake --ticket {ticketKey} --db {dbPath}",
            };
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(config));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJiraTicketDiscoveryClient>();
                    services.AddSingleton<IJiraTicketDiscoveryClient, FakeDiscoveryClient>();
                });
            });
        }

        public string DatabasePath { get; }
        public WebApplicationFactory<Program> Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FakeDiscoveryClient : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([
            Ticket("FHIR-123", "Triaged"),
        ]);

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.FromResult<JiraIssueSummaryEntry?>(Ticket(key, "Submitted"));

        public Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct) => Task.CompletedTask;

        private static JiraIssueSummaryEntry Ticket(string key, string status) => new()
        {
            Key = key,
            ProjectKey = "FHIR",
            Title = "Ticket",
            Type = "Change Request",
            Status = status,
            WorkGroup = "FHIR-I",
            Specification = "FHIR Core",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed record PreparedTicketList(PreparedTicketSummary[] Items, int Limit, int Offset);
    private sealed record PreparedTicketSummary(string Key, string RequestSummary, string ProposalAImpact, string ProposalBImpact, string Recommendation, string RecommendationJustification, DateTimeOffset SavedAt);
}
