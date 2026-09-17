using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Processing.Common.Api;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
                        health.RequiredServices,
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

    [HttpPost("{name}/authoring/runs")]
    public async Task<IActionResult> CreateAuthoringRun(
        string name,
        [FromBody] JsonElement request,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.CreateAuthoringRunAsync(name, request, ct));
    }

    [HttpPost("{name}/authoring/runs/{sourceRunId}/publication-refresh")]
    public async Task<IActionResult> StartPublicationRefresh(
        string name,
        string sourceRunId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.StartPublicationRefreshAsync(
                name,
                sourceRunId,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{sourceRunId}/publication-reconciliation")]
    public async Task<IActionResult> StartPublicationReconciliation(
        string name,
        string sourceRunId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.StartPublicationReconciliationAsync(
                name,
                sourceRunId,
                ct));
    }

    [HttpGet("{name}/authoring/runs/{runId}/publication-reconciliation")]
    public async Task<IActionResult> GetPublicationReconciliation(
        string name,
        string runId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.GetPublicationReconciliationAsync(
                name,
                runId,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{runId}/publication-reconciliation/retry")]
    public async Task<IActionResult> RetryPublicationReconciliation(
        string name,
        string runId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.RetryPublicationReconciliationAsync(
                name,
                runId,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{runId}/publication-reconciliation/cancel")]
    public async Task<IActionResult> CancelPublicationReconciliation(
        string name,
        string runId,
        [FromBody] JsonElement request,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.CancelPublicationReconciliationAsync(
                name,
                runId,
                request,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{runId}/publication-reconciliation/abandon")]
    public async Task<IActionResult> AbandonPublicationReconciliation(
        string name,
        string runId,
        [FromBody] JsonElement request,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.AbandonPublicationReconciliationAsync(
                name,
                runId,
                request,
                ct));
    }

    [HttpGet("{name}/authoring/runs/{runId}")]
    public async Task<IActionResult> GetAuthoringRun(
        string name,
        string runId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.GetAuthoringRunAsync(name, runId, ct));
    }

    [HttpGet("{name}/authoring/runs")]
    public async Task<IActionResult> ListAuthoringRuns(
        string name,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.ListAuthoringRunsAsync(
                name,
                limit,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{runId}/items/{itemId}/retry")]
    public async Task<IActionResult> RetryAuthoringItem(
        string name,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.RetryAuthoringItemAsync(
                name,
                runId,
                itemId,
                ct));
    }

    [HttpPost("{name}/authoring/runs/{runId}/items/{itemId}/supersede")]
    public async Task<IActionResult> SupersedeAuthoringItem(
        string name,
        string runId,
        string itemId,
        [FromBody] JsonElement request,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.SupersedeAuthoringItemAsync(
                name,
                runId,
                itemId,
                request,
                ct));
    }

    [HttpGet("{name}/authoring/runs/{runId}/snapshot")]
    public async Task<IActionResult> GetAuthoringSnapshot(
        string name,
        string runId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return ToActionResult(
            await processingHttpClient.GetAuthoringSnapshotAsync(
                name,
                runId,
                ct));
    }

    [HttpGet("{name}/authoring/runs/{runId}/snapshot/bytes")]
    public async Task<IActionResult> GetAuthoringSnapshotBytes(
        string name,
        string runId,
        CancellationToken ct)
    {
        if (!processingHttpClient.IsProcessingServiceEnabled(name))
        {
            return NotFound(new { error = $"Processing service '{name}' is not configured or disabled." });
        }

        return await processingHttpClient.GetAuthoringSnapshotBytesAsync(
            name,
            runId,
            Request,
            ct);
    }

    private ContentResult ToActionResult(ProcessingProxyResponse response)
    {
        CopyProxyHeaders(response);
        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.ContentType ?? "application/json",
            Content = System.Text.Encoding.UTF8.GetString(response.Content),
        };
    }

    private void CopyProxyHeaders(ProcessingProxyResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.RetryAfter))
        {
            Response.Headers.RetryAfter = response.RetryAfter;
        }
        if (!string.IsNullOrWhiteSpace(response.Location))
        {
            Response.Headers.Location = response.Location;
        }
    }
}
