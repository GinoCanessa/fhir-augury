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
public class ServiceHealthMonitor
{
    private static readonly TimeSpan DefaultPerServiceTimeout = TimeSpan.FromSeconds(10);

    private readonly SourceHttpClient _httpClient;
    private readonly ProcessingHttpClient? _processingHttpClient;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<ServiceHealthMonitor> _logger;
    private readonly TimeSpan _perServiceTimeout;
    private readonly Dictionary<string, ServiceHealthInfo> _healthStatus;
    private readonly object _lock = new();
    private DateTimeOffset? _lastCheckedAt;

    public ServiceHealthMonitor(
        SourceHttpClient httpClient,
        IOptions<OrchestratorOptions> optionsAccessor,
        ILogger<ServiceHealthMonitor> logger,
        ProcessingHttpClient? processingHttpClient = null)
        : this(
            httpClient,
            optionsAccessor,
            logger,
            processingHttpClient,
            DefaultPerServiceTimeout)
    {
    }

    internal ServiceHealthMonitor(
        SourceHttpClient httpClient,
        IOptions<OrchestratorOptions> optionsAccessor,
        ILogger<ServiceHealthMonitor> logger,
        ProcessingHttpClient? processingHttpClient,
        TimeSpan perServiceTimeout)
    {
        _httpClient = httpClient;
        _processingHttpClient = processingHttpClient;
        _options = optionsAccessor.Value;
        _logger = logger;
        _perServiceTimeout = perServiceTimeout;
        _healthStatus = CreateInitialStatus(_options);
    }

    public DateTimeOffset? LastCheckedAt
    {
        get { lock (_lock) { return _lastCheckedAt; } }
    }

    public async Task CheckAllAsync(CancellationToken ct)
    {
        List<Task<(string key, ServiceHealthInfo info)>> tasks = [];

        foreach (string name in _options.Services
            .Where(service => service.Value.Enabled)
            .Select(service => service.Key))
        {
            tasks.Add(CheckSourceWithTimeoutAsync(name, ct));
        }

        if (_processingHttpClient is not null)
        {
            foreach (string name in _options.ProcessingServices
                .Where(service => service.Value.Enabled)
                .Select(service => service.Key))
            {
                tasks.Add(CheckProcessingWithTimeoutAsync(name, ct));
            }
        }

        (string key, ServiceHealthInfo info)[] results = await Task.WhenAll(tasks);

        lock (_lock)
        {
            foreach ((string key, ServiceHealthInfo info) in results)
            {
                _healthStatus[key] = info;
            }
            _lastCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task<ServiceHealthInfo> CheckServiceAsync(string sourceName, CancellationToken ct)
    {
        SourceServiceConfig? config = _httpClient.GetSourceConfig(sourceName);

        if (config is null || !config.Enabled)
        {
            return NotConfiguredInfo(
                sourceName,
                "source",
                config?.HttpAddress,
                configured: config is not null,
                enabled: config?.Enabled ?? false);
        }

        try
        {
            HealthCheckResponse? health = await _httpClient.HealthCheckAsync(sourceName, ct);
            StatsResponse? stats = await _httpClient.GetStatsAsync(sourceName, ct);
            IngestionStatusResponse? ingestionStatus =
                await _httpClient.GetIngestionStatusAsync(sourceName, ct);

            string status = health?.Status ?? "unknown";
            return new ServiceHealthInfo
            {
                Name = sourceName,
                ServiceKind = "source",
                Status = status,
                HttpAddress = config.HttpAddress,
                Enabled = true,
                Configured = true,
                CheckedAt = DateTimeOffset.UtcNow,
                UptimeSeconds = health?.UptimeSeconds ?? 0,
                Version = health?.Version,
                ItemCount = stats?.TotalItems ?? 0,
                DbSizeBytes = stats?.DatabaseSizeBytes ?? 0,
                LastSyncAt = stats?.LastSyncAt,
                LastError = IsHealthy(status)
                    ? ingestionStatus?.LastError
                    : health?.Message ?? ingestionStatus?.LastError,
                Indexes = ingestionStatus?.Indexes ?? [],
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Health check timed out for {Source}", sourceName);
            return TimeoutInfo(sourceName, "source", config.HttpAddress);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                _logger.LogWarning(
                    "Health check failed for {Source} ({HttpStatus})",
                    sourceName,
                    statusDescription);
            else
                _logger.LogWarning(ex, "Health check failed for {Source}", sourceName);

            return UnavailableInfo(
                sourceName,
                "source",
                config.HttpAddress,
                ex.Message);
        }
    }

    public async Task<ServiceHealthInfo> CheckProcessingServiceAsync(
        string name,
        CancellationToken ct)
    {
        _options.ProcessingServices.TryGetValue(
            name,
            out ProcessingServiceConfig? config);
        if (_processingHttpClient is null || config is null || !config.Enabled)
        {
            return NotConfiguredInfo(
                name,
                "processing",
                config?.HttpAddress,
                configured: config is not null,
                enabled: config?.Enabled ?? false,
                config?.RequiredServices);
        }

        try
        {
            HealthCheckResponse? health =
                await _processingHttpClient.HealthCheckAsync(name, ct);
            ProcessingStatusResponse? status =
                await _processingHttpClient.GetStatusAsync(name, ct);
            ProcessingQueueStatsResponse? queue =
                await _processingHttpClient.GetQueueStatsAsync(name, ct);

            string healthStatus = health?.Status ?? "unknown";
            return new ServiceHealthInfo
            {
                Name = name,
                ServiceKind = "processing",
                Status = healthStatus,
                HttpAddress = config.HttpAddress,
                Enabled = true,
                Configured = true,
                CheckedAt = DateTimeOffset.UtcNow,
                UptimeSeconds = health?.UptimeSeconds ?? status?.UptimeSeconds ?? 0,
                Version = health?.Version,
                LastError = IsHealthy(healthStatus) ? null : health?.Message,
                ProcessingStatus = status?.Status,
                ProcessingIsRunning = status?.IsRunning,
                ProcessingRemainingCount = queue?.RemainingCount,
                ProcessingInFlightCount = queue?.InFlightCount,
                ProcessingErrorCount = queue?.ErrorCount,
                LastItemCompletedAt = queue?.LastItemCompletedAt,
                RequiredServices = [.. config.RequiredServices],
                Indexes = [],
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Health check timed out for Processing service {Service}",
                name);
            return TimeoutInfo(
                name,
                "processing",
                config.HttpAddress,
                config.RequiredServices);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                _logger.LogWarning(
                    "Health check failed for Processing service {Service} ({HttpStatus})",
                    name,
                    statusDescription);
            else
                _logger.LogWarning(
                    ex,
                    "Health check failed for Processing service {Service}",
                    name);

            return UnavailableInfo(
                name,
                "processing",
                config.HttpAddress,
                ex.Message,
                config.RequiredServices);
        }
    }

    public async Task<ServiceHealthInfo> CheckAndUpdateServiceAsync(
        string sourceName,
        CancellationToken ct)
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
            return new Dictionary<string, ServiceHealthInfo>(
                _healthStatus,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public ServiceHealthInfo? GetServiceStatus(string sourceName)
    {
        lock (_lock)
        {
            return _healthStatus.GetValueOrDefault(sourceName);
        }
    }

    private async Task<(string key, ServiceHealthInfo info)> CheckSourceWithTimeoutAsync(
        string name,
        CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_perServiceTimeout);
        try
        {
            ServiceHealthInfo info =
                await CheckServiceAsync(name, timeoutCts.Token);
            return (name, info);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SourceServiceConfig config = _options.Services[name];
            _logger.LogWarning("Health check timed out for {Source}", name);
            return (name, TimeoutInfo(name, "source", config.HttpAddress));
        }
    }

    private async Task<(string key, ServiceHealthInfo info)> CheckProcessingWithTimeoutAsync(
        string name,
        CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_perServiceTimeout);
        try
        {
            ServiceHealthInfo info =
                await CheckProcessingServiceAsync(name, timeoutCts.Token);
            return (name, info);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ProcessingServiceConfig config = _options.ProcessingServices[name];
            _logger.LogWarning(
                "Health check timed out for Processing service {Service}",
                name);
            return (
                name,
                TimeoutInfo(
                    name,
                    "processing",
                    config.HttpAddress,
                    config.RequiredServices));
        }
    }

    private static Dictionary<string, ServiceHealthInfo> CreateInitialStatus(
        OrchestratorOptions options)
    {
        Dictionary<string, ServiceHealthInfo> status =
            new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, SourceServiceConfig config) in options.Services)
        {
            status[name] = InitialInfo(
                name,
                "source",
                config.HttpAddress,
                config.Enabled);
        }

        foreach ((string name, ProcessingServiceConfig config) in options.ProcessingServices)
        {
            status[name] = InitialInfo(
                name,
                "processing",
                config.HttpAddress,
                config.Enabled,
                config.RequiredServices);
        }

        return status;
    }

    private static ServiceHealthInfo InitialInfo(
        string name,
        string kind,
        string httpAddress,
        bool enabled,
        IEnumerable<string>? requiredServices = null) => new()
        {
            Name = name,
            ServiceKind = kind,
            Status = enabled ? "unobserved" : "not_configured",
            HttpAddress = httpAddress,
            Enabled = enabled,
            Configured = true,
            RequiredServices = [.. requiredServices ?? []],
            Indexes = [],
        };

    private static ServiceHealthInfo NotConfiguredInfo(
        string name,
        string kind,
        string? httpAddress,
        bool configured,
        bool enabled,
        IEnumerable<string>? requiredServices = null) => new()
        {
            Name = name,
            ServiceKind = kind,
            Status = "not_configured",
            HttpAddress = httpAddress,
            Enabled = enabled,
            Configured = configured,
            CheckedAt = DateTimeOffset.UtcNow,
            RequiredServices = [.. requiredServices ?? []],
            Indexes = [],
        };

    private static ServiceHealthInfo TimeoutInfo(
        string name,
        string kind,
        string httpAddress,
        IEnumerable<string>? requiredServices = null) => new()
        {
            Name = name,
            ServiceKind = kind,
            Status = "timeout",
            HttpAddress = httpAddress,
            Enabled = true,
            Configured = true,
            CheckedAt = DateTimeOffset.UtcNow,
            LastError = "Health check timed out",
            RequiredServices = [.. requiredServices ?? []],
            Indexes = [],
        };

    private static ServiceHealthInfo UnavailableInfo(
        string name,
        string kind,
        string httpAddress,
        string error,
        IEnumerable<string>? requiredServices = null) => new()
        {
            Name = name,
            ServiceKind = kind,
            Status = "unavailable",
            HttpAddress = httpAddress,
            Enabled = true,
            Configured = true,
            CheckedAt = DateTimeOffset.UtcNow,
            LastError = error,
            RequiredServices = [.. requiredServices ?? []],
            Indexes = [],
        };

    private static bool IsHealthy(string status) =>
        string.Equals(status, "healthy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
}
