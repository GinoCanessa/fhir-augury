using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Processing.Common.Api;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1/processing-services")]
public class ProcessingController(
    ProcessingHttpClient processingHttpClient,
    ServiceHealthMonitor monitor) : ControllerBase
{
    [HttpGet]
    public IActionResult GetServices()
    {
        Dictionary<string, ServiceHealthInfo> cachedHealth = monitor.GetCurrentStatus();
        IReadOnlyList<object> services = processingHttpClient.GetEnabledProcessingServiceNames()
            .Select(name =>
            {
                ProcessingServiceConfig? config = processingHttpClient.GetProcessingServiceConfig(name);
                cachedHealth.TryGetValue(name, out ServiceHealthInfo? health);
                return new
                {
                    name,
                    enabled = config?.Enabled ?? false,
                    description = config?.Description,
                    httpAddress = config?.HttpAddress,
                    health = health is null ? null : new
                    {
                        health.Status,
                        health.ServiceKind,
                        health.UptimeSeconds,
                        health.LastError,
                        health.CheckedAt,
                    },
                };
            })
            .ToList<object>();

        return Ok(new { services });
    }

    [HttpGet("{name}/status")]
    public async Task<IActionResult> GetStatus(string name, CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        ProcessingStatusResponse? response = await processingHttpClient.GetStatusAsync(name, ct);
        return response is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(response);
    }

    [HttpGet("{name}/queue")]
    public async Task<IActionResult> GetQueue(string name, CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        ProcessingQueueStatsResponse? response = await processingHttpClient.GetQueueStatsAsync(name, ct);
        return response is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(response);
    }

    [HttpPost("{name}/start")]
    public async Task<IActionResult> Start(string name, CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        ProcessingLifecycleResponse? response = await processingHttpClient.StartAsync(name, ct);
        return response is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(response);
    }

    [HttpPost("{name}/stop")]
    public async Task<IActionResult> Stop(string name, CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        ProcessingLifecycleResponse? response = await processingHttpClient.StopAsync(name, ct);
        return response is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(response);
    }

    [HttpGet("{name}/health")]
    public async Task<IActionResult> Health(string name, CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        FhirAugury.Common.Api.HealthCheckResponse? response = await processingHttpClient.HealthCheckAsync(name, ct);
        return response is null ? StatusCode(StatusCodes.Status502BadGateway) : Ok(response);
    }
}
