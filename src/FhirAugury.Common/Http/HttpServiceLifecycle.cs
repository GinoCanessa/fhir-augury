using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
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
    public static HealthCheckResponse BuildHealthCheck(
        SourceDatabase database,
        IIngestionPipeline pipeline)
    {
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
