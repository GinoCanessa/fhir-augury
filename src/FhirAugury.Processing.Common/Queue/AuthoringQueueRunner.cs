using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Queue;

public sealed record AuthoringQueueClaim(
    string OperationId,
    string OperationToken,
    int AttemptNumber);

public interface IAuthoringQueueStore<TItem>
{
    Task<IReadOnlyList<TItem>> GetPendingAsync(int maxItems, CancellationToken ct);
    Task<AuthoringQueueClaim?> TryClaimAsync(TItem item, DateTimeOffset startedAt, CancellationToken ct);
    Task ApplyResultAsync(
        TItem item,
        AuthoringQueueClaim claim,
        AuthoringWorkResult result,
        DateTimeOffset completedAt,
        CancellationToken ct);
    Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct);
}

public interface IAuthoringWorkItemHandler<TItem>
{
    Task<AuthoringWorkResult> ProcessAsync(
        TItem item,
        AuthoringQueueClaim claim,
        CancellationToken ct);
}

public class AuthoringQueueRunner<TItem>(
    IAuthoringQueueStore<TItem> store,
    IAuthoringWorkItemHandler<TItem> handler,
    ProcessingLifecycleService lifecycle,
    IOptions<ProcessingServiceOptions> optionsAccessor,
    ILogger<AuthoringQueueRunner<TItem>> logger)
{
    private const int MaxErrorLength = 4096;
    private readonly ProcessingServiceOptions _options = optionsAccessor.Value;

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan interval = ParsePositiveTimeSpan(_options.SyncSchedule, TimeSpan.FromMinutes(5), "SyncSchedule");
        TimeSpan orphanedThreshold = ParsePositiveTimeSpan(
            _options.OrphanedInProgressThreshold,
            TimeSpan.FromMinutes(10),
            "OrphanedInProgressThreshold");
        int maxConcurrency = Math.Max(1, _options.MaxConcurrentProcessingThreads);

        int resetCount = await store.ResetOrphanedItemsAsync(orphanedThreshold, UtcNow, ct);
        logger.LogInformation("Reset {ResetCount} orphaned authoring work items", resetCount);

        using SemaphoreSlim semaphore = new(maxConcurrency, maxConcurrency);
        List<Task> inFlight = [];
        while (!ct.IsCancellationRequested)
        {
            inFlight.RemoveAll(task => task.IsCompleted);

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

            foreach (TItem item in pending)
            {
                if (!lifecycle.IsRunning || ct.IsCancellationRequested)
                {
                    break;
                }

                await semaphore.WaitAsync(ct);
                inFlight.Add(ProcessOneAsync(item, semaphore, ct));
            }
        }

        await DrainAsync(inFlight);
    }

    private async Task ProcessOneAsync(TItem item, SemaphoreSlim semaphore, CancellationToken ct)
    {
        try
        {
            AuthoringQueueClaim? claim = await store.TryClaimAsync(item, UtcNow, ct);
            if (claim is null)
            {
                return;
            }

            AuthoringWorkResult result;
            try
            {
                result = await handler.ProcessAsync(item, claim, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = AuthoringWorkResult.Retry(Truncate(ex.Message));
            }

            await store.ApplyResultAsync(item, claim, result, UtcNow, CancellationToken.None);
            lifecycle.RecordError(result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lifecycle.RecordError(Truncate(ex.Message));
            logger.LogError(ex, "Failed to process authoring work item");
        }
        finally
        {
            semaphore.Release();
        }
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

    private static async Task DrainAsync(List<Task> inFlight)
    {
        try
        {
            await Task.WhenAll(inFlight);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForCapacityOrDelayAsync(
        List<Task> inFlight,
        TimeSpan interval,
        CancellationToken ct)
    {
        if (inFlight.Count == 0)
        {
            await SafeDelayAsync(interval, ct);
            return;
        }

        Task delay = Task.Delay(interval, ct);
        await Task.WhenAny(Task.WhenAny(inFlight), delay);
    }

    private static async Task SafeDelayAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static string Truncate(string value)
        => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
