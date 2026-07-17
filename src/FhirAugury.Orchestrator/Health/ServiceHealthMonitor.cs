using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Processing.Common.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Health;

/// <summary>
/// Monitors configured source and Processing service health via HTTP health endpoints.
/// </summary>
public class ServiceHealthMonitor(
    SourceHttpClient httpClient,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<ServiceHealthMonitor> logger,
    ProcessingHttpClient? processingHttpClient = null)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;
    private readonly Dictionary<string, ServiceHealthInfo> _healthStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private DateTimeOffset? _lastCheckedAt;

    private static readonly TimeSpan PerServiceTimeout = TimeSpan.FromSeconds(10);

    public DateTimeOffset? LastCheckedAt
    {
        get { lock (_lock) { return _lastCheckedAt; } }
    }

    public async Task CheckAllAsync(CancellationToken ct)
    {
        List<Task<(string key, ServiceHealthInfo info)>> tasks = [];

        foreach (string name in options.Services.Where(s => s.Value.Enabled).Select(s => s.Key))
        {
            tasks.Add(CheckSourceWithTimeoutAsync(name, ct));
        }

        if (processingHttpClient is not null)
        {
            foreach (string name in options.ProcessingServices.Where(s => s.Value.Enabled).Select(s => s.Key))
            {
                tasks.Add(CheckProcessingWithTimeoutAsync(name, ct));
            }
        }

        (string key, ServiceHealthInfo info)[] results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            foreach ((string? key, ServiceHealthInfo? info) in results)
            {
                _healthStatus[key] = info;
            }
            _lastCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task<ServiceHealthInfo> CheckServiceAsync(string sourceName, CancellationToken ct)
    {
        SourceServiceConfig? config = httpClient.GetSourceConfig(sourceName);

        if (config is null || !config.Enabled)
        {
            return new ServiceHealthInfo
            {
                Name = sourceName,
                ServiceKind = "source",
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
                ServiceKind = "source",
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
            return TimeoutInfo(sourceName, "source", config.HttpAddress);
        }
        catch (Exception ex)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                logger.LogWarning("Health check failed for {Source} ({HttpStatus})", sourceName, statusDescription);
            else
                logger.LogWarning(ex, "Health check failed for {Source}", sourceName);

            return UnavailableInfo(sourceName, "source", config.HttpAddress, ex.Message);
        }
    }

    public async Task<ServiceHealthInfo> CheckProcessingServiceAsync(string name, CancellationToken ct)
    {
        if (processingHttpClient is null)
        {
            return new ServiceHealthInfo { Name = name, ServiceKind = "processing", Status = "not_configured" };
        }

        ProcessingServiceConfig? config = processingHttpClient.GetProcessingServiceConfig(name);
        if (config is null || !config.Enabled)
        {
            return new ServiceHealthInfo
            {
                Name = name,
                ServiceKind = "processing",
                Status = "not_configured",
                HttpAddress = config?.HttpAddress ?? "",
            };
        }

        try
        {
            HealthCheckResponse? health = await processingHttpClient.HealthCheckAsync(name, ct);
            ProcessingStatusResponse? status = await processingHttpClient.GetStatusAsync(name, ct);
            ProcessingQueueStatsResponse? queue = await processingHttpClient.GetQueueStatsAsync(name, ct);

            return new ServiceHealthInfo
            {
                Name = name,
                ServiceKind = "processing",
                Status = health?.Status ?? "unknown",
                HttpAddress = config.HttpAddress,
                UptimeSeconds = health?.UptimeSeconds ?? status?.UptimeSeconds ?? 0,
                Version = health?.Version,
                ProcessingStatus = status?.Status,
                ProcessingIsRunning = status?.IsRunning,
                ProcessingRemainingCount = queue?.RemainingCount,
                ProcessingInFlightCount = queue?.InFlightCount,
                ProcessingErrorCount = queue?.ErrorCount,
                LastItemCompletedAt = queue?.LastItemCompletedAt,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Health check timed out for Processing service {Service}", name);
            return TimeoutInfo(name, "processing", config.HttpAddress);
        }
        catch (Exception ex)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                logger.LogWarning("Health check failed for Processing service {Service} ({HttpStatus})", name, statusDescription);
            else
                logger.LogWarning(ex, "Health check failed for Processing service {Service}", name);

            return UnavailableInfo(name, "processing", config.HttpAddress, ex.Message);
        }
    }

    public async Task<ServiceHealthInfo> CheckAndUpdateServiceAsync(string sourceName, CancellationToken ct)
    {
        ServiceHealthInfo info = await CheckServiceAsync(sourceName, ct);
        lock (_lock)
        {
            _healthStatus[sourceName] = info;
        }
        return info;
    }

    public Dictionary<string, ServiceHealthInfo> GetCurrentStatus()
    {
        lock (_lock)
        {
            return new Dictionary<string, ServiceHealthInfo>(_healthStatus, StringComparer.OrdinalIgnoreCase);
        }
    }

    public ServiceHealthInfo? GetServiceStatus(string sourceName)
    {
        lock (_lock)
        {
            return _healthStatus.GetValueOrDefault(sourceName);
        }
    }

    private async Task<(string key, ServiceHealthInfo info)> CheckSourceWithTimeoutAsync(string name, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerServiceTimeout);
        ServiceHealthInfo info = await CheckServiceAsync(name, timeoutCts.Token);
        return (name, info);
    }

    private async Task<(string key, ServiceHealthInfo info)> CheckProcessingWithTimeoutAsync(string name, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerServiceTimeout);
        ServiceHealthInfo info = await CheckProcessingServiceAsync(name, timeoutCts.Token);
        return (name, info);
    }

    private static ServiceHealthInfo TimeoutInfo(string name, string kind, string httpAddress) => new()
    {
        Name = name,
        ServiceKind = kind,
        Status = "timeout",
        HttpAddress = httpAddress,
        LastError = "Health check timed out",
    };

    private static ServiceHealthInfo UnavailableInfo(string name, string kind, string httpAddress, string error) => new()
    {
        Name = name,
        ServiceKind = kind,
        Status = "unavailable",
        HttpAddress = httpAddress,
        LastError = error,
    };
}

public class ServiceHealthInfo
{
    public required string Name { get; set; }
    public string ServiceKind { get; set; } = "source";
    public required string Status { get; set; }
    public string HttpAddress { get; set; } = "";
    public double UptimeSeconds { get; set; }
    public string? Version { get; set; }
    public int ItemCount { get; set; }
    public long DbSizeBytes { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Common.Api.IndexStatusInfo> Indexes { get; set; } = [];
    public string? ProcessingStatus { get; set; }
    public bool? ProcessingIsRunning { get; set; }
    public int? ProcessingRemainingCount { get; set; }
    public int? ProcessingInFlightCount { get; set; }
    public int? ProcessingErrorCount { get; set; }
    public DateTimeOffset? LastItemCompletedAt { get; set; }
}
