using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Hosting;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Controllers;

/// <summary>
/// Health / stats / status endpoints. This source has no ingestion pipeline, so
/// the health check is built inline (rather than via
/// <see cref="HttpServiceLifecycle.BuildHealthCheck"/>, which requires an
/// ingestion pipeline). Every database access is guarded by
/// <see cref="FhirSpecDatabase.Exists"/> so a missing DB reports <c>degraded</c>
/// instead of throwing.
/// </summary>
[ApiController]
[Route("api/v1")]
public class LifecycleController(
    FhirSpecDatabase db,
    FhirSpecReader reader,
    IIndexTracker indexTracker,
    IStartupRebuildStatus? startupRebuild = null) : ControllerBase
{
    private const string ServiceVersion = "2.0.0";
    private static readonly DateTimeOffset s_startTime = DateTimeOffset.UtcNow;

    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        double uptime = (DateTimeOffset.UtcNow - s_startTime).TotalSeconds;

        if (startupRebuild is not null)
        {
            switch (startupRebuild.State)
            {
                case StartupRebuildState.Pending:
                case StartupRebuildState.Running:
                    return Ok(new HealthCheckResponse(
                        "initializing", ServiceVersion, uptime,
                        string.IsNullOrEmpty(startupRebuild.CurrentPhase)
                            ? "Startup rebuild in progress"
                            : $"Startup rebuild: {startupRebuild.CurrentPhase}"));

                case StartupRebuildState.Failed:
                    return Ok(new HealthCheckResponse(
                        "degraded", ServiceVersion, uptime,
                        $"Startup rebuild failed: {startupRebuild.LastError?.Message ?? "unknown error"}"));
            }
        }

        if (!db.Exists)
        {
            return Ok(new HealthCheckResponse(
                "degraded", ServiceVersion, uptime, $"Spec database not found at {db.DatabasePath}"));
        }

        string liveness;
        try
        {
            liveness = db.QuickCheck();
        }
        catch (Exception ex)
        {
            return Ok(new HealthCheckResponse(
                "degraded", ServiceVersion, uptime, $"Spec database error: {ex.Message}"));
        }

        return Ok(new HealthCheckResponse(
            liveness == "ok" ? "healthy" : "degraded", ServiceVersion, uptime, "OK"));
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        if (!db.Exists)
        {
            return Ok(new StatsResponse
            {
                Source = SourceSystems.Fhir,
                TotalItems = 0,
                DatabaseSizeBytes = 0,
            });
        }

        FhirSpecCounts counts = reader.GetCounts();
        long dbSize = db.GetDatabaseSizeBytes();

        return Ok(new StatsResponse
        {
            Source = SourceSystems.Fhir,
            TotalItems = counts.Structures + counts.CodeSystems + counts.ValueSets
                       + counts.Operations + counts.SearchParameters,
            DatabaseSizeBytes = dbSize,
            AdditionalCounts = new Dictionary<string, int>
            {
                ["releases"] = counts.Releases,
                ["structures"] = counts.Structures,
                ["codesystems"] = counts.CodeSystems,
                ["valuesets"] = counts.ValueSets,
                ["operations"] = counts.Operations,
                ["searchparameters"] = counts.SearchParameters,
            },
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        bool initializing = startupRebuild is
            { State: StartupRebuildState.Pending or StartupRebuildState.Running };
        string status = !db.Exists ? "degraded" : initializing ? "initializing" : "ready";
        string? lastError = startupRebuild?.State == StartupRebuildState.Failed
            ? startupRebuild.LastError?.Message
            : null;

        IngestionStatusResponse response = new(
            SourceSystems.Fhir,
            status,
            LastSyncAt: null,
            ItemsTotal: 0,
            ItemsProcessed: 0,
            LastError: lastError,
            SyncSchedule: null,
            Indexes: HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses()),
            SupportedIndexTypes: ["fts", "all"]);

        return Ok(response);
    }
}
