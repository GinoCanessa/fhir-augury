using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task PreparedRunOpenDoesNotReadPlannerOrOverview()
    {
        AuthoringRunResponse authoritative = RunResponse(
            canRetry: false,
            canSupersede: false,
            completed: true);
        FakeAuthoringClient authoring = new()
        {
            PreparedOnlyRunId = "run-1",
            GetHandler = (_, _, _) => Task.FromResult(authoritative),
        };
        FakeSiteStore store = new(_root, new TicketWorkflowCatalog())
        {
            PreparedOnlyRunId = "run-1",
        };
        using TicketOperationsService service =
            CreateService(authoring, siteStore: store);
        using CancellationTokenSource cancellation = new();

        TicketRunDetails details = await service.OpenRunAsync(
            "PREPARE", "run-1", cancellation.Token);

        Assert.Same(authoritative, details.Response);
        Assert.Equal("prepare", details.Workflow.RouteKey);
        Assert.Equal(TicketSiteKind.Discussion, details.Workflow.SiteKind);
        Assert.Equal(ProcessorRunOutcome.Completed, details.Outcome.Processor);
        Assert.Equal(
            ("Preparer", "run-1", cancellation.Token),
            Assert.Single(authoring.DetailRequests));
        Assert.Equal(("prepare", "run-1"), Assert.Single(store.Reconstructions));
        Assert.Equal(0, authoring.ListCalls);
        Assert.Equal(0, authoring.DownloadCalls);
        Assert.Equal(0, store.PairReads);
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
        authoring.ListHandler = (serviceName, limit, _) =>
        {
            Assert.Equal("Preparer", serviceName);
            Assert.Equal(20, limit);
            return Task.FromResult(new AuthoringRunListResponse(
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
        };
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
        FakeAuthoringClient authoring = new()
        {
            PreparedOnlyRunId = "run-1",
        };
        authoring.GetHandler = (_, _, _) =>
            Task.FromResult(RunResponse(
                canRetry: false,
                canSupersede: false,
                completed: true));
        FakeSiteStore store = new(
            _root,
            new TicketWorkflowCatalog())
        {
            PreparedOnlyRunId = "run-1",
        };
        ReviewSiteCoordinates coordinates =
            store.GetCoordinates("prepare", "run-1");
        authoring.PreparedOnlyPairDirectory = coordinates.SnapshotPairDirectory;
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
        Assert.Equal(2, authoring.GetCalls);
        Assert.Equal(0, authoring.ListCalls);
        Assert.All(publisher.Requests, request =>
        {
            Assert.Same(pair, request.SnapshotPair);
            Assert.Equal(TicketSiteKind.Discussion, request.SiteKind);
            Assert.Equal(coordinates.SiteRoot, request.OutputRoot);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedPublicationUsesOnlyVerifiedPreparerCoordinates(bool cached)
    {
        FakeSiteStore store = new(_root, new TicketWorkflowCatalog())
        {
            PreparedOnlyRunId = "run-1",
        };
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "run-1");
        VerifiedAuthoringSnapshotPair pair = await CreateVerifiedPairAsync(
            coordinates.SnapshotPairDirectory, "Preparer", "run-1");
        FakeAuthoringClient authoring = new()
        {
            PreparedOnlyRunId = "run-1",
            PreparedOnlyPairDirectory = coordinates.SnapshotPairDirectory,
            GetHandler = (_, _, _) => Task.FromResult(RunResponse(
                canRetry: false, canSupersede: false, completed: true)),
            DownloadHandler = (_, _, _, _) => Task.FromResult(pair),
        };
        store.Pair = cached ? pair : null;
        TicketSiteManifest manifest = Manifest(coordinates, pair);
        store.Publication = new(coordinates, manifest, Reconstructed: false);
        FakePublisher publisher = new()
        {
            Handler = (_, _) => Task.FromResult(new TicketSitePublishResult(
                TicketSitePublishOutcome.Published,
                manifest,
                coordinates.SiteRoot,
                coordinates.SiteDirectory,
                TicketSiteFilters.None,
                [])),
        };
        using TicketOperationsService service = CreateService(
            authoring, siteStore: store, publisher: publisher);
        using CancellationTokenSource cancellation = new();

        TicketPublicationResult result = await service.PublishAsync(
            "PREPARE", "run-1", ct: cancellation.Token);

        Assert.Equal(TicketOperationDisposition.Succeeded, result.Disposition);
        Assert.True(result.VerifiedPairAvailable);
        Assert.Same(store.Publication, result.Publication);
        Assert.Equal("/review-sites/prepare/run-1/discussion/", result.Publication?.Coordinates.SiteUrl);
        Assert.Equal(
            ("Preparer", "run-1", cancellation.Token),
            Assert.Single(authoring.DetailRequests));
        Assert.Equal(cached ? 0 : 1, authoring.DownloadCalls);
        if (!cached)
        {
            Assert.Equal(
                ("Preparer", "run-1", coordinates.SnapshotPairDirectory, cancellation.Token),
                Assert.Single(authoring.DownloadRequests));
        }
        Assert.Equal(0, authoring.ListCalls);
        Assert.Empty(store.Reconstructions);
        Assert.Equal(1, store.PairReads);
        Assert.Equal(2, store.Revalidations);
        Assert.Equal(1, store.Validations);
        TicketSitePublishRequest request = Assert.Single(publisher.Requests);
        Assert.Same(pair, request.SnapshotPair);
        Assert.Equal(TicketSiteKind.Discussion, request.SiteKind);
        Assert.Equal("Tickets for Discussion", request.Title);
        Assert.Equal(coordinates.SiteRoot, request.OutputRoot);
        Assert.Equal(TicketSiteFilters.None, request.Filters);
        Assert.False(request.Force);
    }

    [Theory]
    [InlineData("incomplete", false)]
    [InlineData("missing-snapshot", false)]
    [InlineData("download-failed", false)]
    [InlineData("missing-file", false)]
    [InlineData("missing-file", true)]
    [InlineData("invalid-digest", false)]
    [InlineData("invalid-digest", true)]
    [InlineData("wrong-service", false)]
    [InlineData("wrong-service", true)]
    [InlineData("wrong-run", false)]
    [InlineData("wrong-run", true)]
    [InlineData("wrong-directory", false)]
    [InlineData("wrong-directory", true)]
    public async Task PublicationRejectsIncompleteMissingInvalidOrMismatchedSnapshot(
        string problem,
        bool cached)
    {
        FakeSiteStore store = new(_root, new TicketWorkflowCatalog())
        {
            PreparedOnlyRunId = "run-1",
        };
        ReviewSiteCoordinates coordinates = store.GetCoordinates("prepare", "run-1");
        FakeAuthoringClient authoring = new()
        {
            PreparedOnlyRunId = "run-1",
            PreparedOnlyPairDirectory = coordinates.SnapshotPairDirectory,
            GetHandler = (_, _, _) => Task.FromResult(RunResponse(
                canRetry: false,
                canSupersede: false,
                completed: problem != "incomplete")),
        };
        if (problem is "missing-snapshot" or "download-failed")
        {
            Exception failure = problem == "missing-snapshot"
                ? new AuthoringControlException(
                    HttpStatusCode.NotFound, "snapshot-missing", "No snapshot is available.")
                : new HttpRequestException(
                    "Snapshot download failed.", null, HttpStatusCode.ServiceUnavailable);
            authoring.DownloadHandler = (_, _, _, _) =>
                Task.FromException<VerifiedAuthoringSnapshotPair>(failure);
        }
        else if (problem != "incomplete")
        {
            string directory = problem == "wrong-directory"
                ? Path.Combine(_root, "unapproved-pair")
                : coordinates.SnapshotPairDirectory;
            VerifiedAuthoringSnapshotPair pair = await CreateVerifiedPairAsync(
                directory,
                problem == "wrong-service" ? "Planner" : "Preparer",
                problem == "wrong-run" ? "another-run" : "run-1");
            if (problem == "missing-file")
            {
                File.Delete(pair.DatabasePath);
            }
            else if (problem == "invalid-digest")
            {
                await File.WriteAllBytesAsync(pair.DatabasePath, "modified snapshot"u8.ToArray());
            }

            Task<VerifiedAuthoringSnapshotPair> ReadPair(CancellationToken ct) =>
                problem is "missing-file" or "invalid-digest"
                    ? new AuthoringSnapshotPairVerifier().VerifyReadyPairAsync(
                        "Preparer", "run-1", directory, ct)
                    : Task.FromResult(pair);
            if (cached)
            {
                store.PairHandler = async (_, ct) => await ReadPair(ct);
            }
            else
            {
                authoring.DownloadHandler = (_, _, _, ct) => ReadPair(ct);
            }
        }
        FakePublisher publisher = new();
        using TicketOperationsService service = CreateService(
            authoring, siteStore: store, publisher: publisher);

        TicketPublicationResult result = await service.PublishAsync("prepare", "run-1");

        Assert.Equal(
            problem == "incomplete"
                ? TicketOperationDisposition.NotAllowed
                : TicketOperationDisposition.Failed,
            result.Disposition);
        Assert.Null(result.Publication);
        Assert.Equal(problem.StartsWith("wrong-", StringComparison.Ordinal), result.VerifiedPairAvailable);
        Assert.Equal(1, authoring.GetCalls);
        Assert.Equal(0, authoring.ListCalls);
        Assert.Equal(problem == "incomplete" || cached ? 0 : 1, authoring.DownloadCalls);
        Assert.Equal(problem == "incomplete" ? 0 : 1, store.PairReads);
        Assert.Equal(0, store.Validations);
        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task DatabaseOnlyCompletionCannotDownloadOrPublish()
    {
        FakeAuthoringClient authoring = new()
        {
            PreparedOnlyRunId = "run-1",
        };
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
        Assert.Equal(0, authoring.ListCalls);
    }

    private TicketOperationsService CreateService(
        FakeAuthoringClient authoring,
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
            siteStore ?? new FakeSiteStore(_root, catalog),
            publisher ?? new FakePublisher(),
            catalog,
            new JiraTicketKeyParser(),
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

    private sealed class FakeAuthoringClient
        : IAuthoringControlClient
    {
        public string? PreparedOnlyRunId { get; init; }

        public string? PreparedOnlyPairDirectory { get; set; }

        public List<(string Service, string RunId, CancellationToken Token)>
            DetailRequests { get; } = [];

        public List<(string Service, string RunId, string Directory, CancellationToken Token)>
            DownloadRequests { get; } = [];

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
            Assert.Null(PreparedOnlyRunId);
            LastStartRequest = request;
            return StartHandler(serviceName, request!, ct);
        }

        public Task<AuthoringRunListResponse> ListAsync(
            string serviceName,
            int? limit,
            CancellationToken ct)
        {
            ListCalls++;
            Assert.Null(PreparedOnlyRunId);
            return ListHandler(serviceName, limit, ct);
        }

        public Task<AuthoringRunResponse> GetAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
        {
            GetCalls++;
            DetailRequests.Add((serviceName, runId, ct));
            AssertPreparedCoordinates(serviceName, runId);
            return GetHandler(serviceName, runId, ct);
        }

        public Task<AuthoringRetryResponse> RetryAsync(
            string serviceName,
            string runId,
            string itemId,
            CancellationToken ct)
        {
            RetryCalls++;
            Assert.Null(PreparedOnlyRunId);
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
            Assert.Null(PreparedOnlyRunId);
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
            DownloadRequests.Add((serviceName, runId, pairDirectory, ct));
            AssertPreparedCoordinates(serviceName, runId);
            if (PreparedOnlyRunId is not null)
            {
                Assert.True(DevUiPathGuard.PathsEqual(
                    Assert.IsType<string>(PreparedOnlyPairDirectory),
                    pairDirectory));
            }
            return DownloadHandler(
                serviceName,
                runId,
                pairDirectory,
                ct);
        }

        private void AssertPreparedCoordinates(string serviceName, string runId)
        {
            if (PreparedOnlyRunId is not null)
            {
                Assert.Equal("Preparer", serviceName);
                Assert.Equal(PreparedOnlyRunId, runId);
            }
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

        public List<TicketSitePublishRequest> Requests { get; } = [];

        public Task<TicketSitePublishResult> PublishAsync(
            TicketSitePublishRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            Requests.Add(request);
            return Handler(request, ct);
        }
    }

    private sealed class FakeSiteStore(
        string root,
        TicketWorkflowCatalog catalog) : IReviewSiteStore
    {
        public string? PreparedOnlyRunId { get; init; }

        public VerifiedAuthoringSnapshotPair? Pair { get; set; }

        public Func<ReviewSiteCoordinates, CancellationToken,
            Task<VerifiedAuthoringSnapshotPair?>>? PairHandler { get; set; }

        public ReviewSitePublication? Publication { get; set; }

        public Exception? ReconstructionException { get; set; }

        public List<(string Workflow, string RunId)> Reconstructions { get; } = [];

        public int PairReads { get; private set; }

        public int Revalidations { get; private set; }

        public int Validations { get; private set; }

        public void EnsureRoots()
        {
        }

        public ReviewSiteCoordinates GetCoordinates(
            string workflow,
            string runId)
        {
            AssertPreparedRoute(workflow, runId);
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
            Revalidations++;
            AssertPreparedRoute(coordinates.Workflow.RouteKey, coordinates.RunId);
        }

        public Task<VerifiedAuthoringSnapshotPair?>
            TryOpenVerifiedPairAsync(
                ReviewSiteCoordinates coordinates,
                CancellationToken ct = default)
        {
            PairReads++;
            AssertPreparedRoute(coordinates.Workflow.RouteKey, coordinates.RunId);
            return PairHandler is null
                ? Task.FromResult(Pair)
                : PairHandler(coordinates, ct);
        }

        public Task<ReviewSitePublication?> TryReconstructAsync(
            string workflow,
            string runId,
            CancellationToken ct = default)
        {
            Reconstructions.Add((workflow, runId));
            AssertPreparedRoute(workflow, runId);
            return ReconstructionException is null
                ? Task.FromResult(Publication)
                : Task.FromException<ReviewSitePublication?>(
                    ReconstructionException);
        }

        public Task<ReviewSitePublication>
            ValidatePublishedSiteAsync(
                ReviewSiteCoordinates coordinates,
                VerifiedAuthoringSnapshotPair pair,
                CancellationToken ct = default)
        {
            Validations++;
            AssertPreparedRoute(coordinates.Workflow.RouteKey, coordinates.RunId);
            if (PreparedOnlyRunId is not null)
            {
                Assert.Equal("Preparer", pair.ServiceName);
                Assert.Equal(PreparedOnlyRunId, pair.RunId);
                Assert.True(DevUiPathGuard.PathsEqual(
                    coordinates.SnapshotPairDirectory, pair.DirectoryPath));
            }
            return Task.FromResult(
                Publication ??
                throw new InvalidOperationException(
                    "Publication was not configured."));
        }

        private void AssertPreparedRoute(string workflow, string runId)
        {
            if (PreparedOnlyRunId is not null)
            {
                Assert.Equal("prepare", workflow);
                Assert.Equal(PreparedOnlyRunId, runId);
            }
        }
    }
}
