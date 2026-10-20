using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
public class ServicesController(
    SourceHttpClient httpClient,
    ServiceHealthMonitor monitor,
    OrchestratorDatabase database,
    ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("OrchestratorHttpApi");

    [HttpGet("services")]
    public IActionResult GetServices()
    {
        return Ok(CreateServicesStatusResponse());
    }

    [HttpPost("services/refresh")]
    public async Task<IActionResult> RefreshServices(CancellationToken ct)
    {
        await monitor.CheckAllAsync(ct);
        return Ok(CreateServicesStatusResponse());
    }

    [HttpGet("endpoints")]
    public IActionResult GetEndpoints()
    {
        List<ServiceEndpointInfo> endpoints = [];
        foreach (string sourceName in httpClient.GetEnabledSourceNames())
        {
            SourceServiceConfig? config = httpClient.GetSourceConfig(sourceName);
            if (config is null) continue;

            endpoints.Add(new ServiceEndpointInfo(
                Name: sourceName,
                HttpAddress: config.HttpAddress,
                Enabled: config.Enabled));
        }

        return Ok(new ServiceEndpointsResponse(endpoints));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        long dbSize = database.GetDatabaseSizeBytes();

        List<object> sourceStats = [];
        List<string> warnings = [];
        foreach (string sourceName in httpClient.GetEnabledSourceNames())
        {
            try
            {
                StatsResponse? stats = await httpClient.GetStatsAsync(sourceName, ct);
                sourceStats.Add(new
                {
                    source = stats?.Source ?? sourceName,
                    totalItems = stats?.TotalItems ?? 0,
                    totalComments = stats?.TotalComments ?? 0,
                    databaseSizeBytes = stats?.DatabaseSizeBytes ?? 0L,
                    cacheSizeBytes = stats?.CacheSizeBytes ?? 0L,
                    additionalCounts = stats?.AdditionalCounts,
                    status = "ok",
                });
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    _logger.LogWarning("Failed to get stats for source {Source} ({HttpStatus})", sourceName, statusDescription);
                else
                    _logger.LogWarning(ex, "Failed to get stats for source {Source}", sourceName);
                warnings.Add($"Stats unavailable for '{sourceName}': {ex.Message}");
                sourceStats.Add(new
                {
                    source = sourceName,
                    totalItems = 0,
                    totalComments = 0,
                    databaseSizeBytes = 0L,
                    cacheSizeBytes = 0L,
                    additionalCounts = (Dictionary<string, int>?)null,
                    status = "unavailable",
                });
            }
        }

        return Ok(new
        {
            orchestrator = new { databaseSizeBytes = dbSize },
            sources = sourceStats,
            warnings,
        });
    }

    private ServicesStatusResponse CreateServicesStatusResponse()
    {
        Dictionary<string, ServiceHealthInfo> status = monitor.GetCurrentStatus();
        List<ServiceHealthInfo> services =
        [
            new ServiceHealthInfo
            {
                Name = "Orchestrator",
                ServiceKind = "orchestrator",
                Status = "healthy",
                Enabled = true,
                Configured = true,
                CheckedAt = DateTimeOffset.UtcNow,
                Indexes = [],
            },
            .. status.Values,
        ];

        return new ServicesStatusResponse(services, monitor.LastCheckedAt);
    }
}
