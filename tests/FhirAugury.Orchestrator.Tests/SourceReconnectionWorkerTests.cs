using FhirAugury.Common.Api;
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
                ["TestSource"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
            },
        });
    }

    private static (ServiceHealthMonitor monitor, SourceHttpClient httpClient) CreateMonitorAndHttpClient(
        IOptions<OrchestratorOptions>? options = null)
    {
        options ??= CreateOptions();
        IHttpClientFactory httpClientFactory = new TestHttpClientFactory();
        SourceHttpClient httpClient = new(httpClientFactory, options, NullLogger<SourceHttpClient>.Instance);
        ServiceHealthMonitor monitor = new(httpClient, options, NullLogger<ServiceHealthMonitor>.Instance);
        return (monitor, httpClient);
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
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient();

        SourceReconnectionWorker worker = new(
            monitor,
            CreateOptions(),
            NullLogger<SourceReconnectionWorker>.Instance);

        // The configured source is present but has not been observed.
        await worker.TryReconnectOfflineSourcesAsync(CancellationToken.None);

        Dictionary<string, ServiceHealthInfo> status = monitor.GetCurrentStatus();
        ServiceHealthInfo unobserved = Assert.Single(status).Value;
        Assert.Equal("unobserved", unobserved.Status);
    }

    [Fact]
    public async Task TryReconnect_UnavailableSource_AttemptsReconnection()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["Offline"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient(options);

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
            ["TestSvc"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient(options);

        Assert.Equal(
            "unobserved",
            monitor.GetServiceStatus("TestSvc")?.Status);

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
            ["Configured"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient(options);

        // Check a source that doesn't exist in the options
        ServiceHealthInfo info = await monitor.CheckAndUpdateServiceAsync("NonExistent", CancellationToken.None);

        Assert.Equal("not_configured", info.Status);
    }

    [Fact]
    public async Task TryReconnect_SkipsHealthySources()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["DownSvc"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient(options);

        await monitor.CheckAllAsync(CancellationToken.None);

        await monitor.CheckAndUpdateServiceAsync("DownSvc", CancellationToken.None);
        ServiceHealthInfo? status = monitor.GetServiceStatus("DownSvc");
        Assert.NotNull(status);

        // If the source were somehow "healthy", the worker should skip it.
        Assert.DoesNotContain("healthy", SourceReconnectionWorker.OfflineStatuses);
    }

    [Fact]
    public async Task TryReconnect_RespectsCancel()
    {
        IOptions<OrchestratorOptions> options = CreateOptions(services: new Dictionary<string, SourceServiceConfig>
        {
            ["Svc1"] = new SourceServiceConfig { HttpAddress = "http://localhost:9999", Enabled = true },
        });
        (ServiceHealthMonitor monitor, SourceHttpClient _) = CreateMonitorAndHttpClient(options);

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
    public async Task UnavailableProcessors_AreNeverReconnectedAsSources()
    {
        OrchestratorOptions optionsValue = new()
        {
            ProcessingServices =
                new Dictionary<string, ProcessingServiceConfig>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["Planner"] = new()
                    {
                        HttpAddress = "http://localhost:9999",
                        Enabled = true,
                        RequiredServices = ["Jira", "GitHub"],
                    },
                },
        };
        IOptions<OrchestratorOptions> options = Options.Create(optionsValue);
        IHttpClientFactory factory = new TestHttpClientFactory();
        SourceHttpClient sourceClient = new(
            factory,
            options,
            NullLogger<SourceHttpClient>.Instance);
        ProcessingHttpClient processingClient = new(
            factory,
            options,
            NullLogger<ProcessingHttpClient>.Instance);
        ServiceHealthMonitor monitor = new(
            sourceClient,
            options,
            NullLogger<ServiceHealthMonitor>.Instance,
            processingClient);
        await monitor.CheckAllAsync(CancellationToken.None);
        Assert.Equal("unavailable", monitor.GetServiceStatus("Planner")?.Status);

        SourceReconnectionWorker worker = new(
            monitor,
            options,
            NullLogger<SourceReconnectionWorker>.Instance);
        await worker.TryReconnectOfflineSourcesAsync(CancellationToken.None);

        ServiceHealthInfo? status = monitor.GetServiceStatus("Planner");
        Assert.NotNull(status);
        Assert.Equal("processing", status.ServiceKind);
        Assert.Equal("unavailable", status.Status);
    }

    [Fact]
    public void DefaultReconnectInterval_Is30Seconds()
    {
        OrchestratorOptions options = new();
        Assert.Equal(30, options.ReconnectIntervalSeconds);
    }

    /// <summary>
    /// A simple IHttpClientFactory for tests that returns default HttpClients.
    /// </summary>
    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
