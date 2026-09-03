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
    public async Task RunAsync_AppliesExactlyOneTerminalTransition()
    {
        TestItem item = new("one");
        InMemoryStore store = new([item]);
        TestHandler handler = new(_ => Task.FromResult(AuthoringWorkResult.Complete("receipt-1")));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => store.ApplyCount == 1, cts.Token);
        await cts.CancelAsync();
        await run;

        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ApplyCount);
        Assert.Equal(AuthoringWorkDisposition.Complete, store.LastResult!.Disposition);
    }

    [Fact]
    public async Task RunAsync_HandlerFailureBecomesOneRetryableResult()
    {
        TestItem item = new("one");
        InMemoryStore store = new([item]);
        TestHandler handler = new(_ => throw new InvalidOperationException("agent failed"));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => store.ApplyCount == 1, cts.Token);
        await cts.CancelAsync();
        await run;

        Assert.Equal(1, store.ApplyCount);
        Assert.Equal(AuthoringWorkDisposition.RetryableError, store.LastResult!.Disposition);
        Assert.Equal("agent failed", store.LastResult.Error);
    }

    [Fact]
    public async Task RunAsync_RespectsConfiguredConcurrency()
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
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AuthoringQueueRunner<TestItem> runner = CreateRunner(store, handler, maxConcurrency: 2);

        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => store.ApplyCount == 3, cts.Token);
        await cts.CancelAsync();
        await run;

        Assert.Equal(2, maximum);
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

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private sealed record TestItem(string Id);

    private sealed class InMemoryStore(List<TestItem> items) : IAuthoringQueueStore<TestItem>
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _claimed = [];

        public int ClaimCount { get; private set; }
        public int ApplyCount { get; private set; }
        public AuthoringWorkResult? LastResult { get; private set; }

        public Task<IReadOnlyList<TestItem>> GetPendingAsync(int maxItems, CancellationToken ct)
        {
            lock (_lock)
            {
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
            TimeSpan olderThan,
            DateTimeOffset now,
            CancellationToken ct)
            => Task.FromResult(0);
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
