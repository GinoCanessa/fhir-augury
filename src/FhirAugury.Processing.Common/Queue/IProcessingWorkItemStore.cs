namespace FhirAugury.Processing.Common.Queue;

/// <summary>
/// Store contract implemented by concrete Processing services over their domain-owned work-item tables.
/// Pending work is <c>ProcessingStatus IS NULL</c>; in-flight is <c>in-progress</c>, complete is
/// <c>complete</c>, and failed is <c>error</c>. Implementations may adapt generated records into
/// <typeparamref name="TItem"/> instead of making generated records implement <see cref="IProcessingWorkItem"/> directly.
/// </summary>
public interface IProcessingWorkItemStore<TItem>
{
    Task<IReadOnlyList<TItem>> GetPendingAsync(int maxItems, CancellationToken ct);

    /// <summary>
    /// Atomically claims an item. Successful implementations set status to <c>in-progress</c>, set
    /// start/last-attempt timestamps, and increment the attempt count.
    /// </summary>
    Task<bool> ClaimItemAsync(TItem item, DateTimeOffset startedAt, CancellationToken ct);

    Task MarkCompleteAsync(TItem item, DateTimeOffset completedAt, CancellationToken ct);
    Task MarkErrorAsync(TItem item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct);
    Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct);
    Task<ProcessingQueueStats> GetQueueStatsAsync(CancellationToken ct);
}
