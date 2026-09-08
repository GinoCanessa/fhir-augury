using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Queue;

public class AuthoringRunScheduler<TItem>(
    AuthoringRunStore store,
    IAuthoringRunLifecycleAdapter adapter,
    IAuthoringRunFinalizationStrategy finalizationStrategy,
    AuthoringQueueRunner<TItem> runner,
    ProcessingLifecycleService lifecycle,
    IOptions<ProcessingServiceOptions> optionsAccessor,
    ILogger<AuthoringRunScheduler<TItem>> logger)
    : BackgroundService
{
    private readonly ProcessingServiceOptions _options = optionsAccessor.Value;
    private readonly TimeSpan _syncSchedule = ParsePositiveTimeSpan(
        optionsAccessor.Value.SyncSchedule,
        nameof(ProcessingServiceOptions.SyncSchedule));
    private string? _orphanRecoveryRunId;

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken ct)
        => Task.Delay(delay, ct);

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
            _orphanRecoveryRunId = null;
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
            _orphanRecoveryRunId = null;
            return TimeSpan.Zero;
        }

        if (!string.Equals(_orphanRecoveryRunId, run.Id, StringComparison.Ordinal))
        {
            await runner.ResetOrphanedItemsAsync(run.Id, ct);
            _orphanRecoveryRunId = run.Id;
        }

        AuthoringErrorReconciliationResult retryReconciliation =
            await store.ReconcileErroredItemsAsync(run.Id, UtcNow, ct);
        run = await store.GetRunAsync(run.Id, ct)
            ?? throw new InvalidOperationException(
                $"Authoring run '{run.Id}' disappeared during scheduling.");

        if (await store.AllItemsCompleteAsync(run.Id, ct))
        {
            await finalizationStrategy.FinalizeRunAsync(run.Id, ct);
            _orphanRecoveryRunId = null;
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
        if (nextRetryAt is null)
        {
            return _syncSchedule;
        }

        TimeSpan retryWait = nextRetryAt.Value - UtcNow;
        if (retryWait <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return retryWait < _syncSchedule ? retryWait : _syncSchedule;
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

        Task? completion = runner.GetInFlightCompletionTask();
        if (completion is null)
        {
            await SafeDelayAsync(wait, stoppingToken);
            return;
        }

        using CancellationTokenSource delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task delay = DelayAsync(wait, delayCancellation.Token);
        Task winner = await Task.WhenAny(completion, delay);
        if (winner == completion)
        {
            await delayCancellation.CancelAsync();
        }
        await IgnoreExpectedCancellationAsync(winner, stoppingToken);
    }

    private async Task SafeDelayAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await DelayAsync(wait, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
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
}
