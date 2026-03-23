using Fhiraugury;
using FhirAugury.Common.Database;
using FhirAugury.Common.Ingestion;

namespace FhirAugury.Common.Grpc;

/// <summary>
/// Shared helper methods for source service gRPC lifecycle endpoints
/// (GetIngestionStatus, RebuildFromCache, GetStats, HealthCheck).
/// Reduces duplication across the four source services.
/// </summary>
public static class SourceServiceLifecycle
{
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    /// <summary>Builds a standard RebuildResponse by executing the rebuild and timing it.</summary>
    public static async Task<RebuildResponse> RebuildFromCacheAsync(
        Func<CancellationToken, Task<int>> rebuildFunc,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var itemsLoaded = await rebuildFunc(ct);
            return new RebuildResponse
            {
                Success = true,
                ItemsLoaded = itemsLoaded,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
        catch (Exception ex)
        {
            return new RebuildResponse
            {
                Success = false,
                Error = ex.Message,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
            };
        }
    }

    /// <summary>Builds a standard HealthCheckResponse.</summary>
    public static HealthCheckResponse BuildHealthCheck(
        SourceDatabase database,
        IIngestionPipeline pipeline)
    {
        var integrity = database.CheckIntegrity();
        return new HealthCheckResponse
        {
            Status = integrity == "ok" ? "healthy" : "degraded",
            Version = "2.0.0",
            UptimeSeconds = (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
            Message = pipeline.IsRunning ? $"Ingestion in progress: {pipeline.CurrentStatus}" : "OK",
        };
    }
}
