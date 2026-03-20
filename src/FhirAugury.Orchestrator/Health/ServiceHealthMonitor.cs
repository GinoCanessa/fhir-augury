using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Health;

/// <summary>
/// Monitors source service health via gRPC HealthCheck.
/// Maintains aggregate health status for all configured services.
/// </summary>
public class ServiceHealthMonitor(
    SourceRouter router,
    OrchestratorOptions options,
    ILogger<ServiceHealthMonitor> logger)
{
    private readonly Dictionary<string, ServiceHealthInfo> _healthStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Checks health of all enabled source services.
    /// </summary>
    public async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var (name, config) in options.Services)
        {
            if (!config.Enabled) continue;

            var info = await CheckServiceAsync(name, ct);
            lock (_lock)
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
