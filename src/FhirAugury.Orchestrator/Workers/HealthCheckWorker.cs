using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Workers;

/// <summary>
/// Background worker that periodically polls source service health.
/// </summary>
public class HealthCheckWorker(
    ServiceHealthMonitor monitor,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<HealthCheckWorker> logger)
    : BackgroundService
{
    private readonly OrchestratorOptions _options = optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, _options.HealthCheckIntervalSeconds));
        TimeSpan startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.HealthCheckStartupDelaySeconds));
        logger.LogInformation(
            "Health check worker started. Startup delay: {StartupDelay}, Interval: {Interval}",
            startupDelay, interval);

        if (startupDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(startupDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await monitor.CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health check poll failed");
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

        logger.LogInformation("Health check worker stopped");
    }
}
