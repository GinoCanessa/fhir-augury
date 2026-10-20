using FhirAugury.Common.Hosting;

namespace FhirAugury.Server.Terminology.Hosting;

/// <summary>
/// Snapshot of the latest terminology index rebuild attempt.
/// </summary>
public sealed record TerminologyRefreshSnapshot(
    string CorrelationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    StartupRebuildState State,
    string? CurrentPhase,
    string? LastError);

/// <summary>
/// Singleton that tracks the in-progress (or most-recently-completed)
/// terminology index rebuild. The startup hosted service publishes
/// progress here; <c>IndexController</c> reads from it for
/// <c>GET /status</c>.
/// </summary>
public sealed class TerminologyIndexStatusTracker
{
    private readonly object _gate = new();
    private TerminologyRefreshSnapshot? _current;

    public TerminologyRefreshSnapshot? Current
    {
        get { lock (_gate) { return _current; } }
    }

    public string BeginRefresh()
    {
        string correlationId = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _current = new TerminologyRefreshSnapshot(
                CorrelationId: correlationId,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                State: StartupRebuildState.Running,
                CurrentPhase: "starting",
                LastError: null);
        }
        return correlationId;
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = _current with { CurrentPhase = phase };
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = _current with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                State = StartupRebuildState.Completed,
                CurrentPhase = null,
            };
        }
    }

    public void Fail(Exception ex)
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = _current with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                State = StartupRebuildState.Failed,
                LastError = ex.Message,
            };
        }
    }
}
