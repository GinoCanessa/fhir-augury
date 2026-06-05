using System.Net;
using System.Net.Http.Json;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerControllerTests : IClassFixture<PlannerControllerTests.Fixture>
{
    private readonly Fixture _fixture;
    public PlannerControllerTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetPlannedTicket_ReturnsDetail()
    {
        await _fixture.SeedAsync();
        HttpClient client = _fixture.Factory.CreateClient();
        PlannedTicketDetailDto? detail = await client.GetFromJsonAsync<PlannedTicketDetailDto>("/api/v1/planned-tickets/FHIR-1000");
        Assert.NotNull(detail);
        Assert.Equal("FHIR-1000", detail!.Ticket.Key);
        Assert.Single(detail.Repos);
    }

    [Fact]
    public async Task GetPlannedTicket_ReturnsNotFound()
    {
        HttpClient client = _fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/planned-tickets/DOES-NOT-EXIST");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListPlannedTickets_FiltersByRepo()
    {
        await _fixture.SeedAsync();
        HttpClient client = _fixture.Factory.CreateClient();
        PlannedTicketListResponse? response = await client.GetFromJsonAsync<PlannedTicketListResponse>("/api/v1/planned-tickets?repo=HL7%2Ffhir");
        Assert.NotNull(response);
        Assert.NotEmpty(response!.Results);
    }

    [Fact]
    public async Task HydrationDisplay_ReturnsSelfRowsForWorkGroup()
    {
        await _fixture.SeedAsync();
        await _fixture.SeedJiraHydrationSelfRowAsync("FHIR-1000", "FHIR Infrastructure");

        HttpClient client = _fixture.Factory.CreateClient();
        // The cleaner maps "FHIR Infrastructure" → "FHIRInfrastructure"; the
        // controller cleans whatever the URL contains and queries by that.
        PlannedJiraHydrationDisplayResponse? response = await client.GetFromJsonAsync<PlannedJiraHydrationDisplayResponse>("/api/v1/planned-ticket-hydration/FHIRInfrastructure");
        Assert.NotNull(response);
        Assert.Single(response!.Results);
        Assert.Equal("FHIR-1000", response.Results[0].IssueKey);
        Assert.Equal("FHIR-1000", response.Results[0].JiraKey);
    }

    [Fact]
    public async Task PutTopics_PersistsAndRoundTrips()
    {
        await _fixture.SeedAsync();
        HttpClient client = _fixture.Factory.CreateClient();

        // Use the canonical cleaner-output form for WorkGroupClean so the GET
        // URL (also cleaned) round-trips to the same row.
        PlannedTicketTopicGroupingRequest req = new()
        {
            WorkGroupClean = "FHIRInfrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicRequest
                {
                    ShortDescription = "controller topic",
                    LongerDescription = "longer",
                    SpannedRepos = ["HL7/fhir", "HL7/fhir-extensions"],
                    RemainingTicketKeys = ["FHIR-1000"],
                },
            ],
        };
        HttpResponseMessage putResponse = await client.PutAsJsonAsync("/api/v1/planned-ticket-topics", req);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        PlannedTicketTopicGroupingResponse? get = await client.GetFromJsonAsync<PlannedTicketTopicGroupingResponse>(
            "/api/v1/planned-ticket-topics/FHIRInfrastructure/FHIR/Change%20Request");
        Assert.NotNull(get);
        Assert.Single(get!.Topics);
        Assert.Equal(["HL7/fhir", "HL7/fhir-extensions"], get.Topics[0].SpannedRepos);
    }

    [Fact]
    public async Task PutTopics_RejectsMalformedRepo()
    {
        HttpClient client = _fixture.Factory.CreateClient();
        PlannedTicketTopicGroupingRequest req = new()
        {
            WorkGroupClean = "wg",
            WorkGroupDisplay = "WG",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicRequest
                {
                    ShortDescription = "x",
                    LongerDescription = "x",
                    SpannedRepos = ["no-slash"],
                },
            ],
        };
        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/planned-ticket-topics", req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public sealed class Fixture : IDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public PlannerDatabase Database { get; }
        public string DbPath { get; }

        public Fixture()
        {
            string dir = Path.Combine(Environment.CurrentDirectory, "temp", "planner-controller-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            DbPath = Path.Combine(dir, "planner.db");

            // Initialize the database before WebApplicationFactory boots — the
            // service registers PlannerDatabase as a singleton that picks up
            // DatabasePath from configuration; we override that to point at
            // this temp DB and skip the startup sweep.
            Database = new PlannerDatabase(DbPath, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();

            Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Processing:DatabasePath"] = DbPath,
                            ["Processing:Hydration:BackfillOnStartup"] = "false",
                            ["Processing:Jira:JiraSourceAddress"] = "http://localhost:0",
                            ["Processing:Jira:OrchestratorAddress"] = "http://localhost:0",
                            ["Processing:StartProcessingOnStartup"] = "false",
                        });
                    });
                    builder.ConfigureServices(services =>
                    {
                        // Replace the auto-registered PlannerDatabase singleton with
                        // the fixture's instance so seeds-through-the-fixture and
                        // reads-through-the-controller share a connection pool
                        // (avoids WAL cross-instance visibility ambiguity).
                        ServiceDescriptor[] toRemove = services.Where(s =>
                            s.ServiceType == typeof(PlannerDatabase) ||
                            s.ServiceType == typeof(FhirAugury.Processing.Common.Database.ProcessingDatabase)).ToArray();
                        foreach (ServiceDescriptor d in toRemove)
                        {
                            services.Remove(d);
                        }
                        services.AddSingleton(Database);
                        services.AddSingleton<FhirAugury.Processing.Common.Database.ProcessingDatabase>(_ => Database);
                    });
                });
        }

        public async Task SeedAsync()
        {
            if (await Database.PlanExistsAsync("FHIR-1000")) return;
            await Database.SavePlannedTicketAsync(new PlannedTicketPayload
            {
                Key = "FHIR-1000",
                Resolution = "Persuasive",
                ResolutionSummary = "summary",
                FeatureProposal = "proposal",
                DesignRationale = "rationale",
                Repos = [new PlannedTicketRepoPayload { RepoKey = "HL7/fhir", Justification = "primary" }],
            });
        }

        public async Task SeedJiraHydrationSelfRowAsync(string issueKey, string workGroupDisplay)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow;
            HydrationBatch batch = new(
                TicketKey: issueKey,
                Parent: new HydrationTicketRow(issueKey, null, null, null, "FHIR", null, null, null, null, null, null, null, at, "resolved", null),
                JiraRows:
                [
                    // Self row whose WorkGroup will be auto-cleaned by InsertJira; the
                    // controller cleans its URL input by the same function for the WHERE.
                    new HydrationJiraRow(issueKey, issueKey, "Self ticket", "Triaged", "Change Request", null, null, null,
                        workGroupDisplay, "FHIR", null, "https://x", at, "resolved", null),
                ],
                ZulipRows: [],
                GitHubRows: [],
                RepoRows: [],
                JiraXrefRows: []);
            await ((IHydrationTargetDatabase)Database).SaveHydrationAsync(batch, CancellationToken.None);
        }

        public void Dispose()
        {
            Factory.Dispose();
            Database.Dispose();
        }
    }
}
