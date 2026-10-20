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

public sealed record AuthoringOrphanRecoveryResult(
    int RecoveredItems,
    DateTimeOffset? NextRecoveryAt);

public interface IAuthoringQueueStore<TItem>
{
    Task<IReadOnlyList<TItem>> GetPendingAsync(string runId, int maxItems, CancellationToken ct);
    Task<AuthoringQueueClaim?> TryClaimAsync(TItem item, DateTimeOffset startedAt, CancellationToken ct);
    Task ApplyResultAsync(
        TItem item,
        AuthoringQueueClaim claim,
        AuthoringWorkResult result,
        DateTimeOffset completedAt,
        CancellationToken ct);
    Task<AuthoringOrphanRecoveryResult> ResetOrphanedItemsAsync(
        string runId,
        TimeSpan olderThan,
        DateTimeOffset now,
        CancellationToken ct);
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
    private readonly List<Task> _inFlight = [];
    private readonly int _maxConcurrency =
        Math.Max(1, optionsAccessor.Value.MaxConcurrentProcessingThreads);
    private readonly TimeSpan _orphanedThreshold = ParsePositiveTimeSpan(
        optionsAccessor.Value.OrphanedInProgressThreshold,
        TimeSpan.FromMinutes(10),
        nameof(ProcessingServiceOptions.OrphanedInProgressThreshold),
        logger);

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public int InFlightCount
    {
        get
        {
            RemoveCompleted();
            return _inFlight.Count;
        }
    }

    public async Task<AuthoringOrphanRecoveryResult> ResetOrphanedItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringOrphanRecoveryResult result = await store.ResetOrphanedItemsAsync(
            runId,
            _orphanedThreshold,
            UtcNow,
            ct);
        logger.LogInformation(
            "Reset {ResetCount} orphaned authoring work items for run {RunId}",
            result.RecoveredItems,
            runId);
        return result;
    }

    public async Task<int> FillCapacityAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        RemoveCompleted();
        if (!lifecycle.IsRunning)
        {
            return 0;
        }

        int capacity = _maxConcurrency - _inFlight.Count;
        if (capacity <= 0)
        {
            return 0;
        }

        IReadOnlyList<TItem> pending =
            await store.GetPendingAsync(runId, capacity, ct);
        int started = 0;
        foreach (TItem item in pending.Take(capacity))
        {
            if (!lifecycle.IsRunning || ct.IsCancellationRequested)
            {
                break;
            }

            _inFlight.Add(ProcessOneAsync(item, ct));
            started++;
        }
        return started;
    }

    public Task? GetInFlightCompletionTask()
    {
        RemoveCompleted();
        return _inFlight.Count == 0
            ? null
            : Task.WhenAny(_inFlight.ToArray());
    }

    public async Task DrainAsync()
    {
        Task[] tasks = _inFlight.ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RemoveCompleted();
        }
    }

    private async Task ProcessOneAsync(TItem item, CancellationToken ct)
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
    }

    private void RemoveCompleted()
        => _inFlight.RemoveAll(task => task.IsCompleted);

    private static TimeSpan ParsePositiveTimeSpan(
        string value,
        TimeSpan fallback,
        string optionName,
        ILogger logger)
    {
        if (TimeSpan.TryParse(value, out TimeSpan parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        logger.LogWarning("Invalid {OptionName} '{Value}'; using {Fallback}", optionName, value, fallback);
        return fallback;
    }

    private static string Truncate(string value)
        => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
