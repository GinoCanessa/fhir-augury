using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Publishing.Tickets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Tests;

public sealed class TicketOperationsServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"fhir-augury-operations-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public TicketOperationsServiceTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WorkflowCatalogHasExactPrepareAndPlanBindings()
    {
        TicketWorkflowCatalog catalog = new();

        TicketWorkflowDefinition prepare =
            catalog.Get("PREPARE");
        TicketWorkflowDefinition plan =
            catalog.Get("plan");

        Assert.Equal("Preparer", prepare.ProcessingServiceName);
        Assert.Equal(
            TicketSiteKind.Discussion,
            prepare.SiteKind);
        Assert.Equal(
            "Tickets for Discussion",
            prepare.SiteTitle);
        Assert.Equal("Planner", plan.ProcessingServiceName);
        Assert.Equal(TicketSiteKind.Applying, plan.SiteKind);
        Assert.Equal(
            "Tickets for Applying",
            plan.SiteTitle);
    }

    [Fact]
    public async Task OpenByRunIdUsesAuthoritativeDetailEndpoint()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (serviceName, runId, _) =>
        {
            Assert.Equal("Planner", serviceName);
            Assert.Equal("shared-run", runId);
            AuthoringRunResponse response = RunResponse(
                canRetry: false,
                canSupersede: false,
                completed: true);
            return Task.FromResult(response with
            {
                Run = response.Run with { RunId = runId },
                Items = response.Items
                    .Select(item => item with { RunId = runId })
                    .ToArray(),
            });
        };
        using TicketOperationsService service =
            CreateService(authoring);

        TicketRunDetails details =
            await service.OpenRunAsync(
                "plan",
                "shared-run");

        Assert.Equal("shared-run", details.Response.Run.RunId);
        Assert.Equal(1, authoring.GetCalls);
        Assert.Equal(0, authoring.ListCalls);
    }

    [Fact]
    public async Task OpenPreservesRunWhenPublicationReconstructionFails()
    {
        FakeAuthoringClient authoring = new();
        AuthoringRunResponse authoritative = RunResponse(
            canRetry: false,
            canSupersede: false,
            completed: true);
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(authoritative);
        FakeSiteStore store = new(
            _root,
            new TicketWorkflowCatalog())
        {
            ReconstructionException =
                new FileNotFoundException(
                    "The verified publication pair is missing."),
        };
        using TicketOperationsService service =
            CreateService(
                authoring,
                siteStore: store);

        TicketRunDetails details =
            await service.OpenRunAsync(
                "prepare",
                "run-1");

        Assert.Same(authoritative, details.Response);
        Assert.Equal(
            ProcessorRunOutcome.Completed,
            details.Outcome.Processor);
        Assert.Equal(
            PublicationOutcome.Failed,
            details.Outcome.Publication);
        Assert.Null(details.Publication);
        Assert.Contains(
            "missing",
            Assert.IsType<string>(
                details.PublicationError),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverviewReadsBothProcessorOwnedListsOnEveryLoad()
    {
        FakeAuthoringClient authoring = new();
        FakeReadinessClient readiness = new();
        using TicketOperationsService service =
            CreateService(authoring, readiness);

        TicketOperationsOverview first =
            await service.GetOverviewAsync();
        TicketOperationsOverview second =
            await service.GetOverviewAsync();

        Assert.Equal(2, first.Workflows.Count);
        Assert.Equal(2, second.Workflows.Count);
        Assert.Equal(4, authoring.ListCalls);
        Assert.Equal(2, readiness.GetCalls);
    }

    [Fact]
    public async Task StartNormalizesExplicitKeysIntoTypedRequest()
    {
        FakeAuthoringClient authoring = new();
        authoring.StartHandler = (_, _, _) =>
            Task.FromResult(new AuthoringStartResult(null));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketStartResult result = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Explicit,
                " fhir-2;FHIR-1,fhir-2 "));

        Assert.Equal(
            TicketOperationDisposition.NoCandidates,
            result.Disposition);
        PreparedTicketAuthoringRunRequest request =
            Assert.IsType<PreparedTicketAuthoringRunRequest>(
                authoring.LastStartRequest);
        Assert.Equal(
            ["FHIR-2", "FHIR-1"],
            request.TicketKeys);
    }

    [Fact]
    public async Task StructuredStartConflictSurfacesAuthoritativeRunLinks()
    {
        FakeAuthoringClient authoring = new();
        authoring.StartHandler = (_, _, _) =>
            Task.FromException<AuthoringStartResult>(
                new AuthoringControlException(
                    HttpStatusCode.Conflict,
                    "active-run-capacity",
                    "Two runs are active.",
                    relatedRunIds: ["run-2", "run-1"]));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketStartResult result = await service.StartAsync(
            new TicketRunStartRequest(
                "plan",
                TicketSelectionMode.Configured));

        Assert.Equal(
            TicketOperationDisposition.Conflict,
            result.Disposition);
        Assert.Equal(
            ["run-2", "run-1"],
            result.ConflictingRunIds);
        Assert.Equal(1, authoring.StartCalls);
    }

    [Fact]
    public async Task AmbiguousStartReconcilesWithoutReplayAndRequiresReview()
    {
        FakeAuthoringClient authoring = new();
        authoring.StartHandler = (_, _, _) =>
            Task.FromException<AuthoringStartResult>(
                new AuthoringMutationOutcomeUnknownException(
                    "start",
                    "Preparer",
                    null,
                    null,
                    new IOException("response lost")));
        authoring.ListHandler = (_, _, _) =>
            Task.FromResult(new AuthoringRunListResponse(
            [
                RunStatus(
                    "new-run",
                    "running",
                    _now.AddSeconds(1),
                    terminal: false),
                RunStatus(
                    "old-run",
                    "completed",
                    _now.AddSeconds(-1),
                    terminal: true),
            ],
            false));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketStartResult unknown = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));
        TicketStartResult blocked = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));

        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            unknown.Disposition);
        Assert.Equal(
            TicketReconciliationOutcome.Succeeded,
            unknown.Reconciliation?.Outcome);
        Assert.Equal(
            "new-run",
            Assert.Single(unknown.Candidates).RunId);
        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            blocked.Disposition);
        Assert.Equal(
            "new-run",
            Assert.Single(blocked.Candidates).RunId);
        Assert.Equal(1, authoring.StartCalls);
        Assert.Equal(1, authoring.ListCalls);

        service.AcknowledgeUnknownStartReview("prepare");
        authoring.StartHandler = (_, _, _) =>
            Task.FromResult(new AuthoringStartResult(null));
        TicketStartResult afterReview = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));
        Assert.Equal(
            TicketOperationDisposition.NoCandidates,
            afterReview.Disposition);
        Assert.Equal(2, authoring.StartCalls);
    }

    [Fact]
    public async Task UnknownStartTimeoutFailsReconciliationAndKeepsGate()
    {
        FakeAuthoringClient authoring = new();
        authoring.StartHandler = (_, _, _) =>
            Task.FromException<AuthoringStartResult>(
                new AuthoringMutationOutcomeUnknownException(
                    "start",
                    "Preparer",
                    null,
                    null,
                    new IOException("response lost")));
        authoring.ListHandler = (_, _, _) =>
            Task.FromException<AuthoringRunListResponse>(
                new TaskCanceledException("read timed out"));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketStartResult unknown = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));
        TicketStartResult blocked = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));

        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            unknown.Disposition);
        Assert.Equal(
            TicketReconciliationOutcome.Failed,
            unknown.Reconciliation?.Outcome);
        Assert.Contains(
            "timed out",
            Assert.IsType<string>(
                unknown.Reconciliation?.Error),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            blocked.Disposition);
        Assert.Equal(
            TicketReconciliationOutcome.Failed,
            blocked.Reconciliation?.Outcome);
        Assert.Equal(1, authoring.StartCalls);
        Assert.Equal(1, authoring.ListCalls);
    }

    [Fact]
    public async Task UnknownStartRecordsGateBeforeCallerCanceledRead()
    {
        FakeAuthoringClient authoring = new();
        using CancellationTokenSource cancellation = new();
        authoring.StartHandler = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<AuthoringStartResult>(
                new AuthoringMutationOutcomeUnknownException(
                    "start",
                    "Preparer",
                    null,
                    null,
                    new IOException("response lost")));
        };
        authoring.ListHandler = (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new AuthoringRunListResponse([], false));
        };
        using TicketOperationsService service =
            CreateService(authoring);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StartAsync(
                new TicketRunStartRequest(
                    "prepare",
                    TicketSelectionMode.Configured),
                cancellation.Token));
        TicketStartResult blocked = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));

        Assert.True(
            service.RequiresUnknownStartReview("prepare"));
        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            blocked.Disposition);
        Assert.Equal(
            TicketReconciliationOutcome.Failed,
            blocked.Reconciliation?.Outcome);
        Assert.Equal(1, authoring.StartCalls);
        Assert.Equal(1, authoring.ListCalls);
    }

    [Fact]
    public async Task AmbiguousRetryPerformsReadOnlyReconciliationOnly()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: true,
                canSupersede: true));
        authoring.RetryHandler = (_, _, _, _) =>
            Task.FromException<AuthoringRetryResponse>(
                new AuthoringMutationOutcomeUnknownException(
                    "retry",
                    "Preparer",
                    "run-1",
                    "item-1",
                    new HttpRequestException("response lost")));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult result =
            await service.RetryItemAsync(
                "prepare",
                "run-1",
                "item-1");

        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            result.Disposition);
        Assert.NotNull(result.ReconciledRun);
        Assert.Equal(
            TicketReconciliationOutcome.Succeeded,
            result.Reconciliation?.Outcome);
        Assert.Equal(1, authoring.RetryCalls);
        Assert.Equal(2, authoring.GetCalls);
    }

    [Fact]
    public async Task AmbiguousSupersedeIsNotReplayed()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: true,
                canSupersede: true));
        authoring.SupersedeHandler = (_, _, _, _, _) =>
            Task.FromException<AuthoringItemSupersedeResult>(
                new AuthoringMutationOutcomeUnknownException(
                    "supersede",
                    "Preparer",
                    "run-1",
                    "item-1",
                    new IOException("response lost")));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult result =
            await service.SupersedeItemAsync(
                "prepare",
                "run-1",
                "item-1",
                "obsolete");

        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            result.Disposition);
        Assert.Equal(1, authoring.SupersedeCalls);
        Assert.Equal(2, authoring.GetCalls);
    }

    [Fact]
    public async Task UnknownMutationTimeoutDoesNotReturnPremutationRun()
    {
        FakeAuthoringClient authoring = new();
        int getCalls = 0;
        authoring.GetHandler = (_, _, _) =>
        {
            getCalls++;
            return getCalls == 1
                ? Task.FromResult(RunResponse(
                    canRetry: true,
                    canSupersede: false))
                : Task.FromException<AuthoringRunResponse>(
                    new TaskCanceledException(
                        "reconciliation timed out"));
        };
        authoring.RetryHandler = (_, _, _, _) =>
            Task.FromException<AuthoringRetryResponse>(
                new AuthoringMutationOutcomeUnknownException(
                    "retry",
                    "Preparer",
                    "run-1",
                    "item-1",
                    new HttpRequestException("response lost")));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult result =
            await service.RetryItemAsync(
                "prepare",
                "run-1",
                "item-1");

        Assert.Equal(
            TicketOperationDisposition.OutcomeUnknown,
            result.Disposition);
        Assert.Null(result.ReconciledRun);
        Assert.Equal(
            TicketReconciliationOutcome.Failed,
            result.Reconciliation?.Outcome);
        Assert.Contains(
            "timed out",
            Assert.IsType<string>(
                result.Reconciliation?.Error),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, authoring.GetCalls);
        Assert.Equal(1, authoring.RetryCalls);
    }

    [Fact]
    public async Task LatestServerCapabilityIsRequiredBeforeMutation()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: false,
                canSupersede: false));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult retry =
            await service.RetryItemAsync(
                "prepare",
                "run-1",
                "item-1");
        TicketItemMutationResult supersede =
            await service.SupersedeItemAsync(
                "prepare",
                "run-1",
                "item-1",
                "obsolete");

        Assert.Equal(
            TicketOperationDisposition.NotAllowed,
            retry.Disposition);
        Assert.Equal(
            TicketOperationDisposition.NotAllowed,
            supersede.Disposition);
        Assert.Equal(0, authoring.RetryCalls);
        Assert.Equal(0, authoring.SupersedeCalls);
    }

    [Fact]
    public async Task ConflictRefreshesCompleteRunAfterRejectedMutation()
    {
        FakeAuthoringClient authoring = new();
        int getCalls = 0;
        authoring.GetHandler = (_, _, _) =>
        {
            getCalls++;
            return Task.FromResult(RunResponse(
                canRetry: getCalls == 1,
                canSupersede: false));
        };
        authoring.RetryHandler = (_, _, _, _) =>
            Task.FromException<AuthoringRetryResponse>(
                new AuthoringControlException(
                    HttpStatusCode.Conflict,
                    "state-changed",
                    "Item changed."));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult result =
            await service.RetryItemAsync(
                "prepare",
                "run-1",
                "item-1");

        Assert.Equal(
            TicketOperationDisposition.Conflict,
            result.Disposition);
        Assert.Equal(2, authoring.GetCalls);
        Assert.False(
            Assert.Single(result.ReconciledRun!.Items)
                .AllowedActions!.CanRetryNow);
    }

    [Fact]
    public async Task ConflictReconciliationFailureDoesNotReturnPremutationRun()
    {
        FakeAuthoringClient authoring = new();
        int getCalls = 0;
        authoring.GetHandler = (_, _, _) =>
        {
            getCalls++;
            return getCalls == 1
                ? Task.FromResult(RunResponse(
                    canRetry: true,
                    canSupersede: false))
                : Task.FromException<AuthoringRunResponse>(
                    new HttpRequestException(
                        "refresh unavailable"));
        };
        authoring.RetryHandler = (_, _, _, _) =>
            Task.FromException<AuthoringRetryResponse>(
                new AuthoringControlException(
                    HttpStatusCode.Conflict,
                    "state-changed",
                    "Item changed."));
        using TicketOperationsService service =
            CreateService(authoring);

        TicketItemMutationResult result =
            await service.RetryItemAsync(
                "prepare",
                "run-1",
                "item-1");

        Assert.Equal(
            TicketOperationDisposition.Conflict,
            result.Disposition);
        Assert.Null(result.ReconciledRun);
        Assert.Equal(
            TicketReconciliationOutcome.Failed,
            result.Reconciliation?.Outcome);
        Assert.Contains(
            "refresh unavailable",
            Assert.IsType<string>(
                result.Reconciliation?.Error),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, authoring.GetCalls);
    }

    [Fact]
    public async Task PerCircuitMutationGateRejectsDoubleSubmit()
    {
        FakeAuthoringClient authoring = new();
        TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        authoring.StartHandler = async (_, _, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new AuthoringStartResult(null);
        };
        using TicketOperationsService service =
            CreateService(authoring);

        Task<TicketStartResult> first = service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TicketStartResult second = await service.StartAsync(
            new TicketRunStartRequest(
                "prepare",
                TicketSelectionMode.Configured));

        Assert.Equal(
            TicketOperationDisposition.Busy,
            second.Disposition);
        Assert.Equal(1, authoring.StartCalls);
        release.SetResult();
        Assert.Equal(
            TicketOperationDisposition.NoCandidates,
            (await first).Disposition);
    }

    [Fact]
    public async Task PublicationFailureRetainsAndReusesVerifiedPair()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: false,
                canSupersede: false,
                completed: true));
        FakeSiteStore store = new(
            _root,
            new TicketWorkflowCatalog());
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-1");
        VerifiedAuthoringSnapshotPair pair =
            await CreateVerifiedPairAsync(
                coordinates.SnapshotPairDirectory,
                "Preparer",
                "run-1");
        authoring.DownloadHandler = (_, _, _, _) =>
        {
            store.Pair = pair;
            return Task.FromResult(pair);
        };
        FakePublisher publisher = new();
        publisher.Handler = (_, _) =>
        {
            if (publisher.Calls == 1)
            {
                return Task.FromException<TicketSitePublishResult>(
                    new TicketSitePublishException(
                        TicketSitePublishFailure.Publication,
                        "disk full"));
            }
            TicketSiteManifest manifest =
                Manifest(coordinates, pair);
            return Task.FromResult(new TicketSitePublishResult(
                TicketSitePublishOutcome.Published,
                manifest,
                coordinates.SiteRoot,
                coordinates.SiteDirectory,
                TicketSiteFilters.None,
                []));
        };
        store.Publication = new ReviewSitePublication(
            coordinates,
            Manifest(coordinates, pair),
            Reconstructed: false);
        using TicketOperationsService service = CreateService(
            authoring,
            siteStore: store,
            publisher: publisher);

        TicketPublicationResult failed =
            await service.PublishAsync(
                "prepare",
                "run-1");
        TicketPublicationResult retried =
            await service.PublishAsync(
                "prepare",
                "run-1");

        Assert.Equal(
            TicketOperationDisposition.Failed,
            failed.Disposition);
        Assert.True(failed.VerifiedPairAvailable);
        Assert.Equal(
            TicketOperationDisposition.Succeeded,
            retried.Disposition);
        Assert.Equal(1, authoring.DownloadCalls);
        Assert.Equal(2, publisher.Calls);
    }

    [Fact]
    public async Task DatabaseOnlyCompletionCannotDownloadOrPublish()
    {
        FakeAuthoringClient authoring = new();
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: false,
                canSupersede: false,
                completed: true,
                databaseOnly: true));
        FakePublisher publisher = new();
        using TicketOperationsService service =
            CreateService(
                authoring,
                publisher: publisher);

        TicketPublicationResult result =
            await service.PublishAsync(
                "prepare",
                "run-1");

        Assert.Equal(
            TicketOperationDisposition.NotAllowed,
            result.Disposition);
        Assert.Equal(0, authoring.DownloadCalls);
        Assert.Equal(0, publisher.Calls);
    }

    private TicketOperationsService CreateService(
        FakeAuthoringClient authoring,
        FakeReadinessClient? readiness = null,
        IReviewSiteStore? siteStore = null,
        ITicketSitePublisher? publisher = null)
    {
        TicketWorkflowCatalog catalog = new();
        DevUiOptions options = new()
        {
            RecentRunLimit = 20,
            CacheRoot = Path.Combine(_root, "cache"),
            SnapshotCacheRoot =
                Path.Combine(_root, "cache", "snapshots"),
            ReviewSitesRoot =
                Path.Combine(_root, "cache", "sites"),
        };
        return new TicketOperationsService(
            authoring,
            readiness ?? new FakeReadinessClient(),
            siteStore ?? new FakeSiteStore(_root, catalog),
            publisher ?? new FakePublisher(),
            catalog,
            new JiraTicketKeyParser(),
            new ReadinessEvaluator(),
            new RunOutcomeClassifier(),
            Options.Create(options),
            new FixedTimeProvider(_now),
            NullLogger<TicketOperationsService>.Instance);
    }

    private AuthoringRunResponse RunResponse(
        bool canRetry,
        bool canSupersede,
        bool completed = false,
        bool databaseOnly = false)
    {
        string status = completed
            ? databaseOnly
                ? "completed-database-only"
                : "completed"
            : "error";
        AuthoringRunStatus run = RunStatus(
            "run-1",
            status,
            _now.AddMinutes(-1),
            completed,
            databaseOnly);
        AuthoringRunItemStatus item = new(
            "item-1",
            "run-1",
            "FHIR-1",
            "ticket",
            "revision",
            completed ? "completed" : "error",
            null,
            completed ? "receipt-1" : null,
            1,
            _now.AddMinutes(-1),
            _now.AddMinutes(-1),
            completed ? _now : null,
            completed ? null : "failure",
            AttemptsRemaining: completed ? 0 : 1,
            CurrentError: completed ? null : "failure",
            AllowedActions: new AuthoringAllowedActions(
                canRetry,
                canSupersede));
        return new AuthoringRunResponse(run, [item]);
    }

    private static AuthoringRunStatus RunStatus(
        string runId,
        string status,
        DateTimeOffset createdAt,
        bool terminal,
        bool databaseOnly = false) => new(
            runId,
            "jira-fhir",
            1,
            status,
            databaseOnly,
            1,
            terminal ? 1 : 0,
            status == "error" ? 1 : 0,
            createdAt,
            createdAt,
            terminal ? createdAt.AddMinutes(1) : null,
            null,
            RetryableErrorItems: status == "error" ? 1 : 0,
            State: new AuthoringRunStateInfo(
                terminal,
                status == "error"));

    private static async Task<VerifiedAuthoringSnapshotPair>
        CreateVerifiedPairAsync(
            string directory,
            string serviceName,
            string runId)
    {
        Directory.CreateDirectory(directory);
        byte[] databaseBytes = "verified snapshot"u8.ToArray();
        string databaseSha = Hash(databaseBytes);
        AuthoringSnapshotDescriptor descriptor = new(
            "jira-fhir",
            runId,
            "snapshot-1",
            1,
            1,
            1,
            databaseSha,
            databaseBytes.LongLength,
            1,
            1,
            new Dictionary<string, long>(),
            "snapshot.db",
            DateTimeOffset.UtcNow);
        byte[] descriptorBytes =
            JsonSerializer.SerializeToUtf8Bytes(
                descriptor,
                JsonOptions);
        AuthoringSnapshotPairManifest ready = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            runId,
            descriptor.SnapshotId,
            "snapshot.db.descriptor.json",
            "snapshot.db",
            databaseBytes.LongLength,
            Hash(descriptorBytes),
            databaseSha);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, "snapshot.db"),
            databaseBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(
                directory,
                "snapshot.db.descriptor.json"),
            descriptorBytes);
        await File.WriteAllTextAsync(
            Path.Combine(
                directory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            JsonSerializer.Serialize(ready, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                runId,
                directory);
    }

    private static TicketSiteManifest Manifest(
        ReviewSiteCoordinates coordinates,
        VerifiedAuthoringSnapshotPair pair) => new(
            "preparer",
            pair.Descriptor.ProcessorKind,
            pair.RunId,
            pair.SnapshotId,
            pair.Descriptor.AuthoringEpoch,
            pair.Descriptor.Sequence,
            pair.Descriptor.SchemaVersion,
            pair.Descriptor.Sha256,
            pair.Descriptor.SizeBytes,
            pair.Descriptor.Sha256,
            pair.Descriptor.SizeBytes,
            1,
            1,
            new Dictionary<string, long>(),
            new TicketSiteManifestFilters(null, null, null),
            coordinates.Workflow.SiteTitle,
            "assets",
            "build",
            coordinates.SiteDirectory,
            DateTimeOffset.UtcNow);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private sealed class FixedTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeReadinessClient
        : IOrchestratorReadinessClient
    {
        public int GetCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public Task<ServicesStatusResponse> GetAsync(
            CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(Response());
        }

        public Task<ServicesStatusResponse> RefreshAsync(
            CancellationToken ct = default)
        {
            RefreshCalls++;
            return Task.FromResult(Response());
        }

        private static ServicesStatusResponse Response()
        {
            DateTimeOffset checkedAt =
                DateTimeOffset.UtcNow;
            return new ServicesStatusResponse(
            [
                Health(
                    "Orchestrator",
                    "orchestrator",
                    checkedAt),
                Health(
                    "Preparer",
                    "processing",
                    checkedAt,
                    ["Jira"]),
                Health(
                    "Planner",
                    "processing",
                    checkedAt,
                    ["Jira", "GitHub"]),
                Health("Jira", "source", checkedAt),
                Health("GitHub", "source", checkedAt),
            ],
            checkedAt);
        }

        private static ServiceHealthInfo Health(
            string name,
            string kind,
            DateTimeOffset checkedAt,
            List<string>? required = null) => new()
            {
                Name = name,
                ServiceKind = kind,
                Status = "healthy",
                Enabled = true,
                Configured = true,
                CheckedAt = checkedAt,
                ProcessingIsRunning =
                    kind == "processing" ? true : null,
                RequiredServices = required ?? [],
            };
    }

    private sealed class FakeAuthoringClient
        : IAuthoringControlClient
    {
        public Func<
            string,
            object,
            CancellationToken,
            Task<AuthoringStartResult>> StartHandler { get; set; } =
            (_, _, _) => Task.FromResult(
                new AuthoringStartResult(null));

        public Func<
            string,
            int?,
            CancellationToken,
            Task<AuthoringRunListResponse>> ListHandler { get; set; } =
            (_, _, _) => Task.FromResult(
                new AuthoringRunListResponse([], false));

        public Func<
            string,
            string,
            CancellationToken,
            Task<AuthoringRunResponse>> GetHandler { get; set; } =
            (_, _, _) => Task.FromException<AuthoringRunResponse>(
                new InvalidOperationException(
                    "Get handler was not configured."));

        public Func<
            string,
            string,
            string,
            CancellationToken,
            Task<AuthoringRetryResponse>> RetryHandler { get; set; } =
            (_, _, itemId, _) => Task.FromResult(
                new AuthoringRetryResponse(itemId, true));

        public Func<
            string,
            string,
            string,
            string,
            CancellationToken,
            Task<AuthoringItemSupersedeResult>>
            SupersedeHandler { get; set; } =
            (_, runId, itemId, reason, _) => Task.FromResult(
                new AuthoringItemSupersedeResult(
                    runId,
                    itemId,
                    "superseded",
                    reason));

        public Func<
            string,
            string,
            string,
            CancellationToken,
            Task<VerifiedAuthoringSnapshotPair>>
            DownloadHandler { get; set; } =
            (_, _, _, _) =>
                Task.FromException<VerifiedAuthoringSnapshotPair>(
                    new InvalidOperationException(
                        "Download handler was not configured."));

        public int StartCalls { get; private set; }

        public int ListCalls { get; private set; }

        public int GetCalls { get; private set; }

        public int RetryCalls { get; private set; }

        public int SupersedeCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public object? LastStartRequest { get; private set; }

        public Task<AuthoringStartResult> StartAsync<TRequest>(
            string serviceName,
            TRequest request,
            CancellationToken ct)
        {
            StartCalls++;
            LastStartRequest = request;
            return StartHandler(serviceName, request!, ct);
        }

        public Task<AuthoringRunListResponse> ListAsync(
            string serviceName,
            int? limit,
            CancellationToken ct)
        {
            ListCalls++;
            return ListHandler(serviceName, limit, ct);
        }

        public Task<AuthoringRunResponse> GetAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
        {
            GetCalls++;
            return GetHandler(serviceName, runId, ct);
        }

        public Task<AuthoringRetryResponse> RetryAsync(
            string serviceName,
            string runId,
            string itemId,
            CancellationToken ct)
        {
            RetryCalls++;
            return RetryHandler(
                serviceName,
                runId,
                itemId,
                ct);
        }

        public Task<AuthoringItemSupersedeResult> SupersedeAsync(
            string serviceName,
            string runId,
            string itemId,
            string reason,
            CancellationToken ct)
        {
            SupersedeCalls++;
            return SupersedeHandler(
                serviceName,
                runId,
                itemId,
                reason,
                ct);
        }

        public Task<VerifiedAuthoringSnapshotPair>
            DownloadSnapshotPairAsync(
                string serviceName,
                string runId,
                string pairDirectory,
                CancellationToken ct)
        {
            DownloadCalls++;
            return DownloadHandler(
                serviceName,
                runId,
                pairDirectory,
                ct);
        }
    }

    private sealed class FakePublisher : ITicketSitePublisher
    {
        public Func<
            TicketSitePublishRequest,
            CancellationToken,
            Task<TicketSitePublishResult>> Handler { get; set; } =
            (_, _) => Task.FromException<TicketSitePublishResult>(
                new InvalidOperationException(
                    "Publisher handler was not configured."));

        public int Calls { get; private set; }

        public Task<TicketSitePublishResult> PublishAsync(
            TicketSitePublishRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            return Handler(request, ct);
        }
    }

    private sealed class FakeSiteStore(
        string root,
        TicketWorkflowCatalog catalog) : IReviewSiteStore
    {
        public VerifiedAuthoringSnapshotPair? Pair { get; set; }

        public ReviewSitePublication? Publication { get; set; }

        public Exception? ReconstructionException { get; set; }

        public void EnsureRoots()
        {
        }

        public ReviewSiteCoordinates GetCoordinates(
            string workflow,
            string runId)
        {
            TicketWorkflowDefinition definition =
                catalog.Get(workflow);
            string pairDirectory = Path.Combine(
                root,
                "pairs",
                definition.RouteKey,
                runId);
            string siteRoot = Path.Combine(
                root,
                "sites",
                definition.RouteKey,
                runId);
            string siteDirectory =
                Path.Combine(siteRoot, definition.SiteFolder);
            return new ReviewSiteCoordinates(
                definition,
                runId,
                pairDirectory,
                siteRoot,
                siteDirectory,
                Path.Combine(
                    siteDirectory,
                    TicketSiteManifest.FileName),
                $"/review-sites/{definition.RouteKey}/{runId}/{definition.SiteFolder}/");
        }

        public void RevalidateForPublication(
            ReviewSiteCoordinates coordinates)
        {
        }

        public Task<VerifiedAuthoringSnapshotPair?>
            TryOpenVerifiedPairAsync(
                ReviewSiteCoordinates coordinates,
                CancellationToken ct = default) =>
            Task.FromResult(Pair);

        public Task<ReviewSitePublication?> TryReconstructAsync(
            string workflow,
            string runId,
            CancellationToken ct = default) =>
            ReconstructionException is null
                ? Task.FromResult(Publication)
                : Task.FromException<ReviewSitePublication?>(
                    ReconstructionException);

        public Task<ReviewSitePublication>
            ValidatePublishedSiteAsync(
                ReviewSiteCoordinates coordinates,
                VerifiedAuthoringSnapshotPair pair,
                CancellationToken ct = default) =>
            Task.FromResult(
                Publication ??
                throw new InvalidOperationException(
                    "Publication was not configured."));
    }
}
