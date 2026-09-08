using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Queue;

public sealed class AuthoringQueueRunnerTests
{
    [Fact]
    public async Task FillCapacityAsync_AppliesExactlyOneTerminalTransition()
    {
        TestItem item = new("one");
        InMemoryStore store = new([item]);
        TestHandler handler = new(_ => Task.FromResult(AuthoringWorkResult.Complete("receipt-1")));
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Assert.Equal(1, await runner.FillCapacityAsync("run-1"));
        await runner.DrainAsync();

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ApplyCount);
        Assert.Equal(AuthoringWorkDisposition.Complete, store.LastResult!.Disposition);
        Assert.Equal("run-1", store.LastRunId);
    }

    [Fact]
    public async Task FillCapacityAsync_HandlerFailureBecomesOneRetryableResult()
    {
        TestItem item = new("one");
        InMemoryStore store = new([item]);
        TestHandler handler = new(_ => throw new InvalidOperationException("agent failed"));
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler);

        await runner.FillCapacityAsync("run-1");
        await runner.DrainAsync();

        Assert.Equal(1, store.ApplyCount);
        Assert.Equal(AuthoringWorkDisposition.RetryableError, store.LastResult!.Disposition);
        Assert.Equal("agent failed", store.LastResult.Error);
    }

    [Fact]
    public async Task FillCapacityAsync_PreservesRollingConcurrency()
    {
        InMemoryStore store = new([new("one"), new("two"), new("three")]);
        int active = 0;
        int maximum = 0;
        TestHandler handler = new(async _ =>
        {
            int current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await Task.Delay(40);
            Interlocked.Decrement(ref active);
            return AuthoringWorkResult.Complete(Guid.NewGuid().ToString("N"));
        });
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler, maxConcurrency: 2);

        Assert.Equal(2, await runner.FillCapacityAsync("run-1"));
        await Assert.IsAssignableFrom<Task>(runner.GetInFlightCompletionTask()!);
        Assert.Equal(1, await runner.FillCapacityAsync("run-1"));
        await runner.DrainAsync();

        Assert.Equal(2, maximum);
        Assert.Equal(3, store.ApplyCount);
    }

    [Fact]
    public async Task ResetOrphanedItemsAsync_IsScopedToSelectedRun()
    {
        InMemoryStore store = new([]);
        AuthoringQueueRunner<TestItem> runner = CreateRunner(
            store,
            new TestHandler(_ => Task.FromResult(AuthoringWorkResult.Retry("unused"))));

        await runner.ResetOrphanedItemsAsync("run-2");

        Assert.Equal("run-2", store.LastResetRunId);
    }

    private static AuthoringQueueRunner<TestItem> CreateRunner(
        InMemoryStore store,
        TestHandler handler,
        int maxConcurrency = 1)
    {
        ProcessingServiceOptions options = new()
        {
            SyncSchedule = "00:00:00.010",
            OrphanedInProgressThreshold = "00:00:01",
            MaxConcurrentProcessingThreads = maxConcurrency,
        };
        ProcessingLifecycleService lifecycle = new(Options.Create(new ProcessingServiceOptions()));
        return new AuthoringQueueRunner<TestItem>(
            store,
            handler,
            lifecycle,
            Options.Create(options),
            NullLogger<AuthoringQueueRunner<TestItem>>.Instance);
    }

    private sealed record TestItem(string Id);

    private sealed class InMemoryStore(List<TestItem> items) : IAuthoringQueueStore<TestItem>
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _claimed = [];

        public int ClaimCount { get; private set; }
        public int ApplyCount { get; private set; }
        public AuthoringWorkResult? LastResult { get; private set; }
        public string? LastRunId { get; private set; }
        public string? LastResetRunId { get; private set; }

        public Task<IReadOnlyList<TestItem>> GetPendingAsync(
            string runId,
            int maxItems,
            CancellationToken ct)
        {
            lock (_lock)
            {
                LastRunId = runId;
                return Task.FromResult<IReadOnlyList<TestItem>>(
                    items.Where(item => !_claimed.Contains(item.Id)).Take(maxItems).ToList());
            }
        }

        public Task<AuthoringQueueClaim?> TryClaimAsync(
            TestItem item,
            DateTimeOffset startedAt,
            CancellationToken ct)
        {
            lock (_lock)
            {
                if (!_claimed.Add(item.Id))
                {
                    return Task.FromResult<AuthoringQueueClaim?>(null);
                }
                ClaimCount++;
                return Task.FromResult<AuthoringQueueClaim?>(
                    new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1));
            }
        }

        public Task ApplyResultAsync(
            TestItem item,
            AuthoringQueueClaim claim,
            AuthoringWorkResult result,
            DateTimeOffset completedAt,
            CancellationToken ct)
        {
            lock (_lock)
            {
                ApplyCount++;
                LastResult = result;
                return Task.CompletedTask;
            }
        }

        public Task<int> ResetOrphanedItemsAsync(
            string runId,
            TimeSpan olderThan,
            DateTimeOffset now,
            CancellationToken ct)
        {
            LastResetRunId = runId;
            return Task.FromResult(0);
        }
    }

    private sealed class TestHandler(
        Func<TestItem, Task<AuthoringWorkResult>> handler)
        : IAuthoringWorkItemHandler<TestItem>
    {
        public Task<AuthoringWorkResult> ProcessAsync(
            TestItem item,
            AuthoringQueueClaim claim,
            CancellationToken ct)
            => handler(item);
    }
}
