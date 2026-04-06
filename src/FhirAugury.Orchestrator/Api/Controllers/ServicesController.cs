using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Api.Controllers;

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
    public async Task<IActionResult> GetServices(CancellationToken ct)
    {
        await monitor.CheckAllAsync(ct);
        Dictionary<string, Health.ServiceHealthInfo> status = monitor.GetCurrentStatus();

        return Ok(new
        {
            services = status.Values.Select(s => new
            {
                s.Name, s.Status, s.HttpAddress, s.UptimeSeconds,
                s.Version, s.ItemCount, s.DbSizeBytes, s.LastSyncAt, s.LastError,
                indexes = s.Indexes.Select(i => new
                {
                    i.Name, i.Description, i.IsRebuilding,
                    i.LastRebuildStartedAt, i.LastRebuildCompletedAt,
                    i.RecordCount, i.LastError,
                }),
            }),
        });
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
}
