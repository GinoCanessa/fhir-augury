using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketHydratorTests
{
    [Fact]
    public async Task HydrateAsync_Success_PersistsSelfRow()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-201");
        FakeHandler handler = new(JsonMetadata("planned"));
        PlannedTicketHydrator hydrator = CreateHydrator(database.Database, handler);

        await hydrator.HydrateAsync("FHIR-201", CancellationToken.None);

        PlannedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-201");
        Assert.NotNull(read);
        Assert.Equal("resolved", read!.Parent!.HydrationStatus);
        Assert.Equal("planned", Assert.Single(read.JiraRows).Title);
        Assert.Equal("<p>body</p>", read.Parent.DescriptionHtml);
        Assert.Equal("<p>resolution</p>", read.Parent.ResolutionDescriptionHtml);
        Assert.Equal("Ada", read.Parent.Reporter);
        Assert.NotNull(read.Parent.CreatedAt);
        Assert.Equal("Patient, Observation", read.Parent.RelatedArtifactsRaw);
        Assert.Equal("patient.html", read.Parent.RelatedPagesRaw);
        using Microsoft.Data.Sqlite.SqliteConnection connection = database.Database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DescriptionHtml, ResolutionDescriptionHtml FROM planned_ticket_jira_content WHERE TicketKey = 'FHIR-201'";
        using Microsoft.Data.Sqlite.SqliteDataReader content = command.ExecuteReader();
        Assert.True(content.Read());
        Assert.Equal("<p>body</p>", content.GetString(0));
        Assert.Equal("<p>resolution</p>", content.GetString(1));
    }

    [Fact]
    public async Task HydrateAsync_UnexpectedFailure_RemainsNonThrowing()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-202");
        PlannedTicketHydrator hydrator = CreateHydrator(
            database.Database,
            new FakeHandler(new InvalidOperationException("script failed")));

        await hydrator.HydrateAsync("FHIR-202", CancellationToken.None);

        Assert.Null(await database.Database.GetHydrationAsync("FHIR-202"));
    }

    [Fact]
    public async Task HydrateAsync_CallerCancellation_Rethrows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-203");
        PlannedTicketHydrator hydrator = CreateHydrator(database.Database, new FakeHandler(JsonMetadata("planned")));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hydrator.HydrateAsync("FHIR-203", cancellation.Token));
    }

    private static PlannedTicketHydrator CreateHydrator(PlannerDatabase database, HttpMessageHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        return new PlannedTicketHydrator(client, database, NullLogger<PlannedTicketHydrator>.Instance);
    }

    private static Task SeedPlanAsync(PlannerDatabase database, string key) =>
        database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = key,
            ResolutionSummary = "summary",
        });

    private static string JsonMetadata(string title) => JsonSerializer.Serialize(new
    {
        id = "x",
        title,
        url = "https://jira/browse/FHIR-201",
        content = "<p>body</p>",
        createdAt = "2026-04-01T00:00:00Z",
        metadata = new Dictionary<string, string>
        {
            ["status"] = "Triaged",
            ["type"] = "Change Request",
            ["work_group"] = "FHIR-I",
            ["specification"] = "FHIR",
            ["resolution_description"] = "<p>resolution</p>",
            ["reporter"] = "Ada",
            ["related_artifacts"] = "Patient, Observation",
            ["related_pages"] = "patient.html",
        },
    });

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "planner-hydrator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlannerDatabase database = new(
            Path.Combine(directory, "planner.db"),
            NullLogger<PlannerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private sealed class TestDatabase(string directory, PlannerDatabase database) : IDisposable
    {
        public PlannerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string? _json;
        private readonly Exception? _exception;

        public FakeHandler(string json)
        {
            _json = json;
        }

        public FakeHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json!, Encoding.UTF8, "application/json"),
            });
        }
    }
}
