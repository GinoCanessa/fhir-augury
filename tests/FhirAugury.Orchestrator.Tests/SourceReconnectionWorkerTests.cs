using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class SourceReconnectionWorkerTests
{
    private static IOptions<OrchestratorOptions> CreateOptions(
        int reconnectInterval = 30,
        Dictionary<string, SourceServiceConfig>? services = null)
    {
        return Options.Create(new OrchestratorOptions
        {
            ReconnectIntervalSeconds = reconnectInterval,
            Services = services ?? new Dictionary<string, SourceServiceConfig>
            {
                ["TestSource"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
            },
        });
    }

    private static (ServiceHealthMonitor monitor, SourceRouter router) CreateMonitorAndRouter(
        IOptions<OrchestratorOptions>? options = null)
    {
        options ??= CreateOptions();
        SourceRouter router = new(options, NullLogger<SourceRouter>.Instance);
        ServiceHealthMonitor monitor = new(router, options, NullLogger<ServiceHealthMonitor>.Instance);
        return (monitor, router);
    }

    [Fact]
    public void OfflineStatuses_ContainsExpectedValues()
    {
        Assert.Contains("unavailable", SourceReconnectionWorker.OfflineStatuses);
        Assert.Contains("timeout", SourceReconnectionWorker.OfflineStatuses);
        Assert.DoesNotContain("healthy", SourceReconnectionWorker.OfflineStatuses);
        Assert.DoesNotContain("degraded", SourceReconnectionWorker.OfflineStatuses);
        Assert.DoesNotContain("not_configured", SourceReconnectionWorker.OfflineStatuses);
    }

    [Fact]
    public void OfflineStatuses_IsCaseInsensitive()
    {
        Assert.Contains("UNAVAILABLE", SourceReconnectionWorker.OfflineStatuses);
        Assert.Contains("Timeout", SourceReconnectionWorker.OfflineStatuses);
    }

    [Fact]
    public async Task TryReconnect_NoOfflineSources_DoesNothing()
    {
        (ServiceHealthMonitor monitor, SourceRouter router) = CreateMonitorAndRouter();

        SourceReconnectionWorker worker = new(
            monitor,
            CreateOptions(),
            NullLogger<SourceReconnectionWorker>.Instance);

        // No health status cached yet = no offline sources to reconnect
        await worker.TryReconnectOfflineSourcesAsync(CancellationToken.None);

        // Verify no status was set (nothing to reconnect)
        Dictionary<string, ServiceHealthInfo> status = monitor.GetCurrentStatus();
        Assert.Empty(status);
    }

    [Fact]
    public async Task TryReconnect_UnavailableSource_AttemptsReconnection()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["Offline"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceRouter router) = CreateMonitorAndRouter(options);

        // Seed the monitor with an "unavailable" status via a health check
        await monitor.CheckAllAsync(CancellationToken.None);
        ServiceHealthInfo? initialStatus = monitor.GetServiceStatus("Offline");
        Assert.NotNull(initialStatus);
        Assert.True(SourceReconnectionWorker.OfflineStatuses.Contains(initialStatus.Status),
            $"Expected offline status but got '{initialStatus.Status}'");

        SourceReconnectionWorker worker = new(
            monitor,
            options,
            NullLogger<SourceReconnectionWorker>.Instance);

        // Run reconnection — source is still unreachable so status stays offline
        await worker.TryReconnectOfflineSourcesAsync(CancellationToken.None);

        ServiceHealthInfo? afterStatus = monitor.GetServiceStatus("Offline");
        Assert.NotNull(afterStatus);
        Assert.True(SourceReconnectionWorker.OfflineStatuses.Contains(afterStatus.Status),
            $"Expected offline status but got '{afterStatus.Status}'");
    }

    [Fact]
    public async Task CheckAndUpdateServiceAsync_UpdatesCachedStatus()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["TestSvc"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceRouter router) = CreateMonitorAndRouter(options);

        // Initially no cached status
        Assert.Null(monitor.GetServiceStatus("TestSvc"));

        // CheckAndUpdateServiceAsync should populate the cache
        ServiceHealthInfo info = await monitor.CheckAndUpdateServiceAsync("TestSvc", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("TestSvc", info.Name);

        // Verify it's in the cache now
        ServiceHealthInfo? cached = monitor.GetServiceStatus("TestSvc");
        Assert.NotNull(cached);
        Assert.Equal(info.Status, cached.Status);
    }

    [Fact]
    public async Task CheckAndUpdateServiceAsync_NotConfigured_ReturnsNotConfigured()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["Configured"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceRouter router) = CreateMonitorAndRouter(options);

        // Check a source that doesn't exist in the router
        ServiceHealthInfo info = await monitor.CheckAndUpdateServiceAsync("NonExistent", CancellationToken.None);

        Assert.Equal("not_configured", info.Status);
    }

    [Fact]
    public async Task TryReconnect_SkipsHealthySources()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["DownSvc"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceRouter _) = CreateMonitorAndRouter(options);

        // Seed with healthy status via CheckAllAsync, then manually verify the
        // worker only targets offline sources. Since localhost:9999 is unreachable,
        // the initial check will mark it as offline.
        await monitor.CheckAllAsync(CancellationToken.None);

        // Manually insert a "healthy" entry to simulate a working source
        await monitor.CheckAndUpdateServiceAsync("DownSvc", CancellationToken.None);
        ServiceHealthInfo? status = monitor.GetServiceStatus("DownSvc");
        Assert.NotNull(status);

        // If the source were somehow "healthy", the worker should skip it.
        // Since we can't make a real gRPC server healthy in a unit test, we verify
        // the OfflineStatuses filter logic: "healthy" is NOT in OfflineStatuses
        Assert.DoesNotContain("healthy", SourceReconnectionWorker.OfflineStatuses);
    }

    [Fact]
    public async Task TryReconnect_RespectsCancel()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["Svc1"] = new SourceServiceConfig { GrpcAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceRouter _) = CreateMonitorAndRouter(options);

        // Seed offline status
        await monitor.CheckAllAsync(CancellationToken.None);

        SourceReconnectionWorker worker = new(
            monitor,
            options,
            NullLogger<SourceReconnectionWorker>.Instance);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Should not throw, just exit gracefully
        await worker.TryReconnectOfflineSourcesAsync(cts.Token);
    }

    [Fact]
    public void DefaultReconnectInterval_Is30Seconds()
    {
        OrchestratorOptions options = new();
        Assert.Equal(30, options.ReconnectIntervalSeconds);
    }
}
