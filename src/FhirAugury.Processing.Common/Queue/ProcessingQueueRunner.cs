using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// Concurrency-limited runner for Processing work-item stores.
/// </summary>
public class ProcessingQueueRunner<TItem>(
    IProcessingWorkItemStore<TItem> store,
    IProcessingWorkItemHandler<TItem> handler,
    ProcessingLifecycleService lifecycle,
    IOptions<ProcessingServiceOptions> optionsAccessor,
    ILogger<ProcessingQueueRunner<TItem>> logger)
{
    private const int MaxErrorLength = 4096;
    private readonly ProcessingServiceOptions _options = optionsAccessor.Value;

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    protected virtual Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan interval = ParsePositiveTimeSpan(_options.SyncSchedule, TimeSpan.FromMinutes(5), "SyncSchedule");
        TimeSpan orphanedThreshold = ParsePositiveTimeSpan(_options.OrphanedInProgressThreshold, TimeSpan.FromMinutes(10), "OrphanedInProgressThreshold");
        int maxConcurrency = Math.Max(1, _options.MaxConcurrentProcessingThreads);

        int resetCount = await store.ResetOrphanedItemsAsync(orphanedThreshold, UtcNow, ct);
        logger.LogInformation("Reset {ResetCount} orphaned processing work items", resetCount);

        using SemaphoreSlim semaphore = new(maxConcurrency, maxConcurrency);
        List<Task> inFlight = [];

        while (!ct.IsCancellationRequested)
        {
            inFlight.RemoveAll(t => t.IsCompleted);

            if (!lifecycle.IsRunning)
            {
                await SafeDelayAsync(interval, ct);
                continue;
            }

            int capacity = maxConcurrency - inFlight.Count;
            if (capacity <= 0)
            {
                await WaitForCapacityOrDelayAsync(inFlight, interval, ct);
                continue;
            }

            lifecycle.RecordPoll(UtcNow);
            IReadOnlyList<TItem> pending = await store.GetPendingAsync(capacity, ct);
            if (pending.Count == 0)
            {
                await SafeDelayAsync(interval, ct);
                continue;
            }

            bool startedAny = false;
            foreach (TItem item in pending)
            {
                if (!lifecycle.IsRunning || ct.IsCancellationRequested)
                {
                    break;
                }

                await semaphore.WaitAsync(ct);
                Task task = ProcessOneAsync(item, semaphore, ct);
                inFlight.Add(task);
                startedAny = true;
            }

            if (!startedAny)
            {
                await SafeDelayAsync(interval, ct);
            }
        }

        await DrainAsync(inFlight);
    }

    private TimeSpan ParsePositiveTimeSpan(string value, TimeSpan fallback, string optionName)
    {
        if (TimeSpan.TryParse(value, out TimeSpan parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        logger.LogWarning("Invalid {OptionName} '{Value}'; using {Fallback}", optionName, value, fallback);
        return fallback;
    }

    private async Task ProcessOneAsync(TItem item, SemaphoreSlim semaphore, CancellationToken ct)
    {
        try
        {
            DateTimeOffset startedAt = UtcNow;
            bool claimed = await store.ClaimItemAsync(item, startedAt, ct);
            if (!claimed)
            {
                return;
            }

            await handler.ProcessAsync(item, ct);
            await store.MarkCompleteAsync(item, UtcNow, ct);
            lifecycle.RecordError(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            string message = ex.Message.Length > MaxErrorLength ? ex.Message[..MaxErrorLength] : ex.Message;
            lifecycle.RecordError(message);
            try
            {
                await store.MarkErrorAsync(item, message, UtcNow, CancellationToken.None);
            }
            catch (Exception markEx)
            {
                logger.LogError(markEx, "Failed to mark processing item as error");
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task WaitForCapacityOrDelayAsync(List<Task> inFlight, TimeSpan interval, CancellationToken ct)
    {
        if (inFlight.Count == 0)
        {
            await SafeDelayAsync(interval, ct);
            return;
        }

        Task delayTask = DelayAsync(interval, ct);
        Task completed = await Task.WhenAny(Task.WhenAny(inFlight), delayTask);
        if (completed == delayTask)
        {
            await SafeDelayAsync(TimeSpan.Zero, ct);
        }
    }

    private async Task SafeDelayAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await DelayAsync(interval, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static async Task DrainAsync(List<Task> inFlight)
    {
        try
        {
            await Task.WhenAll(inFlight);
        }
        catch
        {
        }
    }
}
