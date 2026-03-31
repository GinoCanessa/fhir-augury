using Fhiraugury;
using FhirAugury.Cli.Models;
using FhirAugury.Common.Text;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class IngestHandler
{
    public static async Task<object> HandleAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        return request.Action.ToLowerInvariant() switch
        {
            "trigger" => await HandleTriggerAsync(request, orchestratorAddr, ct),
            "status" => await HandleStatusAsync(orchestratorAddr, ct),
            "rebuild" => await HandleRebuildAsync(request, orchestratorAddr, ct),
            "index" => await HandleIndexAsync(request, orchestratorAddr, ct),
            _ => throw new ArgumentException(
                $"Unknown ingest action: {request.Action}. Valid actions: trigger, status, rebuild, index"),
        };
    }

    private static async Task<object> HandleTriggerAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        TriggerSyncRequest grpcRequest = new() { Type = request.Type };
        AddSources(grpcRequest, request.Sources);

        TriggerSyncResponse response = await clients.Orchestrator.TriggerSyncAsync(grpcRequest, cancellationToken: ct);

        return new
        {
            statuses = response.Statuses.Select(s => new
            {
                source = s.Source,
                status = s.Status,
                message = s.Message,
            }).ToArray(),
        };
    }

    private static async Task<object> HandleStatusAsync(string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        ServicesStatusResponse response = await clients.Orchestrator.GetServicesStatusAsync(
            new ServicesStatusRequest(), cancellationToken: ct);

        return new
        {
            services = response.Services.Select(s => new
            {
                name = s.Name,
                status = s.Status,
                lastSyncAt = s.LastSyncAt?.ToDateTimeOffset().ToString("o"),
                itemCount = s.ItemCount,
            }).ToArray(),
        };
    }

    private static async Task<object> HandleRebuildAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        TriggerSyncRequest grpcRequest = new() { Type = "rebuild" };
        AddSources(grpcRequest, request.Sources);

        TriggerSyncResponse response = await clients.Orchestrator.TriggerSyncAsync(grpcRequest, cancellationToken: ct);

        return new
        {
            statuses = response.Statuses.Select(s => new
            {
                source = s.Source,
                status = s.Status,
                message = s.Message,
            }).ToArray(),
        };
    }

    private static async Task<object> HandleIndexAsync(IngestRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        OrchestratorRebuildIndexRequest grpcRequest = new() { IndexType = request.IndexType };
        AddSources(grpcRequest, request.Sources);

        OrchestratorRebuildIndexResponse response =
            await clients.Orchestrator.RebuildIndexAsync(grpcRequest, cancellationToken: ct);

        return new
        {
            results = response.Results.Select(r => new
            {
                source = r.Source,
                success = r.Success,
                actionTaken = r.ActionTaken,
                elapsedSeconds = r.ElapsedSeconds,
                error = r.Error,
            }).ToArray(),
        };
    }

    private static void AddSources(TriggerSyncRequest grpcRequest, string[]? sources)
    {
        if (sources is null) return;
        foreach (string s in sources)
            grpcRequest.Sources.Add(s.ToLowerInvariant());
    }

    private static void AddSources(OrchestratorRebuildIndexRequest grpcRequest, string[]? sources)
    {
        if (sources is null) return;
        foreach (string s in sources)
            grpcRequest.Sources.Add(s.ToLowerInvariant());
    }
}
