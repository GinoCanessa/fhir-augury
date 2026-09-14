using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Tests;

public sealed class TicketWorkspaceReaderTests
{
    private const int RecentRunLimit = 37;
    private const string HistoryEndpoint =
        "api/v1/processing-services/Preparer/authoring/runs?limit=37";
    private const string PrivateDetail = "private response detail";
    private static readonly DateTimeOffset ProbeTime =
        new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("PREPARE", "Preparer")]
    [InlineData("PLAN", "Planner")]
    public async Task HistoryUsesSelectedServiceLimitAndCancellationToken(
        string workflow,
        string expectedService)
    {
        List<AuthoringRunStatus> runs =
        [
            Run("run/shared #1") with
            {
                Status = "completed",
                State = new AuthoringRunStateInfo(false, true, ProbeTime),
            },
            Run("terminal-run") with
            {
                Status = "running",
                State = new AuthoringRunStateInfo(true, false),
            },
            Run("legacy-run") with { State = null },
        ];
        AuthoringRunListResponse response = new(runs, true);
        FakeAuthoringClient authoring = new()
        {
            ListHandler = (_, _, _) => Task.FromResult(response),
        };
        FakeReadinessClient readiness = new();
        TicketWorkspaceReader reader = CreateReader(authoring, readiness);
        using CancellationTokenSource cancellation = new();

        WorkspaceReadOutcome<AuthoringRunListResponse> outcome =
            await reader.ReadHistoryAsync(workflow, cancellation.Token);

        AuthoringRunListResponse value = AssertSuccess(outcome);
        Assert.True(value.Truncated);
        Assert.Equal(runs, value.Runs);
        for (int index = 0; index < runs.Count; index++)
        {
            Assert.Same(runs[index], value.Runs[index]);
        }
        Assert.False(value.Runs[0].State?.IsTerminal);
        Assert.True(value.Runs[1].State?.IsTerminal);
        Assert.Null(value.Runs[2].State);
        var request = Assert.Single(authoring.Requests);
        Assert.Equal(expectedService, request.Service);
        Assert.Equal(RecentRunLimit, request.Limit);
        Assert.Equal(cancellation.Token, request.Token);
        Assert.Empty(readiness.Requests);

        runs.Clear();
        Assert.Equal(3, value.Runs.Count);
        IList<AuthoringRunStatus> readOnlyRuns =
            Assert.IsAssignableFrom<IList<AuthoringRunStatus>>(value.Runs);
        Assert.True(readOnlyRuns.IsReadOnly);
        Assert.Throws<NotSupportedException>(
            () => readOnlyRuns.Add(Run("another-run")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyHistoryIsSuccessfulAndPreservesTruncation(
        bool truncated)
    {
        FakeAuthoringClient authoring = new()
        {
            ListHandler = (_, _, _) => Task.FromResult(
                new AuthoringRunListResponse([], truncated)),
        };
        WorkspaceReadOutcome<AuthoringRunListResponse> outcome =
            await CreateReader(authoring).ReadHistoryAsync("prepare");
        WorkspaceObservation<AuthoringRunListResponse> observation =
            WorkspaceObservation<AuthoringRunListResponse>.Pending
                .Begin(ProbeTime)
                .Complete(outcome, ProbeTime.AddMinutes(1));

        AuthoringRunListResponse value = AssertSuccess(outcome);
        Assert.Empty(value.Runs);
        Assert.Equal(truncated, value.Truncated);
        Assert.True(observation.IsCurrentSuccess);
        Assert.True(observation.HasLastSuccess);
        Assert.False(observation.IsStale);
        Assert.Same(value, observation.CurrentValue);
        Assert.Null(observation.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessUsesGetOrRefreshWithoutHistoryReads(bool refresh)
    {
        ServicesStatusResponse response = Services(plannerAvailable: false);
        FakeAuthoringClient authoring = new();
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromResult(response),
        };
        using CancellationTokenSource cancellation = new();
        TicketWorkspaceReader reader = CreateReader(authoring, readiness);

        WorkspaceReadOutcome<TicketWorkspaceReadiness> outcome =
            await reader.ReadReadinessAsync(refresh, cancellation.Token);

        TicketWorkspaceReadiness value = AssertSuccess(outcome);
        Assert.Equal(["plan", "prepare"], value.Workflows.Keys.Order());
        Assert.True(value.Workflows["PREPARE"].CanStart);
        Assert.False(value.Workflows["PLAN"].CanStart);
        Assert.Equal(
            "Planner",
            Assert.Single(value.Workflows["plan"].Blockers).Name);
        Assert.Equal(response.LastCheckedAt, value.LastCheckedAt);
        Assert.All(value.Workflows.Values, workflow =>
        {
            Assert.Equal(response.LastCheckedAt, workflow.CheckedAt);
            Assert.Equal(ProbeTime.AddMinutes(-1), workflow.Orchestrator.CheckedAt);
            Assert.Equal(ProbeTime.AddMinutes(-2), workflow.Processor.CheckedAt);
        });
        var request = Assert.Single(readiness.Requests);
        Assert.Equal(refresh, request.Refresh);
        Assert.Equal(cancellation.Token, request.Token);
        Assert.Empty(authoring.Requests);
        Assert.Same(
            value.Workflows["prepare"],
            AssertSuccess(outcome.Select(report => report.Workflows["prepare"])));
    }

    [Theory]
    [InlineData("PREPARE", "Preparer")]
    [InlineData("plan", "Planner")]
    public async Task HistoryTypedClientUsesCanonicalOrchestratorEndpoint(
        string workflow,
        string service)
    {
        List<(HttpMethod Method, string Path)> requests = [];
        using HttpClient httpClient = HttpClientFor(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(
                new AuthoringRunListResponse([Run("run-1")], true),
                JsonOptions),
            requests: requests);
        FakeReadinessClient readiness = new();
        TicketWorkspaceReader reader = CreateReader(
            new AuthoringControlClient(httpClient),
            readiness);

        AuthoringRunListResponse response = AssertSuccess(
            await reader.ReadHistoryAsync(workflow));

        Assert.Equal("run-1", Assert.Single(response.Runs).RunId);
        Assert.True(response.Truncated);
        Assert.Equal(
            (HttpMethod.Get,
                $"/api/v1/processing-services/{service}/authoring/runs?limit=37"),
            Assert.Single(requests));
        Assert.Empty(readiness.Requests);
    }

    [Theory]
    [InlineData(false, "GET", "/api/v1/services")]
    [InlineData(true, "POST", "/api/v1/services/refresh")]
    public async Task ReadinessTypedClientUsesGetAndRefreshEndpoints(
        bool refresh,
        string method,
        string path)
    {
        List<(HttpMethod Method, string Path)> requests = [];
        using HttpClient httpClient = HttpClientFor(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(Services(), JsonOptions),
            requests: requests);
        FakeAuthoringClient authoring = new();
        TicketWorkspaceReader reader = CreateReader(
            authoring,
            new OrchestratorReadinessClient(httpClient));

        TicketWorkspaceReadiness response = AssertSuccess(
            await reader.ReadReadinessAsync(refresh));

        Assert.All(response.Workflows.Values, value => Assert.True(value.CanStart));
        Assert.Equal((new HttpMethod(method), path), Assert.Single(requests));
        Assert.Empty(authoring.Requests);
    }

    [Theory]
    [InlineData("authoring", 408)]
    [InlineData("authoring", 429)]
    [InlineData("authoring", 500)]
    [InlineData("authoring", 503)]
    [InlineData("http", 408)]
    [InlineData("http", 429)]
    [InlineData("http", 500)]
    [InlineData("http", 503)]
    [InlineData("http", 599)]
    [InlineData("transport", null)]
    [InlineData("stream", null)]
    [InlineData("timeout", null)]
    [InlineData("task-canceled", null)]
    [InlineData("operation-canceled", null)]
    public async Task TransientHttpTransportAndTimeoutAreUnavailable(
        string cause,
        int? status)
    {
        Exception exception = FailureException(cause, status);
        WorkspaceReadFailureReason reason = cause switch
        {
            "authoring" or "http" => WorkspaceReadFailureReason.TransientHttp,
            "timeout" or "task-canceled" or "operation-canceled" =>
                WorkspaceReadFailureReason.Timeout,
            _ => WorkspaceReadFailureReason.Transport,
        };

        await AssertReadFailuresAsync(
            exception,
            WorkspaceObservationPhase.Unavailable,
            reason);
    }

    [Theory]
    [InlineData("authoring", 401, WorkspaceReadFailureReason.Authentication)]
    [InlineData("authoring", 403, WorkspaceReadFailureReason.Authorization)]
    [InlineData("authoring", 404, WorkspaceReadFailureReason.NotFound)]
    [InlineData("authoring", 400, WorkspaceReadFailureReason.HttpError)]
    [InlineData("authoring", 409, WorkspaceReadFailureReason.HttpError)]
    [InlineData("authoring", 422, WorkspaceReadFailureReason.HttpError)]
    [InlineData("http", 401, WorkspaceReadFailureReason.Authentication)]
    [InlineData("http", 403, WorkspaceReadFailureReason.Authorization)]
    [InlineData("http", 404, WorkspaceReadFailureReason.NotFound)]
    [InlineData("http", 400, WorkspaceReadFailureReason.HttpError)]
    [InlineData("http", 302, WorkspaceReadFailureReason.HttpError)]
    [InlineData("json", null, WorkspaceReadFailureReason.InvalidResponse)]
    [InlineData("invalid-response", null, WorkspaceReadFailureReason.InvalidResponse)]
    [InlineData("options", null, WorkspaceReadFailureReason.InvalidOptions)]
    [InlineData("unexpected", null, WorkspaceReadFailureReason.Unexpected)]
    public async Task NonTransientAndInvalidResponsesAreFailed(
        string cause,
        int? status,
        WorkspaceReadFailureReason reason)
    {
        await AssertReadFailuresAsync(
            FailureException(cause, status),
            WorkspaceObservationPhase.Failed,
            reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{invalid json")]
    [InlineData("""{"runs":[{"runId":" "}],"truncated":false}""")]
    public async Task HistoryTypedClientInvalidResponseWrappersAreFailed(string json)
    {
        using HttpClient httpClient = HttpClientFor(HttpStatusCode.OK, json);
        AuthoringControlClient client = new(httpClient);
        InvalidOperationException wrapped =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ListAsync("Preparer", RecentRunLimit, CancellationToken.None));
        if (json is "" or "{invalid json")
        {
            Assert.IsType<JsonException>(wrapped.InnerException);
        }
        FakeReadinessClient readiness = new();

        WorkspaceReadOutcome<AuthoringRunListResponse> outcome =
            await CreateReader(client, readiness).ReadHistoryAsync("prepare");

        AssertFailure(
            outcome,
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidResponse);
        Assert.Empty(readiness.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runs":null,"truncated":false}""")]
    public async Task HistoryTypedClientNullCollectionsAreFailed(string json)
    {
        using HttpClient httpClient = HttpClientFor(HttpStatusCode.OK, json);
        TicketWorkspaceReader reader = CreateReader(
            new AuthoringControlClient(httpClient));

        AssertFailure(
            await reader.ReadHistoryAsync("prepare"),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidResponse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{invalid json")]
    [InlineData("{}")]
    [InlineData("""{"services":null}""")]
    [InlineData("""{"services":[null]}""")]
    [InlineData("""{"services":[{"name":"","status":"healthy"}]}""")]
    [InlineData("""{"services":[{"name":"Preparer","status":"healthy","requiredServices":null}]}""")]
    public async Task ReadinessTypedClientInvalidResponsesAreFailed(string json)
    {
        using HttpClient httpClient = HttpClientFor(HttpStatusCode.OK, json);
        FakeAuthoringClient authoring = new();
        TicketWorkspaceReader reader = CreateReader(
            authoring,
            new OrchestratorReadinessClient(httpClient));

        AssertFailure(
            await reader.ReadReadinessAsync(),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidResponse);
        Assert.Empty(authoring.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("null-runs")]
    [InlineData("null-run")]
    [InlineData("blank-run-id")]
    [InlineData("blank-processor")]
    [InlineData("blank-status")]
    [InlineData("duplicate-run-id")]
    public async Task UnusableHistoryPayloadsAreFailed(string payload)
    {
        // Deliberately violate shared DTO annotations to model malformed payloads.
        AuthoringRunListResponse response = payload switch
        {
            "null" => null!,
            "null-runs" => new(null!, false),
            "null-run" => new([null!], false),
            "blank-run-id" => new([Run(" ")], false),
            "blank-processor" => new([Run("run-1") with { ProcessorKind = "" }], false),
            "blank-status" => new([Run("run-1") with { Status = "" }], false),
            _ => new([Run("run-1"), Run("run-1")], false),
        };
        FakeAuthoringClient authoring = new()
        {
            ListHandler = (_, _, _) => Task.FromResult(response),
        };

        AssertFailure(
            await CreateReader(authoring).ReadHistoryAsync("prepare"),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidResponse);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("null-services")]
    [InlineData("null-service")]
    [InlineData("blank-name")]
    [InlineData("blank-kind")]
    [InlineData("blank-status")]
    [InlineData("null-dependencies")]
    [InlineData("duplicate-service")]
    public async Task UnusableReadinessPayloadsAreFailed(string payload)
    {
        ServiceHealthInfo service = Health("Preparer", "processing");
        ServicesStatusResponse response = payload switch
        {
            "null" => null!,
            "null-services" => new(Services: null!),
            "null-service" => new([null!]),
            "blank-name" => new([service with { Name = "" }]),
            "blank-kind" => new([service with { ServiceKind = "" }]),
            "blank-status" => new([service with { Status = "" }]),
            "null-dependencies" => new([service with { RequiredServices = null! }]),
            _ => new([service, service with { Name = "PREPARER" }]),
        };
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromResult(response),
        };

        AssertFailure(
            await CreateReader(readiness: readiness).ReadReadinessAsync(),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidResponse);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown-private-route")]
    public async Task InvalidWorkflowFailsWithoutClientCalls(string? workflow)
    {
        FakeAuthoringClient authoring = new();
        FakeReadinessClient readiness = new();
        TestLogger logger = new();
        TicketWorkspaceReader reader = CreateReader(authoring, readiness, logger: logger);

        WorkspaceReadFailure failure = AssertFailure(
            await reader.ReadHistoryAsync(workflow),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidWorkflow);

        Assert.Equal(workflow, failure.Workflow);
        Assert.Equal("history", failure.Operation);
        Assert.DoesNotContain("unknown-private-route", failure.Message);
        Assert.Empty(authoring.Requests);
        Assert.Empty(readiness.Requests);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task InvalidRecentRunLimitFailsWithoutClientCalls(int limit)
    {
        FakeAuthoringClient authoring = new();
        FakeReadinessClient readiness = new();
        TicketWorkspaceReader reader = CreateReader(
            authoring,
            readiness,
            Options.Create(new DevUiOptions { RecentRunLimit = limit }));

        WorkspaceReadFailure failure = AssertFailure(
            await reader.ReadHistoryAsync("PREPARE"),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidOptions);

        Assert.Equal("prepare", failure.Workflow);
        Assert.Equal(
            "api/v1/processing-services/Preparer/authoring/runs",
            failure.Endpoint);
        Assert.Empty(authoring.Requests);
        Assert.Empty(readiness.Requests);
    }

    [Fact]
    public async Task OptionsValidationIsAHistoryOutcomeAndDoesNotGateReadiness()
    {
        FakeAuthoringClient authoring = new();
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromResult(Services()),
        };
        TicketWorkspaceReader reader = CreateReader(
            authoring,
            readiness,
            new InvalidOptions());

        AssertFailure(
            await reader.ReadHistoryAsync("prepare"),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.InvalidOptions);
        AssertSuccess(await reader.ReadReadinessAsync());
        Assert.Empty(authoring.Requests);
        Assert.Single(readiness.Requests);
    }

    public static IEnumerable<object[]> CancellationCases()
    {
        foreach (string operation in new[] { "history", "readiness", "readiness-refresh" })
        {
            foreach (string scenario in new[]
                { "before", "pending", "failure-after-cancel", "success-after-cancel" })
            {
                yield return [operation, scenario];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task CallerCancellationPropagates(string operation, string scenario)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestLogger logger = new();
        FakeAuthoringClient authoring = new()
        {
            ListHandler = (_, _, token) => RespondAsync(
                new AuthoringRunListResponse([], false),
                token),
        };
        FakeReadinessClient readiness = new()
        {
            Handler = (_, token) => RespondAsync(Services(), token),
        };
        TicketWorkspaceReader reader = CreateReader(authoring, readiness, logger: logger);
        if (scenario == "before")
        {
            cancellation.Cancel();
        }

        Task read = operation switch
        {
            "history" => reader.ReadHistoryAsync("prepare", cancellation.Token),
            "readiness" => reader.ReadReadinessAsync(false, cancellation.Token),
            _ => reader.ReadReadinessAsync(true, cancellation.Token),
        };
        if (scenario == "pending")
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(logger.Entries);
        if (scenario == "before")
        {
            Assert.Empty(authoring.Requests);
            Assert.Empty(readiness.Requests);
        }
        else if (operation == "history")
        {
            Assert.Single(authoring.Requests);
            Assert.Empty(readiness.Requests);
        }
        else
        {
            Assert.Empty(authoring.Requests);
            Assert.Single(readiness.Requests);
        }

        async Task<T> RespondAsync<T>(T value, CancellationToken token)
        {
            Assert.Equal(cancellation.Token, token);
            if (scenario == "pending")
            {
                started.SetResult();
                await pending.Task.WaitAsync(token);
            }
            cancellation.Cancel();
            if (scenario == "failure-after-cancel")
            {
                throw new HttpRequestException(PrivateDetail);
            }
            return value;
        }
    }

    [Theory]
    [InlineData("text/html", "<html><body>private response detail</body></html>", "http-404")]
    [InlineData(
        "application/json",
        """{"error":"<b>private-code</b>","detail":"<html>private response detail</html>"}""",
        "<b>private-code</b>")]
    public async Task FailureMessagesDoNotExposeRawResponseBodies(
        string mediaType,
        string body,
        string errorCode)
    {
        using HttpClient httpClient = HttpClientFor(HttpStatusCode.NotFound, body, mediaType);
        AuthoringControlClient authoring = new(httpClient);
        AuthoringControlException raw =
            await Assert.ThrowsAsync<AuthoringControlException>(
                () => authoring.ListAsync("Preparer", RecentRunLimit, CancellationToken.None));
        Assert.Contains(PrivateDetail, raw.Detail);
        TicketWorkspaceReader reader = CreateReader(
            authoring,
            new OrchestratorReadinessClient(httpClient));

        WorkspaceReadFailure history = AssertFailure(
            await reader.ReadHistoryAsync("PREPARE"),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.NotFound);
        WorkspaceReadFailure readiness = AssertFailure(
            await reader.ReadReadinessAsync(),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.NotFound);
        WorkspaceReadFailure refresh = AssertFailure(
            await reader.ReadReadinessAsync(refresh: true),
            WorkspaceObservationPhase.Failed,
            WorkspaceReadFailureReason.NotFound);

        Assert.Equal(errorCode, history.ErrorCode);
        Assert.Equal(raw.Endpoint, history.Endpoint);
        Assert.Equal(HistoryEndpoint, history.Endpoint);
        Assert.Equal("prepare", history.Workflow);
        foreach (WorkspaceReadFailure failure in new[] { history, readiness, refresh })
        {
            Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
            Assert.Contains("HTTP 404", failure.Message);
            Assert.Contains(failure.Endpoint, failure.Message);
            Assert.DoesNotContain(PrivateDetail, failure.Message);
            Assert.DoesNotContain("<html>", failure.Message);
            Assert.DoesNotContain("<b>", failure.Message);
            Assert.DoesNotContain("private-code", failure.Message);
            Assert.DoesNotContain(raw.Message, failure.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservationFactoriesRequireSuccessfulValues(bool empty)
    {
        Assert.Throws<ArgumentNullException>(
            () => WorkspaceReadOutcome<AuthoringRunListResponse>.Success(null));
        Assert.Throws<ArgumentNullException>(
            () => WorkspaceReadOutcome<AuthoringRunListResponse>.FromFailure(null!));
        WorkspaceObservation<AuthoringRunListResponse> pending =
            WorkspaceObservation<AuthoringRunListResponse>.Pending;
        Assert.Equal(WorkspaceObservationPhase.Pending, pending.Phase);
        Assert.False(pending.HasLastSuccess);
        Assert.False(pending.IsCurrentSuccess);
        Assert.False(pending.IsStale);
        Assert.Null(pending.CurrentValue);
        Assert.Null(pending.LastSuccessfulValue);
        Assert.Null(pending.LastSucceededAt);
        Assert.Null(pending.AttemptStartedAt);
        Assert.Null(pending.AttemptCompletedAt);
        Assert.Null(pending.Failure);

        WorkspaceReadFailure unavailableFailure = Failure(WorkspaceReadFailureReason.Transport);
        WorkspaceReadOutcome<AuthoringRunListResponse> unavailableOutcome =
            WorkspaceReadOutcome<AuthoringRunListResponse>.FromFailure(unavailableFailure);
        WorkspaceReadOutcome<AuthoringRunListResponse> successOutcome =
            WorkspaceReadOutcome<AuthoringRunListResponse>.Success(
                new AuthoringRunListResponse(empty ? [] : [Run("first-run")], true));
        Assert.Throws<InvalidOperationException>(() => pending.Complete(successOutcome, ProbeTime));
        WorkspaceObservation<AuthoringRunListResponse> loading = pending.Begin(ProbeTime);
        Assert.True(loading.IsLoading);
        Assert.False(loading.IsCurrentSuccess);
        Assert.False(loading.IsStale);
        Assert.False(loading.HasLastSuccess);
        Assert.Null(loading.AttemptCompletedAt);
        Assert.Throws<InvalidOperationException>(() => loading.Begin(ProbeTime));
        WorkspaceObservation<AuthoringRunListResponse> initialFailure =
            loading.Complete(unavailableOutcome, ProbeTime.AddMinutes(1));
        Assert.False(initialFailure.HasLastSuccess);
        Assert.False(initialFailure.IsStale);
        Assert.Null(initialFailure.LastSucceededAt);
        Assert.Null(initialFailure.CurrentValue);
        Assert.Equal(ProbeTime, initialFailure.AttemptStartedAt);
        Assert.Equal(ProbeTime.AddMinutes(1), initialFailure.AttemptCompletedAt);
        Assert.Same(unavailableFailure, initialFailure.Failure);

        WorkspaceObservation<AuthoringRunListResponse> success =
            initialFailure.Begin(ProbeTime.AddMinutes(2))
                .Complete(successOutcome, ProbeTime.AddMinutes(3));
        Assert.True(success.IsCurrentSuccess);
        Assert.True(success.HasLastSuccess);
        Assert.False(success.IsStale);
        Assert.Equal(ProbeTime.AddMinutes(2), success.AttemptStartedAt);
        Assert.Equal(ProbeTime.AddMinutes(3), success.AttemptCompletedAt);
        Assert.Equal(ProbeTime.AddMinutes(3), success.LastSucceededAt);
        Assert.Same(successOutcome.Value, success.CurrentValue);
        Assert.Null(success.Failure);
        Assert.Throws<InvalidOperationException>(
            () => success.Complete(successOutcome, ProbeTime.AddMinutes(4)));
        Assert.Throws<ArgumentNullException>(
            () => success.Select<string>(_ => null!));
        Assert.Throws<ArgumentNullException>(
            () => successOutcome.Select<string>(_ => null!));

        WorkspaceObservation<AuthoringRunListResponse> refreshing =
            success.Begin(ProbeTime.AddMinutes(4));
        WorkspaceObservation<AuthoringRunListResponse> unavailable =
            refreshing.Complete(unavailableOutcome, ProbeTime.AddMinutes(5));
        WorkspaceReadFailure invalidFailure = Failure(WorkspaceReadFailureReason.InvalidResponse);
        WorkspaceObservation<AuthoringRunListResponse> failed =
            unavailable.Begin(ProbeTime.AddMinutes(6)).Complete(
                WorkspaceReadOutcome<AuthoringRunListResponse>.FromFailure(invalidFailure),
                ProbeTime.AddMinutes(7));

        foreach (WorkspaceObservation<AuthoringRunListResponse> stale in
            new[] { refreshing, unavailable, failed })
        {
            Assert.True(stale.HasLastSuccess);
            Assert.True(stale.IsStale);
            Assert.False(stale.IsCurrentSuccess);
            Assert.Null(stale.CurrentValue);
            Assert.Same(success.LastSuccessfulValue, stale.LastSuccessfulValue);
            Assert.Equal(success.LastSucceededAt, stale.LastSucceededAt);
            WorkspaceObservation<string> projection =
                stale.Select(value => value.Runs.Count.ToString());
            Assert.Equal(stale.Phase, projection.Phase);
            Assert.Equal(stale.AttemptStartedAt, projection.AttemptStartedAt);
            Assert.Equal(stale.AttemptCompletedAt, projection.AttemptCompletedAt);
            Assert.Equal(stale.LastSucceededAt, projection.LastSucceededAt);
            Assert.Same(stale.Failure, projection.Failure);
            Assert.True(projection.IsStale);
        }
        Assert.Null(refreshing.Failure);
        Assert.Null(refreshing.AttemptCompletedAt);
        Assert.Equal(ProbeTime.AddMinutes(4), refreshing.AttemptStartedAt);
        Assert.Equal(WorkspaceObservationPhase.Unavailable, unavailable.Phase);
        Assert.Equal(ProbeTime.AddMinutes(5), unavailable.AttemptCompletedAt);
        Assert.Equal(WorkspaceObservationPhase.Failed, failed.Phase);
        Assert.Equal(ProbeTime.AddMinutes(6), failed.AttemptStartedAt);
        Assert.Equal(ProbeTime.AddMinutes(7), failed.AttemptCompletedAt);
        Assert.Same(invalidFailure, failed.Failure);

        AuthoringRunListResponse recoveredValue = new([Run("recovered-run")], false);
        WorkspaceObservation<AuthoringRunListResponse> recovered =
            failed.Begin(ProbeTime.AddMinutes(8)).Complete(
                WorkspaceReadOutcome<AuthoringRunListResponse>.Success(recoveredValue),
                ProbeTime.AddMinutes(9));
        Assert.True(recovered.IsCurrentSuccess);
        Assert.False(recovered.IsStale);
        Assert.Same(recoveredValue, recovered.CurrentValue);
        Assert.Equal(ProbeTime.AddMinutes(8), recovered.AttemptStartedAt);
        Assert.Equal(ProbeTime.AddMinutes(9), recovered.AttemptCompletedAt);
        Assert.Equal(ProbeTime.AddMinutes(9), recovered.LastSucceededAt);
        Assert.Null(recovered.Failure);
        Assert.False(initialFailure.Select<string>(_ =>
            throw new InvalidOperationException("No success to project.")).HasLastSuccess);
        Assert.False(unavailableOutcome.Select<string>(_ =>
            throw new InvalidOperationException("No success to project.")).IsSuccess);
    }

    [Fact]
    public async Task SuccessfulReadinessWithBlockersIsNotPermissionToStart()
    {
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromResult(new ServicesStatusResponse([])),
        };
        WorkspaceReadOutcome<TicketWorkspaceReadiness> outcome =
            await CreateReader(readiness: readiness).ReadReadinessAsync();
        TicketWorkspaceReadiness value = AssertSuccess(outcome);
        WorkspaceObservation<TicketWorkspaceReadiness> observation =
            WorkspaceObservation<TicketWorkspaceReadiness>.Pending
                .Begin(ProbeTime)
                .Complete(outcome, ProbeTime.AddMinutes(1));

        Assert.True(observation.IsCurrentSuccess);
        Assert.Null(value.LastCheckedAt);
        Assert.Equal(2, value.Workflows.Count);
        Assert.All(value.Workflows.Values, workflow =>
        {
            Assert.False(workflow.CanStart);
            Assert.NotEmpty(workflow.Blockers);
            Assert.Null(workflow.CheckedAt);
            Assert.Null(workflow.Processor.CheckedAt);
            Assert.Null(workflow.Orchestrator.CheckedAt);
        });
    }

    [Fact]
    public async Task SnapshotProjectionsPreserveReadAndProbeTimestamps()
    {
        TicketWorkflowCatalog catalog = new();
        TicketWorkspaceSnapshot initial = TicketWorkspaceSnapshot.Create(catalog.Workflows);
        Assert.Equal(0, initial.Revision);
        Assert.Equal(["plan", "prepare"], initial.Workflows.Keys.Order());
        Assert.All(initial.Workflows.Values, workflow =>
        {
            Assert.Same(catalog.Get(workflow.Workflow.RouteKey), workflow.Workflow);
            Assert.Equal(WorkspaceObservationPhase.Pending, workflow.History.Phase);
            Assert.Equal(
                WorkspaceObservationPhase.Pending,
                initial.ReadinessFor(workflow.Workflow.RouteKey).Phase);
        });
        ServicesStatusResponse services = Services();
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromResult(services),
        };
        WorkspaceReadOutcome<TicketWorkspaceReadiness> outcome =
            await CreateReader(readiness: readiness).ReadReadinessAsync();
        DateTimeOffset readStartedAt = ProbeTime.AddHours(1);
        DateTimeOffset readCompletedAt = readStartedAt.AddMinutes(1);
        TicketWorkspaceSnapshot current = initial with
        {
            Revision = 1,
            Readiness = initial.Readiness.Begin(readStartedAt).Complete(outcome, readCompletedAt),
            Workflows = initial.Workflows.SetItem(
                "prepare",
                initial.Workflows["prepare"] with
                {
                    History = initial.Workflows["prepare"].History.Begin(readStartedAt),
                }),
        };
        Assert.Equal(WorkspaceObservationPhase.Pending, initial.Workflows["prepare"].History.Phase);
        Assert.True(current.Workflows["prepare"].History.IsLoading);
        Assert.Same(initial.Workflows["plan"], current.Workflows["plan"]);
        WorkspaceObservation<TicketWorkflowReadiness> prepared =
            current.ReadinessFor("PREPARE");
        Assert.True(prepared.CurrentValue?.CanStart);
        Assert.Equal(readStartedAt, prepared.AttemptStartedAt);
        Assert.Equal(readCompletedAt, prepared.AttemptCompletedAt);
        Assert.Equal(readCompletedAt, prepared.LastSucceededAt);
        Assert.Equal(ProbeTime, prepared.CurrentValue?.CheckedAt);
        Assert.Equal(ProbeTime.AddMinutes(-1), prepared.CurrentValue?.Orchestrator.CheckedAt);
        Assert.Equal(ProbeTime.AddMinutes(-2), prepared.CurrentValue?.Processor.CheckedAt);
        Assert.Equal(ProbeTime, current.Readiness.CurrentValue?.LastCheckedAt);

        TicketWorkspaceSnapshot refreshing = current with
        {
            Revision = 2,
            Readiness = current.Readiness.Begin(readCompletedAt.AddMinutes(1)),
        };
        WorkspaceReadFailure failure = Failure(WorkspaceReadFailureReason.Timeout);
        TicketWorkspaceSnapshot failed = refreshing with
        {
            Revision = 3,
            Readiness = refreshing.Readiness.Complete(
                WorkspaceReadOutcome<TicketWorkspaceReadiness>.FromFailure(failure),
                readCompletedAt.AddMinutes(2)),
        };
        foreach (TicketWorkspaceSnapshot stale in new[] { refreshing, failed })
        {
            WorkspaceObservation<TicketWorkflowReadiness> projection = stale.ReadinessFor("prepare");
            Assert.Null(projection.CurrentValue);
            Assert.True(projection.IsStale);
            Assert.True(projection.LastSuccessfulValue?.CanStart);
            Assert.Equal(readCompletedAt, projection.LastSucceededAt);
            Assert.Equal(ProbeTime, projection.LastSuccessfulValue?.CheckedAt);
            Assert.Same(stale.Readiness.Failure, projection.Failure);
        }
        TicketWorkspaceSnapshot recovered = failed with
        {
            Revision = 4,
            Readiness = failed.Readiness.Begin(readCompletedAt.AddMinutes(3))
                .Complete(outcome, readCompletedAt.AddMinutes(4)),
        };
        WorkspaceObservation<TicketWorkflowReadiness> recoveredProjection =
            recovered.ReadinessFor("prepare");
        Assert.True(recoveredProjection.CurrentValue?.CanStart);
        Assert.Equal(readCompletedAt.AddMinutes(4), recoveredProjection.LastSucceededAt);
        Assert.Equal(ProbeTime, recoveredProjection.CurrentValue?.CheckedAt);
        Assert.Equal(ProbeTime.AddMinutes(-2), recoveredProjection.CurrentValue?.Processor.CheckedAt);
    }

    private static TicketWorkspaceReader CreateReader(
        IAuthoringControlClient? authoring = null,
        IOrchestratorReadinessClient? readiness = null,
        IOptions<DevUiOptions>? options = null,
        ILogger<TicketWorkspaceReader>? logger = null) => new(
            authoring ?? new FakeAuthoringClient(),
            readiness ?? new FakeReadinessClient(),
            new TicketWorkflowCatalog(),
            new ReadinessEvaluator(),
            options ?? Options.Create(new DevUiOptions { RecentRunLimit = RecentRunLimit }),
            logger ?? NullLogger<TicketWorkspaceReader>.Instance);

    private static T AssertSuccess<T>(WorkspaceReadOutcome<T> outcome) where T : class
    {
        Assert.Equal(WorkspaceObservationPhase.Succeeded, outcome.Phase);
        Assert.True(outcome.IsSuccess);
        Assert.Null(outcome.Failure);
        return Assert.IsType<T>(outcome.Value);
    }

    private static WorkspaceReadFailure AssertFailure<T>(
        WorkspaceReadOutcome<T> outcome,
        WorkspaceObservationPhase phase,
        WorkspaceReadFailureReason reason) where T : class
    {
        Assert.Equal(phase, outcome.Phase);
        Assert.False(outcome.IsSuccess);
        Assert.Null(outcome.Value);
        WorkspaceReadFailure failure = Assert.IsType<WorkspaceReadFailure>(outcome.Failure);
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(phase == WorkspaceObservationPhase.Unavailable, failure.IsUnavailable);
        return failure;
    }

    private static async Task AssertReadFailuresAsync(
        Exception exception,
        WorkspaceObservationPhase phase,
        WorkspaceReadFailureReason reason)
    {
        FakeAuthoringClient authoring = new()
        {
            ListHandler = (_, _, _) => Task.FromException<AuthoringRunListResponse>(exception),
        };
        FakeReadinessClient readiness = new()
        {
            Handler = (_, _) => Task.FromException<ServicesStatusResponse>(exception),
        };
        TestLogger logger = new();
        TicketWorkspaceReader reader = CreateReader(authoring, readiness, logger: logger);
        WorkspaceReadFailure history = AssertFailure(
            await reader.ReadHistoryAsync("PREPARE"), phase, reason);
        WorkspaceReadFailure current = AssertFailure(
            await reader.ReadReadinessAsync(), phase, reason);
        WorkspaceReadFailure refresh = AssertFailure(
            await reader.ReadReadinessAsync(refresh: true), phase, reason);

        Assert.Equal("prepare", history.Workflow);
        Assert.Equal("history", history.Operation);
        Assert.Equal(HistoryEndpoint, history.Endpoint);
        Assert.Null(current.Workflow);
        Assert.Null(refresh.Workflow);
        Assert.Equal("readiness", current.Operation);
        Assert.Equal("readiness-refresh", refresh.Operation);
        Assert.Equal("api/v1/services", current.Endpoint);
        Assert.Equal("api/v1/services/refresh", refresh.Endpoint);
        WorkspaceReadFailure[] failures = [history, current, refresh];
        LogEntry[] logs = logger.Entries.ToArray();
        Assert.Equal(failures.Length, logs.Length);
        for (int index = 0; index < failures.Length; index++)
        {
            WorkspaceReadFailure failure = failures[index];
            Assert.Equal((exception as HttpRequestException)?.StatusCode, failure.StatusCode);
            Assert.Equal((exception as AuthoringControlException)?.ErrorCode, failure.ErrorCode);
            Assert.Contains(failure.Endpoint, failure.Message);
            Assert.DoesNotContain(PrivateDetail, failure.Message);
            Assert.DoesNotContain("not started", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NotStarted", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("offline", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stopped", failure.Message, StringComparison.OrdinalIgnoreCase);
            if (failure.StatusCode is HttpStatusCode statusCode)
            {
                Assert.Contains($"HTTP {(int)statusCode}", failure.Message);
            }
            LogEntry log = logs[index];
            Assert.Same(exception, log.Exception);
            Assert.Equal(
                reason == WorkspaceReadFailureReason.Unexpected ? LogLevel.Error : LogLevel.Warning,
                log.Level);
            Assert.Equal(failure.Operation, log.Fields["Operation"]);
            Assert.Equal(failure.Workflow, log.Fields["Workflow"]);
            Assert.Equal(failure.Endpoint, log.Fields["Endpoint"]);
            Assert.Equal(failure.ErrorCode, log.Fields["ErrorCode"]);
            Assert.Equal((int?)failure.StatusCode, log.Fields["StatusCode"]);
            Assert.Equal(failure.Reason, log.Fields["Reason"]);
        }
        Assert.Single(authoring.Requests);
        Assert.Equal(2, readiness.Requests.Count);
    }

    private static Exception FailureException(string cause, int? status) =>
        cause switch
        {
            "authoring" => new AuthoringControlException(
                (HttpStatusCode)(status ?? throw new ArgumentNullException(nameof(status))),
                "processor-read-error",
                PrivateDetail),
            "http" => new HttpRequestException(PrivateDetail, null, (HttpStatusCode?)status),
            "transport" => new HttpRequestException(PrivateDetail),
            "stream" => new IOException(PrivateDetail),
            "timeout" => new TimeoutException(PrivateDetail),
            "task-canceled" => new TaskCanceledException(PrivateDetail),
            "operation-canceled" => new OperationCanceledException(PrivateDetail),
            "json" => new JsonException(PrivateDetail),
            "invalid-response" => new InvalidOperationException(PrivateDetail),
            "options" => new OptionsValidationException("DevUi", typeof(DevUiOptions), [PrivateDetail]),
            _ => new ApplicationException(PrivateDetail),
        };

    private static WorkspaceReadFailure Failure(WorkspaceReadFailureReason reason) =>
        new(reason, "history", "prepare", HistoryEndpoint, "The history read did not succeed.");

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

    private static ServicesStatusResponse Services(bool plannerAvailable = true) => new(
    [
        Health("Orchestrator", "orchestrator") with { CheckedAt = ProbeTime.AddMinutes(-1) },
        Health("Preparer", "processing") with { RequiredServices = ["Jira"] },
        Health("Planner", "processing") with
        {
            Status = plannerAvailable ? "healthy" : "unavailable",
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

    private static HttpClient HttpClientFor(
        HttpStatusCode statusCode,
        string body,
        string mediaType = "application/json",
        List<(HttpMethod Method, string Path)>? requests = null) => new(
            new DelegateHandler(request =>
            {
                requests?.Add((
                    request.Method,
                    request.RequestUri?.PathAndQuery ??
                        throw new InvalidOperationException("A request URI is required.")));
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, mediaType),
                };
            }))
            {
                BaseAddress = new Uri("http://orchestrator/"),
            };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class FakeReadinessClient : IOrchestratorReadinessClient
    {
        public ConcurrentQueue<(bool Refresh, CancellationToken Token)> Requests { get; } = new();

        public Func<bool, CancellationToken, Task<ServicesStatusResponse>> Handler { get; init; } =
            (_, _) => throw new NotSupportedException("Unexpected readiness read.");

        public Task<ServicesStatusResponse> GetAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((false, ct));
            return Handler(false, ct);
        }

        public Task<ServicesStatusResponse> RefreshAsync(CancellationToken ct = default)
        {
            Requests.Enqueue((true, ct));
            return Handler(true, ct);
        }
    }

    private sealed class FakeAuthoringClient : IAuthoringControlClient
    {
        public ConcurrentQueue<(string Service, int? Limit, CancellationToken Token)> Requests { get; } = new();

        public Func<string, int?, CancellationToken, Task<AuthoringRunListResponse>>
            ListHandler { get; init; } =
                (_, _, _) => throw new NotSupportedException("Unexpected history read.");

        public Task<AuthoringRunListResponse> ListAsync(
            string serviceName,
            int? limit,
            CancellationToken ct)
        {
            Requests.Enqueue((serviceName, limit, ct));
            return ListHandler(serviceName, limit, ct);
        }

        public Task<AuthoringStartResult> StartAsync<TRequest>(
            string serviceName, TRequest request, CancellationToken ct) =>
            throw new NotSupportedException("A workspace read must not start a run.");

        public Task<AuthoringRunResponse> GetAsync(
            string serviceName, string runId, CancellationToken ct) =>
            throw new NotSupportedException("A workspace read must not open a run.");

        public Task<AuthoringRetryResponse> RetryAsync(
            string serviceName, string runId, string itemId, CancellationToken ct) =>
            throw new NotSupportedException("A workspace read must not retry an item.");

        public Task<AuthoringItemSupersedeResult> SupersedeAsync(
            string serviceName, string runId, string itemId, string reason, CancellationToken ct) =>
            throw new NotSupportedException("A workspace read must not supersede an item.");

        public Task<VerifiedAuthoringSnapshotPair> DownloadSnapshotPairAsync(
            string serviceName, string runId, string pairDirectory, CancellationToken ct) =>
            throw new NotSupportedException("A workspace read must not download snapshots.");
    }

    private sealed class InvalidOptions : IOptions<DevUiOptions>
    {
        public DevUiOptions Value => throw new OptionsValidationException(
            "DevUi", typeof(DevUiOptions), [PrivateDetail]);
    }

    private sealed record LogEntry(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Fields);

    private sealed class TestLogger : ILogger<TicketWorkspaceReader>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IEnumerable<KeyValuePair<string, object?>> fields =
                Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Entries.Enqueue(new LogEntry(
                logLevel,
                exception,
                fields.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }
}
