using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Queue;

public sealed class AuthoringRunSchedulerWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    internal Task WaitAsync(CancellationToken ct)
        => _signal.WaitAsync(ct);
}

public class AuthoringRunScheduler<TItem>(
    AuthoringRunStore store,
    IAuthoringRunLifecycleAdapter adapter,
    IAuthoringRunFinalizationStrategy finalizationStrategy,
    AuthoringQueueRunner<TItem> runner,
    ProcessingLifecycleService lifecycle,
    IOptions<ProcessingServiceOptions> optionsAccessor,
    ILogger<AuthoringRunScheduler<TItem>> logger,
    AuthoringRunSchedulerWakeSignal? wakeSignal = null)
    : BackgroundService
{
    private readonly ProcessingServiceOptions _options = optionsAccessor.Value;
    private readonly TimeSpan _syncSchedule = ParsePositiveTimeSpan(
        optionsAccessor.Value.SyncSchedule,
        nameof(ProcessingServiceOptions.SyncSchedule));
    private readonly AuthoringRunSchedulerWakeSignal _wakeSignal =
        wakeSignal ?? new AuthoringRunSchedulerWakeSignal();

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken ct)
        => Task.Delay(delay, ct);

    public void Wake() => _wakeSignal.Signal();

    public void SignalWake() => Wake();

    public async Task<TimeSpan> RunCycleAsync(CancellationToken ct = default)
    {
        AuthoringProcessorModeRecord mode =
            await store.GetProcessorModeAsync(adapter.ProcessorKind, ct);
        if (!string.Equals(
                mode.Mode,
                AuthoringStatusValues.ProcessorModes.RunBacked,
                StringComparison.Ordinal))
        {
            return _syncSchedule;
        }

        AuthoringRunRecord? run =
            await store.GetFencedRunAsync(adapter.ProcessorKind, ct);
        if (run is null)
        {
            if (lifecycle.IsRunning)
            {
                AuthoringRunRecord? queued =
                    await store.GetOldestQueuedRunAsync(adapter.ProcessorKind, ct);
                if (queued is not null &&
                    await store.TryAcquireMutationFenceAsync(
                        adapter.ProcessorKind,
                        queued.Id,
                        UtcNow,
                        ct))
                {
                    return TimeSpan.Zero;
                }
            }
            return _syncSchedule;
        }

        AuthoringRunReconciliationResult sourceReconciliation =
            await adapter.ReconcileRunAsync(run, ct);
        if (sourceReconciliation.Outcome !=
            AuthoringRunReconciliationOutcome.Current)
        {
            return TimeSpan.Zero;
        }

        AuthoringOrphanRecoveryResult orphanRecovery =
            await runner.ResetOrphanedItemsAsync(run.Id, ct);

        AuthoringErrorReconciliationResult retryReconciliation =
            await store.ReconcileErroredItemsAsync(run.Id, UtcNow, ct);
        run = await store.GetRunAsync(run.Id, ct)
            ?? throw new InvalidOperationException(
                $"Authoring run '{run.Id}' disappeared during scheduling.");

        if (await store.AllItemsCompleteAsync(run.Id, ct))
        {
            await finalizationStrategy.FinalizeRunAsync(run.Id, ct);
            return TimeSpan.Zero;
        }

        if (lifecycle.IsRunning &&
            string.Equals(
                run.Status,
                AuthoringStatusValues.Runs.Running,
                StringComparison.Ordinal))
        {
            lifecycle.RecordPoll(UtcNow);
            await runner.FillCapacityAsync(run.Id, ct);
        }

        DateTimeOffset? nextRetryAt = retryReconciliation.NextRetryAt ??
            await store.GetEarliestAutomaticRetryAtAsync(run.Id, ct);
        DateTimeOffset? nextWakeAt = Minimum(
            nextRetryAt,
            orphanRecovery.NextRecoveryAt);
        if (nextWakeAt is null)
        {
            return _syncSchedule;
        }

        TimeSpan deadlineWait = nextWakeAt.Value - UtcNow;
        if (deadlineWait <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return deadlineWait < _syncSchedule ? deadlineWait : _syncSchedule;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_options.ReconcileSnapshotsOnStartup)
            {
                await finalizationStrategy.ReconcileSnapshotsOnStartupAsync(
                    stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = await RunCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lifecycle.RecordError(Truncate(ex.Message));
                    logger.LogError(
                        ex,
                        "Authoring lifecycle cycle failed for processor {ProcessorKind}",
                        adapter.ProcessorKind);
                    wait = _syncSchedule;
                }

                await WaitForWakeAsync(wait, stoppingToken);
            }
        }
        finally
        {
            await runner.DrainAsync();
        }
    }

    protected async Task WaitForWakeAsync(
        TimeSpan wait,
        CancellationToken stoppingToken)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        using CancellationTokenSource waitCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task delay = DelayAsync(wait, waitCancellation.Token);
        Task wake = _wakeSignal.WaitAsync(waitCancellation.Token);
        Task? completion = runner.GetInFlightCompletionTask();
        Task winner = completion is null
            ? await Task.WhenAny(delay, wake)
            : await Task.WhenAny(completion, delay, wake);
        if (!stoppingToken.IsCancellationRequested)
        {
            await waitCancellation.CancelAsync();
        }
        await IgnoreExpectedCancellationAsync(winner, stoppingToken);
    }

    private static async Task IgnoreExpectedCancellationAsync(
        Task task,
        CancellationToken stoppingToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan ParsePositiveTimeSpan(string value, string optionName)
    {
        if (TimeSpan.TryParse(value, out TimeSpan result) &&
            result > TimeSpan.Zero)
        {
            return result;
        }
        throw new InvalidOperationException(
            $"{optionName} must be a positive TimeSpan string.");
    }

    private static string Truncate(string value)
        => value.Length <= 4096 ? value : value[..4096];

    private static DateTimeOffset? Minimum(
        DateTimeOffset? first,
        DateTimeOffset? second)
        => first is null
            ? second
            : second is null || first <= second
                ? first
                : second;
}
