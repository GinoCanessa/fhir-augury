using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Common.Hosting;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using System.Diagnostics;

namespace FhirAugury.Common.Http;

/// <summary>
/// Shared helper methods for source service HTTP lifecycle endpoints
/// (status, rebuild, stats, health check).
/// Reduces duplication across the four source services.
/// </summary>
public static class HttpServiceLifecycle
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    /// <summary>Executes a rebuild-from-cache and returns a standardized response.</summary>
    public static async Task<RebuildResponse> RebuildFromCacheAsync(
        Func<CancellationToken, Task<int>> rebuildFunc,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            int itemsLoaded = await rebuildFunc(ct);
            return new RebuildResponse(
                Success: true,
                ItemsLoaded: itemsLoaded,
                ElapsedSeconds: sw.Elapsed.TotalSeconds,
                Error: null);
        }
        catch (Exception ex)
        {
            return new RebuildResponse(
                Success: false,
                ItemsLoaded: 0,
                ElapsedSeconds: sw.Elapsed.TotalSeconds,
                Error: ex.Message);
        }
    }

    /// <summary>Builds a standard health check response using a lightweight liveness probe.</summary>
    /// <param name="database">Source database used for the liveness probe.</param>
    /// <param name="pipeline">Ingestion pipeline whose run state is reflected in the message.</param>
    /// <param name="startupRebuild">
    /// Optional startup-rebuild status. When supplied and the rebuild is still
    /// pending or running, the response reports <c>Status = "initializing"</c>
    /// so callers can tell the difference between "service is down" and
    /// "service is up but warming up". When the rebuild has failed, the
    /// response reports <c>Status = "degraded"</c> with the error message.
    /// </param>
    public static HealthCheckResponse BuildHealthCheck(
        SourceDatabase database,
        IIngestionPipeline pipeline,
        IStartupRebuildStatus? startupRebuild = null)
    {
        if (startupRebuild is not null)
        {
            switch (startupRebuild.State)
            {
                case StartupRebuildState.Pending:
                case StartupRebuildState.Running:
                    return new HealthCheckResponse(
                        Status: "initializing",
                        Version: "2.0.0",
                        UptimeSeconds: (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
                        Message: string.IsNullOrEmpty(startupRebuild.CurrentPhase)
                            ? "Startup rebuild in progress"
                            : $"Startup rebuild: {startupRebuild.CurrentPhase}");

                case StartupRebuildState.Failed:
                    return new HealthCheckResponse(
                        Status: "degraded",
                        Version: "2.0.0",
                        UptimeSeconds: (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
                        Message: $"Startup rebuild failed: {startupRebuild.LastError?.Message ?? "unknown error"}");
            }
        }

        string liveness = database.QuickCheck();
        return new HealthCheckResponse(
            Status: liveness == "ok" ? "healthy" : "degraded",
            Version: "2.0.0",
            UptimeSeconds: (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
            Message: pipeline.IsRunning ? $"Ingestion in progress: {pipeline.CurrentStatus}" : "OK");
    }

    /// <summary>Converts IndexInfo list to API IndexStatusInfo records.</summary>
    public static List<IndexStatusInfo> ToIndexStatuses(IReadOnlyList<IndexInfo> indexes)
    {
        List<IndexStatusInfo> result = [];
        foreach (IndexInfo info in indexes)
        {
            result.Add(new IndexStatusInfo(
                Name: info.Name,
                Description: info.Description,
                IsRebuilding: info.IsRebuilding,
                LastRebuildStartedAt: info.LastRebuildStartedAt,
                LastRebuildCompletedAt: info.LastRebuildCompletedAt,
                RecordCount: info.RecordCount,
                LastError: info.LastError));
        }
        return result;
    }
}
