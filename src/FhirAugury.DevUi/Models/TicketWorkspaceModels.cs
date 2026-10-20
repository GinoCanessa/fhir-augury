using System.Collections.Immutable;
using System.Net;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.DevUi.Models;

public enum WorkspaceObservationPhase
{
    Pending,
    Loading,
    Succeeded,
    Unavailable,
    Failed,
}

public enum WorkspaceReadFailureReason
{
    TransientHttp,
    Transport,
    Timeout,
    Authentication,
    Authorization,
    NotFound,
    HttpError,
    InvalidWorkflow,
    InvalidOptions,
    InvalidResponse,
    Unexpected,
}

public sealed record WorkspaceReadFailure(
    WorkspaceReadFailureReason Reason,
    string Operation,
    string? Workflow,
    string Endpoint,
    string Message,
    HttpStatusCode? StatusCode = null,
    string? ErrorCode = null)
{
    public bool IsUnavailable =>
        Reason is WorkspaceReadFailureReason.TransientHttp or
            WorkspaceReadFailureReason.Transport or
            WorkspaceReadFailureReason.Timeout;
}

public sealed record WorkspaceReadOutcome<T> where T : class
{
    private WorkspaceReadOutcome(
        WorkspaceObservationPhase phase,
        T? value,
        WorkspaceReadFailure? failure)
    {
        Phase = phase;
        Value = value;
        Failure = failure;
    }

    public WorkspaceObservationPhase Phase { get; }

    public T? Value { get; }

    public WorkspaceReadFailure? Failure { get; }

    public bool IsSuccess =>
        Phase == WorkspaceObservationPhase.Succeeded;

    public static WorkspaceReadOutcome<T> Success(T? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(
            WorkspaceObservationPhase.Succeeded,
            value,
            null);
    }

    public static WorkspaceReadOutcome<T> FromFailure(
        WorkspaceReadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(
            failure.IsUnavailable
                ? WorkspaceObservationPhase.Unavailable
                : WorkspaceObservationPhase.Failed,
            null,
            failure);
    }

    public WorkspaceReadOutcome<TResult> Select<TResult>(
        Func<T, TResult> selector) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        return Value is T value
            ? WorkspaceReadOutcome<TResult>.Success(selector(value))
            : WorkspaceReadOutcome<TResult>.FromFailure(
                Failure ?? throw new InvalidOperationException(
                    "An unsuccessful read must have a failure."));
    }
}

public sealed record WorkspaceObservation<T> where T : class
{
    private WorkspaceObservation(
        WorkspaceObservationPhase phase,
        DateTimeOffset? attemptStartedAt,
        DateTimeOffset? attemptCompletedAt,
        WorkspaceReadFailure? failure,
        T? lastSuccessfulValue,
        DateTimeOffset? lastSucceededAt)
    {
        Phase = phase;
        AttemptStartedAt = attemptStartedAt;
        AttemptCompletedAt = attemptCompletedAt;
        Failure = failure;
        LastSuccessfulValue = lastSuccessfulValue;
        LastSucceededAt = lastSucceededAt;
    }

    public static WorkspaceObservation<T> Pending { get; } =
        new(WorkspaceObservationPhase.Pending, null, null, null, null, null);

    public WorkspaceObservationPhase Phase { get; }

    public DateTimeOffset? AttemptStartedAt { get; }

    public DateTimeOffset? AttemptCompletedAt { get; }

    public WorkspaceReadFailure? Failure { get; }

    public T? LastSuccessfulValue { get; }

    public DateTimeOffset? LastSucceededAt { get; }

    public bool IsLoading =>
        Phase == WorkspaceObservationPhase.Loading;

    public bool IsCurrentSuccess =>
        Phase == WorkspaceObservationPhase.Succeeded;

    public bool HasLastSuccess =>
        LastSuccessfulValue is not null;

    public bool IsStale =>
        HasLastSuccess && !IsCurrentSuccess;

    public T? CurrentValue =>
        IsCurrentSuccess ? LastSuccessfulValue : null;

    public WorkspaceObservation<T> Begin(DateTimeOffset startedAt)
    {
        if (IsLoading)
        {
            throw new InvalidOperationException(
                "This observation already has a read in progress.");
        }
        return new(
            WorkspaceObservationPhase.Loading,
            startedAt,
            null,
            null,
            LastSuccessfulValue,
            LastSucceededAt);
    }

    public WorkspaceObservation<T> Complete(
        WorkspaceReadOutcome<T> outcome,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IsLoading)
        {
            throw new InvalidOperationException(
                "Only an in-progress observation can complete a read.");
        }
        return new(
            outcome.Phase,
            AttemptStartedAt,
            completedAt,
            outcome.Failure,
            outcome.IsSuccess ? outcome.Value : LastSuccessfulValue,
            outcome.IsSuccess ? completedAt : LastSucceededAt);
    }

    public WorkspaceObservation<TResult> Select<TResult>(
        Func<T, TResult> selector) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        TResult? value = null;
        if (LastSuccessfulValue is T lastSuccess)
        {
            value = selector(lastSuccess);
            ArgumentNullException.ThrowIfNull(value);
        }
        return new WorkspaceObservation<TResult>(
            Phase,
            AttemptStartedAt,
            AttemptCompletedAt,
            Failure,
            value,
            LastSucceededAt);
    }
}

public sealed record TicketWorkspaceReadiness(
    DateTimeOffset? LastCheckedAt,
    ImmutableDictionary<string, TicketWorkflowReadiness> Workflows);

public sealed record TicketWorkflowWorkspace(
    TicketWorkflowDefinition Workflow,
    WorkspaceObservation<AuthoringRunListResponse> History);

public sealed record TicketWorkspaceSnapshot(
    long Revision,
    WorkspaceObservation<TicketWorkspaceReadiness> Readiness,
    ImmutableDictionary<string, TicketWorkflowWorkspace> Workflows)
{
    public static TicketWorkspaceSnapshot Create(
        IEnumerable<TicketWorkflowDefinition> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return new(
            0,
            WorkspaceObservation<TicketWorkspaceReadiness>.Pending,
            workflows.ToImmutableDictionary(
                workflow => workflow.RouteKey,
                workflow => new TicketWorkflowWorkspace(
                    workflow,
                    WorkspaceObservation<AuthoringRunListResponse>.Pending),
                StringComparer.OrdinalIgnoreCase));
    }

    public WorkspaceObservation<TicketWorkflowReadiness> ReadinessFor(
        string workflow)
    {
        TicketWorkflowDefinition definition = Workflows[workflow].Workflow;
        return Readiness.Select(
            report => report.Workflows[definition.RouteKey]);
    }
}
