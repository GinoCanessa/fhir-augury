namespace FhirAugury.Common.Hosting;

/// <summary>
/// Lifecycle state of a background startup rebuild for a source service.
/// </summary>
public enum StartupRebuildState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Read-only status snapshot of an in-progress (or completed) startup rebuild.
/// Surfaced by <see cref="Http.HttpServiceLifecycle.BuildHealthCheck"/> so that
/// callers (orchestrator health monitor) can distinguish "service is up but
/// still warming up" from "service is unavailable".
/// </summary>
public interface IStartupRebuildStatus
{
    StartupRebuildState State { get; }
    string? CurrentPhase { get; }
    Exception? LastError { get; }
}
