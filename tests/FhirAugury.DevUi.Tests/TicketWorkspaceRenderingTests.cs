using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Components.Operations;
using FhirAugury.DevUi.Components.Pages;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Publishing.Tickets;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.DevUi.Tests;

public sealed class TicketWorkspaceRenderingTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset ReadTime =
        new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProbeTime = ReadTime.AddMinutes(-5);

    [Fact]
    public async Task HomePrerenderShowsWorkflowShellWithoutReads()
    {
        await using Fixture fixture = new();

        string html = await fixture.RenderHtmlAsync<Home>();

        Assert.Contains("Operations workspace", html);
        Assert.Contains("Prepare tickets", html);
        Assert.Contains("Plan tickets", html);
        Assert.Contains("href=\"/operations/prepare/new\"", html);
        Assert.Contains("href=\"/operations/plan/new\"", html);
        Assert.Equal(2, Regex.Matches(html, "Open by run ID").Count);
        Assert.Equal(2, Regex.Matches(html, "Start a run").Count);
        Assert.Contains("id=\"workflow-prepare-run-id\"", html);
        Assert.Contains("id=\"workflow-plan-run-id\"", html);
        Assert.Contains("Counts are unknown.", html);
        Assert.DoesNotContain("No active runs.", html);
        Assert.DoesNotContain("No recent completed runs.", html);
        Assert.DoesNotContain("Ready to start", html);
        Assert.Empty(fixture.Authoring.ListRequests);
        Assert.Empty(fixture.Readiness.Requests);
        Assert.Empty(fixture.Mutations);
        PageSession page = Assert.Single(fixture.PageSessions);
        Assert.True(page.Scope.Disposed.Task.IsCompleted);
        Assert.Throws<ObjectDisposedException>(page.Session.Start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparerIsUsableWhilePlannerIsPending(bool readinessFails)
    {
        await using Fixture fixture = new();
        Mounted<Home> home = await fixture.Renderer.MountAsync<Home>();
        await fixture.WaitForInitialReadsAsync();
        if (readinessFails)
        {
            fixture.InitialReadiness.Response.SetException(
                new HttpRequestException("readiness failed", null, HttpStatusCode.ServiceUnavailable));
        }
        fixture.InitialPreparer.Response.SetResult(History("prepared-1"));
        Markup markup = await fixture.Renderer.WaitForAsync(home.Id,
            page => page.Html.Contains("/operations/prepare/prepared-1", StringComparison.Ordinal) &&
                (!readinessFails || page.Html.Contains("Readiness is unavailable.", StringComparison.Ordinal)));

        Assert.False(fixture.InitialPlanner.Response.Task.IsCompleted);
        Assert.False(fixture.InitialPlanner.Finished.Task.IsCompleted);
        Assert.False(markup.Button("Refresh prepare tickets history").Disabled);
        Assert.True(markup.Button("Refresh plan tickets history").Disabled);
        Assert.False(markup.Button("Refresh and recheck").Disabled);
        Assert.Contains("in progress", markup.Html);
        Assert.Contains("id=\"workflow-prepare-run-id\"", markup.Html);
        Assert.Contains("href=\"/operations/prepare/new\"", markup.Html);
        if (readinessFails)
        {
            Assert.Contains("failed or are unavailable", markup.Html);
        }
        else
        {
            Assert.False(fixture.InitialReadiness.Response.Task.IsCompleted);
        }

        ControlledRead<AuthoringRunListResponse> refresh = fixture.Authoring.Preparer.Enqueue();
        await fixture.Renderer.ClickAsync(home.Id, "Refresh prepare tickets history");
        await refresh.Entered.Task.WaitAsync(HangGuard);
        refresh.Response.SetResult(History("prepared-2"));
        await fixture.Renderer.WaitForAsync(home.Id,
            page => page.Html.Contains("/operations/prepare/prepared-2", StringComparison.Ordinal));
        Assert.False(fixture.InitialPlanner.Response.Task.IsCompleted);

        ControlledRead<AuthoringRunListResponse> headerRefresh = fixture.Authoring.Preparer.Enqueue();
        ControlledRead<ServicesStatusResponse>? recheck =
            readinessFails ? fixture.Readiness.Refresh.Enqueue() : null;
        await fixture.Renderer.ClickAsync(home.Id, "Refresh and recheck");
        await headerRefresh.Entered.Task.WaitAsync(HangGuard);
        if (recheck is not null)
        {
            await recheck.Entered.Task.WaitAsync(HangGuard);
        }
        Assert.Equal(3, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        Assert.Equal(readinessFails ? 1 : 0, fixture.Readiness.Refresh.Calls);
        Assert.False(fixture.InitialPlanner.Response.Task.IsCompleted);
        Assert.Equal(0, fixture.Authoring.StartCalls);
    }

    [Theory]
    [InlineData(WorkspaceObservationPhase.Pending, false, false)]
    [InlineData(WorkspaceObservationPhase.Loading, false, false)]
    [InlineData(WorkspaceObservationPhase.Unavailable, false, false)]
    [InlineData(WorkspaceObservationPhase.Failed, false, false)]
    [InlineData(WorkspaceObservationPhase.Succeeded, true, false)]
    [InlineData(WorkspaceObservationPhase.Succeeded, true, true)]
    [InlineData(WorkspaceObservationPhase.Loading, true, false)]
    [InlineData(WorkspaceObservationPhase.Loading, true, true)]
    [InlineData(WorkspaceObservationPhase.Unavailable, true, false)]
    [InlineData(WorkspaceObservationPhase.Unavailable, true, true)]
    [InlineData(WorkspaceObservationPhase.Failed, true, false)]
    [InlineData(WorkspaceObservationPhase.Failed, true, true)]
    public async Task HistoryStatesDistinguishUnknownCurrentEmptyAndStale(
        WorkspaceObservationPhase phase,
        bool retainSuccess,
        bool empty)
    {
        await using Fixture fixture = new();
        AuthoringRunListResponse history = new(
            empty ? [] :
            [
                Run("active-1", "completed", terminal: false),
                Run("recovering-1", "error", terminal: false),
                Run("terminal-1", "running", terminal: true),
                Run("unclassified-1", "running", terminal: null),
            ],
            Truncated: true);
        WorkspaceObservation<AuthoringRunListResponse> observation =
            Observation(history, phase, retainSuccess);

        string html = await fixture.RenderHtmlAsync<WorkflowCard>(Parameters(
            (nameof(WorkflowCard.Workflow), fixture.Catalog.Get("prepare")),
            (nameof(WorkflowCard.History), observation),
            (nameof(WorkflowCard.Readiness), WorkspaceObservation<TicketWorkflowReadiness>.Pending)));

        Assert.Contains("href=\"/operations/prepare/new\"", html);
        Assert.Contains("Open by run ID", html);
        if (!retainSuccess)
        {
            Assert.Equal(2, Regex.Matches(html, "class=\"count-badge\">Unknown</span>").Count);
            Assert.DoesNotContain("No active runs.", html);
            Assert.DoesNotContain("No recent completed runs.", html);
            Assert.DoesNotContain("Last successful history read", html);
        }
        else
        {
            Assert.Contains($"datetime=\"{ReadTime:O}\"", html);
            Assert.Contains("Only the newest bounded history is shown.", html);
            Assert.Equal(2, Regex.Matches(html, $"class=\"count-badge\">{(empty ? 0 : 2)}</span>").Count);
            if (!empty)
            {
                string activeSection = html.Split("Recent completed runs", StringSplitOptions.None)[0];
                string terminalSection = html.Split("Recent completed runs", StringSplitOptions.None)[1];
                Assert.Contains("/operations/prepare/active-1", activeSection);
                Assert.Contains("/operations/prepare/recovering-1", activeSection);
                Assert.DoesNotContain("/operations/prepare/terminal-1", activeSection);
                Assert.Contains("/operations/prepare/terminal-1", terminalSection);
                Assert.Contains("/operations/prepare/unclassified-1", terminalSection);
            }
            if (phase == WorkspaceObservationPhase.Succeeded)
            {
                Assert.DoesNotContain("Stale history", html);
                if (empty)
                {
                    Assert.Contains("No active runs.", html);
                    Assert.Contains("No recent completed runs.", html);
                    Assert.DoesNotContain("Previously observed empty", html);
                }
            }
            else
            {
                Assert.Contains("Stale history", html);
                if (empty)
                {
                    Assert.Contains("Previously observed empty history", html);
                    Assert.DoesNotContain("No active runs.", html);
                    Assert.DoesNotContain("No recent completed runs.", html);
                }
            }
        }
        if (phase == WorkspaceObservationPhase.Loading && retainSuccess)
        {
            Assert.Contains("refreshing.", html);
        }
        if (phase is WorkspaceObservationPhase.Unavailable or WorkspaceObservationPhase.Failed)
        {
            Assert.Contains(phase == WorkspaceObservationPhase.Unavailable
                ? "Run history is unavailable."
                : "Run history read failed.", html);
            Assert.Contains("The selected read failed.", html);
        }
    }

    [Fact]
    public async Task RefreshPreservesTypedRunIdAndWorkflowIdentity()
    {
        await using Fixture fixture = new();
        Mounted<Home> home = await fixture.Renderer.MountAsync<Home>();
        await fixture.WaitForInitialReadsAsync();
        fixture.InitialPreparer.Response.SetResult(History("prepared-1"));
        await fixture.Renderer.WaitForAsync(home.Id,
            page => page.Html.Contains("/operations/prepare/prepared-1", StringComparison.Ordinal));
        ComponentIdentity<WorkflowCard>[] before =
            await fixture.Renderer.ComponentsAsync<WorkflowCard>(home.Id);
        ComponentIdentity<WorkflowCard> prepare =
            Assert.Single(before, card => card.Instance.Workflow.RouteKey == "prepare");
        ComponentIdentity<WorkflowCard> plan =
            Assert.Single(before, card => card.Instance.Workflow.RouteKey == "plan");
        Assert.Equal("prepare", prepare.Key);
        Assert.Equal("plan", plan.Key);

        await fixture.Renderer.SubmitAsync(home.Id, "open-run-form", prepare.Id);
        Assert.Contains("Enter a run ID.", (await fixture.Renderer.MarkupAsync(home.Id)).Html);
        await fixture.Renderer.ChangeAsync(home.Id, "workflow-prepare-run-id", "  shared /#?  ");
        await fixture.Renderer.ChangeAsync(home.Id, "workflow-plan-run-id", "planned-input");
        ControlledRead<AuthoringRunListResponse> refresh = fixture.Authoring.Preparer.Enqueue();
        await fixture.Renderer.ClickAsync(home.Id, "Refresh prepare tickets history");
        await refresh.Entered.Task.WaitAsync(HangGuard);
        fixture.InitialReadiness.Response.SetResult(Services());
        fixture.InitialPlanner.Response.SetResult(new([], false));
        refresh.Response.SetException(
            new HttpRequestException("history failed", null, HttpStatusCode.ServiceUnavailable));
        Markup markup = await fixture.Renderer.WaitForAsync(home.Id,
            page => page.Html.Contains("Stale history", StringComparison.Ordinal) &&
                page.Html.Contains("No recent completed runs.", StringComparison.Ordinal) &&
                page.Html.Contains("Ready to start", StringComparison.Ordinal));

        Assert.Equal("  shared /#?  ", markup.Input("workflow-prepare-run-id").Attribute("value"));
        Assert.Equal("planned-input", markup.Input("workflow-plan-run-id").Attribute("value"));
        Assert.Contains("/operations/prepare/prepared-1", markup.Html);
        Assert.DoesNotContain("Enter a run ID.", markup.Html);
        ComponentIdentity<WorkflowCard>[] after =
            await fixture.Renderer.ComponentsAsync<WorkflowCard>(home.Id);
        foreach (ComponentIdentity<WorkflowCard> original in before)
        {
            ComponentIdentity<WorkflowCard> retained =
                Assert.Single(after, card => Equals(card.Key, original.Key));
            Assert.Equal(original.Id, retained.Id);
            Assert.Same(original.Instance, retained.Instance);
        }

        await fixture.Renderer.SubmitAsync(home.Id, "open-run-form", prepare.Id);
        Assert.Equal($"/operations/prepare/{Uri.EscapeDataString("shared /#?")}", fixture.Navigation.Destination);
        Assert.Equal(0, fixture.Authoring.StartCalls);
    }

    [Theory]
    [InlineData(WorkspaceObservationPhase.Loading)]
    [InlineData(WorkspaceObservationPhase.Unavailable)]
    [InlineData(WorkspaceObservationPhase.Failed)]
    public async Task StaleReadinessNeverDisplaysCurrentReadyBadge(WorkspaceObservationPhase phase)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition workflow = fixture.Catalog.Get("prepare");
        TicketWorkflowReadiness ready = new ReadinessEvaluator().Evaluate(Services(), workflow);
        WorkspaceObservation<TicketWorkflowReadiness> observation = Observation(ready, phase, true);

        string html = await fixture.RenderHtmlAsync<ReadinessObservationPanel>(Parameters(
            (nameof(ReadinessObservationPanel.Workflow), workflow),
            (nameof(ReadinessObservationPanel.Observation), observation)));

        Assert.DoesNotContain("Ready to start", html);
        Assert.DoesNotContain("readiness-strip__services", html);
        Assert.Contains("Previous readiness is stale", html);
        Assert.Contains($"datetime=\"{ReadTime:O}\"", html);
        Assert.DoesNotContain($"datetime=\"{ReadTime.AddMinutes(1):O}\"", html);
        Assert.Contains("current successful readiness read", html);
    }

    [Fact]
    public async Task SuccessfulReadinessWithBlockersShowsEvidenceRatherThanReadFailure()
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition workflow = fixture.Catalog.Get("prepare");
        TicketWorkflowReadiness blocked = new ReadinessEvaluator().Evaluate(
            new ServicesStatusResponse([], ProbeTime), workflow);

        string html = await fixture.RenderHtmlAsync<ReadinessObservationPanel>(Parameters(
            (nameof(ReadinessObservationPanel.Workflow), workflow),
            (nameof(ReadinessObservationPanel.Observation),
                Observation(blocked, WorkspaceObservationPhase.Succeeded, true))));

        Assert.Contains("3 blockers", html);
        Assert.Contains("Jira is not observed", html);
        Assert.Contains("Preparer is not observed", html);
        Assert.Contains($"datetime=\"{ProbeTime:O}\"", html);
        Assert.DoesNotContain("Readiness read failed", html);
        Assert.DoesNotContain("Ready to start", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StartSubmissionRequiresLatestSuccessfulReadiness(HttpStatusCode failureStatus)
    {
        await using Fixture fixture = new();
        fixture.InitialReadiness.Response.SetResult(Services());
        Mounted<TicketRunStart> page = await fixture.Renderer.MountAsync<TicketRunStart>(
            Parameters((nameof(TicketRunStart.Workflow), "prepare")));
        await page.Rendering.WaitAsync(HangGuard);
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(0, fixture.Authoring.StartCalls);

        await fixture.Renderer.ChangeAsync(page.Id, "selection-explicit", "explicit");
        await fixture.Renderer.ChangeAsync(page.Id, "explicit-ticket-keys", "not-a-key");
        await fixture.Renderer.ClickAsync(page.Id, "Continue to review");
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(0, fixture.Authoring.StartCalls);
        Assert.Contains("Review the ticket selection.", (await fixture.Renderer.MarkupAsync(page.Id)).Html);
        await fixture.Renderer.ChangeAsync(page.Id, "explicit-ticket-keys", "fhir-1");
        await fixture.Renderer.ClickAsync(page.Id, "Continue to review");
        Assert.False((await fixture.Renderer.MarkupAsync(page.Id)).Button("Start prepare tickets").Disabled);

        ControlledRead<ServicesStatusResponse> recheck = fixture.Readiness.Refresh.Enqueue();
        Task rechecking = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await recheck.Entered.Task.WaitAsync(HangGuard);
        Markup pending = await fixture.Renderer.MarkupAsync(page.Id);
        Assert.True(pending.Button("Recheck readiness").Disabled);
        Assert.True(pending.Button("Start prepare tickets").Disabled);
        Assert.DoesNotContain("Ready to start", pending.Html);
        Assert.Contains("Wait for a current successful readiness read", pending.Html);
        Task duplicate = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(0, fixture.Authoring.StartCalls);
        Assert.Equal(1, fixture.Readiness.Refresh.Calls);

        recheck.Response.SetException(new HttpRequestException("private failure", null, failureStatus));
        await Task.WhenAll(rechecking, duplicate).WaitAsync(HangGuard);
        Markup failed = await fixture.Renderer.MarkupAsync(page.Id);
        Assert.True(failed.Button("Start prepare tickets").Disabled);
        Assert.False(failed.Button("Recheck readiness").Disabled);
        Assert.DoesNotContain("Ready to start", failed.Html);
        Assert.DoesNotContain("private failure", failed.Html);
        Assert.Contains("Readiness must be successfully rechecked", failed.Html);
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(0, fixture.Authoring.StartCalls);

        ControlledRead<ServicesStatusResponse> recovery = fixture.Readiness.Refresh.Enqueue();
        Task recovering = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await recovery.Entered.Task.WaitAsync(HangGuard);
        recovery.Response.SetResult(Services());
        await recovering.WaitAsync(HangGuard);
        Assert.False((await fixture.Renderer.MarkupAsync(page.Id)).Button("Start prepare tickets").Disabled);

        ControlledRead<AuthoringStartResult> start = new();
        fixture.Authoring.StartHandler = (_, _, ct) => start.ExecuteAsync(ct);
        Task submitting = fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        await start.Entered.Task.WaitAsync(HangGuard);
        Assert.True((await fixture.Renderer.MarkupAsync(page.Id)).Button("Starting once…").Disabled);
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(1, fixture.Authoring.StartCalls);
        start.Response.SetResult(new AuthoringStartResult(null));
        await submitting.WaitAsync(HangGuard);
        Assert.Contains("No authoring candidates were found.", (await fixture.Renderer.MarkupAsync(page.Id)).Html);
        Assert.Equal(1, fixture.Readiness.Cached.Calls);
        Assert.Equal(2, fixture.Readiness.Refresh.Calls);
        Assert.Empty(fixture.Authoring.ListRequests);
    }

    [Fact]
    public async Task UnknownStartOutcomeStillRequiresExplicitReview()
    {
        await using Fixture fixture = new();
        fixture.InitialReadiness.Response.SetResult(Services());
        fixture.InitialPreparer.Response.SetResult(new([], false));
        fixture.Authoring.StartHandler = (_, _, _) => Task.FromException<AuthoringStartResult>(
            new AuthoringMutationOutcomeUnknownException(
                "start", "Preparer", null, null, new IOException("response lost")));
        Mounted<TicketRunStart> page = await fixture.Renderer.MountAsync<TicketRunStart>(
            Parameters((nameof(TicketRunStart.Workflow), "prepare")));
        await page.Rendering.WaitAsync(HangGuard);
        await fixture.Renderer.ClickAsync(page.Id, "Continue to review");

        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");

        Assert.Equal(1, fixture.Authoring.StartCalls);
        Assert.Equal("Preparer", Assert.Single(fixture.Authoring.ListRequests).Service);
        Assert.True((await fixture.Renderer.MarkupAsync(page.Id)).Button("Start prepare tickets").Disabled);
        TicketOperationsService operations = Assert.Single(fixture.Mutations);
        Assert.True(operations.RequiresUnknownStartReview("prepare"));
        ControlledRead<ServicesStatusResponse> recheck = fixture.Readiness.Refresh.Enqueue();
        recheck.Response.SetResult(Services());
        await fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(1, fixture.Authoring.StartCalls);

        await fixture.Renderer.ClickAsync(page.Id, "I inspected the result; allow another start");
        Assert.False(operations.RequiresUnknownStartReview("prepare"));
        fixture.Authoring.StartHandler = (_, _, _) => Task.FromResult(new AuthoringStartResult(null));
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(2, fixture.Authoring.StartCalls);
        Assert.Single(fixture.Authoring.ListRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RouteChangeRejectsOldReadinessCompletion(bool oldCompletesWhileNewReadIsPending)
    {
        await using Fixture fixture = new();
        fixture.InitialReadiness.IgnoreCancellation = true;
        Mounted<TicketRunStart> page = await fixture.Renderer.MountAsync<TicketRunStart>(
            Parameters((nameof(TicketRunStart.Workflow), "prepare")));
        await fixture.InitialReadiness.Entered.Task.WaitAsync(HangGuard);
        Task oldRead = ReadinessCompletion(page.Instance);
        await fixture.Renderer.ClickAsync(page.Id, "Continue to review");
        ControlledRead<ServicesStatusResponse> newRead = fixture.Readiness.Cached.Enqueue();

        Task routeChange = fixture.Renderer.RenderAsync(page.Id,
            Parameters((nameof(TicketRunStart.Workflow), "PLAN")));
        await newRead.Entered.Task.WaitAsync(HangGuard);
        await fixture.Renderer.MarkupAsync(page.Id);
        Task currentRead = ReadinessCompletion(page.Instance);
        Assert.True(fixture.InitialReadiness.Token.IsCancellationRequested);
        Assert.False(newRead.Token.IsCancellationRequested);
        Assert.False(fixture.InitialReadiness.Response.Task.IsCompleted);
        Assert.Contains("Continue to review", (await fixture.Renderer.MarkupAsync(page.Id)).Html);
        await fixture.Renderer.ClickAsync(page.Id, "Continue to review");
        Task duplicate = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        Assert.Equal(2, fixture.Readiness.Cached.Calls);
        Assert.Equal(0, fixture.Readiness.Refresh.Calls);

        if (oldCompletesWhileNewReadIsPending)
        {
            fixture.InitialReadiness.Response.SetResult(Services());
            await oldRead.WaitAsync(HangGuard);
            Markup pending = await fixture.Renderer.MarkupAsync(page.Id);
            Assert.True(pending.Button("Recheck readiness").Disabled);
            Assert.True(pending.Button("Start plan tickets").Disabled);
            Assert.DoesNotContain("Ready to start", pending.Html);
            await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
            Assert.Equal(0, fixture.Authoring.StartCalls);
        }
        newRead.Response.SetException(
            new HttpRequestException("new route failed", null, HttpStatusCode.Forbidden));
        await Task.WhenAll(currentRead, duplicate).WaitAsync(HangGuard);
        if (!oldCompletesWhileNewReadIsPending)
        {
            fixture.InitialReadiness.Response.SetResult(Services());
            await oldRead.WaitAsync(HangGuard);
        }
        await Task.WhenAll(page.Rendering, routeChange).WaitAsync(HangGuard);
        Markup failed = await fixture.Renderer.MarkupAsync(page.Id);
        Assert.True(failed.Button("Start plan tickets").Disabled);
        Assert.False(failed.Button("Recheck readiness").Disabled);
        Assert.Contains("Readiness read failed.", failed.Html);
        Assert.DoesNotContain("Ready to start", failed.Html);
        await fixture.Renderer.SubmitAsync(page.Id, "start-run-form");
        Assert.Equal(0, fixture.Authoring.StartCalls);

        ControlledRead<ServicesStatusResponse> recovery = fixture.Readiness.Refresh.Enqueue();
        Task first = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await recovery.Entered.Task.WaitAsync(HangGuard);
        Task second = fixture.Renderer.ClickAsync(page.Id, "Recheck readiness");
        await fixture.Renderer.MarkupAsync(page.Id);
        Assert.Equal(1, fixture.Readiness.Refresh.Calls);
        recovery.Response.SetResult(Services());
        await Task.WhenAll(first, second).WaitAsync(HangGuard);
        Assert.False((await fixture.Renderer.MarkupAsync(page.Id)).Button("Start plan tickets").Disabled);
        Assert.Empty(fixture.Authoring.ListRequests);
    }

    [Fact]
    public async Task InteractiveHomeStartsOnceAndIgnoresObsoleteNotifications()
    {
        await using Fixture fixture = new();
        Mounted<Home> home = await fixture.Renderer.MountAsync<Home>();
        await fixture.WaitForInitialReadsAsync();
        await fixture.Renderer.WaitForAsync(home.Id,
            page => page.Html.Contains("Loading Preparer run history.", StringComparison.Ordinal) &&
                page.Html.Contains("Loading Planner run history.", StringComparison.Ordinal));
        await fixture.Renderer.RenderAsync(home.Id);
        Assert.Equal(1, fixture.Authoring.Preparer.Calls);
        Assert.Equal(1, fixture.Authoring.Planner.Calls);
        Assert.Equal(1, fixture.Readiness.Cached.Calls);
        PageSession owned = Assert.Single(fixture.PageSessions);
        Func<TicketWorkspaceSnapshot, Task> notification =
            PrivateField<Func<TicketWorkspaceSnapshot, Task>>(home.Instance, "_changed");
        TicketWorkspaceSnapshot baseline = owned.Session.Snapshot;
        TicketWorkspaceSnapshot newer = WithHistory(baseline, "newer", baseline.Revision + 2);
        TicketWorkspaceSnapshot older = WithHistory(baseline, "older", baseline.Revision + 1);

        fixture.Renderer.Events.HoldActions();
        Task newNotification = Task.Run(() => notification(newer));
        await fixture.Renderer.Events.WaitForQueuedAsync(1);
        Task oldNotification = Task.Run(() => notification(older));
        await fixture.Renderer.Events.WaitForQueuedAsync(2);
        await fixture.Renderer.Events.ReleaseNextAsync();
        await fixture.Renderer.Events.ReleaseNextAsync();
        await Task.WhenAll(newNotification, oldNotification).WaitAsync(HangGuard);
        Assert.Same(newer, PrivateField<TicketWorkspaceSnapshot>(home.Instance, "_snapshot"));
        Assert.DoesNotContain("/operations/prepare/older", (await fixture.Renderer.MarkupAsync(home.Id)).Html);

        await using AsyncServiceScope foreignScope = fixture.Root.CreateAsyncScope();
        TicketWorkspaceSession foreignSession =
            foreignScope.ServiceProvider.GetRequiredService<TicketWorkspaceSession>();
        // Only adversarial notifications use reflection; all user actions use
        // their real framework event-handler IDs below.
        Task foreignNotification = Task.Run(() => InvokeNotification(
            home.Instance, foreignSession, WithHistory(newer, "foreign", newer.Revision + 1)));
        await fixture.Renderer.Events.WaitForQueuedAsync(1);
        await fixture.Renderer.Events.ReleaseNextAsync();
        await foreignNotification.WaitAsync(HangGuard);
        Assert.Same(newer, PrivateField<TicketWorkspaceSnapshot>(home.Instance, "_snapshot"));

        Task queuedBeforeDisposal = Task.Run(() =>
            notification(WithHistory(newer, "disposed", newer.Revision + 2)));
        await fixture.Renderer.Events.WaitForQueuedAsync(1);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => home.Instance.DisposeAsync().AsTask())
            .WaitAsync(HangGuard);
        Assert.False(queuedBeforeDisposal.IsCompleted);
        Assert.True(owned.Scope.Disposed.Task.IsCompleted);
        Assert.True(fixture.InitialPlanner.Token.IsCancellationRequested);
        await fixture.Renderer.Events.ReleaseNextAsync();
        await queuedBeforeDisposal.WaitAsync(HangGuard);
        Assert.Same(newer, PrivateField<TicketWorkspaceSnapshot>(home.Instance, "_snapshot"));
        Assert.Throws<ObjectDisposedException>(owned.Session.Start);
        fixture.Renderer.Events.ResumeActions();
    }

    [Fact]
    public async Task PageScopeDisposesSessionWithoutOwningMutationGate()
    {
        await using Fixture fixture = new();
        Assert.Equal(ServiceLifetime.Transient, fixture.Lifetime<TicketWorkspaceReader>());
        Assert.Equal(ServiceLifetime.Transient, fixture.Lifetime<TicketWorkspaceSession>());
        Assert.Equal(ServiceLifetime.Scoped, fixture.Lifetime<TicketOperationsService>());
        TicketOperationsService mutation =
            fixture.Circuit.ServiceProvider.GetRequiredService<TicketOperationsService>();
        ControlledRead<AuthoringRunListResponse> secondPreparer = fixture.Authoring.Preparer.Enqueue();
        ControlledRead<AuthoringRunListResponse> secondPlanner = fixture.Authoring.Planner.Enqueue();
        ControlledRead<ServicesStatusResponse> secondReadiness = fixture.Readiness.Cached.Enqueue();
        Mounted<Home> first = await fixture.Renderer.MountAsync<Home>();
        await fixture.WaitForInitialReadsAsync();
        Mounted<Home> second = await fixture.Renderer.MountAsync<Home>();
        await Task.WhenAll(secondPreparer.Entered.Task, secondPlanner.Entered.Task, secondReadiness.Entered.Task)
            .WaitAsync(HangGuard);
        PageSession firstPage = fixture.PageSessions[0];
        PageSession secondPage = fixture.PageSessions[1];
        Assert.NotSame(firstPage.Session, secondPage.Session);
        Assert.NotSame(firstPage.Scope, secondPage.Scope);
        Assert.Single(fixture.Mutations);
        ControlledRead<AuthoringRunListResponse> reconciliation = fixture.Authoring.Preparer.Enqueue();
        reconciliation.Response.SetResult(new([], false));
        fixture.Authoring.StartHandler = (_, _, _) => Task.FromException<AuthoringStartResult>(
            new AuthoringMutationOutcomeUnknownException(
                "start", "Preparer", null, null, new IOException("response lost")));
        TicketStartResult unknown = await mutation.StartAsync(
            new("prepare", TicketSelectionMode.Configured));
        Assert.Equal(TicketOperationDisposition.OutcomeUnknown, unknown.Disposition);

        await fixture.Renderer.RemoveAsync(first.Id);
        await firstPage.Scope.Disposed.Task.WaitAsync(HangGuard);

        Assert.Throws<ObjectDisposedException>(firstPage.Session.Start);
        Assert.True(fixture.InitialPreparer.Token.IsCancellationRequested);
        Assert.True(fixture.InitialPlanner.Token.IsCancellationRequested);
        Assert.True(fixture.InitialReadiness.Token.IsCancellationRequested);
        Assert.False(secondPreparer.Token.IsCancellationRequested);
        Assert.False(secondPlanner.Token.IsCancellationRequested);
        Assert.False(secondReadiness.Token.IsCancellationRequested);
        Assert.False(secondPage.Scope.Disposed.Task.IsCompleted);
        Assert.Same(mutation, fixture.Circuit.ServiceProvider.GetRequiredService<TicketOperationsService>());
        Assert.Single(fixture.Mutations);
        TicketStartResult blocked = await mutation.StartAsync(new("prepare", TicketSelectionMode.Configured));
        Assert.Equal(TicketOperationDisposition.OutcomeUnknown, blocked.Disposition);
        Assert.Equal(1, fixture.Authoring.StartCalls);

        await fixture.Renderer.RemoveAsync(second.Id);
        await secondPage.Scope.Disposed.Task.WaitAsync(HangGuard);
        Assert.True(mutation.RequiresUnknownStartReview("prepare"));
        await fixture.Circuit.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mutation.StartAsync(new("prepare", TicketSelectionMode.Configured)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyPublication_OffersRefreshAndGenerationSeparately(
        bool proofReady)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare =
            fixture.Catalog.Get("prepare");
        AuthoringRunStatus sourceRun =
            Run("source-run", "completed", terminal: true);
        int reconciliations = 0;
        int refreshes = 0;
        List<bool> generations = [];
        Mounted<PublicationPanel> panel =
            await fixture.Renderer.MountAsync<PublicationPanel>(
                Parameters(
                    (nameof(PublicationPanel.Details), Details(
                        prepare,
                        sourceRun,
                        Publication(
                            prepare,
                            sourceRun,
                            proofReady ? ReadyReadiness() : DegradedReadiness()))),
                    (nameof(PublicationPanel.OnReconcilePublication),
                        EventCallback.Factory.Create(this, () => { reconciliations++; })),
                    (nameof(PublicationPanel.OnRefreshPublication),
                        EventCallback.Factory.Create(this, () => { refreshes++; })),
                    (nameof(PublicationPanel.OnPublish),
                        EventCallback.Factory.Create<bool>(this, force => generations.Add(force)))));
        await panel.Rendering.WaitAsync(HangGuard);
        Markup markup = await fixture.Renderer.MarkupAsync(panel.Id);

        Assert.Contains(
            proofReady
                ? "Publication readiness is verified."
                : "Publication readiness is degraded.",
            markup.Html);
        Assert.False(markup.Button(
            "Reconcile changed tickets and refresh snapshot").Disabled);
        Assert.False(markup.Button(
            "Repair publication metadata and snapshot").Disabled);
        Assert.False(markup.Button("Regenerate review site").Disabled);
        Assert.Contains("href=\"/api-test?source=Jira\"", markup.Html);
        string text = Normalize(Regex.Replace(markup.Html, "<[^>]*>", " "));
        Assert.Contains("re-authors changed tickets", text);
        Assert.Contains("carries unchanged output forward", text);
        Assert.Contains("Advanced: repair publication metadata only", text);
        Assert.Contains(
            "Metadata repair does not re-author tickets or recompute grouping.",
            text);
        Assert.Contains("It refuses changed Jira revisions.", text);
        Assert.Contains("Preview public people population", text);
        Assert.Contains("Apply public people population", text);
        Assert.Contains("This refresh never performs source backfill.", text);
        Assert.Contains("It uses this run's frozen snapshot, not current source data.", text);
        Assert.DoesNotContain("Replace existing output", markup.Html);
        Assert.DoesNotContain("Replace and regenerate", markup.Html);

        await fixture.Renderer.ClickAsync(
            panel.Id,
            "Reconcile changed tickets and refresh snapshot");
        Assert.Equal(1, reconciliations);
        Assert.Equal(0, refreshes);
        Assert.Empty(generations);
        await fixture.Renderer.ClickAsync(
            panel.Id,
            "Repair publication metadata and snapshot");
        Assert.Equal(1, refreshes);
        Assert.Equal(1, reconciliations);
        Assert.Empty(generations);
        await fixture.Renderer.ClickAsync(panel.Id, "Regenerate review site");
        Assert.True(Assert.Single(generations));
        Assert.Equal(1, refreshes);
        Assert.Equal(1, reconciliations);
        Assert.Equal(0, fixture.Authoring.RefreshCalls);
        Assert.Empty(fixture.Readiness.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coverage_IsSeparateFromReadiness(bool proofReady)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare = fixture.Catalog.Get("prepare");
        AuthoringRunStatus run = Run("source-run", "completed", terminal: true);
        ReviewSitePublication publication = Publication(
            prepare, run, proofReady ? ReadyReadiness() : DegradedReadiness());
        publication = publication with
        {
            Manifest = publication.Manifest with
            {
                JiraSourceLastSuccessfulRefreshAt =
                    new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
            },
        };
        string html = await fixture.RenderHtmlAsync<PublicationPanel>(
            Parameters((nameof(PublicationPanel.Details), Details(prepare, run, publication))));
        string text = Normalize(Regex.Replace(html, "<[^>]*>", " "));

        Assert.Contains("Provenance readiness", text);
        Assert.Contains(
            proofReady ? "Publication readiness is verified." : "Publication readiness is degraded.",
            text);
        Assert.Contains("Proof readiness does not establish complete public names or links.", text);
        Assert.Contains("Upstream Jira last successful refresh (provenance): 2026-09-20 12:00:00 UTC", text);
        Assert.Contains("This is not the selected-corpus ticket date.", text);
        Assert.Contains("3 tickets across 2 projects", text);
        Assert.Contains("Date coverage complete", text);
        Assert.Contains("Valid self-ticket Jira dates 3 / 3 tickets", text);
        Assert.Contains("Maximum known self-ticket Jira update 2026-09-15 00:30:00 UTC", text);
        Assert.Contains("title suffix independently of provenance readiness", text);
        Assert.Contains("Reporter 1 / 3 tickets", text);
        Assert.Contains("Assignee 0 / 3 tickets", text);
        Assert.Contains("In-person requester 1 / 3 tickets", text);
        Assert.Contains("Counts measure tickets with public names, not distinct people.", text);
        Assert.Contains("An empty Assignee does not establish whether someone is assigned.", text);
        Assert.Contains("A current policy marker does not prove a display name exists or that a role is absent.", text);
        Assert.Contains("Related-item rows by kind, not ticket counts.", text);
        Assert.Contains("Resolved safe links", text);
        Assert.Contains("Unresolved with retained safe URLs", text);
        Assert.Contains("Without usable URL", text);
        Assert.Contains("repo 3 1 1 1", text);
        Assert.Contains("A retained safe URL does not certify current resolution or source backing.", text);
        Assert.DoesNotContain("unassigned", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0, DiscussionDateCoverage.Empty)]
    [InlineData(3, 0, DiscussionDateCoverage.None)]
    [InlineData(3, 2, DiscussionDateCoverage.Partial)]
    public async Task IncompleteDateCoverageDoesNotInventCompleteDates(
        int tickets, int dates, string coverage)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare = fixture.Catalog.Get("prepare");
        AuthoringRunStatus run = Run("source-run", "completed", terminal: true);
        DiscussionCorpusSummary corpus = new(
            tickets, tickets == 0 ? 0 : 1, dates,
            dates == 0 ? null : new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.Zero),
            coverage, 0, 0, 0, []);
        string html = await fixture.RenderHtmlAsync<PublicationPanel>(
            Parameters((nameof(PublicationPanel.Details), Details(
                prepare, run, Publication(prepare, run, ReadyReadiness(), corpus,
                    displayTitle: prepare.SiteTitle)))));
        string text = Normalize(Regex.Replace(html, "<[^>]*>", " "));

        Assert.Contains($"Date coverage {coverage}", text);
        Assert.Contains($"Valid self-ticket Jira dates {dates} / {tickets} tickets", text);
        Assert.Contains("Empty or incomplete date coverage does not supply a ticket-date title suffix", text);
        Assert.DoesNotContain("Complete selected-corpus dates supply", text);
        if (dates == 0)
        {
            Assert.Contains("Unavailable; no valid self-ticket date is recorded.", text);
            Assert.DoesNotContain("2026-09-15", text);
        }
        else
        {
            Assert.Contains("2026-09-15 00:30:00 UTC", text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPublication_HasUnknownCoverageNotZeroCoverage(bool proofReady)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare = fixture.Catalog.Get("prepare");
        AuthoringRunStatus run = Run("source-run", "completed", terminal: true);
        string html = await fixture.RenderHtmlAsync<PublicationPanel>(
            Parameters((nameof(PublicationPanel.Details), Details(
                prepare, run, Publication(
                    prepare, run, proofReady ? ReadyReadiness() : null, legacy: true)))));
        string text = Normalize(Regex.Replace(html, "<[^>]*>", " "));

        Assert.Contains("Selected-corpus coverage is unavailable.", text);
        Assert.Contains("unknown, not zero; its stored title is unchanged.", text);
        Assert.DoesNotContain("0 /", text);
        Assert.DoesNotContain("Date coverage complete", text);
        Assert.DoesNotContain("2026-09-15", text);
        Assert.Contains(
            "Reconcile changed tickets and refresh snapshot",
            text);
        Assert.Contains(
            "Advanced: repair publication metadata only",
            text);
        Assert.Contains(
            "Repair publication metadata and snapshot",
            text);
        Assert.Contains("Regenerate review site", text);
        if (!proofReady)
        {
            Assert.Contains("legacy manifest predates structured readiness evidence", text);
        }
    }

    [Theory]
    [InlineData("prepare", false, false)]
    [InlineData("prepare", false, true)]
    [InlineData("prepare", true, true)]
    [InlineData("plan", false, false)]
    [InlineData("plan", true, false)]
    public async Task UnpublishedRefreshOutputAndApplyingRunsOfferOnlyGeneration(
        string workflow, bool published, bool refreshOutput)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition definition = fixture.Catalog.Get(workflow);
        AuthoringRunStatus run = Run("run-1", "completed", terminal: true) with
        {
            Purpose = refreshOutput ? "publication-refresh" : "authoring",
            SourceRunId = refreshOutput ? "source-run" : null,
        };
        string html = await fixture.RenderHtmlAsync<PublicationPanel>(
            Parameters((nameof(PublicationPanel.Details), Details(
                definition, run,
                published ? Publication(definition, run, ReadyReadiness()) : null))));

        Assert.Contains(published ? "Regenerate review site" : "Generate review site", html);
        Assert.DoesNotContain(
            "Reconcile changed tickets and refresh snapshot",
            html);
        Assert.DoesNotContain(
            "Repair publication metadata and snapshot",
            html);
        Assert.DoesNotContain("Replace existing output", html);
    }

    [Theory]
    [InlineData("running", false, false)]
    [InlineData("error", false, false)]
    [InlineData("completed", false, false)]
    [InlineData("completed-database-only", true, true)]
    [InlineData("completed", true, true)]
    [InlineData("superseded", true, false)]
    public async Task IneligibleRunsOfferNeitherPublicationAction(
        string status, bool terminal, bool databaseOnly)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare = fixture.Catalog.Get("prepare");
        AuthoringRunStatus run = Run("run-1", status, terminal) with
        {
            DatabaseOnly = databaseOnly,
        };
        string html = await fixture.RenderHtmlAsync<PublicationPanel>(
            Parameters((nameof(PublicationPanel.Details), Details(
                prepare, run, Publication(prepare, run, ReadyReadiness())))));

        Assert.DoesNotContain(
            "Reconcile changed tickets and refresh snapshot",
            html);
        Assert.DoesNotContain(
            "Repair publication metadata and snapshot",
            html);
        Assert.DoesNotContain("Generate review site", html);
        Assert.DoesNotContain("Regenerate review site", html);
        Assert.DoesNotContain("Replace existing output", html);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task PublicationActionsRespectMutationAndUnknownOutcomeGates(
        bool busy, bool refreshing, bool disabled, bool unknownRefresh)
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare = fixture.Catalog.Get("prepare");
        AuthoringRunStatus run = Run("run-1", "completed", terminal: true);
        Mounted<PublicationPanel> panel = await fixture.Renderer.MountAsync<PublicationPanel>(
            Parameters(
                (nameof(PublicationPanel.Details), Details(
                    prepare, run, Publication(prepare, run, ReadyReadiness()))),
                (nameof(PublicationPanel.Busy), busy),
                (nameof(PublicationPanel.Refreshing), refreshing),
                (nameof(PublicationPanel.Disabled), disabled),
                (nameof(PublicationPanel.RefreshDisabled), unknownRefresh)));
        await panel.Rendering.WaitAsync(HangGuard);
        Markup markup = await fixture.Renderer.MarkupAsync(panel.Id);

        Assert.True(markup.Button(
            refreshing
                ? "Starting reconciliation\u2026"
                : "Reconcile changed tickets and refresh snapshot").Disabled);
        Assert.True(markup.Button(
            "Repair publication metadata and snapshot").Disabled);
        Assert.Equal(!unknownRefresh, markup.Button(
            busy ? "Generating site\u2026" : "Regenerate review site").Disabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RefreshRun_ShowsDurableCorpusDifference(int additionalTickets)
    {
        await using Fixture fixture = new();
        AuthoringRunCorpusComparison comparison = new(
            "original-snapshot", 1177, 1177 + additionalTickets, additionalTickets);
        AuthoringRunStatus refreshRun = Run("refresh-run", "completed", terminal: true) with
        {
            Purpose = "publication-refresh",
            SourceRunId = "source-run",
            CorpusComparison = comparison,
        };
        string responseJson = JsonSerializer.Serialize(new AuthoringRunResponse(refreshRun, []));
        fixture.Authoring.GetHandler = (service, runId, _) =>
        {
            Assert.Equal("Preparer", service);
            Assert.Equal("refresh-run", runId);
            return Task.FromResult(
                JsonSerializer.Deserialize<AuthoringRunResponse>(responseJson)
                ?? throw new InvalidOperationException("The run fixture is empty."));
        };
        Mounted<TicketRunDetail> page = await fixture.Renderer.MountAsync<TicketRunDetail>(
            Parameters(
                (nameof(TicketRunDetail.Workflow), "prepare"),
                (nameof(TicketRunDetail.RunId), "refresh-run")));
        await page.Rendering.WaitAsync(HangGuard);
        Markup initial = await fixture.Renderer.WaitForAsync(
            page.Id, markup => markup.Html.Contains("Frozen refresh corpus comparison", StringComparison.Ordinal));

        await fixture.Renderer.ClickAsync(page.Id, "Refresh now");
        Markup reloaded = await fixture.Renderer.MarkupAsync(page.Id);
        foreach (Markup markup in new[] { initial, reloaded })
        {
            string text = Normalize(Regex.Replace(markup.Html, "<[^>]*>", " "));
            Assert.Contains("Source snapshot original-snapshot", text);
            Assert.Contains("Source exported tickets 1177", text);
            Assert.Contains($"Current accepted tickets {1177 + additionalTickets}", text);
            Assert.Contains($"Additional current tickets {additionalTickets}", text);
            Assert.Contains("not the run's completed-item count", text);
            Assert.Equal(additionalTickets > 0,
                text.Contains("Additional current output is included.", StringComparison.Ordinal));
            Assert.Contains("Generate review site", text);
            Assert.DoesNotContain("Open review site", text);
            Assert.DoesNotContain(
                "Reconcile changed tickets and refresh snapshot",
                text);
            Assert.DoesNotContain(
                "Repair publication metadata and snapshot",
                text);
        }
        Assert.Equal(2, fixture.Authoring.GetCalls);
        Assert.Equal(0, fixture.Authoring.RefreshCalls);
        Assert.Empty(fixture.Authoring.ListRequests);
    }

    [Fact]
    public async Task TicketRunDetailNavigatesToReturnedPublicationRefreshRun()
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare =
            fixture.Catalog.Get("prepare");
        AuthoringRunStatus sourceRun =
            Run("source-run", "completed", terminal: true);
        fixture.ReviewSites.Publication = Publication(
            prepare,
            sourceRun,
            ReadyReadiness());
        fixture.Authoring.GetHandler = (_, runId, _) =>
        {
            Assert.Equal("source-run", runId);
            return Task.FromResult(
                new AuthoringRunResponse(sourceRun, []));
        };
        AuthoringRunStatus refreshRun = Run(
            "refresh-run",
            "queued",
            terminal: false) with
        {
            Purpose = "publication-refresh",
            SourceRunId = "source-run",
        };
        fixture.Authoring.RefreshHandler =
            (serviceName, sourceRunId, _) =>
            {
                Assert.Equal("Preparer", serviceName);
                Assert.Equal("source-run", sourceRunId);
                return Task.FromResult(
                    new AuthoringRunResponse(refreshRun, []));
            };

        Mounted<TicketRunDetail> page =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "prepare"),
                    (nameof(TicketRunDetail.RunId), "source-run")));
        await page.Rendering.WaitAsync(HangGuard);
        await fixture.Renderer.WaitForAsync(
            page.Id,
            markup => markup.Html.Contains(
                "Repair publication metadata and snapshot",
                StringComparison.Ordinal));

        await fixture.Renderer.ClickAsync(
            page.Id,
            "Repair publication metadata and snapshot");

        Assert.Equal(
            "/operations/prepare/refresh-run",
            fixture.Navigation.Destination);
        Assert.Equal(2, fixture.Authoring.GetCalls);
        Assert.Equal(1, fixture.Authoring.RefreshCalls);
        Assert.Equal(
            ("Preparer", "source-run"),
            Assert.Single(fixture.Authoring.RefreshRequests));
    }

    [Fact]
    public async Task TicketRunDetailRestoresAmbiguousRefreshReviewOnRemount()
    {
        await using Fixture fixture = new();
        TicketWorkflowDefinition prepare =
            fixture.Catalog.Get("prepare");
        AuthoringRunStatus sourceRun =
            Run("source-run", "completed", terminal: true);
        fixture.ReviewSites.Publication = Publication(
            prepare,
            sourceRun,
            ReadyReadiness());
        fixture.Authoring.GetHandler = (_, _, _) =>
            Task.FromResult(
                new AuthoringRunResponse(sourceRun, []));
        fixture.Authoring.RefreshHandler =
            (_, sourceRunId, _) =>
                Task.FromException<AuthoringRunResponse>(
                    new AuthoringMutationOutcomeUnknownException(
                        "publication-refresh",
                        "Preparer",
                        sourceRunId,
                        null,
                        new IOException("response lost")));
        fixture.InitialPreparer.Response.SetResult(
            new AuthoringRunListResponse(
            [
                Run(
                    "candidate-refresh",
                    "queued",
                    terminal: false) with
                {
                    CreatedAt = ReadTime.AddSeconds(1),
                    Purpose = "publication-refresh",
                    SourceRunId = "source-run",
                    CorpusComparison = new(
                        "original-snapshot", 1177, 1180, 3),
                },
                Run(
                    "unrelated",
                    "queued",
                    terminal: false) with
                {
                    CreatedAt = ReadTime.AddSeconds(1),
                    Purpose = "authoring",
                },
            ],
            Truncated: false));

        Mounted<TicketRunDetail> page =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "prepare"),
                    (nameof(TicketRunDetail.RunId), "source-run")));
        await page.Rendering.WaitAsync(HangGuard);
        await fixture.Renderer.WaitForAsync(
            page.Id,
            markup => markup.Html.Contains(
                "Repair publication metadata and snapshot",
                StringComparison.Ordinal));

        await fixture.Renderer.ClickAsync(
            page.Id,
            "Repair publication metadata and snapshot");
        Markup reconciled = await fixture.Renderer.WaitForAsync(
            page.Id,
            markup => markup.Html.Contains(
                "/operations/prepare/candidate-refresh",
                StringComparison.Ordinal));

        Assert.Contains(
            "Publication refresh outcome unknown.",
            reconciled.Html);
        Assert.Contains("candidate-refresh", reconciled.Html);
        Assert.DoesNotContain(
            "/operations/prepare/unrelated",
            reconciled.Html);
        Assert.Null(fixture.Navigation.Destination);
        Assert.Equal(1, fixture.Authoring.RefreshCalls);
        Assert.Single(fixture.Authoring.ListRequests);

        TicketOperationsService operations =
            Assert.Single(fixture.Mutations);
        Assert.True(
            operations.RequiresUnknownPublicationRefreshReview(
                "prepare",
                "source-run"));
        await fixture.Renderer.RemoveAsync(page.Id);

        Mounted<TicketRunDetail> remounted =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "PREPARE"),
                    (nameof(TicketRunDetail.RunId), "source-run")));
        await remounted.Rendering.WaitAsync(HangGuard);
        Markup restored = await fixture.Renderer.WaitForAsync(
            remounted.Id,
            markup => markup.Html.Contains(
                "/operations/prepare/candidate-refresh",
                StringComparison.Ordinal));

        Assert.Same(
            operations,
            fixture.Circuit.ServiceProvider
                .GetRequiredService<TicketOperationsService>());
        Assert.True(
            restored.Button(
                "Repair publication metadata and snapshot").Disabled);
        Assert.False(restored.Button("Regenerate review site").Disabled);
        TicketPublicationRefreshResult restoredReview =
            Assert.IsType<TicketPublicationRefreshResult>(
                operations.GetUnknownPublicationRefreshReview("prepare", "source-run"));
        Assert.Equal(
            new AuthoringRunCorpusComparison("original-snapshot", 1177, 1180, 3),
            Assert.Single(restoredReview.Candidates).CorpusComparison);
        await fixture.Renderer.ClickAsync(
            remounted.Id,
            "Repair publication metadata and snapshot");
        Assert.Equal(1, fixture.Authoring.RefreshCalls);
        Assert.Single(fixture.Authoring.ListRequests);

        await fixture.Renderer.ClickAsync(
            remounted.Id,
            "I inspected the refresh result; allow another refresh");
        Assert.False(
            operations.RequiresUnknownPublicationRefreshReview(
                "prepare",
                "source-run"));
        Assert.False(
            (await fixture.Renderer.MarkupAsync(remounted.Id))
                .Button(
                    "Repair publication metadata and snapshot")
                .Disabled);
    }

    [Fact]
    public async Task ReconciliationRunRendersDeltaPromotionAndInvalidation()
    {
        await using Fixture fixture = new();
        PublicationReconciliationStatusResult status =
            ReconciliationStatus(
                promotionState: "staged",
                journalState: "grouping-complete",
                invalidatedTicketKeys: ["FHIR-2"]);
        fixture.Authoring.GetHandler = (serviceName, runId, _) =>
        {
            Assert.Equal("Preparer", serviceName);
            Assert.Equal("reconciliation-run", runId);
            return Task.FromResult(
                new AuthoringRunResponse(status.Run, status.Items));
        };
        fixture.Authoring.ReconciliationStatusHandler =
            (serviceName, runId, _) =>
            {
                Assert.Equal("Preparer", serviceName);
                Assert.Equal("reconciliation-run", runId);
                return Task.FromResult(status);
            };

        Mounted<TicketRunDetail> page =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "prepare"),
                    (nameof(TicketRunDetail.RunId), "reconciliation-run")));
        await page.Rendering.WaitAsync(HangGuard);
        Markup markup = await fixture.Renderer.WaitForAsync(
            page.Id,
            value => value.Html.Contains(
                "Frozen ticket delta and source revisions.",
                StringComparison.Ordinal));
        string text = Normalize(
            Regex.Replace(markup.Html, "<[^>]*>", " "));

        Assert.Contains("Publication reconciliation", text);
        Assert.Contains("Stable Jira generation jira-generation-42", text);
        Assert.Contains("Accepted tickets 2", text);
        Assert.Contains("Carried forward 1", text);
        Assert.Contains("Changed / re-authored 1", text);
        Assert.Contains("Promotion state staged", text);
        Assert.Contains(
            "Recovery journal grouping-complete · mutation fence held",
            text);
        Assert.Contains(
            "Later Jira revisions invalidated staged work.",
            text);
        Assert.Contains(
            "FHIR-1 carry-forward revision-1 revision-1",
            text);
        Assert.Contains(
            "FHIR-2 re-author revision-1 revision-2",
            text);
        Assert.Equal(1, fixture.Authoring.ReconciliationStatusCalls);
        Assert.Equal(
            ("Preparer", "reconciliation-run"),
            Assert.Single(
                fixture.Authoring.ReconciliationStatusRequests));
    }

    [Fact]
    public async Task PendingReconciliationOffersRetryAndShowsPromotionWarning()
    {
        await using Fixture fixture = new();
        PublicationReconciliationStatusResult pending =
            ReconciliationStatus(
                "snapshot-publish-pending",
                "snapshot-publish-pending");
        PublicationReconciliationStatusResult ready =
            ReconciliationStatus("ready", "ready");
        fixture.Authoring.GetHandler = (_, _, _) =>
            Task.FromResult(
                new AuthoringRunResponse(pending.Run, pending.Items));
        fixture.Authoring.ReconciliationStatusHandler =
            (_, _, _) => Task.FromResult(pending);
        fixture.Authoring.ReconciliationRetryHandler =
            (serviceName, runId, _) =>
            {
                Assert.Equal("Preparer", serviceName);
                Assert.Equal("reconciliation-run", runId);
                return Task.FromResult(
                    new PublicationReconciliationRetryResult(
                        ready,
                        RecoveryStarted: true));
            };

        Mounted<TicketRunDetail> page =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "prepare"),
                    (nameof(TicketRunDetail.RunId), "reconciliation-run")));
        await page.Rendering.WaitAsync(HangGuard);
        Markup pendingMarkup = await fixture.Renderer.WaitForAsync(
            page.Id,
            value => value.Html.Contains(
                "Snapshot publication recovery is pending.",
                StringComparison.Ordinal));
        string pendingText = Normalize(
            Regex.Replace(pendingMarkup.Html, "<[^>]*>", " "));

        Assert.Contains(
            "Canonical database promotion has already occurred and competing mutations remain blocked.",
            pendingText);
        Assert.False(
            pendingMarkup.Button("Retry snapshot publication").Disabled);
        Assert.False(
            pendingMarkup.Button("Abandon without publication\u2026").Disabled);

        await fixture.Renderer.ClickAsync(
            page.Id,
            "Retry snapshot publication");
        Markup retried = await fixture.Renderer.WaitForAsync(
            page.Id,
            value => Normalize(
                Regex.Replace(value.Html, "<[^>]*>", " "))
                .Contains("Promotion state ready", StringComparison.Ordinal));

        Assert.DoesNotContain(
            "Snapshot publication recovery is pending.",
            retried.Html);
        Assert.DoesNotContain("Retry snapshot publication", retried.Html);
        Assert.DoesNotContain("Abandon without publication", retried.Html);
        Assert.Equal(1, fixture.Authoring.ReconciliationRetryCalls);
        Assert.Equal(
            ("Preparer", "reconciliation-run"),
            Assert.Single(
                fixture.Authoring.ReconciliationRetryRequests));
    }

    [Fact]
    public async Task PendingReconciliationWarnsBeforeAuditedAbandonment()
    {
        await using Fixture fixture = new();
        PublicationReconciliationStatusResult pending =
            ReconciliationStatus(
                "snapshot-publish-pending",
                "snapshot-publish-pending");
        PublicationReconciliationStatusResult abandoned =
            ReconciliationStatus(
                "canonical-unpublished",
                "canonical-unpublished");
        fixture.Authoring.GetHandler = (_, _, _) =>
            Task.FromResult(
                new AuthoringRunResponse(pending.Run, pending.Items));
        fixture.Authoring.ReconciliationStatusHandler =
            (_, _, _) => Task.FromResult(pending);
        fixture.Authoring.ReconciliationAbandonHandler =
            (serviceName, runId, reason, _) =>
            {
                Assert.Equal("Preparer", serviceName);
                Assert.Equal("reconciliation-run", runId);
                Assert.Equal("storage incident", reason);
                return Task.FromResult(
                    new PublicationReconciliationAbandonResult(
                        abandoned,
                        ReadTime,
                        reason));
            };

        Mounted<TicketRunDetail> page =
            await fixture.Renderer.MountAsync<TicketRunDetail>(
                Parameters(
                    (nameof(TicketRunDetail.Workflow), "prepare"),
                    (nameof(TicketRunDetail.RunId), "reconciliation-run")));
        await page.Rendering.WaitAsync(HangGuard);
        await fixture.Renderer.WaitForAsync(
            page.Id,
            value => value.Html.Contains(
                "Snapshot publication recovery is pending.",
                StringComparison.Ordinal));

        await fixture.Renderer.ClickAsync(
            page.Id,
            "Abandon without publication\u2026");
        Markup warning = await fixture.Renderer.MarkupAsync(page.Id);
        string warningText = Normalize(
            Regex.Replace(warning.Html, "<[^>]*>", " "));

        Assert.Contains(
            "Abandonment does not roll back canonical data.",
            warningText);
        Assert.Contains(
            "It leaves this database epoch canonical without a replacement publication and restricts later snapshot-producing workflows.",
            warningText);
        Assert.True(
            warning.Button(
                "Confirm canonical-unpublished abandonment").Disabled);

        await fixture.Renderer.ChangeAsync(
            page.Id,
            "abandonment-reason",
            "  storage incident  ");
        Assert.False(
            (await fixture.Renderer.MarkupAsync(page.Id))
                .Button("Confirm canonical-unpublished abandonment")
                .Disabled);
        await fixture.Renderer.ClickAsync(
            page.Id,
            "Confirm canonical-unpublished abandonment");
        Markup completed = await fixture.Renderer.WaitForAsync(
            page.Id,
            value => value.Html.Contains(
                "Canonical data is unpublished.",
                StringComparison.Ordinal));
        string completedText = Normalize(
            Regex.Replace(completed.Html, "<[^>]*>", " "));

        Assert.Contains(
            "No replacement snapshot was produced; snapshot-producing workflows remain restricted for this canonical epoch.",
            completedText);
        Assert.DoesNotContain("Retry snapshot publication", completed.Html);
        Assert.DoesNotContain("Abandon without publication", completed.Html);
        Assert.Equal(1, fixture.Authoring.ReconciliationAbandonCalls);
        Assert.Equal(
            ("Preparer", "reconciliation-run", "storage incident"),
            Assert.Single(
                fixture.Authoring.ReconciliationAbandonRequests));
    }

    private static PublicationReconciliationStatusResult
        ReconciliationStatus(
            string promotionState,
            string? journalState,
            IReadOnlyList<string>? invalidatedTicketKeys = null)
    {
        IReadOnlyList<string> invalidated =
            invalidatedTicketKeys ?? [];
        bool fenceHeld = string.Equals(
            promotionState,
            "snapshot-publish-pending",
            StringComparison.Ordinal) ||
            string.Equals(
                promotionState,
                "staged",
                StringComparison.Ordinal);
        AuthoringRunReconciliationCounts counts =
            new(2, 1, 1, invalidated.Count);
        AuthoringRunStatus run =
            Run("reconciliation-run", "completed", terminal: true) with
            {
                Purpose = "publication-reconciliation",
                SourceRunId = "source-run",
                ReconciliationCounts = counts,
                Recovery = new(
                    promotionState,
                    journalState,
                    fenceHeld,
                    invalidated.Count > 0
                        ? "revision-invalidation"
                        : null),
            };
        PublicationReconciliationComparison comparison = new(
            1,
            "source-run",
            "source-snapshot",
            "source-snapshot-sha",
            "jira-generation-42",
            ReadTime,
            "corpus-fingerprint",
            [
                new PublicationReconciliationItemDecision(
                    "FHIR-1",
                    "carry-forward",
                    "revision-1",
                    "revision-1",
                    "receipt-1",
                    "source-item-1",
                    "source-run",
                    "authored-1",
                    "grouping-1"),
                new PublicationReconciliationItemDecision(
                    "FHIR-2",
                    "re-author",
                    "revision-1",
                    "revision-2",
                    "receipt-2",
                    "source-item-2",
                    "source-run",
                    "authored-2",
                    "grouping-2"),
            ]);
        PublicationReconciliationPromotionStatus promotion = new(
            promotionState,
            journalState,
            fenceHeld,
            LastRecoveryAttemptAt: string.Equals(
                promotionState,
                "snapshot-publish-pending",
                StringComparison.Ordinal)
                    ? ReadTime
                    : null,
            FailureCode: invalidated.Count > 0
                ? "revision-invalidation"
                : null,
            FailureDetail: invalidated.Count > 0
                ? "A ticket changed after the frozen comparison."
                : null,
            AbandonedAt: string.Equals(
                promotionState,
                "canonical-unpublished",
                StringComparison.Ordinal)
                    ? ReadTime
                    : null,
            AbandonmentReason: string.Equals(
                promotionState,
                "canonical-unpublished",
                StringComparison.Ordinal)
                    ? "storage incident"
                    : null);
        return new PublicationReconciliationStatusResult(
            run,
            [],
            comparison,
            counts,
            [
                new PublicationReconciliationGroupingImpact(
                    "fhir-core",
                    ["FHIR-2"],
                    "baseline-corpus",
                    "baseline-output",
                    "baseline-protected",
                    "staged-corpus",
                    "staged-output",
                    "staged-protected",
                    Complete: true),
            ],
            promotion,
            invalidated);
    }

    private static TicketRunDetails Details(
        TicketWorkflowDefinition workflow,
        AuthoringRunStatus run,
        ReviewSitePublication? publication)
    {
        AuthoringRunResponse response = new(run, []);
        return new TicketRunDetails(
            workflow,
            response,
            new RunOutcomeClassifier().Classify(
                run,
                publication),
            publication);
    }

    private static ReviewSitePublication Publication(
        TicketWorkflowDefinition workflow,
        AuthoringRunStatus run,
        DiscussionPublicationReadiness? readiness,
        DiscussionCorpusSummary? corpus = null,
        bool legacy = false,
        string? displayTitle = null)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "fhir-augury-rendering",
            workflow.RouteKey,
            run.RunId);
        string siteDirectory =
            Path.Combine(root, workflow.SiteFolder);
        ReviewSiteCoordinates coordinates = new(
            workflow,
            run.RunId,
            Path.Combine(root, "pair"),
            root,
            siteDirectory,
            Path.Combine(
                siteDirectory,
                TicketSiteManifest.FileName),
            $"/review-sites/{workflow.RouteKey}/{run.RunId}/{workflow.SiteFolder}/");
        bool discussion = workflow.SiteKind == TicketSiteKind.Discussion;
        DiscussionCorpusSummary? selectedCorpus = discussion && !legacy
            ? corpus ?? SampleCorpus()
            : null;
        TicketSiteManifest manifest = new(
            discussion ? "preparer" : "planner",
            run.ProcessorKind,
            run.RunId,
            "snapshot-1",
            run.AuthoringEpoch,
            1,
            3,
            "snapshot-sha",
            1,
            "embedded-sha",
            1,
            checked((int)(selectedCorpus?.TicketCount ?? run.TotalItems)),
            1,
            new Dictionary<string, long>
            {
                ["tickets"] = selectedCorpus?.TicketCount ?? run.TotalItems,
                ["related_items"] = selectedCorpus?.LinksByKind.Sum(links => links.TotalRows) ?? 0,
            },
            new TicketSiteManifestFilters(null, null, null),
            workflow.SiteTitle,
            "assets",
            "build",
            coordinates.SiteDirectory,
            ReadTime,
            DisplayTitle: displayTitle ?? (discussion
                ? legacy
                    ? "Tickets for Discussion - Built September 08, 2026"
                    : "Tickets for Discussion - Sept 15, 2026"
                : null),
            RendererSchemaVersion: discussion ? legacy ? 2 : 3 : null,
            DiscussionReadiness: discussion ? readiness : null,
            DiscussionCorpus: selectedCorpus);
        return new ReviewSitePublication(
            coordinates,
            manifest,
            Reconstructed: true);
    }

    private static DiscussionPublicationReadiness DegradedReadiness() =>
        new(
            IsReady: false,
            Evidence:
                DiscussionPublicationReadinessEvidence.OrdinarySnapshot,
            JiraSourceContentRevision: null,
            PublicDisplayNamePolicyVersion: null,
            Reasons:
            [
                new DiscussionPublicationReadinessReason(
                    DiscussionPublicationReadinessReasonCodes
                        .MissingOrdinaryProvenance,
                    "Complete receipt-backed Jira source provenance is unavailable."),
            ]);

    private static DiscussionPublicationReadiness ReadyReadiness() =>
        new(
            IsReady: true,
            DiscussionPublicationReadinessEvidence.PublicationRefresh,
            JiraSourceContentRevision: 42,
            PublicDisplayNamePolicyVersion: 1,
            []);

    private static DiscussionCorpusSummary SampleCorpus() =>
        new(
            3, 2, 3,
            new DateTimeOffset(2026, 9, 15, 0, 30, 0, TimeSpan.Zero),
            DiscussionDateCoverage.Complete,
            1, 0, 1,
            [
                new("github", 0, 0, 0, 0),
                new("jira", 0, 0, 0, 0),
                new("jira-xref", 0, 0, 0, 0),
                new("repo", 3, 1, 1, 1),
                new("zulip", 0, 0, 0, 0),
            ]);

    private static ParameterView Parameters(params (string Name, object? Value)[] values) =>
        ParameterView.FromDictionary(values.ToDictionary(value => value.Name, value => value.Value));

    private static WorkspaceObservation<T> Observation<T>(
        T value,
        WorkspaceObservationPhase phase,
        bool retainSuccess) where T : class
    {
        WorkspaceObservation<T> observation = WorkspaceObservation<T>.Pending;
        if (retainSuccess)
        {
            observation = observation.Begin(ReadTime.AddSeconds(-1))
                .Complete(WorkspaceReadOutcome<T>.Success(value), ReadTime);
        }
        if (phase is WorkspaceObservationPhase.Pending or WorkspaceObservationPhase.Succeeded)
        {
            return observation;
        }
        observation = observation.Begin(ReadTime.AddMinutes(1));
        return phase == WorkspaceObservationPhase.Loading
            ? observation
            : observation.Complete(WorkspaceReadOutcome<T>.FromFailure(new(
                phase == WorkspaceObservationPhase.Unavailable
                    ? WorkspaceReadFailureReason.Transport
                    : WorkspaceReadFailureReason.Authorization,
                "test-read", "prepare", "api/test", "The selected read failed.")),
                ReadTime.AddMinutes(2));
    }

    private static TicketWorkspaceSnapshot WithHistory(
        TicketWorkspaceSnapshot snapshot,
        string runId,
        long revision) => snapshot with
        {
            Revision = revision,
            Workflows = snapshot.Workflows.SetItem("prepare",
                snapshot.Workflows["prepare"] with
                {
                    History = Observation(History(runId), WorkspaceObservationPhase.Succeeded, true),
                }),
        };

    private static AuthoringRunListResponse History(string runId) =>
        new([Run(runId, "completed", terminal: true)], false);

    private static AuthoringRunStatus Run(string runId, string status, bool? terminal) => new(
        runId, "jira-fhir", 1, status, false, 2, terminal == true ? 2 : 0,
        status == "error" ? 1 : 0, ProbeTime, ProbeTime, terminal == true ? ReadTime : null, null,
        RetryableErrorItems: status == "error" ? 1 : 0,
        State: terminal is bool isTerminal ? new AuthoringRunStateInfo(isTerminal, status == "error") : null);

    private static ServicesStatusResponse Services() => new(
    [
        Health("Orchestrator", "orchestrator"),
        Health("Preparer", "processing", ["Jira"]),
        Health("Planner", "processing", ["Jira", "GitHub"]),
        Health("Jira", "source"),
        Health("GitHub", "source"),
    ], ProbeTime);

    private static ServiceHealthInfo Health(string name, string kind, List<string>? required = null) => new()
    {
        Name = name,
        ServiceKind = kind,
        Status = "healthy",
        Configured = true,
        Enabled = true,
        CheckedAt = ProbeTime,
        ProcessingIsRunning = kind == "processing" ? true : null,
        RequiredServices = required ?? [],
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T PrivateField<T>(object instance, string name) =>
        Assert.IsType<T>(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance));

    private static Task ReadinessCompletion(TicketRunStart page)
    {
        object attempt = Assert.IsAssignableFrom<object>(typeof(TicketRunStart)
            .GetField("_readinessAttempt", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(page));
        return Assert.IsAssignableFrom<Task>(attempt.GetType().GetProperty("Completion")?.GetValue(attempt));
    }

    private static Task InvokeNotification(
        Home page,
        TicketWorkspaceSession session,
        TicketWorkspaceSnapshot snapshot) =>
        Assert.IsAssignableFrom<Task>(typeof(Home)
            .GetMethod("ApplySnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(page, [session, snapshot]));

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private sealed record Mounted<T>(int Id, T Instance, Task Rendering) where T : IComponent;

    private sealed record ComponentIdentity<T>(int Id, T Instance, object? Key) where T : IComponent;

    private sealed record PageSession(TicketWorkspaceSession Session, PageScopeProbe Scope);

    private sealed class PageScopeProbe : IAsyncDisposable
    {
        public TaskCompletionSource Disposed { get; } = Signal();

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ReadTime;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://devui/", "http://devui/");

        public string? Destination { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Destination = uri;
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "FhirAugury.DevUi.Tests";

        public string ContentRootPath { get; set; } = Path.Combine(root, "content");

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ControlledRead<T> where T : class
    {
        public TaskCompletionSource<T> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = Signal();

        public TaskCompletionSource Finished { get; } = Signal();

        public CancellationToken Token { get; private set; }

        public bool IgnoreCancellation { get; set; }

        public async Task<T> ExecuteAsync(CancellationToken ct)
        {
            Token = ct;
            Entered.TrySetResult();
            try
            {
                return IgnoreCancellation
                    ? await Response.Task
                    : await Response.Task.WaitAsync(ct);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class ReadQueue<T>(ConcurrentQueue<string> unexpected, string operation) where T : class
    {
        private readonly ConcurrentQueue<ControlledRead<T>> _reads = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ControlledRead<T> Enqueue()
        {
            ControlledRead<T> read = new();
            _reads.Enqueue(read);
            return read;
        }

        public Task<T> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (!_reads.TryDequeue(out ControlledRead<T>? read))
            {
                unexpected.Enqueue(operation);
                throw new InvalidOperationException($"Unexpected {operation} read.");
            }
            return read.ExecuteAsync(ct);
        }
    }

    private sealed class FakeReadinessClient(ConcurrentQueue<string> unexpected)
        : IOrchestratorReadinessClient
    {
        public ReadQueue<ServicesStatusResponse> Cached { get; } = new(unexpected, "cached readiness");

        public ReadQueue<ServicesStatusResponse> Refresh { get; } = new(unexpected, "readiness recheck");

        public ConcurrentQueue<(bool Refresh, CancellationToken Token)> Requests { get; } = new();

        public Task<ServicesStatusResponse> GetAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((false, ct));
            return Cached.ReadAsync(ct);
        }

        public Task<ServicesStatusResponse> RefreshAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((true, ct));
            return Refresh.ReadAsync(ct);
        }
    }

    private sealed class FakeAuthoringClient(ConcurrentQueue<string> unexpected) : IAuthoringControlClient
    {
        private int _startCalls;
        private int _getCalls;
        private int _refreshCalls;
        private int _reconciliationStartCalls;
        private int _reconciliationStatusCalls;
        private int _reconciliationRetryCalls;
        private int _reconciliationAbandonCalls;

        public ReadQueue<AuthoringRunListResponse> Preparer { get; } = new(unexpected, "Preparer history");

        public ReadQueue<AuthoringRunListResponse> Planner { get; } = new(unexpected, "Planner history");

        public ConcurrentQueue<(string Service, int? Limit, CancellationToken Token)> ListRequests { get; } = new();

        public int StartCalls => Volatile.Read(ref _startCalls);

        public int GetCalls => Volatile.Read(ref _getCalls);

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public int ReconciliationStartCalls =>
            Volatile.Read(ref _reconciliationStartCalls);

        public int ReconciliationStatusCalls =>
            Volatile.Read(ref _reconciliationStatusCalls);

        public int ReconciliationRetryCalls =>
            Volatile.Read(ref _reconciliationRetryCalls);

        public int ReconciliationAbandonCalls =>
            Volatile.Read(ref _reconciliationAbandonCalls);

        public Func<string, object?, CancellationToken, Task<AuthoringStartResult>> StartHandler { get; set; } =
            (_, _, _) => Task.FromResult(new AuthoringStartResult(null));

        public Func<string, string, CancellationToken, Task<AuthoringRunResponse>>
            GetHandler { get; set; } =
                (_, _, _) => Task.FromException<AuthoringRunResponse>(
                    new InvalidOperationException(
                        "Detail handler was not configured."));

        public Func<string, string, CancellationToken, Task<AuthoringRunResponse>>
            RefreshHandler { get; set; } =
                (_, _, _) => Task.FromException<AuthoringRunResponse>(
                    new InvalidOperationException(
                        "Publication refresh handler was not configured."));

        public Func<
            string,
            string,
            CancellationToken,
            Task<PublicationReconciliationStartResult>>
            ReconciliationStartHandler { get; set; } =
            (_, _, _) =>
                Task.FromException<PublicationReconciliationStartResult>(
                    new InvalidOperationException(
                        "Publication reconciliation start handler was not configured."));

        public Func<
            string,
            string,
            CancellationToken,
            Task<PublicationReconciliationStatusResult>>
            ReconciliationStatusHandler { get; set; } =
            (_, _, _) =>
                Task.FromException<PublicationReconciliationStatusResult>(
                    new InvalidOperationException(
                        "Publication reconciliation status handler was not configured."));

        public Func<
            string,
            string,
            CancellationToken,
            Task<PublicationReconciliationRetryResult>>
            ReconciliationRetryHandler { get; set; } =
            (_, _, _) =>
                Task.FromException<PublicationReconciliationRetryResult>(
                    new InvalidOperationException(
                        "Publication reconciliation retry handler was not configured."));

        public Func<
            string,
            string,
            string,
            CancellationToken,
            Task<PublicationReconciliationAbandonResult>>
            ReconciliationAbandonHandler { get; set; } =
            (_, _, _, _) =>
                Task.FromException<PublicationReconciliationAbandonResult>(
                    new InvalidOperationException(
                        "Publication reconciliation abandon handler was not configured."));

        public ConcurrentQueue<(string Service, string SourceRunId)>
            RefreshRequests { get; } = new();

        public ConcurrentQueue<(string Service, string SourceRunId)>
            ReconciliationStartRequests { get; } = new();

        public ConcurrentQueue<(string Service, string RunId)>
            ReconciliationStatusRequests { get; } = new();

        public ConcurrentQueue<(string Service, string RunId)>
            ReconciliationRetryRequests { get; } = new();

        public ConcurrentQueue<(string Service, string RunId, string Reason)>
            ReconciliationAbandonRequests { get; } = new();

        public Task<AuthoringRunListResponse> ListAsync(string serviceName, int? limit, CancellationToken ct)
        {
            ListRequests.Enqueue((serviceName, limit, ct));
            Assert.Equal(20, limit);
            return serviceName switch
            {
                "Preparer" => Preparer.ReadAsync(ct),
                "Planner" => Planner.ReadAsync(ct),
                _ => throw Unexpected(serviceName),
            };
        }

        public Task<AuthoringStartResult> StartAsync<TRequest>(
            string serviceName, TRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _startCalls);
            return StartHandler(serviceName, request, ct);
        }

        public Task<AuthoringRunResponse> StartPublicationRefreshAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _refreshCalls);
            RefreshRequests.Enqueue((serviceName, sourceRunId));
            return RefreshHandler(serviceName, sourceRunId, ct);
        }

        public Task<PublicationReconciliationStartResult>
            StartPublicationReconciliationAsync(
                string serviceName,
                string sourceRunId,
                CancellationToken ct)
        {
            Interlocked.Increment(ref _reconciliationStartCalls);
            ReconciliationStartRequests.Enqueue(
                (serviceName, sourceRunId));
            return ReconciliationStartHandler(
                serviceName,
                sourceRunId,
                ct);
        }

        public Task<PublicationReconciliationStatusResult>
            GetPublicationReconciliationAsync(
                string serviceName,
                string runId,
                CancellationToken ct)
        {
            Interlocked.Increment(ref _reconciliationStatusCalls);
            ReconciliationStatusRequests.Enqueue(
                (serviceName, runId));
            return ReconciliationStatusHandler(
                serviceName,
                runId,
                ct);
        }

        public Task<PublicationReconciliationRetryResult>
            RetryPublicationReconciliationAsync(
                string serviceName,
                string runId,
                CancellationToken ct)
        {
            Interlocked.Increment(ref _reconciliationRetryCalls);
            ReconciliationRetryRequests.Enqueue(
                (serviceName, runId));
            return ReconciliationRetryHandler(
                serviceName,
                runId,
                ct);
        }

        public Task<PublicationReconciliationAbandonResult>
            AbandonPublicationReconciliationAsync(
                string serviceName,
                string runId,
                string reason,
                CancellationToken ct)
        {
            Interlocked.Increment(ref _reconciliationAbandonCalls);
            ReconciliationAbandonRequests.Enqueue(
                (serviceName, runId, reason));
            return ReconciliationAbandonHandler(
                serviceName,
                runId,
                reason,
                ct);
        }

        public Task<AuthoringRunResponse> GetAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _getCalls);
            return GetHandler(serviceName, runId, ct);
        }

        public Task<AuthoringRetryResponse> RetryAsync(
            string serviceName, string runId, string itemId, CancellationToken ct) =>
            throw Unexpected("retry");

        public Task<AuthoringItemSupersedeResult> SupersedeAsync(
            string serviceName, string runId, string itemId, string reason, CancellationToken ct) =>
            throw Unexpected("supersede");

        public Task<VerifiedAuthoringSnapshotPair> DownloadSnapshotPairAsync(
            string serviceName, string runId, string pairDirectory, CancellationToken ct) =>
            throw Unexpected("snapshot");

        private InvalidOperationException Unexpected(string operation)
        {
            unexpected.Enqueue(operation);
            return new InvalidOperationException($"Unexpected {operation} access.");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"fhir-augury-workspace-rendering-{Guid.NewGuid():N}");
        private readonly ConcurrentQueue<string> _unexpected = new();
        private readonly ServiceDescriptor[] _registrations;

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Authoring = new(_unexpected);
            Readiness = new(_unexpected);
            ReviewSites = new();
            InitialPreparer = Authoring.Preparer.Enqueue();
            InitialPlanner = Authoring.Planner.Enqueue();
            InitialReadiness = Readiness.Cached.Enqueue();
            ServiceCollection services = new();
            ConfigurationManager configuration = new();
            configuration["DevUi:CacheRoot"] = Path.Combine(_directory, "cache");
            configuration["DevUi:SnapshotCacheRoot"] = "snapshots";
            configuration["DevUi:ReviewSitesRoot"] = "sites";
            services.AddLogging();
            services.AddDevUiOperations(configuration, new TestEnvironment(_directory));
            _registrations = services.ToArray();
            services.AddSingleton<IAuthoringControlClient>(Authoring);
            services.AddSingleton<IOrchestratorReadinessClient>(Readiness);
            services.AddSingleton<IReviewSiteStore>(ReviewSites);
            services.AddSingleton<NavigationManager>(Navigation);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            services.AddScoped<PageScopeProbe>();
            services.AddTransient(provider =>
            {
                PageScopeProbe probe = provider.GetRequiredService<PageScopeProbe>();
                TicketWorkspaceSession session = ActivatorUtilities.CreateInstance<TicketWorkspaceSession>(provider);
                PageSessions.Add(new(session, probe));
                return session;
            });
            services.AddScoped(provider =>
            {
                TicketOperationsService operations = ActivatorUtilities.CreateInstance<TicketOperationsService>(provider);
                Mutations.Add(operations);
                return operations;
            });
            Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Circuit = Root.CreateAsyncScope();
            Renderer = new(Circuit.ServiceProvider);
            Catalog = Root.GetRequiredService<TicketWorkflowCatalog>();
        }

        public ServiceProvider Root { get; }

        public AsyncServiceScope Circuit { get; }

        public EventRenderer Renderer { get; }

        public TicketWorkflowCatalog Catalog { get; }

        public TestNavigationManager Navigation { get; } = new();

        public FakeAuthoringClient Authoring { get; }

        public FakeReadinessClient Readiness { get; }

        public FakeReviewSiteStore ReviewSites { get; }

        public ControlledRead<AuthoringRunListResponse> InitialPreparer { get; }

        public ControlledRead<AuthoringRunListResponse> InitialPlanner { get; }

        public ControlledRead<ServicesStatusResponse> InitialReadiness { get; }

        public List<PageSession> PageSessions { get; } = [];

        public List<TicketOperationsService> Mutations { get; } = [];

        public ServiceLifetime Lifetime<T>() =>
            _registrations.Last(descriptor => descriptor.ServiceType == typeof(T)).Lifetime;

        public async Task<string> RenderHtmlAsync<T>(ParameterView? parameters = null) where T : IComponent
        {
            await using HtmlRenderer renderer = new(
                Circuit.ServiceProvider, Root.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<T>(parameters ?? ParameterView.Empty);
                return Normalize(WebUtility.HtmlDecode(component.ToHtmlString()));
            }).WaitAsync(HangGuard);
        }

        public Task WaitForInitialReadsAsync() =>
            Task.WhenAll(InitialPreparer.Entered.Task, InitialPlanner.Entered.Task, InitialReadiness.Entered.Task)
                .WaitAsync(HangGuard);

        public async ValueTask DisposeAsync()
        {
            // Noncooperative route fixtures are explicitly released even if an
            // assertion fails; cancellation cannot forcibly finish arbitrary code.
            if (InitialReadiness.IgnoreCancellation)
            {
                InitialReadiness.Response.TrySetResult(Services());
            }
            await Renderer.ReleaseDispatchesAsync();
            await Renderer.DisposeRendererAsync();
            await Renderer.ObserveOperationsAsync();
            await Circuit.DisposeAsync();
            await Root.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
            Assert.Empty(Renderer.Errors);
            Assert.Empty(_unexpected);
        }
    }

    private sealed class FakeReviewSiteStore : IReviewSiteStore
    {
        public ReviewSitePublication? Publication { get; set; }

        public int ReconstructionCalls { get; private set; }

        public void EnsureRoots()
        {
        }

        public ReviewSiteCoordinates GetCoordinates(
            string workflow,
            string runId) =>
            throw new NotSupportedException(
                "The rendering fixture does not publish review sites.");

        public void RevalidateForPublication(
            ReviewSiteCoordinates coordinates) =>
            throw new NotSupportedException(
                "The rendering fixture does not publish review sites.");

        public Task<VerifiedAuthoringSnapshotPair?>
            TryOpenVerifiedPairAsync(
                ReviewSiteCoordinates coordinates,
                CancellationToken ct = default) =>
            throw new NotSupportedException(
                "The rendering fixture does not download review snapshots.");

        public Task<ReviewSitePublication?> TryReconstructAsync(
            string workflow,
            string runId,
            CancellationToken ct = default)
        {
            ReconstructionCalls++;
            if (Publication is not null)
            {
                Assert.Equal(
                    Publication.Coordinates.Workflow.RouteKey,
                    workflow);
                Assert.Equal(
                    Publication.Coordinates.RunId,
                    runId);
            }
            return Task.FromResult(Publication);
        }

        public Task<ReviewSitePublication>
            ValidatePublishedSiteAsync(
                ReviewSiteCoordinates coordinates,
                VerifiedAuthoringSnapshotPair pair,
                CancellationToken ct = default) =>
            throw new NotSupportedException(
                "The rendering fixture does not publish review sites.");
    }

    private sealed record Element(
        int ComponentId,
        string Name,
        IReadOnlyDictionary<string, object?> Attributes,
        IReadOnlyDictionary<string, ulong> Events,
        string Content)
    {
        public bool Disabled => Attributes.TryGetValue("disabled", out object? value) && value is not false;

        public string Text => Normalize(WebUtility.HtmlDecode(Regex.Replace(Content, "<[^>]*>", " ")));

        public string? Attribute(string name) => Attributes.TryGetValue(name, out object? value) ? value?.ToString() : null;
    }

    private sealed record Markup(string Html, IReadOnlyList<Element> Elements)
    {
        public Element Button(string label) => Assert.Single(Elements, element =>
            element.Name == "button" && (element.Text == label || element.Attribute("aria-label") == label));

        public Element Input(string id) => Assert.Single(Elements, element => element.Attribute("id") == id);
    }

    // Framework render-tree APIs are deliberately confined to this in-file
    // event harness. They inspect actual frames and dispatch actual handlers;
    // they do not model browser hydration, layout, focus, or DOM patching.
#pragma warning disable BL0006
    private sealed class EventRenderer(IServiceProvider services)
        : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        private TaskCompletionSource _updated = Signal();
        private readonly List<Task> _operations = [];

        public ControlledDispatcher Events { get; } = new();

        public override Dispatcher Dispatcher => Events;

        public ConcurrentQueue<Exception> Errors { get; } = new();

        protected override void HandleException(Exception exception)
        {
            Errors.Enqueue(exception);
            SignalUpdate();
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            SignalUpdate();
            return Task.CompletedTask;
        }

        public Task<Mounted<T>> MountAsync<T>(ParameterView? parameters = null) where T : IComponent =>
            Dispatcher.InvokeAsync(() =>
            {
                T component = (T)InstantiateComponent(typeof(T));
                int id = AssignRootComponentId(component);
                return new Mounted<T>(id, component, Track(RenderRootComponentAsync(id, parameters ?? ParameterView.Empty)));
            });

        public Task RenderAsync(int id, ParameterView? parameters = null) =>
            Dispatcher.InvokeAsync(() => Track(RenderRootComponentAsync(id, parameters ?? ParameterView.Empty)));

        public Task RemoveAsync(int id) => Dispatcher.InvokeAsync(() =>
        {
            RemoveRootComponent(id);
            return Task.CompletedTask;
        });

        public Task<Markup> MarkupAsync(int id) => Dispatcher.InvokeAsync(() => ReadMarkup(id));

        public async Task<Markup> WaitForAsync(int id, Func<Markup, bool> predicate)
        {
            using CancellationTokenSource timeout = new(HangGuard);
            while (true)
            {
                (Markup markup, Task next, bool matches) = await Dispatcher.InvokeAsync(() =>
                {
                    Assert.Empty(Errors);
                    Markup markup = ReadMarkup(id);
                    return (markup, _updated.Task, predicate(markup));
                });
                if (matches)
                {
                    return markup;
                }
                await next.WaitAsync(timeout.Token);
            }
        }

        public Task ClickAsync(int id, string label) => Dispatcher.InvokeAsync(() =>
        {
            Element button = ReadMarkup(id).Button(label);
            return Track(DispatchEventAsync(button.Events["onclick"], null, new MouseEventArgs(), false));
        });

        public Task SubmitAsync(int id, string formClass, int? owner = null) => Dispatcher.InvokeAsync(() =>
        {
            Element form = Assert.Single(ReadMarkup(id).Elements, element =>
                element.Name == "form" && element.Attribute("class") == formClass &&
                (owner is null || element.ComponentId == owner));
            return Track(DispatchEventAsync(form.Events["onsubmit"], null, EventArgs.Empty, false));
        });

        public Task ChangeAsync(int id, string inputId, object value) => Dispatcher.InvokeAsync(() =>
        {
            Element input = ReadMarkup(id).Input(inputId);
            string eventName = input.Events.ContainsKey("oninput") ? "oninput" : "onchange";
            return Track(DispatchEventAsync(input.Events[eventName],
                new EventFieldInfo { ComponentId = input.ComponentId, FieldValue = value },
                new ChangeEventArgs { Value = value }, false));
        });

        public Task<ComponentIdentity<T>[]> ComponentsAsync<T>(int id) where T : IComponent =>
            Dispatcher.InvokeAsync(() =>
            {
                List<ComponentIdentity<T>> components = [];
                FindComponents(id, components);
                return components.ToArray();
            });

        public Task ObserveOperationsAsync() => Task.WhenAll(_operations).WaitAsync(HangGuard);

        public Task DisposeRendererAsync() => DisposeAsync().AsTask();

        public async Task ReleaseDispatchesAsync()
        {
            Events.ResumeActions();
            while (Events.QueuedCount > 0)
            {
                await Events.ReleaseNextAsync();
            }
        }

        private void FindComponents<T>(int id, ICollection<ComponentIdentity<T>> components) where T : IComponent
        {
            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(id);
            for (int index = 0; index < frames.Count; index++)
            {
                RenderTreeFrame frame = frames.Array[index];
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    if (frame.Component is T component)
                    {
                        components.Add(new(frame.ComponentId, component, frame.ComponentKey));
                    }
                    FindComponents(frame.ComponentId, components);
                }
            }
        }

        private Task Track(Task task)
        {
            _operations.Add(task);
            return task;
        }

        private void SignalUpdate()
        {
            TaskCompletionSource previous = _updated;
            _updated = Signal();
            previous.TrySetResult();
        }

        private Markup ReadMarkup(int id)
        {
            List<Element> elements = [];
            StringBuilder html = new();
            WriteComponent(id, html, elements);
            return new(Normalize(html.ToString()), elements);
        }

        private void WriteComponent(int id, StringBuilder html, ICollection<Element> elements)
        {
            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(id);
            WriteFrames(id, frames.Array, 0, frames.Count, html, elements);
        }

        private void WriteFrames(int owner, RenderTreeFrame[] frames, int start, int end,
            StringBuilder html, ICollection<Element> elements)
        {
            for (int index = start; index < end;)
            {
                RenderTreeFrame frame = frames[index];
                switch (frame.FrameType)
                {
                    case RenderTreeFrameType.Element:
                        int subtreeEnd = index + frame.ElementSubtreeLength;
                        Dictionary<string, object?> attributes = [];
                        Dictionary<string, ulong> events = [];
                        html.Append('<').Append(frame.ElementName);
                        int child = index + 1;
                        while (child < subtreeEnd && frames[child].FrameType == RenderTreeFrameType.Attribute)
                        {
                            RenderTreeFrame attribute = frames[child++];
                            if (attribute.AttributeEventHandlerId != 0)
                            {
                                events.Add(attribute.AttributeName, attribute.AttributeEventHandlerId);
                            }
                            else if (attribute.AttributeValue is not null and not false)
                            {
                                attributes[attribute.AttributeName] = attribute.AttributeValue;
                                html.Append(' ').Append(attribute.AttributeName);
                                if (attribute.AttributeValue is not true)
                                {
                                    html.Append("=\"").Append(WebUtility.HtmlEncode(attribute.AttributeValue.ToString())).Append('"');
                                }
                            }
                        }
                        html.Append('>');
                        StringBuilder content = new();
                        WriteFrames(owner, frames, child, subtreeEnd, content, elements);
                        html.Append(content).Append("</").Append(frame.ElementName).Append('>');
                        elements.Add(new(owner, frame.ElementName, attributes, events, content.ToString()));
                        index = subtreeEnd;
                        break;
                    case RenderTreeFrameType.Component:
                        WriteComponent(frame.ComponentId, html, elements);
                        index += frame.ComponentSubtreeLength;
                        break;
                    case RenderTreeFrameType.Region:
                        WriteFrames(owner, frames, index + 1, index + frame.RegionSubtreeLength, html, elements);
                        index += frame.RegionSubtreeLength;
                        break;
                    case RenderTreeFrameType.Text:
                        html.Append(WebUtility.HtmlEncode(frame.TextContent));
                        index++;
                        break;
                    case RenderTreeFrameType.Markup:
                        html.Append(frame.MarkupContent);
                        index++;
                        break;
                    default:
                        index++;
                        break;
                }
            }
        }
    }
#pragma warning restore BL0006

    private sealed class ControlledDispatcher : Dispatcher
    {
        private readonly Dispatcher _inner = CreateDefault();
        private readonly object _sync = new();
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _queued = new();
        private TaskCompletionSource _changed = Signal();
        private bool _hold;

        public int QueuedCount
        {
            get
            {
                lock (_sync)
                {
                    return _queued.Count;
                }
            }
        }

        public override bool CheckAccess() => _inner.CheckAccess();

        public override Task InvokeAsync(Action workItem)
        {
            lock (_sync)
            {
                if (_hold)
                {
                    TaskCompletionSource completion = Signal();
                    _queued.Enqueue((workItem, completion));
                    TaskCompletionSource previous = _changed;
                    _changed = Signal();
                    previous.TrySetResult();
                    return completion.Task;
                }
            }
            return _inner.InvokeAsync(workItem);
        }

        public override Task InvokeAsync(Func<Task> workItem) => _inner.InvokeAsync(workItem);

        public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) => _inner.InvokeAsync(workItem);

        public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) => _inner.InvokeAsync(workItem);

        public void HoldActions()
        {
            lock (_sync)
            {
                _hold = true;
            }
        }

        public void ResumeActions()
        {
            lock (_sync)
            {
                _hold = false;
            }
        }

        public async Task WaitForQueuedAsync(int count)
        {
            using CancellationTokenSource timeout = new(HangGuard);
            while (true)
            {
                Task next;
                lock (_sync)
                {
                    if (_queued.Count >= count)
                    {
                        return;
                    }
                    next = _changed.Task;
                }
                await next.WaitAsync(timeout.Token);
            }
        }

        public Task ReleaseNextAsync()
        {
            (Action action, TaskCompletionSource completion) queued;
            lock (_sync)
            {
                queued = _queued.Dequeue();
            }
            return _inner.InvokeAsync(() =>
            {
                try
                {
                    queued.action();
                    queued.completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    queued.completion.TrySetException(ex);
                }
            });
        }
    }
}
