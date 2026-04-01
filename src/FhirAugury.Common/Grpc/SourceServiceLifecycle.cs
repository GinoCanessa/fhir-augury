using Fhiraugury;
using FhirAugury.Common.Database;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using Google.Protobuf.WellKnownTypes;
using System.Diagnostics;

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
        Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int itemsLoaded = await rebuildFunc(ct);
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

    /// <summary>Builds a standard HealthCheckResponse using a lightweight liveness probe.</summary>
    public static HealthCheckResponse BuildHealthCheck(
        SourceDatabase database,
        IIngestionPipeline pipeline)
    {
        string liveness = database.QuickCheck();
        return new HealthCheckResponse
        {
            Status = liveness == "ok" ? "healthy" : "degraded",
            Version = "2.0.0",
            UptimeSeconds = (DateTimeOffset.UtcNow - StartTime).TotalSeconds,
            Message = pipeline.IsRunning ? $"Ingestion in progress: {pipeline.CurrentStatus}" : "OK",
        };
    }

    /// <summary>Converts IndexInfo list to proto IndexStatus messages.</summary>
    public static IEnumerable<IndexStatus> ToProtoIndexStatuses(IReadOnlyList<IndexInfo> indexes)
    {
        foreach (IndexInfo info in indexes)
        {
            IndexStatus status = new IndexStatus
            {
                Name = info.Name,
                Description = info.Description,
                IsRebuilding = info.IsRebuilding,
                RecordCount = info.RecordCount,
                LastError = info.LastError ?? "",
            };
            if (info.LastRebuildStartedAt is DateTimeOffset started)
                status.LastRebuildStartedAt = Timestamp.FromDateTimeOffset(started);
            if (info.LastRebuildCompletedAt is DateTimeOffset completed)
                status.LastRebuildCompletedAt = Timestamp.FromDateTimeOffset(completed);
            yield return status;
        }
    }
}
