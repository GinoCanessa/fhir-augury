using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Queue;

public class ProcessingQueueRunnerTests
{
    [Fact]
    public async Task RunAsync_ProcessesPendingItems_AndMarksComplete()
    {
        TestItem item = new("one");
        InMemoryStore store = new([item]);
        TestHandler handler = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ProcessingQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Task runTask = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => item.ProcessingStatus == ProcessingStatusValues.Complete, cts.Token);
        await cts.CancelAsync();
        await runTask;

        Assert.Equal(ProcessingStatusValues.Complete, item.ProcessingStatus);
        Assert.Equal(1, item.ProcessingAttemptCount);
    }

    [Fact]
    public async Task RunAsync_RespectsMaxConcurrentProcessingThreads()
    {
        InMemoryStore store = new([new TestItem("one"), new TestItem("two"), new TestItem("three")]);
        TestHandler handler = new() { Delay = TimeSpan.FromMilliseconds(40) };
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ProcessingQueueRunner<TestItem> runner = CreateRunner(store, handler, maxConcurrency: 2);

        Task runTask = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => store.Items.All(i => i.ProcessingStatus == ProcessingStatusValues.Complete), cts.Token);
        await cts.CancelAsync();
        await runTask;

        Assert.Equal(2, handler.MaxObservedConcurrency);
    }

    [Fact]
    public async Task RunAsync_MarksHandlerFailuresAsError()
    {
        TestItem item = new("bad");
        InMemoryStore store = new([item]);
        TestHandler handler = new() { ThrowOnIds = ["bad"] };
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ProcessingQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Task runTask = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => item.ProcessingStatus == ProcessingStatusValues.Error, cts.Token);
        await cts.CancelAsync();
        await runTask;

        Assert.Equal("handler failed", item.ProcessingError);
    }

    [Fact]
    public async Task RunAsync_ResetOrphanedItems_OnStartup()
    {
        TestItem item = new("orphan")
        {
            ProcessingStatus = ProcessingStatusValues.InProgress,
            StartedProcessingAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        InMemoryStore store = new([item]);
        TestHandler handler = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ProcessingQueueRunner<TestItem> runner = CreateRunner(store, handler);

        Task runTask = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => item.ProcessingStatus == ProcessingStatusValues.Complete, cts.Token);
        await cts.CancelAsync();
        await runTask;

        Assert.Equal(1, store.ResetCount);
        Assert.Equal(ProcessingStatusValues.Complete, item.ProcessingStatus);
    }

    [Fact]
    public async Task Stop_PreventsNewDequeues_AndDrainsInFlight()
    {
        TestItem first = new("first");
        TestItem second = new("second");
        InMemoryStore store = new([first, second]);
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHandler handler = new()
        {
            OnProcessAsync = async item =>
            {
                if (item.Id == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            },
        };
        ProcessingLifecycleService lifecycle = CreateLifecycle(startOnStartup: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ProcessingQueueRunner<TestItem> runner = CreateRunner(store, handler, maxConcurrency: 1, lifecycle: lifecycle);

        Task runTask = runner.RunAsync(cts.Token);
        await firstStarted.Task.WaitAsync(cts.Token);
        lifecycle.Stop();
        releaseFirst.SetResult();
        await WaitUntilAsync(() => first.ProcessingStatus == ProcessingStatusValues.Complete, cts.Token);
        await Task.Delay(50, cts.Token);
        await cts.CancelAsync();
        await runTask;

        Assert.Equal(ProcessingStatusValues.Complete, first.ProcessingStatus);
        Assert.Null(second.ProcessingStatus);
    }

    [Fact]
    public void Restart_UsesStartProcessingOnStartup_AndDoesNotPersistPausedState()
    {
        ProcessingLifecycleService stopped = CreateLifecycle(startOnStartup: true);
        stopped.Stop();

        ProcessingLifecycleService restarted = CreateLifecycle(startOnStartup: true);
        ProcessingLifecycleService pausedRestart = CreateLifecycle(startOnStartup: false);

        Assert.True(restarted.IsRunning);
        Assert.False(pausedRestart.IsRunning);
    }

    private static ProcessingQueueRunner<TestItem> CreateRunner(
        InMemoryStore store,
        TestHandler handler,
        int maxConcurrency = 1,
        ProcessingLifecycleService? lifecycle = null)
    {
        ProcessingServiceOptions options = new()
        {
            SyncSchedule = "00:00:00.010",
            OrphanedInProgressThreshold = "00:00:01",
            MaxConcurrentProcessingThreads = maxConcurrency,
        };
        return new TestRunner(
            store,
            handler,
            lifecycle ?? CreateLifecycle(startOnStartup: true),
            Options.Create(options));
    }

    private static ProcessingLifecycleService CreateLifecycle(bool startOnStartup)
    {
        ProcessingServiceOptions options = new() { StartProcessingOnStartup = startOnStartup };
        return new ProcessingLifecycleService(Options.Create(options));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private sealed class TestRunner(
        IProcessingWorkItemStore<TestItem> store,
        IProcessingWorkItemHandler<TestItem> handler,
        ProcessingLifecycleService lifecycle,
        IOptions<ProcessingServiceOptions> options)
        : ProcessingQueueRunner<TestItem>(store, handler, lifecycle, options, NullLogger<ProcessingQueueRunner<TestItem>>.Instance)
    {
        protected override Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(5), ct);
    }

    private sealed class TestItem(string id) : IProcessingWorkItem
    {
        public string Id { get; } = id;
        public DateTimeOffset? StartedProcessingAt { get; set; }
        public DateTimeOffset? CompletedProcessingAt { get; set; }
        public DateTimeOffset? LastProcessingAttemptAt { get; set; }
        public string? ProcessingStatus { get; set; }
        public string? ProcessingError { get; set; }
        public int ProcessingAttemptCount { get; set; }
    }

    private sealed class InMemoryStore(List<TestItem> items) : IProcessingWorkItemStore<TestItem>
    {
        private readonly object _lock = new();
        public IReadOnlyList<TestItem> Items => items;
        public int ResetCount { get; private set; }

        public Task<IReadOnlyList<TestItem>> GetPendingAsync(int maxItems, CancellationToken ct)
        {
            lock (_lock)
            {
                IReadOnlyList<TestItem> pending = items.Where(i => i.ProcessingStatus is null).Take(maxItems).ToList();
                return Task.FromResult(pending);
            }
        }

        public Task<bool> ClaimItemAsync(TestItem item, DateTimeOffset startedAt, CancellationToken ct)
        {
            lock (_lock)
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
        }

        public Task MarkCompleteAsync(TestItem item, DateTimeOffset completedAt, CancellationToken ct)
        {
            lock (_lock)
            {
                item.ProcessingStatus = ProcessingStatusValues.Complete;
                item.CompletedProcessingAt = completedAt;
                item.ProcessingError = null;
                return Task.CompletedTask;
            }
        }

        public Task MarkErrorAsync(TestItem item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
        {
            lock (_lock)
            {
                item.ProcessingStatus = ProcessingStatusValues.Error;
                item.CompletedProcessingAt = completedAt;
                item.ProcessingError = errorMessage;
                return Task.CompletedTask;
            }
        }

        public Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct)
        {
            lock (_lock)
            {
                int reset = 0;
                foreach (TestItem item in items.Where(i => i.ProcessingStatus == ProcessingStatusValues.InProgress && i.StartedProcessingAt <= now - olderThan))
                {
                    item.ProcessingStatus = null;
                    item.ProcessingError = null;
                    reset++;
                }
                ResetCount = reset;
                return Task.FromResult(reset);
            }
        }

        public Task<ProcessingQueueStats> GetQueueStatsAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                List<TestItem> completeItems = items.Where(i => i.ProcessingStatus == ProcessingStatusValues.Complete).ToList();
                double? average = completeItems.Count == 0
                    ? null
                    : completeItems.Average(i => (i.CompletedProcessingAt!.Value - i.StartedProcessingAt!.Value).TotalMilliseconds);
                DateTimeOffset? lastCompleted = completeItems.Select(i => i.CompletedProcessingAt).Max();
                ProcessingQueueStats stats = new(
                    completeItems.Count,
                    items.Count(i => i.ProcessingStatus is null),
                    items.Count(i => i.ProcessingStatus == ProcessingStatusValues.InProgress),
                    items.Count(i => i.ProcessingStatus == ProcessingStatusValues.Error),
                    average,
                    lastCompleted);
                return Task.FromResult(stats);
            }
        }
    }

    private sealed class TestHandler : IProcessingWorkItemHandler<TestItem>
    {
        private int _active;
        public TimeSpan Delay { get; set; }
        public List<string> ThrowOnIds { get; set; } = [];
        public int MaxObservedConcurrency { get; private set; }
        public Func<TestItem, Task>? OnProcessAsync { get; set; }

        public async Task ProcessAsync(TestItem item, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active);
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, active);
            try
            {
                if (ThrowOnIds.Contains(item.Id))
                {
                    throw new InvalidOperationException("handler failed");
                }

                if (OnProcessAsync is not null)
                {
                    await OnProcessAsync(item);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
