using FhirAugury.Processing.Common.Queue;

namespace FhirAugury.Processing.Common.Tests.Queue;

public class ProcessingAbstractionTests
{
    [Fact]
    public async Task StoreTransitions_ModelPendingClaimCompleteAndError()
    {
        FakeProcessingWorkItem completeItem = new("complete-item");
        FakeProcessingWorkItem errorItem = new("error-item");
        FakeProcessingWorkItemStore store = new([completeItem, errorItem]);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset completedAt = startedAt.AddSeconds(5);

        IReadOnlyList<FakeProcessingWorkItem> pending = await store.GetPendingAsync(10, CancellationToken.None);
        Assert.Equal(2, pending.Count);

        Assert.True(await store.ClaimItemAsync(completeItem, startedAt, CancellationToken.None));
        await store.MarkCompleteAsync(completeItem, completedAt, CancellationToken.None);

        Assert.True(await store.ClaimItemAsync(errorItem, startedAt, CancellationToken.None));
        await store.MarkErrorAsync(errorItem, "boom", completedAt, CancellationToken.None);

        ProcessingQueueStats stats = await store.GetQueueStatsAsync(CancellationToken.None);
        Assert.Equal(1, stats.ProcessedCount);
        Assert.Equal(0, stats.RemainingCount);
        Assert.Equal(0, stats.InFlightCount);
        Assert.Equal(1, stats.ErrorCount);
        Assert.Equal(5000, stats.AverageItemDurationMs);
        Assert.Equal(completedAt, stats.LastItemCompletedAt);
        Assert.Equal(1, completeItem.ProcessingAttemptCount);
        Assert.Equal(ProcessingStatusValues.Error, errorItem.ProcessingStatus);
        Assert.Equal("boom", errorItem.ProcessingError);
    }

    private sealed class FakeProcessingWorkItem(string id) : IProcessingWorkItem
    {
        public string Id { get; } = id;
        public DateTimeOffset? StartedProcessingAt { get; set; }
        public DateTimeOffset? CompletedProcessingAt { get; set; }
        public DateTimeOffset? LastProcessingAttemptAt { get; set; }
        public string? ProcessingStatus { get; set; }
        public string? ProcessingError { get; set; }
        public int ProcessingAttemptCount { get; set; }
    }

    private sealed class FakeProcessingWorkItemStore(List<FakeProcessingWorkItem> items) : IProcessingWorkItemStore<FakeProcessingWorkItem>
    {
        public Task<IReadOnlyList<FakeProcessingWorkItem>> GetPendingAsync(int maxItems, CancellationToken ct)
        {
            IReadOnlyList<FakeProcessingWorkItem> pending = items
                .Where(i => i.ProcessingStatus is null)
                .Take(maxItems)
                .ToList();
            return Task.FromResult(pending);
        }

        public Task<bool> ClaimItemAsync(FakeProcessingWorkItem item, DateTimeOffset startedAt, CancellationToken ct)
        {
            if (item.ProcessingStatus is not null)
            {
                return Task.FromResult(false);
            }

            item.ProcessingStatus = ProcessingStatusValues.InProgress;
            item.StartedProcessingAt = startedAt;
            item.LastProcessingAttemptAt = startedAt;
            item.ProcessingAttemptCount++;
            return Task.FromResult(true);
        }

        public Task MarkCompleteAsync(FakeProcessingWorkItem item, DateTimeOffset completedAt, CancellationToken ct)
        {
            item.ProcessingStatus = ProcessingStatusValues.Complete;
            item.CompletedProcessingAt = completedAt;
            item.ProcessingError = null;
            return Task.CompletedTask;
        }

        public Task MarkErrorAsync(FakeProcessingWorkItem item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
        {
            item.ProcessingStatus = ProcessingStatusValues.Error;
            item.CompletedProcessingAt = completedAt;
            item.ProcessingError = errorMessage;
            return Task.CompletedTask;
        }

        public Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct)
        {
            int reset = 0;
            foreach (FakeProcessingWorkItem item in items.Where(i => i.ProcessingStatus == ProcessingStatusValues.InProgress && i.StartedProcessingAt <= now - olderThan))
            {
                item.ProcessingStatus = null;
                reset++;
            }
            return Task.FromResult(reset);
        }

        public Task<ProcessingQueueStats> GetQueueStatsAsync(CancellationToken ct)
        {
            List<FakeProcessingWorkItem> completeItems = items.Where(i => i.ProcessingStatus == ProcessingStatusValues.Complete).ToList();
            double? average = completeItems.Count == 0
                ? null
                : completeItems.Average(i => (i.CompletedProcessingAt!.Value - i.StartedProcessingAt!.Value).TotalMilliseconds);
            DateTimeOffset? lastCompleted = completeItems.Select(i => i.CompletedProcessingAt).Max();
            ProcessingQueueStats stats = new(
                ProcessedCount: completeItems.Count,
                RemainingCount: items.Count(i => i.ProcessingStatus is null),
                InFlightCount: items.Count(i => i.ProcessingStatus == ProcessingStatusValues.InProgress),
                ErrorCount: items.Count(i => i.ProcessingStatus == ProcessingStatusValues.Error),
                AverageItemDurationMs: average,
                LastItemCompletedAt: lastCompleted);
            return Task.FromResult(stats);
        }
    }
}
