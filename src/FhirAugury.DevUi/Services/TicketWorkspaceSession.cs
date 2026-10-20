using FhirAugury.DevUi.Models;

namespace FhirAugury.DevUi.Services;

public sealed class TicketWorkspaceSession : IAsyncDisposable
{
    private readonly TicketWorkspaceReader _reader;
    private readonly TicketWorkflowCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TicketWorkspaceSession> _logger;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<string, ReadAttempt> _historyAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ReadAttempt> _workers = [];

    private TicketWorkspaceSnapshot _snapshot;
    private ReadAttempt? _readinessAttempt;
    private Func<TicketWorkspaceSnapshot, Task>? _changed;
    private TaskCompletionSource? _disposal;
    private bool _started;
    private bool _disposed;

    public TicketWorkspaceSession(
        TicketWorkspaceReader reader,
        TicketWorkflowCatalog catalog,
        TimeProvider timeProvider,
        ILogger<TicketWorkspaceSession> logger)
    {
        _reader = reader ??
            throw new ArgumentNullException(nameof(reader));
        _catalog = catalog ??
            throw new ArgumentNullException(nameof(catalog));
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger));
        _lifetimeToken = _lifetimeCancellation.Token;
        _snapshot = TicketWorkspaceSnapshot.Create(_catalog.Workflows);
    }

    public TicketWorkspaceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    // Notifications are detached from reads and may reach a dispatcher out of
    // order. Consumers must check page identity and revision inside dispatch.
    public event Func<TicketWorkspaceSnapshot, Task> Changed
    {
        add
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _changed += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                _changed -= value;
            }
        }
    }

    public void Start()
    {
        TicketWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }
            _started = true;
            bool scheduled = ScheduleReadiness(refresh: false);
            foreach (TicketWorkflowDefinition workflow in _catalog.Workflows)
            {
                scheduled |= ScheduleHistory(workflow);
            }
            if (!scheduled)
            {
                return;
            }
            snapshot = _snapshot;
        }
        QueueNotification(snapshot);
    }

    public void RefreshHistory(string workflow)
    {
        TicketWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TicketWorkflowDefinition definition = _catalog.Get(workflow);
            if (!ScheduleHistory(definition))
            {
                return;
            }
            snapshot = _snapshot;
        }
        QueueNotification(snapshot);
    }

    public void RefreshAll()
    {
        TicketWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool scheduled = ScheduleReadiness(refresh: true);
            foreach (TicketWorkflowDefinition workflow in _catalog.Workflows)
            {
                scheduled |= ScheduleHistory(workflow);
            }
            if (!scheduled)
            {
                return;
            }
            snapshot = _snapshot;
        }
        QueueNotification(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        Task[] workers;
        TaskCompletionSource disposal;
        lock (_sync)
        {
            if (_disposal is not null)
            {
                return new ValueTask(_disposal.Task);
            }

            _disposed = true;
            _readinessAttempt = null;
            _historyAttempts.Clear();
            _changed = null;
            disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = disposal;
            workers = _workers.Select(attempt => attempt.Worker).ToArray();
        }

        _ = DisposeCoreAsync(workers, disposal);
        return new ValueTask(disposal.Task);
    }

    // The caller holds _sync through reservation and worker registration.
    // Workers check identity under the same lock before calling the reader.
    private bool ScheduleReadiness(bool refresh)
    {
        if (_readinessAttempt is not null)
        {
            return false;
        }

        ReadAttempt attempt = new(
            null,
            refresh ? "readiness-refresh" : "readiness",
            refresh ? "api/v1/services/refresh" : "api/v1/services");
        _readinessAttempt = attempt;
        _snapshot = _snapshot with
        {
            Revision = _snapshot.Revision + 1,
            Readiness = _snapshot.Readiness.Begin(_timeProvider.GetUtcNow()),
        };
        StartWorker(
            attempt,
            ct => _reader.ReadReadinessAsync(refresh, ct),
            (snapshot, outcome, completedAt) => snapshot with
            {
                Readiness = snapshot.Readiness.Complete(outcome, completedAt),
            });
        return true;
    }

    private bool ScheduleHistory(TicketWorkflowDefinition workflow)
    {
        string route = workflow.RouteKey;
        if (_historyAttempts.ContainsKey(route))
        {
            return false;
        }

        ReadAttempt attempt = new(
            route,
            "history",
            $"api/v1/processing-services/{Uri.EscapeDataString(workflow.ProcessingServiceName)}/authoring/runs");
        _historyAttempts.Add(route, attempt);
        TicketWorkflowWorkspace workspace = _snapshot.Workflows[route];
        _snapshot = _snapshot with
        {
            Revision = _snapshot.Revision + 1,
            Workflows = _snapshot.Workflows.SetItem(
                route,
                workspace with
                {
                    History = workspace.History.Begin(_timeProvider.GetUtcNow()),
                }),
        };
        StartWorker(
            attempt,
            ct => _reader.ReadHistoryAsync(route, ct),
            (snapshot, outcome, completedAt) => snapshot with
            {
                Workflows = snapshot.Workflows.SetItem(
                    route,
                    snapshot.Workflows[route] with
                    {
                        History = snapshot.Workflows[route].History.Complete(
                            outcome,
                            completedAt),
                    }),
            });
        return true;
    }

    private void StartWorker<T>(
        ReadAttempt attempt,
        Func<CancellationToken, Task<WorkspaceReadOutcome<T>>> read,
        Func<TicketWorkspaceSnapshot, WorkspaceReadOutcome<T>,
            DateTimeOffset, TicketWorkspaceSnapshot> complete) where T : class
    {
        _workers.Add(attempt);
        attempt.Worker = Task.Run(async () =>
        {
            try
            {
                lock (_sync)
                {
                    if (_disposed || !IsCurrent(attempt))
                    {
                        return;
                    }
                }

                WorkspaceReadOutcome<T> outcome;
                try
                {
                    outcome = await read(_lifetimeToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _lifetimeToken.ThrowIfCancellationRequested();
                    _logger.LogError(
                        ex,
                        "Workspace {Operation} reader faulted for {Workflow} at {Endpoint}",
                        attempt.Operation,
                        attempt.Workflow,
                        attempt.Endpoint);
                    outcome = WorkspaceReadOutcome<T>.FromFailure(new(
                        WorkspaceReadFailureReason.Unexpected,
                        attempt.Operation,
                        attempt.Workflow,
                        attempt.Endpoint,
                        "The workspace read could not be completed."));
                }

                TicketWorkspaceSnapshot snapshot;
                lock (_sync)
                {
                    if (_disposed || _lifetimeToken.IsCancellationRequested ||
                        !IsCurrent(attempt))
                    {
                        return;
                    }

                    _snapshot = complete(
                        _snapshot,
                        outcome,
                        _timeProvider.GetUtcNow()) with
                    {
                        Revision = _snapshot.Revision + 1,
                    };
                    if (attempt.Workflow is string route)
                    {
                        _historyAttempts.Remove(route);
                    }
                    else
                    {
                        _readinessAttempt = null;
                    }
                    snapshot = _snapshot;
                }
                QueueNotification(snapshot);
            }
            catch (OperationCanceledException)
                when (_lifetimeToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Workspace {Operation} worker failed for {Workflow} at {Endpoint}",
                    attempt.Operation,
                    attempt.Workflow,
                    attempt.Endpoint);
            }
            finally
            {
                lock (_sync)
                {
                    _workers.Remove(attempt);
                }
            }
        });
    }

    private bool IsCurrent(ReadAttempt attempt) =>
        attempt.Workflow is string route
            ? _historyAttempts.TryGetValue(route, out ReadAttempt? current) &&
                ReferenceEquals(current, attempt)
            : ReferenceEquals(_readinessAttempt, attempt);

    private void QueueNotification(TicketWorkspaceSnapshot snapshot)
    {
        _ = Task.Run(async () =>
        {
            Delegate[] subscribers;
            lock (_sync)
            {
                if (_disposed || _changed is null)
                {
                    return;
                }
                subscribers = _changed.GetInvocationList();
            }

            foreach (Func<TicketWorkspaceSnapshot, Task> subscriber in subscribers)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    if (_changed is null ||
                        !_changed.GetInvocationList().Contains(subscriber))
                    {
                        continue;
                    }
                }

                try
                {
                    await subscriber(snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Workspace subscriber failed for revision {Revision}",
                        snapshot.Revision);
                }
            }
        });
    }

    private async Task DisposeCoreAsync(
        Task[] workers,
        TaskCompletionSource disposal)
    {
        try
        {
            try
            {
                await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workspace read cancellation callback failed.");
            }

            // Renderer callbacks can themselves be awaiting page disposal.
            // Observe only reads here, never the detached notifications.
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                _lifetimeCancellation.Dispose();
            }
            disposal.TrySetResult();
        }
        catch (Exception ex)
        {
            disposal.TrySetException(ex);
        }
    }

    private sealed class ReadAttempt(
        string? workflow,
        string operation,
        string endpoint)
    {
        public string? Workflow { get; } = workflow;

        public string Operation { get; } = operation;

        public string Endpoint { get; } = endpoint;

        public Task Worker { get; set; } = Task.CompletedTask;
    }
}
