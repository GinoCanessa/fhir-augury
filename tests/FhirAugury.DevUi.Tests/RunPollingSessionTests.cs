using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.DevUi.Tests;

public sealed class RunPollingSessionTests
{
    [Fact]
    public async Task TimerAndManualRefreshesAreSequential()
    {
        ManualTimeProvider time = new();
        await using RunPollingSession session =
            new(time, TimeSpan.FromSeconds(3));
        TaskCompletionSource tickEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTick =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int active = 0;
        int maximumActive = 0;

        async Task<AuthoringRunResponse> Refresh(
            CancellationToken ct)
        {
            int currentActive =
                Interlocked.Increment(ref active);
            maximumActive = Math.Max(
                maximumActive,
                currentActive);
            int call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 2)
                {
                    tickEntered.SetResult();
                    await releaseTick.Task.WaitAsync(ct);
                }
                return Response(
                    terminal: call == 3,
                    recoverable: false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        await session.StartAsync(Refresh);
        time.Advance(TimeSpan.FromSeconds(3));
        await tickEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Task<AuthoringRunResponse> manual =
            session.RefreshNowAsync();
        await Task.Yield();
        Assert.Equal(2, Volatile.Read(ref calls));

        releaseTick.SetResult();
        AuthoringRunResponse terminal = await manual;
        Assert.True(terminal.Run.State!.IsTerminal);
        Assert.Equal(3, calls);
        Assert.Equal(1, maximumActive);
        Assert.False(session.IsPolling);

        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RecoverableErrorRemainsPollable()
    {
        ManualTimeProvider time = new();
        await using RunPollingSession session =
            new(time, TimeSpan.FromSeconds(3));
        TaskCompletionSource secondRefresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        await session.StartAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                secondRefresh.SetResult();
            }
            return Task.FromResult(Response(
                terminal: false,
                recoverable: true));
        });

        Assert.True(session.IsPolling);
        time.Advance(TimeSpan.FromSeconds(3));
        await secondRefresh.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.True(session.IsPolling);
    }

    [Fact]
    public async Task TerminalInitialReadDoesNotScheduleTimer()
    {
        ManualTimeProvider time = new();
        await using RunPollingSession session =
            new(time, TimeSpan.FromSeconds(3));
        int calls = 0;

        AuthoringRunResponse response =
            await session.StartAsync(_ =>
            {
                calls++;
                return Task.FromResult(Response(
                    terminal: true,
                    recoverable: false));
            });
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();

        Assert.True(response.Run.State!.IsTerminal);
        Assert.Equal(1, calls);
        Assert.False(session.IsPolling);
    }

    [Fact]
    public async Task DisposalCancelsInFlightPollingRefresh()
    {
        ManualTimeProvider time = new();
        RunPollingSession session =
            new(time, TimeSpan.FromSeconds(3));
        TaskCompletionSource inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        await session.StartAsync(async ct =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 2)
            {
                inFlight.SetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        ct);
                }
                catch (OperationCanceledException)
                    when (ct.IsCancellationRequested)
                {
                    canceled.SetResult();
                    throw;
                }
            }
            return Response(
                terminal: false,
                recoverable: false);
        });
        time.Advance(TimeSpan.FromSeconds(3));
        await inFlight.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        await canceled.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.False(session.IsPolling);
    }

    [Fact]
    public async Task NavigationCancellationStopsAndCancelsPolling()
    {
        ManualTimeProvider time = new();
        await using RunPollingSession session =
            new(time, TimeSpan.FromSeconds(3));
        using CancellationTokenSource navigation = new();
        TaskCompletionSource inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        await session.StartAsync(
            async ct =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    inFlight.SetResult();
                    try
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            ct);
                    }
                    catch (OperationCanceledException)
                        when (ct.IsCancellationRequested)
                    {
                        canceled.SetResult();
                        throw;
                    }
                }
                return Response(
                    terminal: false,
                    recoverable: false);
            },
            navigation.Token);
        time.Advance(TimeSpan.FromSeconds(3));
        await inFlight.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        navigation.Cancel();

        await canceled.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.False(session.IsPolling);
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        Assert.Equal(2, calls);
    }

    private static AuthoringRunResponse Response(
        bool terminal,
        bool recoverable)
    {
        string status = terminal
            ? "completed"
            : recoverable
                ? "error"
                : "running";
        AuthoringRunStatus run = new(
            "run-1",
            "jira-fhir",
            1,
            status,
            false,
            1,
            terminal ? 1 : 0,
            recoverable ? 1 : 0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            terminal ? DateTimeOffset.UtcNow : null,
            null,
            RetryableErrorItems: recoverable ? 1 : 0,
            State: new AuthoringRunStateInfo(
                terminal,
                recoverable));
        return new AuthoringRunResponse(run, []);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow =
            new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer = new(
                this,
                callback,
                state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeUnderLock(
                    dueTime,
                    period,
                    _utcNow);
            }
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration));
            }

            List<ManualTimer> due = [];
            lock (_sync)
            {
                _utcNow += duration;
                foreach (ManualTimer timer in _timers.ToArray())
                {
                    if (timer.IsDueUnderLock(_utcNow))
                    {
                        timer.RescheduleUnderLock(_utcNow);
                        due.Add(timer);
                    }
                }
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        private bool Change(
            ManualTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_sync)
            {
                if (timer.IsDisposed)
                {
                    return false;
                }
                timer.ChangeUnderLock(
                    dueTime,
                    period,
                    _utcNow);
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider provider,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;
            private TimeSpan _period;

            public bool IsDisposed { get; private set; }

            public bool Change(
                TimeSpan dueTime,
                TimeSpan period) =>
                provider.Change(this, dueTime, period);

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }
                IsDisposed = true;
                provider.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void ChangeUnderLock(
                TimeSpan dueTime,
                TimeSpan period,
                DateTimeOffset now)
            {
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : now + dueTime;
            }

            public bool IsDueUnderLock(
                DateTimeOffset now) =>
                !IsDisposed &&
                _dueAt is DateTimeOffset dueAt &&
                dueAt <= now;

            public void RescheduleUnderLock(
                DateTimeOffset now)
            {
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : now + _period;
            }

            public void Fire()
            {
                if (!IsDisposed)
                {
                    callback(state);
                }
            }
        }
    }
}
