using FhirAugury.Common.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Common.Tests.Hosting;

public class StartupRebuildHostedServiceTests
{
    /// <summary>
    /// Test double for <see cref="IHostApplicationLifetime"/> that lets the
    /// test trigger ApplicationStarted on demand.
    /// </summary>
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
        public void TriggerStarted() => _started.Cancel();
    }

    private sealed class TestService : StartupRebuildHostedService
    {
        public TestService(IHostApplicationLifetime lifetime, Func<CancellationToken, Task> body)
            : base(lifetime, NullLogger.Instance)
        {
            _body = body;
        }

        private readonly Func<CancellationToken, Task> _body;

        public new void SetPhase(string phase) => base.SetPhase(phase);

        protected override Task RunStartupAsync(CancellationToken ct) => _body(ct);
    }

    [Fact]
    public async Task DoesNotRunWorkBeforeApplicationStarted()
    {
        FakeLifetime lifetime = new();
        bool ran = false;
        TestService svc = new(lifetime, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await svc.StartAsync(CancellationToken.None);

        // Give the background loop a brief chance to schedule.
        await Task.Delay(50);

        Assert.False(ran, "Startup work must not run before ApplicationStarted fires.");
        Assert.Equal(StartupRebuildState.Pending, svc.State);

        lifetime.TriggerStarted();
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ran);
        Assert.Equal(StartupRebuildState.Completed, svc.State);
    }

    [Fact]
    public async Task TransitionsThroughRunningToCompleted()
    {
        FakeLifetime lifetime = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestService svc = new(lifetime, async _ =>
        {
            await gate.Task;
        });

        await svc.StartAsync(CancellationToken.None);
        lifetime.TriggerStarted();

        // Wait for State to flip to Running.
        await WaitForAsync(() => svc.State == StartupRebuildState.Running, TimeSpan.FromSeconds(5));
        Assert.Equal(StartupRebuildState.Running, svc.State);

        gate.TrySetResult();
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StartupRebuildState.Completed, svc.State);
    }

    [Fact]
    public async Task CapturesExceptionAndMarksFailed()
    {
        FakeLifetime lifetime = new();
        InvalidOperationException boom = new("kaboom");
        TestService svc = new(lifetime, _ => throw boom);

        await svc.StartAsync(CancellationToken.None);
        lifetime.TriggerStarted();
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StartupRebuildState.Failed, svc.State);
        Assert.Same(boom, svc.LastError);
    }

    [Fact]
    public async Task SetPhaseUpdatesCurrentPhase()
    {
        FakeLifetime lifetime = new();
        TaskCompletionSource phaseSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestService? captured = null;
        TestService svc = new(lifetime, _ =>
        {
            captured!.SetPhase("phase-1");
            phaseSeen.TrySetResult();
            return Task.CompletedTask;
        });
        captured = svc;

        await svc.StartAsync(CancellationToken.None);
        lifetime.TriggerStarted();
        await phaseSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await svc.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("phase-1", svc.CurrentPhase);
    }

    [Fact]
    public async Task RespectsStopTokenBeforeApplicationStarted()
    {
        FakeLifetime lifetime = new();
        bool ran = false;
        TestService svc = new(lifetime, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await svc.StartAsync(CancellationToken.None);
        // Stop before triggering ApplicationStarted.
        await svc.StopAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(StartupRebuildState.Cancelled, svc.State);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
