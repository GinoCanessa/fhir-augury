using System.Net;
using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
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

        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1?shape=FHIR", null);

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

    [Fact]
    public async Task PostAuthoringRun_LegacyAndCuttingOverModesRejectActivation()
    {
        FakeDiscovery legacyDiscovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient legacy = CreateClientForMode(
            legacyDiscovery,
            AuthoringStatusValues.ProcessorModes.Legacy,
            out _,
            out _,
            out _);
        HttpResponseMessage legacyResponse = await legacy.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-1"]));
        Assert.Equal(HttpStatusCode.Conflict, legacyResponse.StatusCode);

        FakeDiscovery cuttingOverDiscovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient cuttingOver = CreateClientForMode(
            cuttingOverDiscovery,
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            out _,
            out _,
            out _);
        HttpResponseMessage cuttingOverResponse = await cuttingOver.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-1"]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, cuttingOverResponse.StatusCode);
        Assert.Empty(cuttingOverDiscovery.RequestedShapes);
    }

    [Fact]
    public async Task PostTicket_RunBackedCreatesOneItemRunWithoutResettingLegacyStatus()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient client = CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore store,
            out AuthoringRunStore authoringStore,
            out _);
        JiraProcessingSourceTicketRecord legacy = await store.UpsertAsync(
            CreateTicket("FHIR-1", "Triaged"),
            "fhir",
            false,
            CancellationToken.None);
        await store.MarkCompleteAsync(legacy, DateTimeOffset.UtcNow, CancellationToken.None);

        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1", null);
        JiraProcessingEnqueueTicketResponse payload =
            (await response.Content.ReadFromJsonAsync<JiraProcessingEnqueueTicketResponse>())!;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(payload.RunId);
        Assert.NotNull(payload.RunItemId);
        Assert.Equal(
            ProcessingStatusValues.Complete,
            (await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None))!.ProcessingStatus);
        Assert.NotNull(await authoringStore.GetRunAsync(payload.RunId!));

        HttpResponseMessage replayResponse = await client.PostAsync("/processing/tickets/FHIR-1?shape=fhir", null);
        JiraProcessingEnqueueTicketResponse replay =
            (await replayResponse.Content.ReadFromJsonAsync<JiraProcessingEnqueueTicketResponse>())!;
        Assert.Equal(payload.RunId, replay.RunId);
    }

    [Fact]
    public async Task PostTicket_ExistingBatchRunReturnsMatchedItem()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient client = CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore store,
            out _,
            out JiraAuthoringRunCoordinator coordinator);
        JiraIssueSummaryEntry first = CreateTicket("FHIR-1", "Triaged");
        JiraIssueSummaryEntry second = CreateTicket("FHIR-2", "Triaged");
        await store.UpsertAsync(first, "fhir", false, CancellationToken.None);
        await store.UpsertAsync(second, "fhir", false, CancellationToken.None);
        JiraAuthoringRunCreation batch = (await coordinator.CreateScheduledRunAsync())!;
        Assert.Equal(2, batch.Items.Count);

        HttpResponseMessage response = await client.PostAsync("/processing/tickets/FHIR-1", null);
        JiraProcessingEnqueueTicketResponse payload =
            (await response.Content.ReadFromJsonAsync<JiraProcessingEnqueueTicketResponse>())!;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(batch.Run.Id, payload.RunId);
        Assert.Equal(
            batch.Items.Single(item => item.BusinessKey == "FHIR-1").Id,
            payload.RunItemId);
    }

    [Fact]
    public async Task PostTicket_RunBackedAtActiveCapacityReturnsConflict()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-2", "Triaged"));
        using HttpClient client = CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore store,
            out _,
            out JiraAuthoringRunCoordinator coordinator,
            maxActiveAuthoringRuns: 1);
        JiraProcessingSourceTicketRecord activeTicket = await store.UpsertAsync(
            CreateTicket("FHIR-1", "Triaged"),
            "fhir",
            false,
            CancellationToken.None);
        await coordinator.CreateOneItemRunAsync(activeTicket);

        HttpResponseMessage response =
            await client.PostAsync("/processing/tickets/FHIR-2", null);
        Dictionary<string, object> payload =
            (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>())!;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "active-run-capacity-reached",
            payload["error"].ToString());
    }

    [Fact]
    public async Task PostAuthoringRun_ReplaysExactBatchAndConflictsOnOverlapOrModeChange()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-1", "Triaged"));
        using HttpClient client = CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore store,
            out _,
            out _,
            maxActiveAuthoringRuns: 1);
        foreach (string key in new[] { "FHIR-1", "FHIR-2", "FHIR-3" })
        {
            await store.UpsertAsync(
                CreateTicket(key, "Triaged"),
                "fhir",
                false,
                CancellationToken.None);
        }

        JiraAuthoringRunRequest exact = new(["FHIR-1", "FHIR-2"], DatabaseOnly: false);
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            exact);
        JiraAuthoringRunResponse first =
            (await firstResponse.Content.ReadFromJsonAsync<JiraAuthoringRunResponse>())!;
        HttpResponseMessage replayResponse = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            exact);
        JiraAuthoringRunResponse replay =
            (await replayResponse.Content.ReadFromJsonAsync<JiraAuthoringRunResponse>())!;

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        Assert.Equal(first.Run.RunId, replay.Run.RunId);

        HttpResponseMessage overlap = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-1", "FHIR-3"]));
        HttpResponseMessage modeChange = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            exact with { DatabaseOnly = true });

        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, modeChange.StatusCode);
    }

    [Fact]
    public async Task PostAuthoringRun_AtActiveCapacityReturnsConflictBeforeDiscovery()
    {
        FakeDiscovery discovery = new(CreateTicket("FHIR-2", "Triaged"));
        using HttpClient client = CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore store,
            out _,
            out _,
            maxActiveAuthoringRuns: 1);
        foreach (string key in new[] { "FHIR-1", "FHIR-2" })
        {
            await store.UpsertAsync(
                CreateTicket(key, "Triaged"),
                "fhir",
                false,
                CancellationToken.None);
        }

        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-1"]));
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-2"]));
        Dictionary<string, object> response =
            (await second.Content.ReadFromJsonAsync<Dictionary<string, object>>())!;

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            "active-run-capacity-reached",
            response["error"].ToString());
        Assert.Empty(discovery.RequestedShapes);
    }

    [Fact]
    public async Task AuthoringControl_ReportsRetryMetadataAndRequiresSupersedeReason()
    {
        using HttpClient client = CreateClientForMode(
            new FakeDiscovery(CreateTicket("FHIR-1", "Triaged")),
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out JiraProcessingSourceTicketStore sourceStore,
            out AuthoringRunStore authoringStore,
            out JiraAuthoringRunCoordinator coordinator);
        await sourceStore.UpsertAsync(
            CreateTicket("FHIR-1", "Triaged"),
            "fhir",
            false,
            CancellationToken.None);
        JiraAuthoringRunCreation creation =
            (await coordinator.CreateScheduledRunAsync())!;
        Assert.True(await authoringStore.TryAcquireMutationFenceAsync(
            coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await authoringStore.ClaimItemAsync(creation.Run.Id, item.Id));
        await authoringStore.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");

        JiraAuthoringRunResponse before =
            (await client.GetFromJsonAsync<JiraAuthoringRunResponse>(
                $"/processing/authoring/runs/{creation.Run.Id}"))!;
        Assert.Equal(1, before.Run.FailedItems);
        Assert.Equal(1, before.Run.RetryableErrorItems);
        Assert.Equal(2, Assert.Single(before.Items).AttemptsRemaining);

        HttpResponseMessage missingReason = await client.PostAsJsonAsync(
            $"/processing/authoring/runs/{creation.Run.Id}/items/{item.Id}/supersede",
            new AuthoringItemSupersedeRequest(" "));
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        HttpResponseMessage superseded = await client.PostAsJsonAsync(
            $"/processing/authoring/runs/{creation.Run.Id}/items/{item.Id}/supersede",
            new AuthoringItemSupersedeRequest("not actionable"));
        Assert.Equal(HttpStatusCode.OK, superseded.StatusCode);
        AuthoringItemSupersedeResult result =
            (await superseded.Content.ReadFromJsonAsync<AuthoringItemSupersedeResult>())!;
        Assert.Equal(AuthoringStatusValues.Items.Superseded, result.Status);

        JiraAuthoringRunResponse after =
            (await client.GetFromJsonAsync<JiraAuthoringRunResponse>(
                $"/api/v1/processing/authoring/runs/{creation.Run.Id}"))!;
        Assert.Equal(1, after.Run.FailedItems);
        Assert.Equal(0, after.Run.RetryableErrorItems);
        Assert.Equal(1, after.Run.SupersededItems);
    }

    [Fact]
    public async Task ReadySnapshotBytesAreStreamedFromProcessorOwnedRecord()
    {
        using HttpClient client = CreateClientForMode(
            new FakeDiscovery(CreateTicket("FHIR-1", "Triaged")),
            AuthoringStatusValues.ProcessorModes.RunBacked,
            out _,
            out AuthoringRunStore authoringStore,
            out JiraAuthoringRunCoordinator coordinator,
            out string dbPath);
        AuthoringRunRecord run = await authoringStore.CreateRunAsync(
            coordinator.ProcessorKind,
            [
                new AuthoringRunItemDefinition(
                    "FHIR-1",
                    "fhir",
                    "revision-1"),
            ]);
        byte[] expected = [5, 4, 3, 2, 1];
        string snapshotPath = Path.Combine(
            Path.GetDirectoryName(dbPath)!,
            $"jira-snapshot-{Guid.NewGuid():N}.db");
        await File.WriteAllBytesAsync(snapshotPath, expected);
        await using (SqliteConnection connection =
            new($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO authoring_review_snapshots(
                    Id, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                    SchemaVersion, Status, TempPath, Path, ChecksumSha256,
                    SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                    CreatedAt)
                VALUES(
                    'snapshot-1', 'jira-fhir', @runId, 1, 1,
                    1, 'ready', '', @path, 'hash',
                    @size, 1, 1, '{}', @createdAt);
                UPDATE authoring_runs
                SET SnapshotId = 'snapshot-1'
                WHERE Id = @runId
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            command.Parameters.AddWithValue("@path", snapshotPath);
            command.Parameters.AddWithValue("@size", expected.Length);
            command.Parameters.AddWithValue(
                "@createdAt",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        HttpResponseMessage response = await client.GetAsync(
            $"/processing/authoring/runs/{run.Id}/snapshot/bytes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/vnd.sqlite3",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        File.Delete(snapshotPath);
    }

    private static HttpClient CreateClient(FakeDiscovery discovery, out JiraProcessingSourceTicketStore store)
        => CreateClientForMode(
            discovery,
            AuthoringStatusValues.ProcessorModes.Legacy,
            out store,
            out _,
            out _);

    private static HttpClient CreateClientForMode(
        FakeDiscovery discovery,
        string mode,
        out JiraProcessingSourceTicketStore store,
        out AuthoringRunStore authoringStore,
        out JiraAuthoringRunCoordinator coordinator,
        int maxActiveAuthoringRuns = int.MaxValue)
        => CreateClientForMode(
            discovery,
            mode,
            out store,
            out authoringStore,
            out coordinator,
            out _,
            maxActiveAuthoringRuns);

    private static HttpClient CreateClientForMode(
        FakeDiscovery discovery,
        string mode,
        out JiraProcessingSourceTicketStore store,
        out AuthoringRunStore authoringStore,
        out JiraAuthoringRunCoordinator coordinator,
        out string dbPath,
        int maxActiveAuthoringRuns = int.MaxValue)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        dbPath = Path.Combine(AppContext.BaseDirectory, $"jira-endpoint-{Guid.NewGuid():N}.db");
        store = new JiraProcessingSourceTicketStore(dbPath, new ResolvedJiraProcessingFilters { TicketStatuses = ["Triaged"], SourceTicketShape = "fhir" });
        JiraProcessingDatabase processingDatabase = new(
            dbPath,
            NullLogger<JiraProcessingDatabase>.Instance);
        processingDatabase.Initialize();
        IOptions<FhirAugury.Processing.Common.Configuration.ProcessingServiceOptions>
            processingOptions = Options.Create(
                new FhirAugury.Processing.Common.Configuration.ProcessingServiceOptions
                {
                    MaxActiveAuthoringRuns = maxActiveAuthoringRuns,
                });
        AuthoringRetryPolicy retryPolicy = new(processingOptions);
        authoringStore = new AuthoringRunStore(
            processingDatabase,
            retryPolicy: retryPolicy);
        AuthoringRunControlService controlService =
            new(authoringStore, retryPolicy);
        IOptions<JiraProcessingOptions> options = Options.Create(new JiraProcessingOptions
        {
            AgentCliCommand = "agent {ticketKey}",
            JiraSourceAddress = "http://source",
            SourceTicketShape = "fhir",
        });
        coordinator = new JiraAuthoringRunCoordinator(
            authoringStore,
            store,
            new JiraProcessingFilterResolver(),
            options,
            processingOptions);
        authoringStore.EnsureProcessorModeAsync(coordinator.ProcessorKind).GetAwaiter().GetResult();
        if (mode == AuthoringStatusValues.ProcessorModes.CuttingOver)
        {
            authoringStore.TransitionProcessorModeAsync(
                coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver).GetAwaiter().GetResult();
        }
        else if (mode == AuthoringStatusValues.ProcessorModes.RunBacked)
        {
            authoringStore.TransitionProcessorModeAsync(
                coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver).GetAwaiter().GetResult();
            authoringStore.TransitionProcessorModeAsync(
                coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked).GetAwaiter().GetResult();
        }
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(authoringStore);
        builder.Services.AddSingleton(retryPolicy);
        builder.Services.AddSingleton(controlService);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton<IJiraTicketDiscoveryClient>(discovery);
        builder.Services.AddSingleton(options);
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
