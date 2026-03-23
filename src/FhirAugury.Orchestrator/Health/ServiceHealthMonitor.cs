using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Health;

/// <summary>
/// Monitors source service health via gRPC HealthCheck.
/// Maintains aggregate health status for all configured services.
/// </summary>
public class ServiceHealthMonitor(
    SourceRouter router,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<ServiceHealthMonitor> logger)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;
    private readonly Dictionary<string, ServiceHealthInfo> _healthStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private static readonly TimeSpan PerServiceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Checks health of all enabled source services in parallel with per-service timeouts.
    /// </summary>
    public async Task CheckAllAsync(CancellationToken ct)
    {
        var enabledServices = options.Services
            .Where(s => s.Value.Enabled)
            .Select(s => s.Key)
            .ToList();

        var tasks = enabledServices.Select(async name =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerServiceTimeout);
            var info = await CheckServiceAsync(name, timeoutCts.Token);
            return (name, info);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            foreach (var (name, info) in results)
            {
                _healthStatus[name] = info;
            }
        }
    }

    /// <summary>
    /// Checks health of a single source service.
    /// </summary>
    public async Task<ServiceHealthInfo> CheckServiceAsync(string sourceName, CancellationToken ct)
    {
        var client = router.GetSourceClient(sourceName);
        var config = router.GetSourceConfig(sourceName);

        if (client is null || config is null)
        {
            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "not_configured",
                GrpcAddress = config?.GrpcAddress ?? "",
            };
        }

        try
        {
            var response = await client.HealthCheckAsync(new HealthCheckRequest(), cancellationToken: ct);
            var stats = await client.GetStatsAsync(new StatsRequest(), cancellationToken: ct);

            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = response.Status,
                GrpcAddress = config.GrpcAddress,
                UptimeSeconds = response.UptimeSeconds,
                Version = response.Version,
                ItemCount = stats.TotalItems,
                DbSizeBytes = stats.DatabaseSizeBytes,
                LastSyncAt = stats.LastSyncAt?.ToDateTimeOffset(),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Health check timed out for {Source}", sourceName);
            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "timeout",
                GrpcAddress = config.GrpcAddress,
                LastError = "Health check timed out",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check failed for {Source}", sourceName);
            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "unavailable",
                GrpcAddress = config.GrpcAddress,
                LastError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Gets the current cached health status of all services.
    /// </summary>
    public Dictionary<string, ServiceHealthInfo> GetCurrentStatus()
    {
        lock (_lock)
        {
            return new Dictionary<string, ServiceHealthInfo>(_healthStatus, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets the cached health status of a single service.
    /// </summary>
    public ServiceHealthInfo? GetServiceStatus(string sourceName)
    {
        lock (_lock)
        {
            return _healthStatus.GetValueOrDefault(sourceName);
        }
    }
}

public class ServiceHealthInfo
{
    public required string Name { get; set; }
    public required string Status { get; set; }
    public required string GrpcAddress { get; set; }
    public double UptimeSeconds { get; set; }
    public string? Version { get; set; }
    public int ItemCount { get; set; }
    public long DbSizeBytes { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
}
