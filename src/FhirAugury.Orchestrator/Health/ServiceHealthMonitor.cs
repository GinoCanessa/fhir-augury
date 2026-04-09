using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Health;

/// <summary>
/// Monitors source service health via HTTP health endpoints.
/// Maintains aggregate health status for all configured services.
/// </summary>
public class ServiceHealthMonitor(
    SourceHttpClient httpClient,
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
        List<string> enabledServices = options.Services
            .Where(s => s.Value.Enabled)
            .Select(s => s.Key)
            .ToList();

        List<Task<(string name, ServiceHealthInfo info)>> tasks = enabledServices.Select(async name =>
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerServiceTimeout);
            ServiceHealthInfo info = await CheckServiceAsync(name, timeoutCts.Token);
            return (name, info);
        }).ToList();

        (string name, ServiceHealthInfo info)[] results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            foreach ((string? name, ServiceHealthInfo? info) in results)
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
        SourceServiceConfig? config = httpClient.GetSourceConfig(sourceName);

        if (config is null || !config.Enabled)
        {
            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "not_configured",
                HttpAddress = config?.HttpAddress ?? "",
            };
        }

        try
        {
            HealthCheckResponse? health = await httpClient.HealthCheckAsync(sourceName, ct);
            StatsResponse? stats = await httpClient.GetStatsAsync(sourceName, ct);
            IngestionStatusResponse? ingestionStatus = await httpClient.GetIngestionStatusAsync(sourceName, ct);

            List<Common.Api.IndexStatusInfo> indexes = ingestionStatus?.Indexes ?? [];

            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = health?.Status ?? "unknown",
                HttpAddress = config.HttpAddress,
                UptimeSeconds = health?.UptimeSeconds ?? 0,
                Version = health?.Version,
                ItemCount = stats?.TotalItems ?? 0,
                DbSizeBytes = stats?.DatabaseSizeBytes ?? 0,
                LastSyncAt = stats?.LastSyncAt,
                Indexes = indexes,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Health check timed out for {Source}", sourceName);
            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "timeout",
                HttpAddress = config.HttpAddress,
                LastError = "Health check timed out",
            };
        }
        catch (Exception ex)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                logger.LogWarning("Health check failed for {Source} ({HttpStatus})", sourceName, statusDescription);
            else
                logger.LogWarning(ex, "Health check failed for {Source}", sourceName);

            return new ServiceHealthInfo
            {
                Name = sourceName,
                Status = "unavailable",
                HttpAddress = config.HttpAddress,
                LastError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Checks health of a single source service and updates the cached status.
    /// Used by the reconnection worker to refresh status of offline sources.
    /// </summary>
    public async Task<ServiceHealthInfo> CheckAndUpdateServiceAsync(string sourceName, CancellationToken ct)
    {
        ServiceHealthInfo info = await CheckServiceAsync(sourceName, ct);
        lock (_lock)
        {
            _healthStatus[sourceName] = info;
        }
        return info;
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
    public string HttpAddress { get; set; } = "";
    public double UptimeSeconds { get; set; }
    public string? Version { get; set; }
    public int ItemCount { get; set; }
    public long DbSizeBytes { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public List<Common.Api.IndexStatusInfo> Indexes { get; set; } = [];
}
