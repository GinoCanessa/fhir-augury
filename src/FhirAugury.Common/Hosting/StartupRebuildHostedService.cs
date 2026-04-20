using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Hosting;

/// <summary>
/// Base class for source-service startup rebuild work that previously ran
/// synchronously between <c>builder.Build()</c> and <c>app.Run()</c>.
///
/// Derived classes move that work into <see cref="RunStartupAsync"/>.
/// The base implementation:
///
/// 1. Defers work until <see cref="IHostApplicationLifetime.ApplicationStarted"/>
///    fires, so Kestrel is already listening when heavy work begins.
/// 2. Tracks state so the health endpoint can report "initializing" rather
///    than failing (or being unreachable).
/// 3. Captures (rather than rethrows) failures so the host stays up — failure
///    is surfaced through <see cref="LastError"/> and a "degraded" health status.
/// </summary>
public abstract class StartupRebuildHostedService(
    IHostApplicationLifetime lifetime,
    ILogger logger)
    : BackgroundService, IStartupRebuildStatus
{
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly ILogger _logger = logger;

    public StartupRebuildState State { get; private set; } = StartupRebuildState.Pending;
    public string? CurrentPhase { get; private set; }
    public Exception? LastError { get; private set; }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
        {
            State = StartupRebuildState.Cancelled;
            return;
        }

        State = StartupRebuildState.Running;
        try
        {
            await RunStartupAsync(stoppingToken).ConfigureAwait(false);
            State = StartupRebuildState.Completed;
            _logger.LogInformation("Startup rebuild completed");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            State = StartupRebuildState.Cancelled;
        }
        catch (Exception ex)
        {
            LastError = ex;
            State = StartupRebuildState.Failed;
            _logger.LogError(ex, "Startup rebuild failed");
        }
    }

    /// <summary>Performs the actual startup work. Override in derived classes.</summary>
    protected abstract Task RunStartupAsync(CancellationToken ct);

    /// <summary>
    /// Records the current rebuild phase and emits an info-level log entry.
    /// Use this to give operators visibility into long-running steps.
    /// </summary>
    protected void SetPhase(string phase)
    {
        CurrentPhase = phase;
        _logger.LogInformation("Startup rebuild phase: {Phase}", phase);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        if (_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration startedReg =
            _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using CancellationTokenRegistration stopReg =
            stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));

        try
        {
            await started.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller checks stoppingToken.IsCancellationRequested.
        }
    }
}
