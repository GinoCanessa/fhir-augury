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
        SourceService.SourceServiceClient? client = router.GetSourceClient(sourceName);
        SourceServiceConfig? config = router.GetSourceConfig(sourceName);

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
            HealthCheckResponse response = await client.HealthCheckAsync(new HealthCheckRequest(), cancellationToken: ct);
            StatsResponse stats = await client.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
            IngestionStatusResponse ingestionStatus = await client.GetIngestionStatusAsync(new IngestionStatusRequest(), cancellationToken: ct);

            List<ServiceIndexInfo> indexes = ingestionStatus.Indexes.Select(idx => new ServiceIndexInfo
            {
                Name = idx.Name,
                Description = idx.Description,
                IsRebuilding = idx.IsRebuilding,
                LastRebuildStartedAt = idx.LastRebuildStartedAt?.ToDateTimeOffset(),
                LastRebuildCompletedAt = idx.LastRebuildCompletedAt?.ToDateTimeOffset(),
                RecordCount = idx.RecordCount,
                LastError = string.IsNullOrEmpty(idx.LastError) ? null : idx.LastError,
            }).ToList();

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
    public List<ServiceIndexInfo> Indexes { get; set; } = [];
}

public class ServiceIndexInfo
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public bool IsRebuilding { get; set; }
    public DateTimeOffset? LastRebuildStartedAt { get; set; }
    public DateTimeOffset? LastRebuildCompletedAt { get; set; }
    public int RecordCount { get; set; }
    public string? LastError { get; set; }
}
