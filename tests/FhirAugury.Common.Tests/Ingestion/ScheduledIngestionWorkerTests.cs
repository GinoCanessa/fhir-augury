using FhirAugury.Common.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Common.Tests.Ingestion;

public class ScheduledIngestionWorkerTests
{
    /// <summary>
    /// Test pipeline that records invocations, lets the test decide what
    /// <see cref="GetLastSyncCompletedAt"/> returns, and can throw on demand.
    /// </summary>
    private sealed class FakePipeline : IIngestionPipeline
    {
        public int RunCount;
        public DateTimeOffset? LastSyncCompletedAt;
        public Exception? ThrowOnRun;
        public Func<CancellationToken, Task>? OnRun;

        public Task RunIncrementalIngestionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref RunCount);
            if (ThrowOnRun is not null)
            {
                throw ThrowOnRun;
            }
            return OnRun?.Invoke(ct) ?? Task.CompletedTask;
        }

        public bool IsRunning => false;
        public string CurrentStatus => "fake";
        public DateTimeOffset? GetLastSyncCompletedAt() => LastSyncCompletedAt;
    }

    /// <summary>
    /// Test-only worker that overrides the 30 s startup delay so tests don't
    /// sit idle, and exposes ExecuteAsync publicly.
    /// </summary>
    private sealed class TestWorker : ScheduledIngestionWorker<FakePipeline>
    {
        public TestWorker(
            FakePipeline pipeline,
            Func<string> syncScheduleProvider,
            Func<string> minSyncAgeProvider,
            Func<bool> ingestionPausedProvider,
            Func<bool> startupOnlyProvider)
            : base(pipeline, syncScheduleProvider, minSyncAgeProvider,
                   ingestionPausedProvider, startupOnlyProvider,
                   NullLogger.Instance)
        {
        }

        protected override TimeSpan StartupDelay => TimeSpan.Zero;

        public Task RunExecuteAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private static TestWorker NewWorker(
        FakePipeline pipeline,
        string schedule = "01:00:00",
        string minSyncAge = "00:00:00",
        bool paused = false,
        bool startupOnly = false)
    {
        return new TestWorker(
            pipeline,
            () => schedule,
            () => minSyncAge,
            () => paused,
            () => startupOnly);
    }

    [Fact]
    public async Task StartupOnly_RunsExactlyOnce_ThenExits()
    {
        FakePipeline pipeline = new();
        TestWorker worker = NewWorker(pipeline, startupOnly: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await worker.RunExecuteAsync(cts.Token);

        Assert.Equal(1, pipeline.RunCount);
        Assert.False(cts.IsCancellationRequested, "Worker should exit on its own, not via cancellation timeout.");
    }

    [Fact]
    public async Task StartupOnly_SkipsWhenMinSyncAgeNotMet_AndExits()
    {
        FakePipeline pipeline = new()
        {
            // Last sync was 1 minute ago; threshold is 1 hour => still fresh.
            LastSyncCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        TestWorker worker = NewWorker(pipeline, minSyncAge: "01:00:00", startupOnly: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await worker.RunExecuteAsync(cts.Token);

        Assert.Equal(0, pipeline.RunCount);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task StartupOnly_HonorsIngestionPaused_AndExits()
    {
        FakePipeline pipeline = new();
        TestWorker worker = NewWorker(pipeline, paused: true, startupOnly: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await worker.RunExecuteAsync(cts.Token);

        Assert.Equal(0, pipeline.RunCount);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task StartupOnly_StillExitsWhenPipelineThrows()
    {
        FakePipeline pipeline = new()
        {
            ThrowOnRun = new InvalidOperationException("boom"),
        };
        TestWorker worker = NewWorker(pipeline, startupOnly: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await worker.RunExecuteAsync(cts.Token);

        // Pipeline was invoked once, exception was swallowed, loop not re-entered.
        Assert.Equal(1, pipeline.RunCount);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task DefaultMode_LoopsMultipleTimes()
    {
        using CancellationTokenSource cts = new();
        FakePipeline pipeline = new();
        pipeline.OnRun = _ =>
        {
            if (pipeline.RunCount >= 3)
            {
                cts.Cancel();
            }
            return Task.CompletedTask;
        };

        // Very short interval so the test finishes quickly; startupOnly is false.
        TestWorker worker = NewWorker(pipeline, schedule: "00:00:00.010", startupOnly: false);

        await worker.RunExecuteAsync(cts.Token);

        Assert.True(pipeline.RunCount >= 2, $"Expected at least 2 loop iterations, got {pipeline.RunCount}");
    }

    [Fact]
    public async Task DefaultMode_InvalidSchedule_ExitsImmediately()
    {
        FakePipeline pipeline = new();
        TestWorker worker = NewWorker(pipeline, schedule: "not-a-timespan");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await worker.RunExecuteAsync(cts.Token);

        Assert.Equal(0, pipeline.RunCount);
    }
}
