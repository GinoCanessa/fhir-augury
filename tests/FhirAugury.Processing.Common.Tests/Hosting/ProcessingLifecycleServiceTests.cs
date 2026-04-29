using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Hosting;

public class ProcessingLifecycleServiceTests
{
    [Fact]
    public void StartStop_TogglesRunningState()
    {
        ProcessingLifecycleService lifecycle = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = false }));

        Assert.False(lifecycle.IsRunning);
        Assert.True(lifecycle.IsPaused);

        lifecycle.Start();
        Assert.True(lifecycle.IsRunning);
        Assert.False(lifecycle.IsPaused);

        lifecycle.Stop();
        Assert.False(lifecycle.IsRunning);
        Assert.True(lifecycle.IsPaused);
    }

    [Fact]
    public void NewInstance_UsesConfiguredStartupState_NotPreviousPauseState()
    {
        ProcessingLifecycleService oldLifecycle = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = true }));
        oldLifecycle.Stop();

        ProcessingLifecycleService restarted = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = true }));
        ProcessingLifecycleService paused = new(Options.Create(new ProcessingServiceOptions { StartProcessingOnStartup = false }));

        Assert.True(restarted.IsRunning);
        Assert.False(paused.IsRunning);
    }
}
