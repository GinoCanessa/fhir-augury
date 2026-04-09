using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Workers;

/// <summary>
/// Background worker that periodically attempts to reconnect offline source services.
/// Only targets sources with "unavailable" or "timeout" status to avoid redundant work
/// with the general <see cref="HealthCheckWorker"/>.
/// </summary>
public class SourceReconnectionWorker(
    ServiceHealthMonitor monitor,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<SourceReconnectionWorker> logger)
    : BackgroundService
{
    internal static readonly HashSet<string> OfflineStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "unavailable",
        "timeout",
    };

    private static readonly TimeSpan PerServiceTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OrchestratorOptions options = optionsAccessor.Value;
        if (options.ReconnectIntervalSeconds <= 0)
        {
            logger.LogInformation("Source reconnection worker disabled (ReconnectIntervalSeconds = {Value})",
                options.ReconnectIntervalSeconds);
            return;
        }

        TimeSpan interval = TimeSpan.FromSeconds(options.ReconnectIntervalSeconds);
        logger.LogInformation("Source reconnection worker started. Interval: {Interval}s",
            options.ReconnectIntervalSeconds);

        // Wait for health checks to establish baseline
        await Task.Delay(interval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryReconnectOfflineSourcesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Source reconnection check failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Source reconnection worker stopped");
    }

    internal async Task TryReconnectOfflineSourcesAsync(CancellationToken ct)
    {
        Dictionary<string, ServiceHealthInfo> currentStatus = monitor.GetCurrentStatus();

        List<string> offlineSources = currentStatus
            .Where(kv => OfflineStatuses.Contains(kv.Value.Status))
            .Select(kv => kv.Key)
            .ToList();

        if (offlineSources.Count == 0)
            return;

        logger.LogDebug("Attempting reconnection for {Count} offline source(s): {Sources}",
            offlineSources.Count, string.Join(", ", offlineSources));

        foreach (string source in offlineSources)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerServiceTimeout);

                ServiceHealthInfo info = await monitor.CheckAndUpdateServiceAsync(source, timeoutCts.Token);

                if (!OfflineStatuses.Contains(info.Status))
                {
                    logger.LogInformation("Source '{Source}' reconnected successfully (status: {Status})",
                        source, info.Status);
                }
                else
                {
                    logger.LogDebug("Source '{Source}' still offline (status: {Status})",
                        source, info.Status);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Reconnection attempt failed for source '{Source}'", source);
            }
        }
    }
}
