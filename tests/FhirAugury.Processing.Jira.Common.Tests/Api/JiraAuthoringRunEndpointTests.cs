using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Api;

public sealed class JiraAuthoringRunEndpointTests
{
    [Fact]
    public async Task ListRuns_ReturnsAggregateSummaries()
    {
        using EndpointFixture fixture = new();
        DateTimeOffset createdAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first =
            await fixture.SeedAsync("FHIR-1", createdAt);
        JiraProcessingSourceTicketRecord second =
            await fixture.SeedAsync("FHIR-2", createdAt);
        JiraProcessingSourceTicketRecord third =
            await fixture.SeedAsync("FHIR-3", createdAt);
        JiraAuthoringRunCreation active =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [first, second],
                databaseOnly: false);
        JiraAuthoringRunCreation terminal =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [third],
                databaseOnly: false);
        Assert.True(await fixture.Store.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            active.Run.Id,
            createdAt));
        fixture.Execute(
            """
            UPDATE authoring_run_items
            SET Status = @complete, CompletedAt = @completedAt
            WHERE Id = @completeId;
            UPDATE authoring_run_items
            SET Status = @error, AttemptCount = 1, CompletedAt = @completedAt,
                Error = 'worker failure'
            WHERE Id = @errorId;
            UPDATE authoring_runs
            SET Status = @completed, CompletedAt = @completedAt
            WHERE Id = @terminalRunId;
            """,
            ("@complete", AuthoringStatusValues.Items.Complete),
            ("@error", AuthoringStatusValues.Items.Error),
            ("@completed", AuthoringStatusValues.Runs.Completed),
            ("@completedAt", createdAt.AddMinutes(1).ToString("O")),
            ("@completeId", active.Items[0].Id),
            ("@errorId", active.Items[1].Id),
            ("@terminalRunId", terminal.Run.Id));

        HttpResponseMessage response = await fixture.Client.GetAsync(
            "/processing/authoring/runs?limit=1");
        AuthoringRunListResponse page =
            (await response.Content.ReadFromJsonAsync<AuthoringRunListResponse>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(page.Truncated);
        AuthoringRunStatus summary = Assert.Single(page.Runs);
        Assert.Equal(active.Run.Id, summary.RunId);
        Assert.Equal(2, summary.TotalItems);
        Assert.Equal(1, summary.CompletedItems);
        Assert.Equal(1, summary.FailedItems);
        Assert.Equal(1, summary.RetryableErrorItems);
        Assert.Equal(0, summary.SupersededItems);
        Assert.False(summary.State!.IsTerminal);
        Assert.NotNull(summary.State.NextAutomaticRecoveryAt);

        AuthoringRunListResponse versioned =
            (await fixture.Client.GetFromJsonAsync<AuthoringRunListResponse>(
                "/api/v1/processing/authoring/runs"))!;
        Assert.Equal(
            [active.Run.Id, terminal.Run.Id],
            versioned.Runs.Select(run => run.RunId));
        Assert.False(versioned.Truncated);

        HttpResponseMessage invalid = await fixture.Client.GetAsync(
            "/processing/authoring/runs?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task CreateRun_ConflictIncludesRunCoordinates()
    {
        using EndpointFixture fixture = new(maxActiveAuthoringRuns: 1);
        DateTimeOffset revision =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        HttpResponseMessage accepted = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-1"]));
        AuthoringRunResponse acceptedBody =
            (await accepted.Content.ReadFromJsonAsync<AuthoringRunResponse>())!;

        HttpResponseMessage conflict = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-2"]));
        string json = await conflict.Content.ReadAsStringAsync();
        AuthoringConflictResponse body =
            JsonSerializer.Deserialize<AuthoringConflictResponse>(
                json,
                JsonSerializerOptions.Web)!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("active-run-capacity-reached", body.Error);
        Assert.NotNull(body.Detail);
        Assert.Equal([acceptedBody.Run.RunId], body.ConflictingRunIds);
        Assert.Equal(acceptedBody.Run.RunId, body.RunId);
        Assert.True(document.RootElement.TryGetProperty("error", out _));
        Assert.True(document.RootElement.TryGetProperty("detail", out _));
        Assert.True(document.RootElement.TryGetProperty(
            "conflictingRunIds",
            out _));
        Assert.True(document.RootElement.TryGetProperty("runId", out _));

        AuthoringRunItemStatus item = Assert.Single(acceptedBody.Items);
        HttpResponseMessage retryConflict = await fixture.Client.PostAsync(
            $"/processing/authoring/runs/{acceptedBody.Run.RunId}/items/{item.ItemId}/retry",
            null);
        AuthoringConflictResponse retryBody =
            (await retryConflict.Content.ReadFromJsonAsync<AuthoringConflictResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, retryConflict.StatusCode);
        Assert.Equal("MutationFenceUnavailable", retryBody.Error);
        Assert.NotNull(retryBody.Detail);
        Assert.Empty(retryBody.ConflictingRunIds!);
    }

    [Fact]
    public async Task CreateRun_RevisionAndRevalidationConflictsIncludeRunCoordinates()
    {
        using EndpointFixture revisionFixture = new();
        DateTimeOffset revision =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await revisionFixture.SeedAsync("FHIR-1", revision);
        await revisionFixture.SeedAsync("FHIR-2", revision);
        await revisionFixture.SeedAsync("FHIR-3", revision);
        HttpResponseMessage accepted =
            await revisionFixture.Client.PostAsJsonAsync(
                "/processing/authoring/runs",
                new JiraAuthoringRunRequest(["FHIR-1", "FHIR-2"]));
        AuthoringRunResponse acceptedBody =
            (await accepted.Content.ReadFromJsonAsync<AuthoringRunResponse>())!;

        HttpResponseMessage revisionConflict =
            await revisionFixture.Client.PostAsJsonAsync(
                "/processing/authoring/runs",
                new JiraAuthoringRunRequest(["FHIR-1", "FHIR-3"]));
        AuthoringConflictResponse revisionBody =
            (await revisionConflict.Content
                .ReadFromJsonAsync<AuthoringConflictResponse>())!;

        Assert.Equal(HttpStatusCode.Conflict, revisionConflict.StatusCode);
        Assert.Equal("revision-already-scheduled", revisionBody.Error);
        Assert.Equal(
            [acceptedBody.Run.RunId],
            revisionBody.ConflictingRunIds);

        using EndpointFixture revalidationFixture = new();
        AuthoringRunRecord revalidationRun =
            await revalidationFixture.Store.CreateRunAsync(
                revalidationFixture.Coordinator.ProcessorKind,
                [new("FHIR-9", "fhir", "revision-9")],
                runId: "revalidation-run");
        revalidationFixture.Execute(
            """
            UPDATE authoring_processor_modes
            SET RevalidationRequired = 1, RevalidationRunId = @runId
            WHERE ProcessorKind = @processorKind
            """,
            ("@runId", revalidationRun.Id),
            ("@processorKind", revalidationFixture.Coordinator.ProcessorKind));

        HttpResponseMessage revalidationConflict =
            await revalidationFixture.Client.PostAsJsonAsync(
                "/processing/authoring/runs",
                new JiraAuthoringRunRequest());
        AuthoringConflictResponse revalidationBody =
            (await revalidationConflict.Content
                .ReadFromJsonAsync<AuthoringConflictResponse>())!;

        Assert.Equal(
            HttpStatusCode.Conflict,
            revalidationConflict.StatusCode);
        Assert.Equal("revalidation-required", revalidationBody.Error);
        Assert.Equal(
            [revalidationRun.Id],
            revalidationBody.ConflictingRunIds);
        Assert.Equal(revalidationRun.Id, revalidationBody.RunId);
    }

    private sealed class EndpointFixture : IDisposable
    {
        private readonly string _directory;
        private readonly WebApplication _app;

        public EndpointFixture(int maxActiveAuthoringRuns = int.MaxValue)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-jira-authoring-endpoint-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "processing.db");
            SourceStore = new JiraProcessingSourceTicketStore(
                DatabasePath,
                new ResolvedJiraProcessingFilters
                {
                    TicketStatuses = ["Triaged"],
                    SourceTicketShape = "fhir",
                });
            JiraProcessingDatabase database = new(
                DatabasePath,
                NullLogger<JiraProcessingDatabase>.Instance);
            database.Initialize();
            IOptions<ProcessingServiceOptions> processingOptions =
                Options.Create(new ProcessingServiceOptions
                {
                    MaxActiveAuthoringRuns = maxActiveAuthoringRuns,
                });
            AuthoringRetryPolicy retryPolicy = new(processingOptions);
            Store = new AuthoringRunStore(
                database,
                retryPolicy: retryPolicy);
            IOptions<JiraProcessingOptions> options =
                Options.Create(new JiraProcessingOptions
                {
                    AgentCliCommand = "agent {ticketKey}",
                    JiraSourceAddress = "http://source",
                    SourceTicketShape = "fhir",
                });
            Coordinator = new JiraAuthoringRunCoordinator(
                Store,
                SourceStore,
                new JiraProcessingFilterResolver(),
                options,
                processingOptions);
            Store.EnsureProcessorModeAsync(Coordinator.ProcessorKind)
                .GetAwaiter()
                .GetResult();
            Store.TransitionProcessorModeAsync(
                    Coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.Legacy,
                    AuthoringStatusValues.ProcessorModes.CuttingOver)
                .GetAwaiter()
                .GetResult();
            Store.TransitionProcessorModeAsync(
                    Coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.CuttingOver,
                    AuthoringStatusValues.ProcessorModes.RunBacked)
                .GetAwaiter()
                .GetResult();

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(SourceStore);
            builder.Services.AddSingleton(Store);
            builder.Services.AddSingleton(retryPolicy);
            builder.Services.AddSingleton(
                new AuthoringRunControlService(Store, retryPolicy));
            builder.Services.AddSingleton(Coordinator);
            builder.Services.AddSingleton<IJiraTicketDiscoveryClient>(
                new EmptyDiscoveryClient());
            builder.Services.AddSingleton(options);
            _app = builder.Build();
            _app.MapJiraProcessingTicketEndpoints();
            _app.StartAsync().GetAwaiter().GetResult();
            Client = _app.GetTestClient();
        }

        public string DatabasePath { get; }
        public HttpClient Client { get; }
        public JiraProcessingSourceTicketStore SourceStore { get; }
        public AuthoringRunStore Store { get; }
        public JiraAuthoringRunCoordinator Coordinator { get; }

        public Task<JiraProcessingSourceTicketRecord> SeedAsync(
            string key,
            DateTimeOffset revision)
            => SourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    UpdatedAt = revision,
                },
                "fhir",
                resetProcessingStatus: false,
                CancellationToken.None);

        public int Execute(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Client.Dispose();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class EmptyDiscoveryClient : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(
            ResolvedJiraProcessingFilters filters,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.FromResult<JiraIssueSummaryEntry?>(null);

        public Task MarkProcessedAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.CompletedTask;
    }
}
