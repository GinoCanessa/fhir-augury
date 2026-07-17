using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Hosting;

public static class ProcessingEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapProcessingEndpoints<TItem>(this IEndpointRouteBuilder endpoints)
    {
        MapProcessingEndpointsCore<TItem>(endpoints, "");
        MapProcessingEndpointsCore<TItem>(endpoints, "/api/v1");
        return endpoints;
    }

    private static void MapProcessingEndpointsCore<TItem>(IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapPost($"{prefix}/processing/start", (ProcessingLifecycleService lifecycle) =>
        {
            lifecycle.Start();
            return Results.Ok(new ProcessingLifecycleResponse("running", true, "Processing started."));
        });

        endpoints.MapPost($"{prefix}/processing/stop", (ProcessingLifecycleService lifecycle) =>
        {
            lifecycle.Stop();
            return Results.Ok(new ProcessingLifecycleResponse("paused", false, "Processing stop requested; in-flight items will drain."));
        });

        endpoints.MapGet($"{prefix}/status", (ProcessingLifecycleService lifecycle, IOptions<ProcessingServiceOptions> optionsAccessor) =>
        {
            ProcessingServiceOptions options = optionsAccessor.Value;
            DateTimeOffset startedAt = lifecycle.StartedAt;
            ProcessingStatusResponse response = new(
                Status: lifecycle.IsRunning ? "running" : "paused",
                IsRunning: lifecycle.IsRunning,
                IsPaused: lifecycle.IsPaused,
                StartedAt: startedAt,
                UptimeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                LastPollAt: lifecycle.LastPollAt,
                SyncSchedule: options.SyncSchedule,
                MaxConcurrentProcessingThreads: options.MaxConcurrentProcessingThreads,
                StartProcessingOnStartup: options.StartProcessingOnStartup);
            return Results.Ok(response);
        });

        endpoints.MapGet($"{prefix}/processing/queue", async (IProcessingWorkItemStore<TItem> store, CancellationToken ct) =>
        {
            ProcessingQueueStats stats = await store.GetQueueStatsAsync(ct);
            ProcessingQueueStatsResponse response = new(
                stats.ProcessedCount,
                stats.RemainingCount,
                stats.InFlightCount,
                stats.ErrorCount,
                stats.AverageItemDurationMs,
                stats.LastItemCompletedAt);
            return Results.Ok(response);
        });

        endpoints.MapGet($"{prefix}/health", (ProcessingLifecycleService lifecycle, IServiceProvider serviceProvider) =>
        {
            string status = "ok";
            string? message = lifecycle.LastError;
            ProcessingDatabase? database = serviceProvider.GetService<ProcessingDatabase>();
            if (database is not null)
            {
                try
                {
                    status = database.QuickCheck();
                }
                catch (Exception ex)
                {
                    status = "unhealthy";
                    message = ex.Message;
                }
            }

            double uptimeSeconds = (DateTimeOffset.UtcNow - lifecycle.StartedAt).TotalSeconds;
            return Results.Ok(new HealthCheckResponse(status, null, uptimeSeconds, message));
        });
    }
}
