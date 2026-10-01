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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateRun_NoKeysUsesConfiguredLabels(bool emptyKeys, bool databaseOnly)
    {
        using EndpointFixture fixture = new(configure: options =>
        {
            options.LabelsToInclude = ["cohort"];
            options.LabelsToExclude = ["blocked"];
        });
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision.AddDays(-1));
        await fixture.SeedAsync("FHIR-2", revision);
        fixture.Matcher.Enqueue(["FHIR-2"]);

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(emptyKeys ? [] : null, databaseOnly));
        AuthoringRunResponse body = Assert.IsType<AuthoringRunResponse>(
            await response.Content.ReadFromJsonAsync<AuthoringRunResponse>());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            $"/processing/authoring/runs/{body.Run.RunId}",
            response.Headers.Location?.OriginalString);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(body.Run.RunId));
        Assert.Equal("FHIR-2", item.BusinessKey);
        Assert.Equal(0, item.AttemptCount);
        Assert.Equal(databaseOnly, (await fixture.Store.GetRunAsync(body.Run.RunId))!.DatabaseOnly);
        Assert.Equal(["FHIR-1", "FHIR-2"], Assert.Single(fixture.Matcher.Calls).Keys);
        Assert.Equal(["cohort"], fixture.Matcher.Calls[0].Filters.LabelsToInclude);
        Assert.Equal(["blocked"], fixture.Matcher.Calls[0].Filters.LabelsToExclude);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateRun_SelectionUnavailableReturns503WithoutRun(bool emptyKeys, bool databaseOnly)
    {
        using EndpointFixture fixture = new(configure: options => options.LabelsToInclude = ["cohort"]);
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        const string detail = "Jira selection response was incomplete.";
        fixture.Matcher.Enqueue((_, _, _) =>
            throw new JiraTicketSelectionUnavailableException(detail));

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(emptyKeys ? [] : null, databaseOnly));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("jira-selection-unavailable", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(detail, body.RootElement.GetProperty("detail").GetString());
        Assert.Single(fixture.Matcher.Calls);
        AssertNoAuthoringWork(fixture);
        JiraProcessingSourceTicketRecord source = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.Equal(0, source.ProcessingAttemptCount);
        Assert.Null(source.ProcessingStatus);
        Assert.Null(source.ProcessingError);
    }

    [Fact]
    public async Task CreateRun_LaterBatchSelectionFailureReturns503WithoutPartialRun()
    {
        using EndpointFixture fixture = new(configure: options => options.LabelsToInclude = ["cohort"]);
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        for (int index = 1; index <= 501; index++)
        {
            await fixture.SeedAsync($"FHIR-{index:D4}", revision.AddSeconds(index));
        }
        fixture.Matcher.Enqueue(["FHIR-0001"]);
        fixture.Matcher.Enqueue((_, _, _) =>
            throw new JiraTicketSelectionUnavailableException("Jira selection is unavailable."));

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(DatabaseOnly: true));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal([500, 1], fixture.Matcher.Calls.Select(call => call.Keys.Count));
        AssertNoAuthoringWork(fixture);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateRun_NoLabelMatchesReturns204(bool emptyKeys, bool databaseOnly)
    {
        using EndpointFixture fixture = new(configure: options => options.LabelsToExclude = ["blocked"]);
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        fixture.Matcher.Enqueue([]);

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(emptyKeys ? [] : null, databaseOnly));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Single(fixture.Matcher.Calls);
        AssertNoAuthoringWork(fixture);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateRun_ExplicitKeysBypassConfiguredLabels(bool singleKey, bool databaseOnly)
    {
        using EndpointFixture fixture = new(configure: options =>
        {
            options.LabelsToInclude = ["different-cohort"];
            options.LabelsToExclude = ["%"];
        });
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        string[] keys = singleKey ? ["FHIR-1"] : ["FHIR-1", "FHIR-2"];

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs", new JiraAuthoringRunRequest(keys, databaseOnly));
        AuthoringRunResponse body = Assert.IsType<AuthoringRunResponse>(
            await response.Content.ReadFromJsonAsync<AuthoringRunResponse>());
        HttpResponseMessage replay = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(
                keys.Reverse().Select(key => key.ToLowerInvariant()).ToArray(), databaseOnly));
        AuthoringRunResponse replayBody = Assert.IsType<AuthoringRunResponse>(
            await replay.Content.ReadFromJsonAsync<AuthoringRunResponse>());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(body.Run.RunId, replayBody.Run.RunId);
        Assert.Equal(
            keys,
            (await fixture.Store.GetRunItemsAsync(body.Run.RunId))
                .Select(item => item.BusinessKey).Order(StringComparer.Ordinal));
        Assert.Equal(databaseOnly, (await fixture.Store.GetRunAsync(body.Run.RunId))!.DatabaseOnly);
        Assert.Empty(fixture.Matcher.Calls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task CreateRun_ExplicitDiscoveryFailureIsNotTranslatedToSelection503()
    {
        JiraTicketSelectionUnavailableException failure = new("Explicit discovery failed.");
        using EndpointFixture fixture = new(
            discoveryClient: new EmptyDiscoveryClient(failure),
            configure: options => options.LabelsToInclude = ["cohort"]);

        JiraTicketSelectionUnavailableException actual =
            await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
                () => fixture.Client.PostAsJsonAsync(
                    "/processing/authoring/runs", new JiraAuthoringRunRequest(["FHIR-1"])));

        Assert.Same(failure, actual);
        Assert.Empty(fixture.Matcher.Calls);
        AssertNoAuthoringWork(fixture);
    }

    [Fact]
    public async Task CreateRun_SelectionPreservesCallerCancellation()
    {
        using EndpointFixture fixture = new(configure: options => options.LabelsToInclude = ["cohort"]);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        fixture.Matcher.Enqueue(async (_, _, ct) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return [];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled.SetResult();
                throw;
            }
        });
        Task<HttpResponseMessage> request = fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs", new JiraAuthoringRunRequest(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(fixture.Matcher.Calls);
            AssertNoAuthoringWork(fixture);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Theory]
    [InlineData("capacity", HttpStatusCode.Conflict, "active-run-capacity-reached")]
    [InlineData("workflow", HttpStatusCode.Conflict, "canonical-unpublished-restriction")]
    [InlineData("legacy", HttpStatusCode.Conflict, "authoring-not-activated")]
    [InlineData("cutover", HttpStatusCode.ServiceUnavailable, "cutover-in-progress")]
    [InlineData("revalidation", HttpStatusCode.Conflict, "revalidation-required")]
    public async Task CreateRun_NoKeysPreservesEarlyConflicts(
        string guard, HttpStatusCode expectedStatus, string expectedError)
    {
        using EndpointFixture fixture = new(
            maxActiveAuthoringRuns: guard == "capacity" ? 1 : int.MaxValue,
            configure: options => options.LabelsToInclude = ["cohort"],
            snapshotWorkflowGuard: guard == "workflow" ? new RejectingSnapshotWorkflowGuard() : null);
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        if (guard == "capacity")
        {
            await fixture.Coordinator.CreateExplicitRunAsync([source], databaseOnly: true);
        }
        else if (guard is "legacy" or "cutover")
        {
            fixture.Execute(
                "UPDATE authoring_processor_modes SET Mode = @mode",
                ("@mode", guard == "legacy"
                    ? AuthoringStatusValues.ProcessorModes.Legacy
                    : AuthoringStatusValues.ProcessorModes.CuttingOver));
        }
        else if (guard == "revalidation")
        {
            fixture.Execute(
                "UPDATE authoring_processor_modes SET RevalidationRequired = 1, RevalidationRunId = 'revalidation-run'");
        }

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs", new JiraAuthoringRunRequest());
        AuthoringConflictResponse body = Assert.IsType<AuthoringConflictResponse>(
            await response.Content.ReadFromJsonAsync<AuthoringConflictResponse>());

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedError, body.Error);
        Assert.Empty(fixture.Matcher.Calls);
        Assert.Equal(guard == "capacity" ? 1 : 0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

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
    public async Task CreateRun_MissingSourceRowFreezesDiscoveryProvenance()
    {
        DateTimeOffset refreshedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        StaticDiscoveryClient discovery = new(
            new JiraIssueSummaryEntry
            {
                Key = "FHIR-42",
                ProjectKey = "FHIR",
                Title = "Discovered",
                Type = "Change Request",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
                UpdatedAt = refreshedAt.AddHours(1),
            },
            new SourceReadProvenance
            {
                Source = "jira",
                ContentRevision = 77,
                IsStable = true,
                ProjectLastSuccessfulRefreshAt =
                    new Dictionary<string, DateTimeOffset?>
                    {
                        ["FHIR"] = refreshedAt,
                    },
            });
        using EndpointFixture fixture = new(discoveryClient: discovery);

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync(
            "/processing/authoring/runs",
            new JiraAuthoringRunRequest(["FHIR-42"]));
        AuthoringRunResponse body =
            (await response.Content
                .ReadFromJsonAsync<AuthoringRunResponse>())!;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JiraProcessingSourceTicketRecord source =
            (await fixture.SourceStore.GetByKeyAsync(
                "FHIR-42",
                "fhir",
                CancellationToken.None))!;
        Assert.Equal(refreshedAt, source.SourceProjectLastSuccessfulRefreshAt);
        Assert.Equal(77, source.SourceContentRevision);
        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await fixture.Store.GetRunInputProvenanceAsync(
                body.Run.RunId));
        Assert.Equal(refreshedAt, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(77, provenance.ContentRevision);
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

    private static void AssertNoAuthoringWork(EndpointFixture fixture)
    {
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_items"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_input_provenance"));
    }

    private sealed class RejectingSnapshotWorkflowGuard : IAuthoringSnapshotWorkflowGuard
    {
        public Task EnsureSnapshotWorkflowAllowedAsync(
            AuthoringSnapshotWorkflowIntent intent,
            CancellationToken ct = default)
            => Task.FromException(AuthoringConflictException.ForCanonicalUnpublishedRestriction());
    }

    private sealed class EndpointFixture : IDisposable
    {
        private readonly string _directory;
        private readonly WebApplication _app;

        public EndpointFixture(
            int maxActiveAuthoringRuns = int.MaxValue,
            IJiraTicketDiscoveryClient? discoveryClient = null,
            Action<JiraProcessingOptions>? configure = null,
            IAuthoringSnapshotWorkflowGuard? snapshotWorkflowGuard = null)
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
            configure?.Invoke(options.Value);
            Coordinator = new JiraAuthoringRunCoordinator(
                Store,
                SourceStore,
                new JiraConfiguredTicketSelector(SourceStore, Matcher),
                new JiraProcessingFilterResolver(),
                options,
                processingOptions,
                snapshotWorkflowGuard);
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
                discoveryClient ?? new EmptyDiscoveryClient());
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
        public TestJiraTicketLabelMatcher Matcher { get; } = new();
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

        public int Scalar(string sql)
        {
            using SqliteConnection connection = new($"Data Source={DatabasePath};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
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

    private sealed class EmptyDiscoveryClient(Exception? failure = null) : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(
            ResolvedJiraProcessingFilters filters,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => failure is null
                ? Task.FromResult<JiraIssueSummaryEntry?>(null)
                : Task.FromException<JiraIssueSummaryEntry?>(failure);

        public Task MarkProcessedAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StaticDiscoveryClient(
        JiraIssueSummaryEntry ticket,
        SourceReadProvenance provenance) : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(
            ResolvedJiraProcessingFilters filters,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([]);

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.FromResult<JiraIssueSummaryEntry?>(
                string.Equals(key, ticket.Key, StringComparison.OrdinalIgnoreCase)
                    ? ticket
                    : null);

        public Task<JiraTicketDiscoveryItem?> GetTicketWithProvenanceAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.FromResult(
                string.Equals(key, ticket.Key, StringComparison.OrdinalIgnoreCase)
                    ? new JiraTicketDiscoveryItem(ticket, provenance)
                    : null);

        public Task MarkProcessedAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.CompletedTask;
    }
}
