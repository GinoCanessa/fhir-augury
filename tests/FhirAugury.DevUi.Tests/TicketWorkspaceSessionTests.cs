using System.Collections.Concurrent;
using System.Net;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Tests;

public sealed class TicketWorkspaceSessionTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProbeTime = InitialTime.AddMinutes(-5);

    [Fact]
    public async Task InitialStateIsCatalogBackedAndDoesNotRead()
    {
        await using Fixture fixture = new();

        TicketWorkspaceSnapshot snapshot = fixture.Session.Snapshot;

        Assert.Same(snapshot, fixture.Session.Snapshot);
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(["plan", "prepare"], snapshot.Workflows.Keys.Order());
        Assert.Equal(WorkspaceObservationPhase.Pending, snapshot.Readiness.Phase);
        Assert.Null(snapshot.Readiness.AttemptStartedAt);
        Assert.Null(snapshot.Readiness.AttemptCompletedAt);
        Assert.False(snapshot.Readiness.HasLastSuccess);
        foreach (TicketWorkflowDefinition workflow in fixture.Catalog.Workflows)
        {
            TicketWorkflowWorkspace workspace = snapshot.Workflows[workflow.RouteKey];
            Assert.Same(workflow, workspace.Workflow);
            Assert.Equal(WorkspaceObservationPhase.Pending, workspace.History.Phase);
            Assert.Null(workspace.History.AttemptStartedAt);
            Assert.Null(workspace.History.AttemptCompletedAt);
            Assert.False(workspace.History.HasLastSuccess);
            Assert.Null(workspace.History.Failure);
        }
        Assert.Same(snapshot.Workflows["prepare"], snapshot.Workflows["PREPARE"]);
        IDictionary<string, TicketWorkflowWorkspace> workflows = snapshot.Workflows;
        Assert.True(workflows.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => workflows.Clear());
        Assert.Empty(fixture.Authoring.Requests);
        Assert.Empty(fixture.Readiness.Requests);
        Assert.Empty(fixture.Changes.Snapshots);
    }

    [Fact]
    public async Task PreparerCompletesWhilePlannerIsUnresolved()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();

        fixture.ReadinessRead.Response.SetResult(Services());
        fixture.PreparerRead.Response.SetResult(History("prepared-1"));
        TicketWorkspaceSnapshot prepared = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.IsCurrentSuccess &&
                HasRun(snapshot, "prepare", "prepared-1"));

        Assert.True(prepared.ReadinessFor("prepare").CurrentValue?.CanStart);
        Assert.True(prepared.Workflows["plan"].History.IsLoading);
        Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);
        Assert.False(fixture.PlannerRead.Finished.Task.IsCompleted);

        ControlledRead<AuthoringRunListResponse> refresh =
            fixture.Authoring.Preparer.Enqueue();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.Session.RefreshHistory("PREPARE");
        await refresh.Entered.Task.WaitAsync(HangGuard);
        Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);

        refresh.Response.SetResult(History("prepared-2"));
        TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, "prepare", "prepared-2"));

        Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);
        Assert.False(fixture.PlannerRead.Finished.Task.IsCompleted);
        Assert.True(refreshed.Workflows["plan"].History.IsLoading);
        Assert.Same(prepared.Readiness, refreshed.Readiness);
        Assert.Same(prepared.Workflows["plan"], refreshed.Workflows["plan"]);
        Assert.Same(
            prepared.Workflows["prepare"].Workflow,
            refreshed.Workflows["prepare"].Workflow);
        Assert.True(refreshed.Revision > prepared.Revision);
        Assert.True(HasRun(prepared, "prepare", "prepared-1"));
        Assert.Equal(2, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        Assert.False(Assert.Single(fixture.Readiness.Requests).Refresh);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, WorkspaceObservationPhase.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, WorkspaceObservationPhase.Failed)]
    public async Task ReadinessCannotGateHistoryCompletion(
        HttpStatusCode statusCode,
        WorkspaceObservationPhase expectedPhase)
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();

        fixture.PreparerRead.Response.SetResult(History("prepared"));
        fixture.PlannerRead.Response.SetResult(History("planned"));
        TicketWorkspaceSnapshot histories = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, "prepare", "prepared") &&
                HasRun(snapshot, "plan", "planned"));

        Assert.True(histories.Readiness.IsLoading);
        Assert.False(fixture.ReadinessRead.Response.Task.IsCompleted);
        fixture.ReadinessRead.Response.SetException(
            new HttpRequestException("Readiness failed.", null, statusCode));
        TicketWorkspaceSnapshot failed = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.Phase == expectedPhase);

        Assert.False(failed.Readiness.HasLastSuccess);
        Assert.Null(failed.Readiness.CurrentValue);
        Assert.Equal(statusCode, failed.Readiness.Failure?.StatusCode);
        Assert.Same(histories.Workflows["prepare"], failed.Workflows["prepare"]);
        Assert.Same(histories.Workflows["plan"], failed.Workflows["plan"]);
        Assert.True(failed.Revision > histories.Revision);
        Assert.Empty(fixture.SessionLogger.Entries);
    }

    [Fact]
    public async Task PreparerFailureDoesNotHideOtherObservations()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();

        fixture.ReadinessRead.Response.SetResult(Services());
        fixture.PlannerRead.Response.SetResult(History("planned"));
        fixture.PreparerRead.Response.SetException(new AuthoringControlException(
            HttpStatusCode.Forbidden, "preparer-denied", "Private detail."));
        TicketWorkspaceSnapshot snapshot = await fixture.Changes.WaitForAsync(
            value => value.Readiness.IsCurrentSuccess &&
                HasRun(value, "plan", "planned") &&
                value.Workflows["prepare"].History.Phase == WorkspaceObservationPhase.Failed);

        WorkspaceObservation<AuthoringRunListResponse> preparer =
            snapshot.Workflows["prepare"].History;
        Assert.False(preparer.HasLastSuccess);
        Assert.Null(preparer.CurrentValue);
        Assert.Equal(WorkspaceReadFailureReason.Authorization, preparer.Failure?.Reason);
        Assert.Equal("prepare", preparer.Failure?.Workflow);
        Assert.Equal("preparer-denied", preparer.Failure?.ErrorCode);
        Assert.True(snapshot.ReadinessFor("prepare").CurrentValue?.CanStart);
        Assert.Single(fixture.ReaderLogger.Entries);
        Assert.Empty(fixture.SessionLogger.Entries);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, WorkspaceObservationPhase.Unavailable)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, WorkspaceObservationPhase.Unavailable)]
    [InlineData(false, HttpStatusCode.Unauthorized, WorkspaceObservationPhase.Failed)]
    [InlineData(true, HttpStatusCode.Unauthorized, WorkspaceObservationPhase.Failed)]
    public async Task RefreshPreservesLastSuccessUntilRecovery(
        bool empty,
        HttpStatusCode statusCode,
        WorkspaceObservationPhase expectedPhase)
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        List<AuthoringRunStatus> originalRuns = empty ? [] : [Run("original")];
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset firstSucceededAt = fixture.Time.GetUtcNow();
        fixture.PreparerRead.Response.SetResult(new(originalRuns, true));
        TicketWorkspaceSnapshot successful = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Workflows["prepare"].History.IsCurrentSuccess);
        WorkspaceObservation<AuthoringRunListResponse> first =
            successful.Workflows["prepare"].History;
        AuthoringRunListResponse original = Assert.IsType<AuthoringRunListResponse>(
            first.CurrentValue);
        Assert.Equal(InitialTime, first.AttemptStartedAt);
        Assert.Equal(firstSucceededAt, first.AttemptCompletedAt);
        Assert.Equal(firstSucceededAt, first.LastSucceededAt);
        Assert.Equal(empty ? 0 : 1, original.Runs.Count);
        originalRuns.Clear();
        Assert.Equal(empty ? 0 : 1, original.Runs.Count);

        ControlledRead<AuthoringRunListResponse> refresh =
            fixture.Authoring.Preparer.Enqueue();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset refreshStartedAt = fixture.Time.GetUtcNow();
        fixture.Session.RefreshHistory("prepare");
        TicketWorkspaceSnapshot refreshing = fixture.Session.Snapshot;
        WorkspaceObservation<AuthoringRunListResponse> loading =
            refreshing.Workflows["prepare"].History;
        Assert.True(loading.IsLoading);
        Assert.True(loading.IsStale);
        Assert.True(loading.HasLastSuccess);
        Assert.Null(loading.CurrentValue);
        Assert.Null(loading.Failure);
        Assert.Null(loading.AttemptCompletedAt);
        Assert.Equal(refreshStartedAt, loading.AttemptStartedAt);
        Assert.Equal(firstSucceededAt, loading.LastSucceededAt);
        Assert.Same(original, loading.LastSuccessfulValue);
        await refresh.Entered.Task.WaitAsync(HangGuard);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset failedAt = fixture.Time.GetUtcNow();
        refresh.Response.SetException(new HttpRequestException(
            "Refresh failed.", null, statusCode));
        TicketWorkspaceSnapshot failed = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Workflows["prepare"].History.Phase == expectedPhase);
        WorkspaceObservation<AuthoringRunListResponse> stale =
            failed.Workflows["prepare"].History;
        Assert.True(stale.IsStale);
        Assert.True(stale.HasLastSuccess);
        Assert.Null(stale.CurrentValue);
        Assert.Equal(firstSucceededAt, stale.LastSucceededAt);
        Assert.Equal(refreshStartedAt, stale.AttemptStartedAt);
        Assert.Equal(failedAt, stale.AttemptCompletedAt);
        Assert.Same(original, stale.LastSuccessfulValue);
        Assert.True(original.Truncated);
        Assert.Equal(empty ? 0 : 1, original.Runs.Count);
        Assert.Equal(statusCode, stale.Failure?.StatusCode);

        ControlledRead<AuthoringRunListResponse> recovery =
            fixture.Authoring.Preparer.Enqueue();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.Session.RefreshHistory("prepare");
        WorkspaceObservation<AuthoringRunListResponse> recovering =
            fixture.Session.Snapshot.Workflows["prepare"].History;
        Assert.True(recovering.IsStale);
        Assert.Null(recovering.Failure);
        Assert.Same(original, recovering.LastSuccessfulValue);
        Assert.Equal(firstSucceededAt, recovering.LastSucceededAt);
        await recovery.Entered.Task.WaitAsync(HangGuard);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset recoveredAt = fixture.Time.GetUtcNow();
        recovery.Response.SetResult(History("recovered"));
        TicketWorkspaceSnapshot recovered = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, "prepare", "recovered"));
        WorkspaceObservation<AuthoringRunListResponse> current =
            recovered.Workflows["prepare"].History;
        Assert.False(current.IsStale);
        Assert.Null(current.Failure);
        Assert.Equal(recoveredAt, current.LastSucceededAt);
        Assert.Equal(recoveredAt, current.AttemptCompletedAt);
        Assert.Equal(recoveredAt.AddMinutes(-1), current.AttemptStartedAt);
        Assert.False(current.CurrentValue?.Truncated);
        Assert.NotSame(original, current.CurrentValue);
        Assert.True(first.IsCurrentSuccess);
        Assert.True(loading.IsLoading);
        Assert.Equal(expectedPhase, stale.Phase);
        Assert.Equal(firstSucceededAt, first.LastSucceededAt);
        Assert.Same(successful.Readiness, recovered.Readiness);
        Assert.Same(successful.Workflows["plan"], recovered.Workflows["plan"]);
        Assert.True(successful.Revision < refreshing.Revision);
        Assert.True(refreshing.Revision < failed.Revision);
        Assert.True(failed.Revision < recovered.Revision);
    }

    [Fact]
    public async Task ReadinessRefreshRetainsSuccessAndOriginalProbeTimes()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        fixture.ReadinessRead.Response.SetResult(Services());
        TicketWorkspaceSnapshot successful = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.IsCurrentSuccess);
        Assert.Equal(InitialTime, successful.Readiness.LastSucceededAt);
        Assert.Equal(ProbeTime, successful.Readiness.CurrentValue?.LastCheckedAt);

        ControlledRead<ServicesStatusResponse> refresh = fixture.Readiness.Refresh.Enqueue();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.Session.RefreshAll();
        await refresh.Entered.Task.WaitAsync(HangGuard);
        TicketWorkspaceSnapshot loading = fixture.Session.Snapshot;
        WorkspaceObservation<TicketWorkflowReadiness> projection =
            loading.ReadinessFor("prepare");
        Assert.True(projection.IsLoading);
        Assert.True(projection.IsStale);
        Assert.Null(projection.CurrentValue);
        Assert.True(projection.LastSuccessfulValue?.CanStart);
        Assert.Equal(InitialTime, projection.LastSucceededAt);
        Assert.Equal(ProbeTime, projection.LastSuccessfulValue?.CheckedAt);
        Assert.Same(successful.Workflows, loading.Workflows);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        refresh.Response.SetException(new HttpRequestException(
            "Recheck failed.", null, HttpStatusCode.ServiceUnavailable));
        TicketWorkspaceSnapshot failed = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.Phase == WorkspaceObservationPhase.Unavailable);
        projection = failed.ReadinessFor("prepare");
        Assert.Null(projection.CurrentValue);
        Assert.True(projection.IsStale);
        Assert.Equal(InitialTime, projection.LastSucceededAt);
        Assert.Equal(ProbeTime, projection.LastSuccessfulValue?.CheckedAt);

        ControlledRead<ServicesStatusResponse> recovery = fixture.Readiness.Refresh.Enqueue();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.Session.RefreshAll();
        await recovery.Entered.Task.WaitAsync(HangGuard);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        recovery.Response.SetResult(Services());
        TicketWorkspaceSnapshot recovered = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.IsCurrentSuccess &&
                snapshot.Readiness.LastSucceededAt > InitialTime);
        projection = recovered.ReadinessFor("prepare");
        Assert.True(projection.CurrentValue?.CanStart);
        Assert.False(projection.IsStale);
        Assert.Equal(fixture.Time.GetUtcNow(), projection.LastSucceededAt);
        Assert.Equal(ProbeTime, projection.CurrentValue?.CheckedAt);
        Assert.Equal(ProbeTime.AddMinutes(-1), projection.CurrentValue?.Orchestrator.CheckedAt);
        Assert.Equal(ProbeTime.AddMinutes(-2), projection.CurrentValue?.Processor.CheckedAt);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(2, fixture.Readiness.Refresh.Calls);
        Assert.Equal(1, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
    }

    [Fact]
    public async Task RefreshCoalescesPerObservation()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        TicketWorkspaceSnapshot loading = fixture.Session.Snapshot;

        fixture.Session.Start();
        fixture.Session.Start();
        fixture.Session.RefreshHistory("PREPARE");
        fixture.Session.RefreshHistory("plan");
        fixture.Session.RefreshAll();
        await RunConcurrentlyAsync(
            fixture.Session.RefreshAll,
            () => fixture.Session.RefreshHistory("prepare"),
            fixture.Session.Start);

        Assert.Same(loading, fixture.Session.Snapshot);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(0, fixture.Readiness.Refresh.Calls);
        Assert.Equal(1, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        fixture.ReadinessRead.Response.SetResult(Services());
        fixture.PreparerRead.Response.SetResult(History("prepared-1"));
        fixture.PlannerRead.Response.SetResult(History("planned-1"));
        TicketWorkspaceSnapshot successful =
            await fixture.Changes.WaitForAsync(AllSucceeded);

        ControlledRead<ServicesStatusResponse> readiness = fixture.Readiness.Refresh.Enqueue();
        ControlledRead<AuthoringRunListResponse> preparer = fixture.Authoring.Preparer.Enqueue();
        ControlledRead<AuthoringRunListResponse> planner = fixture.Authoring.Planner.Enqueue();
        await RunConcurrentlyAsync(
            fixture.Session.RefreshAll,
            () => fixture.Session.RefreshHistory("PREPARE"),
            () => fixture.Session.RefreshHistory("plan"));
        await Task.WhenAll(
            readiness.Entered.Task,
            preparer.Entered.Task,
            planner.Entered.Task).WaitAsync(HangGuard);
        loading = fixture.Session.Snapshot;
        fixture.Session.RefreshAll();
        fixture.Session.RefreshHistory("prepare");
        fixture.Session.Start();
        Assert.Same(loading, fixture.Session.Snapshot);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(1, fixture.Readiness.Refresh.Calls);
        Assert.Equal(2, fixture.Authoring.Preparer.Calls);
        Assert.Equal(2, fixture.Authoring.Planner.Calls);
        Assert.True(loading.Readiness.IsStale);
        Assert.All(loading.Workflows.Values, workspace => Assert.True(workspace.History.IsStale));

        readiness.Response.SetResult(Services());
        preparer.Response.SetResult(History("prepared-2"));
        planner.Response.SetResult(History("planned-2"));
        TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
            snapshot => AllSucceeded(snapshot) && snapshot.Revision > successful.Revision);
        fixture.Session.Start();
        Assert.Same(refreshed, fixture.Session.Snapshot);
        Assert.True(HasRun(refreshed, "prepare", "prepared-2"));
        Assert.True(HasRun(refreshed, "plan", "planned-2"));
        Assert.Equal(1, fixture.Readiness.Get.MaximumActive);
        Assert.Equal(1, fixture.Readiness.Refresh.MaximumActive);
        Assert.Equal(1, fixture.Authoring.Preparer.MaximumActive);
        Assert.Equal(1, fixture.Authoring.Planner.MaximumActive);
        Assert.Equal([false, true], fixture.Readiness.Requests.Select(request => request.Refresh));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("plan")]
    public async Task RefreshCoalescesPerObservationWithSynchronousHistoryCompletion(
        string workflow)
    {
        await using Fixture fixture = new();
        ReadEndpoint<AuthoringRunListResponse> endpoint =
            workflow == "prepare" ? fixture.Authoring.Preparer : fixture.Authoring.Planner;
        ControlledRead<AuthoringRunListResponse> initial =
            workflow == "prepare" ? fixture.PreparerRead : fixture.PlannerRead;
        TaskCompletionSource<TicketWorkspaceSnapshot> initialEntered = NewSignal<TicketWorkspaceSnapshot>();
        initial.OnEntered = () =>
        {
            initialEntered.TrySetResult(fixture.Session.Snapshot);
            fixture.Session.Start();
            fixture.Session.RefreshHistory(workflow.ToUpperInvariant());
            fixture.Session.RefreshAll();
        };
        initial.Response.SetResult(History("synchronous-1"));

        await fixture.StartAsync();
        TicketWorkspaceSnapshot entered = await initialEntered.Task.WaitAsync(HangGuard);
        Assert.True(entered.Workflows[workflow].History.IsLoading);
        TicketWorkspaceSnapshot successful = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, workflow, "synchronous-1"));
        Assert.Equal(1, endpoint.Calls);

        ControlledRead<AuthoringRunListResponse> refresh = endpoint.Enqueue();
        TaskCompletionSource<TicketWorkspaceSnapshot> refreshEntered = NewSignal<TicketWorkspaceSnapshot>();
        refresh.OnEntered = () =>
        {
            refreshEntered.TrySetResult(fixture.Session.Snapshot);
            fixture.Session.RefreshAll();
            fixture.Session.RefreshHistory(workflow);
        };
        refresh.Response.SetResult(History("synchronous-2"));
        fixture.Session.RefreshHistory(workflow);

        entered = await refreshEntered.Task.WaitAsync(HangGuard);
        Assert.True(entered.Workflows[workflow].History.IsLoading);
        Assert.True(entered.Workflows[workflow].History.IsStale);
        TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, workflow, "synchronous-2"));
        Assert.True(refreshed.Revision > successful.Revision);
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal(1, endpoint.MaximumActive);
        Assert.True(refreshed.Readiness.IsLoading);
        Assert.True(refreshed.Workflows[workflow == "prepare" ? "plan" : "prepare"].History.IsLoading);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(0, fixture.Readiness.Refresh.Calls);
        Assert.Equal(3, fixture.Authoring.Requests.Count);
    }

    [Fact]
    public async Task RefreshCoalescesSynchronousCachedAndExplicitReadiness()
    {
        await using Fixture fixture = new();
        TaskCompletionSource<TicketWorkspaceSnapshot> initialEntered = NewSignal<TicketWorkspaceSnapshot>();
        fixture.ReadinessRead.OnEntered = () =>
        {
            initialEntered.TrySetResult(fixture.Session.Snapshot);
            fixture.Session.Start();
            fixture.Session.RefreshAll();
        };
        fixture.ReadinessRead.Response.SetResult(Services());
        await fixture.StartAsync();
        Assert.True((await initialEntered.Task.WaitAsync(HangGuard)).Readiness.IsLoading);
        await fixture.Changes.WaitForAsync(snapshot => snapshot.Readiness.IsCurrentSuccess);
        Assert.Equal(0, fixture.Readiness.Refresh.Calls);

        ControlledRead<ServicesStatusResponse> refresh = fixture.Readiness.Refresh.Enqueue();
        TaskCompletionSource<TicketWorkspaceSnapshot> refreshEntered = NewSignal<TicketWorkspaceSnapshot>();
        refresh.OnEntered = () =>
        {
            refreshEntered.TrySetResult(fixture.Session.Snapshot);
            fixture.Session.RefreshAll();
            fixture.Session.Start();
        };
        refresh.Response.SetResult(Services());
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        fixture.Session.RefreshAll();

        Assert.True((await refreshEntered.Task.WaitAsync(HangGuard)).Readiness.IsLoading);
        TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.IsCurrentSuccess &&
                snapshot.Readiness.LastSucceededAt > InitialTime);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(1, fixture.Readiness.Refresh.Calls);
        Assert.Equal(1, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        Assert.All(refreshed.Workflows.Values, workspace => Assert.True(workspace.History.IsLoading));
    }

    [Fact]
    public async Task PreparerRefreshWorksWhilePlannerIsPending()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        fixture.PreparerRead.Response.SetResult(History("prepared-1"));
        TicketWorkspaceSnapshot first = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, "prepare", "prepared-1"));
        ControlledRead<AuthoringRunListResponse> refresh = fixture.Authoring.Preparer.Enqueue();

        fixture.Session.RefreshAll();
        await refresh.Entered.Task.WaitAsync(HangGuard);
        fixture.Session.RefreshAll();
        fixture.Session.RefreshHistory("prepare");
        refresh.Response.SetResult(History("prepared-2"));
        TicketWorkspaceSnapshot second = await fixture.Changes.WaitForAsync(
            snapshot => HasRun(snapshot, "prepare", "prepared-2"));

        Assert.False(fixture.ReadinessRead.Response.Task.IsCompleted);
        Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);
        Assert.Same(first.Readiness, second.Readiness);
        Assert.Same(first.Workflows["plan"], second.Workflows["plan"]);
        Assert.Equal(2, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        Assert.Equal(1, fixture.Readiness.Get.Calls);
        Assert.Equal(0, fixture.Readiness.Refresh.Calls);
    }

    [Fact]
    public async Task ConcurrentCompletionsPreserveEverySlice()
    {
        await using Fixture fixture = new();
        TicketWorkspaceSnapshot initial = fixture.Session.Snapshot;
        await fixture.StartAsync();
        TicketWorkspaceSnapshot loading = fixture.Session.Snapshot;
        AuthoringRunListResponse preparer = new(
            [Run("shared-id") with { State = new AuthoringRunStateInfo(false, true) }], true);
        AuthoringRunListResponse planner = new(
            [Run("shared-id") with
            {
                ProcessorKind = "jira-fhir-planner",
                State = new AuthoringRunStateInfo(true, false),
            }], false);

        await RunConcurrentlyAsync(
            () => fixture.ReadinessRead.Response.SetResult(Services()),
            () => fixture.PreparerRead.Response.SetResult(preparer),
            () => fixture.PlannerRead.Response.SetResult(planner));
        TicketWorkspaceSnapshot successful = await fixture.Changes.WaitForAsync(AllSucceeded);

        Assert.Equal(loading.Revision + 3, successful.Revision);
        AuthoringRunListResponse preparedRuns = Assert.IsType<AuthoringRunListResponse>(
            successful.Workflows["PREPARE"].History.CurrentValue);
        AuthoringRunListResponse plannedRuns = Assert.IsType<AuthoringRunListResponse>(
            successful.Workflows["PLAN"].History.CurrentValue);
        Assert.Equal("jira-fhir-preparer",
            Assert.Single(preparedRuns.Runs).ProcessorKind);
        Assert.Equal("jira-fhir-planner",
            Assert.Single(plannedRuns.Runs).ProcessorKind);
        Assert.False(successful.Workflows["prepare"].History.CurrentValue?.Runs[0].State?.IsTerminal);
        Assert.True(successful.Workflows["plan"].History.CurrentValue?.Runs[0].State?.IsTerminal);
        Assert.True(successful.Workflows["prepare"].History.CurrentValue?.Truncated);
        Assert.False(successful.Workflows["plan"].History.CurrentValue?.Truncated);
        TicketWorkspaceSnapshot previous = loading;
        for (long revision = loading.Revision + 1; revision <= successful.Revision; revision++)
        {
            long expectedRevision = revision;
            TicketWorkspaceSnapshot current = await fixture.Changes.WaitForAsync(
                snapshot => snapshot.Revision == expectedRevision);
            Assert.Equal((int)(revision - loading.Revision), SuccessCount(current));
            if (previous.Readiness.IsCurrentSuccess)
            {
                Assert.Same(previous.Readiness, current.Readiness);
            }
            foreach (TicketWorkflowWorkspace workspace in previous.Workflows.Values)
            {
                string route = workspace.Workflow.RouteKey;
                Assert.Same(initial.Workflows[route].Workflow, current.Workflows[route].Workflow);
                if (workspace.History.IsCurrentSuccess)
                {
                    Assert.Same(workspace, current.Workflows[route]);
                }
            }
            previous = current;
        }
        Assert.Equal(0, initial.Revision);
        Assert.Equal(WorkspaceObservationPhase.Pending, initial.Readiness.Phase);
        Assert.All(initial.Workflows.Values,
            workspace => Assert.Equal(WorkspaceObservationPhase.Pending, workspace.History.Phase));
        Assert.True(loading.Readiness.IsLoading);
        Assert.All(loading.Workflows.Values, workspace => Assert.True(workspace.History.IsLoading));
        Assert.Same(successful, fixture.Session.Snapshot);

        ControlledRead<ServicesStatusResponse> readinessRefresh = fixture.Readiness.Refresh.Enqueue();
        ControlledRead<AuthoringRunListResponse> preparerRefresh = fixture.Authoring.Preparer.Enqueue();
        ControlledRead<AuthoringRunListResponse> plannerRefresh = fixture.Authoring.Planner.Enqueue();
        fixture.Session.RefreshAll();
        await Task.WhenAll(
            readinessRefresh.Entered.Task,
            preparerRefresh.Entered.Task,
            plannerRefresh.Entered.Task).WaitAsync(HangGuard);
        await RunConcurrentlyAsync(
            () => readinessRefresh.Response.SetResult(Services()),
            () => preparerRefresh.Response.SetResult(History("prepared-new")),
            () => plannerRefresh.Response.SetResult(History("planned-new")));
        TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
            snapshot => AllSucceeded(snapshot) && snapshot.Revision > successful.Revision);
        Assert.Equal(successful.Revision + 6, refreshed.Revision);
        Assert.True(HasRun(refreshed, "prepare", "prepared-new"));
        Assert.True(HasRun(refreshed, "plan", "planned-new"));
        Assert.True(HasRun(successful, "prepare", "shared-id"));
        Assert.True(HasRun(successful, "plan", "shared-id"));
    }

    [Fact]
    public async Task DisposalCancelsReadsWithoutOutageNotifications()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        TicketWorkspaceSnapshot before = await fixture.Changes.WaitForAsync(
            snapshot => snapshot.Readiness.IsLoading &&
                snapshot.Workflows.Values.All(workspace => workspace.History.IsLoading));
        int notifications = fixture.Changes.Snapshots.Length;
        TaskCompletionSource<TicketWorkspaceSnapshot> cancellationState = NewSignal<TicketWorkspaceSnapshot>();
        using CancellationTokenRegistration registration = fixture.PreparerRead.Token.Register(
            () => cancellationState.TrySetResult(fixture.Session.Snapshot));

        Task firstDisposal = fixture.Session.DisposeAsync().AsTask();
        Task secondDisposal = fixture.Session.DisposeAsync().AsTask();
        Assert.Same(firstDisposal, secondDisposal);
        await Task.WhenAll(
            firstDisposal,
            secondDisposal,
            fixture.ReadinessRead.CancellationRequested.Task,
            fixture.PreparerRead.CancellationRequested.Task,
            fixture.PlannerRead.CancellationRequested.Task,
            fixture.ReadinessRead.Finished.Task,
            fixture.PreparerRead.Finished.Task,
            fixture.PlannerRead.Finished.Task).WaitAsync(HangGuard);

        Assert.Same(before, await cancellationState.Task.WaitAsync(HangGuard));
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.Equal(notifications, fixture.Changes.Snapshots.Length);
        CancellationToken token = fixture.ReadinessRead.Token;
        Assert.True(token.CanBeCanceled);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(token, fixture.PreparerRead.Token);
        Assert.Equal(token, fixture.PlannerRead.Token);
        Assert.All(fixture.Authoring.Requests, request =>
        {
            Assert.Equal(token, request.Token);
            Assert.Equal(25, request.Limit);
        });
        Assert.Equal(0, fixture.Readiness.Get.Active);
        Assert.Equal(0, fixture.Authoring.Preparer.Active);
        Assert.Equal(0, fixture.Authoring.Planner.Active);
        Assert.Empty(fixture.ReaderLogger.Entries);
        Assert.Empty(fixture.SessionLogger.Entries);
        Assert.Throws<ObjectDisposedException>(fixture.Session.Start);
        Assert.Throws<ObjectDisposedException>(fixture.Session.RefreshAll);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session.RefreshHistory("prepare"));
        Func<TicketWorkspaceSnapshot, Task> subscriber = _ => Task.CompletedTask;
        Assert.Throws<ObjectDisposedException>(() => fixture.Session.Changed += subscriber);
        fixture.Session.Changed -= subscriber;
    }

    [Fact]
    public async Task DisposalBeforeStartIsIdempotent()
    {
        await using Fixture fixture = new();
        TicketWorkspaceSnapshot before = fixture.Session.Snapshot;

        Task first = fixture.Session.DisposeAsync().AsTask();
        Task second = fixture.Session.DisposeAsync().AsTask();
        await first.WaitAsync(HangGuard);

        Assert.Same(first, second);
        Assert.Same(first, fixture.Session.DisposeAsync().AsTask());
        Assert.Same(before, fixture.Session.Snapshot);
        Assert.Empty(fixture.Authoring.Requests);
        Assert.Empty(fixture.Readiness.Requests);
        Assert.Empty(fixture.Changes.Snapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCompletionCannotPublishAfterDisposal(bool lateFailure)
    {
        await using Fixture fixture = new();
        fixture.PlannerRead.CooperatesWithCancellation = false;
        try
        {
            await fixture.StartAsync();
            fixture.ReadinessRead.Response.SetResult(Services());
            fixture.PreparerRead.Response.SetResult(History("prepared"));
            TicketWorkspaceSnapshot before = await fixture.Changes.WaitForAsync(
                snapshot => snapshot.Readiness.IsCurrentSuccess &&
                    HasRun(snapshot, "prepare", "prepared"));
            await fixture.Changes.WaitForAsync(snapshot => snapshot.Revision == before.Revision - 1);
            await fixture.Changes.WaitForAsync(snapshot => snapshot.Revision == 3);
            int notifications = fixture.Changes.Snapshots.Length;

            Task firstDisposal = fixture.Session.DisposeAsync().AsTask();
            Task secondDisposal = fixture.Session.DisposeAsync().AsTask();
            await fixture.PlannerRead.CancellationRequested.Task.WaitAsync(HangGuard);
            Assert.Same(firstDisposal, secondDisposal);
            Assert.False(firstDisposal.IsCompleted);
            Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);
            Assert.Same(before, fixture.Session.Snapshot);

            ApplicationException lateError = new("A late fake failure.");
            if (lateFailure)
            {
                fixture.PlannerRead.Response.SetException(lateError);
            }
            else
            {
                fixture.PlannerRead.Response.SetResult(History("obsolete-planned"));
            }
            await Task.WhenAll(
                firstDisposal,
                secondDisposal,
                fixture.PlannerRead.Finished.Task).WaitAsync(HangGuard);

            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Equal(notifications, fixture.Changes.Snapshots.Length);
            Assert.True(before.Workflows["plan"].History.IsLoading);
            Assert.False(before.Workflows["plan"].History.HasLastSuccess);
            Assert.Equal(0, fixture.Authoring.Planner.Active);
            if (lateFailure)
            {
                Assert.Same(lateError, fixture.PlannerRead.Error);
            }
            Assert.Empty(fixture.ReaderLogger.Entries);
            Assert.Empty(fixture.SessionLogger.Entries);
        }
        finally
        {
            // Cancellation cannot force an arbitrary fake to finish.
            fixture.PlannerRead.Response.TrySetResult(History("cleanup"));
            await fixture.Session.DisposeAsync().AsTask().WaitAsync(HangGuard);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriberFailuresAreObservedWithoutChangingReadOutcome(
        bool asynchronous)
    {
        await using Fixture fixture = new();
        TaskCompletionSource<TicketWorkspaceSnapshot> entered = NewSignal<TicketWorkspaceSnapshot>();
        TaskCompletionSource<TicketWorkspaceSnapshot> nextSubscriber = NewSignal<TicketWorkspaceSnapshot>();
        TaskCompletionSource renderResult = NewSignal();
        ApplicationException renderError = new("Rendering failed.");
        fixture.Session.Changed += snapshot =>
        {
            if (!HasRun(snapshot, "prepare", "prepared"))
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult(snapshot);
            if (!asynchronous)
            {
                throw renderError;
            }
            return renderResult.Task;
        };
        fixture.Session.Changed += snapshot =>
        {
            if (HasRun(snapshot, "prepare", "prepared"))
            {
                nextSubscriber.TrySetResult(snapshot);
            }
            return Task.CompletedTask;
        };
        try
        {
            await fixture.StartAsync();
            fixture.PreparerRead.Response.SetResult(History("prepared"));
            TicketWorkspaceSnapshot successful = await entered.Task.WaitAsync(HangGuard);
            if (asynchronous)
            {
                Assert.Same(successful, fixture.Session.Snapshot);
                renderResult.SetException(renderError);
            }
            LogEntry log = await fixture.SessionLogger.FirstEntry.Task.WaitAsync(HangGuard);
            Assert.Same(successful, await nextSubscriber.Task.WaitAsync(HangGuard));
            Assert.Equal(LogLevel.Error, log.Level);
            Assert.Same(renderError, log.Exception);
            Assert.Equal(successful.Revision, log.Fields["Revision"]);
            Assert.Contains("subscriber", log.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Same(successful, fixture.Session.Snapshot);
            Assert.Null(successful.Workflows["prepare"].History.Failure);
            Assert.True(successful.Workflows["prepare"].History.IsCurrentSuccess);
            Assert.Single(fixture.SessionLogger.Entries);
            Assert.Empty(fixture.ReaderLogger.Entries);
        }
        finally
        {
            renderResult.TrySetResult();
        }
    }

    [Fact]
    public async Task ReaderCompletionDoesNotWaitForRendererCallback()
    {
        await using Fixture fixture = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource renderResult = NewSignal();
        fixture.Session.Changed += snapshot =>
        {
            if (!HasRun(snapshot, "prepare", "prepared-1"))
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult();
            return renderResult.Task;
        };
        try
        {
            await fixture.StartAsync();
            fixture.PreparerRead.Response.SetResult(History("prepared-1"));
            await entered.Task.WaitAsync(HangGuard);
            ControlledRead<AuthoringRunListResponse> refresh = fixture.Authoring.Preparer.Enqueue();

            fixture.Session.RefreshHistory("prepare");
            await refresh.Entered.Task.WaitAsync(HangGuard);
            refresh.Response.SetResult(History("prepared-2"));
            TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
                snapshot => HasRun(snapshot, "prepare", "prepared-2"));
            Assert.False(renderResult.Task.IsCompleted);
            Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);

            await fixture.Session.DisposeAsync().AsTask().WaitAsync(HangGuard);
            Assert.False(renderResult.Task.IsCompleted);
            Assert.Same(refreshed, fixture.Session.Snapshot);
            ApplicationException renderError = new("A queued renderer callback failed.");
            renderResult.SetException(renderError);
            LogEntry log = await fixture.SessionLogger.FirstEntry.Task.WaitAsync(HangGuard);
            Assert.Same(renderError, log.Exception);
            Assert.Contains("subscriber", log.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Same(refreshed, fixture.Session.Snapshot);
            Assert.Empty(fixture.ReaderLogger.Entries);
        }
        finally
        {
            renderResult.TrySetResult();
        }
    }

    [Fact]
    public async Task SubscriberCanDisposeSession()
    {
        await using Fixture fixture = new();
        TaskCompletionSource disposedFromCallback = NewSignal();
        fixture.Session.Changed += async snapshot =>
        {
            if (HasRun(snapshot, "prepare", "prepared"))
            {
                await fixture.Session.DisposeAsync();
                disposedFromCallback.TrySetResult();
            }
        };
        await fixture.StartAsync();

        fixture.PreparerRead.Response.SetResult(History("prepared"));
        await disposedFromCallback.Task.WaitAsync(HangGuard);

        Assert.True(HasRun(fixture.Session.Snapshot, "prepare", "prepared"));
        Assert.True(fixture.ReadinessRead.Finished.Task.IsCompleted);
        Assert.True(fixture.PlannerRead.Finished.Task.IsCompleted);
        Assert.True(fixture.ReadinessRead.Token.IsCancellationRequested);
        Assert.True(fixture.PlannerRead.Token.IsCancellationRequested);
        Assert.Empty(fixture.SessionLogger.Entries);
    }

    [Fact]
    public async Task SynchronousSubscriberDoesNotHoldStateLockOrReadWorker()
    {
        await using Fixture fixture = new();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource exited = NewSignal();
        fixture.Session.Changed += snapshot =>
        {
            if (!HasRun(snapshot, "prepare", "prepared-1"))
            {
                return Task.CompletedTask;
            }
            entered.TrySetResult();
            try
            {
                // Deliberately block the synchronous part, not just its Task.
                if (!release.Wait(HangGuard))
                {
                    throw new TimeoutException("The subscriber was not released.");
                }
            }
            finally
            {
                exited.TrySetResult();
            }
            return Task.CompletedTask;
        };
        try
        {
            await fixture.StartAsync();
            fixture.PreparerRead.Response.SetResult(History("prepared-1"));
            await entered.Task.WaitAsync(HangGuard);
            ControlledRead<AuthoringRunListResponse> refresh = fixture.Authoring.Preparer.Enqueue();

            await Task.Run(() => fixture.Session.RefreshHistory("prepare")).WaitAsync(HangGuard);
            await refresh.Entered.Task.WaitAsync(HangGuard);
            refresh.Response.SetResult(History("prepared-2"));
            TicketWorkspaceSnapshot refreshed = await fixture.Changes.WaitForAsync(
                snapshot => HasRun(snapshot, "prepare", "prepared-2"));
            await Task.Run(async () => await fixture.Session.DisposeAsync()).WaitAsync(HangGuard);

            Assert.False(release.IsSet);
            Assert.False(exited.Task.IsCompleted);
            Assert.Same(refreshed, fixture.Session.Snapshot);
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted)
            {
                await exited.Task.WaitAsync(HangGuard);
            }
        }
        Assert.Empty(fixture.SessionLogger.Entries);
    }

    [Fact]
    public async Task SchedulingDoesNotWaitForSynchronousClientCode()
    {
        await using Fixture fixture = new();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource entered = NewSignal();
        fixture.ReadinessRead.OnEntered = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(HangGuard))
            {
                throw new TimeoutException("The synchronous client was not released.");
            }
        };
        fixture.ReadinessRead.Response.SetResult(Services());
        try
        {
            Task start = Task.Run(fixture.Session.Start);
            await entered.Task.WaitAsync(HangGuard);
            await Task.WhenAll(
                start,
                fixture.PreparerRead.Entered.Task,
                fixture.PlannerRead.Entered.Task).WaitAsync(HangGuard);
            fixture.PreparerRead.Response.SetResult(History("prepared"));
            TicketWorkspaceSnapshot prepared = await fixture.Changes.WaitForAsync(
                snapshot => HasRun(snapshot, "prepare", "prepared"));

            Assert.False(release.IsSet);
            Assert.False(fixture.ReadinessRead.Finished.Task.IsCompleted);
            Assert.True(prepared.Readiness.IsLoading);
            Assert.False(fixture.PlannerRead.Response.Task.IsCompleted);
            release.Set();
            await fixture.Changes.WaitForAsync(snapshot => snapshot.Readiness.IsCurrentSuccess);
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted)
            {
                await fixture.ReadinessRead.Finished.Task.WaitAsync(HangGuard);
            }
        }
        Assert.Empty(fixture.ReaderLogger.Entries);
        Assert.Empty(fixture.SessionLogger.Entries);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillObservesAllReads()
    {
        await using Fixture fixture = new();
        await fixture.StartAsync();
        TicketWorkspaceSnapshot before = fixture.Session.Snapshot;
        TaskCompletionSource<Task> reentrantDisposal = NewSignal<Task>();
        ApplicationException cancellationError = new("A cancellation callback failed.");
        using CancellationTokenRegistration registration = fixture.PreparerRead.Token.Register(() =>
        {
            reentrantDisposal.TrySetResult(fixture.Session.DisposeAsync().AsTask());
            throw cancellationError;
        });

        Task disposal = fixture.Session.DisposeAsync().AsTask();
        await disposal.WaitAsync(HangGuard);

        Assert.Same(disposal, await reentrantDisposal.Task.WaitAsync(HangGuard));
        Assert.True(fixture.PreparerRead.Finished.Task.IsCompleted);
        Assert.True(fixture.PlannerRead.Finished.Task.IsCompleted);
        Assert.True(fixture.ReadinessRead.Finished.Task.IsCompleted);
        Assert.Same(before, fixture.Session.Snapshot);
        LogEntry log = Assert.Single(fixture.SessionLogger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Same(cancellationError, log.Exception?.GetBaseException());
        Assert.Contains("cancellation callback", log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.ReaderLogger.Entries);
    }

    private static bool HasRun(TicketWorkspaceSnapshot snapshot, string workflow, string runId) =>
        snapshot.Workflows[workflow].History.CurrentValue?.Runs.Any(run => run.RunId == runId) == true;

    private static bool AllSucceeded(TicketWorkspaceSnapshot snapshot) =>
        snapshot.Readiness.IsCurrentSuccess &&
        snapshot.Workflows.Values.All(workspace => workspace.History.IsCurrentSuccess);

    private static int SuccessCount(TicketWorkspaceSnapshot snapshot) =>
        (snapshot.Readiness.IsCurrentSuccess ? 1 : 0) +
        snapshot.Workflows.Values.Count(workspace => workspace.History.IsCurrentSuccess);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task RunConcurrentlyAsync(params Action[] actions)
    {
        TaskCompletionSource release = NewSignal();
        Task[] tasks = actions.Select(action => Task.Run(async () =>
        {
            await release.Task;
            action();
        })).ToArray();
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(HangGuard);
    }

    private static AuthoringRunListResponse History(string runId) => new([Run(runId)], false);

    private static AuthoringRunStatus Run(string runId) => new(
        runId,
        "jira-fhir-preparer",
        7,
        "running",
        false,
        3,
        1,
        0,
        ProbeTime.AddHours(-1),
        ProbeTime,
        null,
        null,
        RetryableErrorItems: 2,
        State: new AuthoringRunStateInfo(false, true));

    private static ServicesStatusResponse Services() => new(
    [
        Health("Orchestrator", "orchestrator") with { CheckedAt = ProbeTime.AddMinutes(-1) },
        Health("Preparer", "processing") with { RequiredServices = ["Jira"] },
        Health("Planner", "processing") with
        {
            Status = "unavailable",
            RequiredServices = ["Jira", "GitHub"],
        },
        Health("Jira", "source"),
        Health("GitHub", "source"),
    ],
    ProbeTime);

    private static ServiceHealthInfo Health(string name, string kind) => new()
    {
        Name = name,
        ServiceKind = kind,
        Status = "healthy",
        Enabled = true,
        Configured = true,
        CheckedAt = ProbeTime.AddMinutes(-2),
        ProcessingIsRunning = kind == "processing" ? true : null,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            PreparerRead = Authoring.Preparer.Enqueue();
            PlannerRead = Authoring.Planner.Enqueue();
            ReadinessRead = Readiness.Get.Enqueue();
            TicketWorkspaceReader reader = new(
                Authoring,
                Readiness,
                Catalog,
                new ReadinessEvaluator(),
                Options.Create(new DevUiOptions { RecentRunLimit = 25 }),
                ReaderLogger);
            Session = new(reader, Catalog, Time, SessionLogger);
            Session.Changed += Changes.OnChangedAsync;
        }

        public FakeAuthoringClient Authoring { get; } = new();
        public FakeReadinessClient Readiness { get; } = new();
        public TicketWorkflowCatalog Catalog { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public TestLogger<TicketWorkspaceReader> ReaderLogger { get; } = new();
        public TestLogger<TicketWorkspaceSession> SessionLogger { get; } = new();
        public SnapshotProbe Changes { get; } = new();
        public TicketWorkspaceSession Session { get; }
        public ControlledRead<AuthoringRunListResponse> PreparerRead { get; }
        public ControlledRead<AuthoringRunListResponse> PlannerRead { get; }
        public ControlledRead<ServicesStatusResponse> ReadinessRead { get; }

        public async Task StartAsync()
        {
            Session.Start();
            await Task.WhenAll(
                PreparerRead.Entered.Task,
                PlannerRead.Entered.Task,
                ReadinessRead.Entered.Task).WaitAsync(HangGuard);
        }

        public async ValueTask DisposeAsync() =>
            await Session.DisposeAsync().AsTask().WaitAsync(HangGuard);
    }

    private sealed class ControlledRead<T>
    {
        public TaskCompletionSource<T> Response { get; } = NewSignal<T>();
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Finished { get; } = NewSignal();
        public TaskCompletionSource CancellationRequested { get; } = NewSignal();
        public CancellationToken Token { get; private set; }
        public Exception? Error { get; private set; }
        public bool CooperatesWithCancellation { get; set; } = true;
        public Action? OnEntered { get; set; }

        public async Task<T> ReadAsync(CancellationToken ct)
        {
            Token = ct;
            using CancellationTokenRegistration registration = ct.Register(
                () => CancellationRequested.TrySetResult());
            Entered.TrySetResult();
            try
            {
                OnEntered?.Invoke();
                return CooperatesWithCancellation
                    ? await Response.Task.WaitAsync(ct).ConfigureAwait(false)
                    : await Response.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Error = ex;
                throw;
            }
            finally
            {
                if (ct.IsCancellationRequested)
                {
                    CancellationRequested.TrySetResult();
                }
                Finished.TrySetResult();
            }
        }
    }

    private sealed class ReadEndpoint<T>
    {
        private readonly ConcurrentQueue<ControlledRead<T>> _responses = new();
        private int _calls;
        private int _active;
        private int _maximumActive;

        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public ControlledRead<T> Enqueue()
        {
            ControlledRead<T> read = new();
            _responses.Enqueue(read);
            return read;
        }

        public async Task<T> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int maximum = Volatile.Read(ref _maximumActive);
            while (active > maximum)
            {
                int previous = Interlocked.CompareExchange(ref _maximumActive, active, maximum);
                if (previous == maximum)
                {
                    break;
                }
                maximum = previous;
            }
            try
            {
                if (!_responses.TryDequeue(out ControlledRead<T>? read))
                {
                    throw new NotSupportedException("An unplanned workspace read was issued.");
                }
                return await read.ReadAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FakeReadinessClient : IOrchestratorReadinessClient
    {
        public ReadEndpoint<ServicesStatusResponse> Get { get; } = new();
        public ReadEndpoint<ServicesStatusResponse> Refresh { get; } = new();
        public ConcurrentQueue<(bool Refresh, CancellationToken Token)> Requests { get; } = new();

        public Task<ServicesStatusResponse> GetAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((false, ct));
            return Get.ReadAsync(ct);
        }

        public Task<ServicesStatusResponse> RefreshAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((true, ct));
            return Refresh.ReadAsync(ct);
        }
    }

    private sealed class FakeAuthoringClient : IAuthoringControlClient
    {
        public ReadEndpoint<AuthoringRunListResponse> Preparer { get; } = new();
        public ReadEndpoint<AuthoringRunListResponse> Planner { get; } = new();
        public ConcurrentQueue<(string Service, int? Limit, CancellationToken Token)> Requests { get; } = new();

        public Task<AuthoringRunListResponse> ListAsync(
            string serviceName, int? limit, CancellationToken ct)
        {
            Requests.Enqueue((serviceName, limit, ct));
            return serviceName switch
            {
                "Preparer" => Preparer.ReadAsync(ct),
                "Planner" => Planner.ReadAsync(ct),
                _ => throw new NotSupportedException("Unexpected processing service."),
            };
        }

        public Task<AuthoringStartResult> StartAsync<TRequest>(
            string serviceName, TRequest request, CancellationToken ct) =>
            throw new NotSupportedException("A workspace must not start a run.");

        public Task<AuthoringRunResponse> StartPublicationRefreshAsync(
            string serviceName, string sourceRunId, CancellationToken ct) =>
            throw new NotSupportedException(
                "A workspace must not start a publication refresh.");

        public Task<AuthoringRunResponse> GetAsync(
            string serviceName, string runId, CancellationToken ct) =>
            throw new NotSupportedException("A workspace must not open a run.");

        public Task<AuthoringRetryResponse> RetryAsync(
            string serviceName, string runId, string itemId, CancellationToken ct) =>
            throw new NotSupportedException("A workspace must not retry an item.");

        public Task<AuthoringItemSupersedeResult> SupersedeAsync(
            string serviceName, string runId, string itemId, string reason, CancellationToken ct) =>
            throw new NotSupportedException("A workspace must not supersede an item.");

        public Task<VerifiedAuthoringSnapshotPair> DownloadSnapshotPairAsync(
            string serviceName, string runId, string pairDirectory, CancellationToken ct) =>
            throw new NotSupportedException("A workspace must not download snapshots.");
    }

    private sealed class SnapshotProbe
    {
        private readonly object _sync = new();
        private readonly List<TicketWorkspaceSnapshot> _snapshots = [];
        private readonly List<(Func<TicketWorkspaceSnapshot, bool> Predicate,
            TaskCompletionSource<TicketWorkspaceSnapshot> Completion)> _waiters = [];

        public TicketWorkspaceSnapshot[] Snapshots
        {
            get
            {
                lock (_sync)
                {
                    return _snapshots.ToArray();
                }
            }
        }

        public Task OnChangedAsync(TicketWorkspaceSnapshot snapshot)
        {
            lock (_sync)
            {
                _snapshots.Add(snapshot);
                for (int index = _waiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _waiters[index];
                    if (waiter.Predicate(snapshot))
                    {
                        waiter.Completion.TrySetResult(snapshot);
                        _waiters.RemoveAt(index);
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task<TicketWorkspaceSnapshot> WaitForAsync(
            Func<TicketWorkspaceSnapshot, bool> predicate)
        {
            lock (_sync)
            {
                TicketWorkspaceSnapshot? existing =
                    _snapshots.LastOrDefault(snapshot => predicate(snapshot));
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }
                TaskCompletionSource<TicketWorkspaceSnapshot> completion = NewSignal<TicketWorkspaceSnapshot>();
                _waiters.Add((predicate, completion));
                return completion.Task.WaitAsync(HangGuard);
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _utcTicks = InitialTime.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed record LogEntry(
        LogLevel Level,
        Exception? Exception,
        string Message,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed class TestLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public TaskCompletionSource<LogEntry> FirstEntry { get; } = NewSignal<LogEntry>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?> fields =
                state is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : new Dictionary<string, object?>();
            LogEntry entry = new(logLevel, exception, formatter(state, exception), fields);
            Entries.Enqueue(entry);
            FirstEntry.TrySetResult(entry);
        }
    }
}
