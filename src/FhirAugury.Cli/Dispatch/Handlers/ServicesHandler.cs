using Fhiraugury;
using FhirAugury.Cli.Models;
using FhirAugury.Common.Text;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class ServicesHandler
{
    public static async Task<object> HandleAsync(ServicesRequest request, string orchestratorAddr, CancellationToken ct)
    {
        return request.Action.ToLowerInvariant() switch
        {
            "status" => await HandleStatusAsync(orchestratorAddr, ct),
            "stats" => await HandleStatsAsync(orchestratorAddr, ct),
            _ => throw new ArgumentException(
                $"Unknown services action: {request.Action}. Valid actions: status, stats"),
        };
    }

    private static async Task<object> HandleStatusAsync(string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        ServicesStatusResponse response = await clients.Orchestrator.GetServicesStatusAsync(
            new ServicesStatusRequest(), cancellationToken: ct);

        return new
        {
            lastXrefScanAt = response.LastXrefScanAt?.ToDateTimeOffset().ToString("o"),
            services = response.Services.Select(s => new
            {
                name = s.Name,
                status = s.Status,
                grpcAddress = s.GrpcAddress,
                itemCount = s.ItemCount,
                dbSizeBytes = s.DbSizeBytes,
                lastSyncAt = s.LastSyncAt?.ToDateTimeOffset().ToString("o"),
                lastError = s.LastError,
                indexes = s.Indexes.Select(i => new
                {
                    name = i.Name,
                    description = i.Description,
                    isRebuilding = i.IsRebuilding,
                    lastRebuildStartedAt = i.LastRebuildStartedAt?.ToDateTimeOffset().ToString("o"),
                    lastRebuildCompletedAt = i.LastRebuildCompletedAt?.ToDateTimeOffset().ToString("o"),
                    recordCount = i.RecordCount,
                    lastError = i.LastError,
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static async Task<object> HandleStatsAsync(string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);

        ServicesStatusResponse statusResponse = await clients.Orchestrator.GetServicesStatusAsync(
            new ServicesStatusRequest(), cancellationToken: ct);

        List<object> sources = [];
        foreach (ServiceHealth svc in statusResponse.Services)
        {
            try
            {
                SourceService.SourceServiceClient sourceClient = new(
                    Grpc.Net.Client.GrpcChannel.ForAddress(svc.GrpcAddress));
                StatsResponse stats = await sourceClient.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
                sources.Add(new
                {
                    source = stats.Source,
                    totalItems = stats.TotalItems,
                    totalComments = stats.TotalComments,
                    databaseSizeBytes = stats.DatabaseSizeBytes,
                    cacheSizeBytes = stats.CacheSizeBytes,
                    lastSyncAt = stats.LastSyncAt?.ToDateTimeOffset().ToString("o"),
                    oldestItem = stats.OldestItem?.ToDateTimeOffset().ToString("o"),
                    newestItem = stats.NewestItem?.ToDateTimeOffset().ToString("o"),
                    additionalCounts = new Dictionary<string, int>(stats.AdditionalCounts),
                });
            }
            catch
            {
                // Service may be unreachable — skip it
            }
        }

        return new
        {
            lastXrefScanAt = statusResponse.LastXrefScanAt?.ToDateTimeOffset().ToString("o"),
            sources,
        };
    }
}
