using Fhiraugury;
using FhirAugury.Orchestrator.CrossRef;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using FhirAugury.Orchestrator.Workers;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Api;

/// <summary>
/// Implements the OrchestratorService gRPC contract.
/// </summary>
public class OrchestratorGrpcService(
    OrchestratorServices services,
    ILogger<OrchestratorGrpcService> logger)
    : OrchestratorService.OrchestratorServiceBase
{
    private readonly UnifiedSearchService searchService = services.SearchService;
    private readonly RelatedItemFinder relatedFinder = services.RelatedFinder;
    private readonly OrchestratorDatabase database = services.Database;
    private readonly SourceRouter router = services.Router;
    private readonly ServiceHealthMonitor healthMonitor = services.HealthMonitor;
    private readonly CrossRefLinker crossRefLinker = services.CrossRefLinker;
    private readonly XRefScanWorker xrefScanWorker = services.XRefScanWorker;
    public override async Task<SearchResponse> UnifiedSearch(UnifiedSearchRequest request, ServerCallContext context)
    {
        var (results, warnings) = await searchService.SearchAsync(
            request.Query,
            request.Sources.Count > 0 ? request.Sources.ToList() : null,
            request.Limit,
            context.CancellationToken);

        var response = new SearchResponse { Query = request.Query };
        response.Warnings.AddRange(warnings);

        foreach (var item in results)
        {
            var resultItem = new SearchResultItem
            {
                Source = item.Source,
                Id = item.Id,
                Title = item.Title,
                Snippet = item.Snippet,
                Score = item.Score,
                Url = item.Url,
            };
            if (item.UpdatedAt is DateTimeOffset updatedAt)
                resultItem.UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt);
            foreach (var (k, v) in item.Metadata)
                resultItem.Metadata[k] = v;

            response.Results.Add(resultItem);
        }

        response.TotalResults = response.Results.Count;
        return response;
    }

    public override async Task<FindRelatedResponse> FindRelated(FindRelatedRequest request, ServerCallContext context)
    {
        return await relatedFinder.FindRelatedAsync(
            request.Source,
            request.Id,
            request.Limit,
            request.TargetSources.Count > 0 ? request.TargetSources.ToList() : null,
            context.CancellationToken);
    }

    public override Task<GetXRefResponse> GetCrossReferences(GetXRefRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var response = new GetXRefResponse();

        var direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (direction is "outgoing" or "both")
        {
            var outgoing = CrossRefLinkRecord.SelectList(connection,
                SourceType: request.Source, SourceId: request.Id);
            foreach (var link in outgoing)
            {
                response.References.Add(new CrossReference
                {
                    SourceType = link.SourceType,
                    SourceId = link.SourceId,
                    TargetType = link.TargetType,
                    TargetId = link.TargetId,
                    LinkType = link.LinkType,
                    Context = link.Context ?? "",
                });
            }
        }

        if (direction is "incoming" or "both")
        {
            var incoming = CrossRefLinkRecord.SelectList(connection,
                TargetType: request.Source, TargetId: request.Id);
            foreach (var link in incoming)
            {
                response.References.Add(new CrossReference
                {
                    SourceType = link.SourceType,
                    SourceId = link.SourceId,
                    TargetType = link.TargetType,
                    TargetId = link.TargetId,
                    LinkType = link.LinkType,
                    Context = link.Context ?? "",
                });
            }
        }

        return Task.FromResult(response);
    }

    public override async Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        var source = !string.IsNullOrEmpty(request.SourceName)
            ? request.SourceName
            : context.RequestHeaders.GetValue("x-source") ?? "";
        var client = router.GetSourceClient(source);
        if (client is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Source '{source}' not found or disabled"));

        return await client.GetItemAsync(request, cancellationToken: context.CancellationToken);
    }

    public override async Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        var source = !string.IsNullOrEmpty(request.SourceName)
            ? request.SourceName
            : context.RequestHeaders.GetValue("x-source") ?? "";
        var client = router.GetSourceClient(source);
        if (client is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Source '{source}' not found or disabled"));

        return await client.GetSnapshotAsync(request, cancellationToken: context.CancellationToken);
    }

    public override async Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        var source = !string.IsNullOrEmpty(request.SourceName)
            ? request.SourceName
            : context.RequestHeaders.GetValue("x-source") ?? "";
        var client = router.GetSourceClient(source);
        if (client is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Source '{source}' not found or disabled"));

        return await client.GetContentAsync(request, cancellationToken: context.CancellationToken);
    }

    public override async Task<TriggerSyncResponse> TriggerSync(TriggerSyncRequest request, ServerCallContext context)
    {
        var response = new TriggerSyncResponse();
        var targetSources = request.Sources.Count > 0
            ? request.Sources.ToList()
            : router.GetEnabledSources().ToList();

        foreach (var sourceName in targetSources)
        {
            var client = router.GetSourceClient(sourceName);
            if (client is null)
            {
                response.Statuses.Add(new SourceSyncStatus
                {
                    Source = sourceName,
                    Status = "error",
                    Message = "Source not configured or disabled",
                });
                continue;
            }

            try
            {
                var ingestionRequest = new TriggerIngestionRequest { Type = request.Type };
                var result = await client.TriggerIngestionAsync(ingestionRequest,
                    cancellationToken: context.CancellationToken);

                response.Statuses.Add(new SourceSyncStatus
                {
                    Source = sourceName,
                    Status = result.Status,
                    Message = $"Items: {result.ItemsTotal}",
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TriggerSync failed for {Source}", sourceName);
                response.Statuses.Add(new SourceSyncStatus
                {
                    Source = sourceName,
                    Status = "error",
                    Message = ex.Message,
                });
            }
        }

        return response;
    }

    public override async Task<ServicesStatusResponse> GetServicesStatus(
        ServicesStatusRequest request, ServerCallContext context)
    {
        await healthMonitor.CheckAllAsync(context.CancellationToken);

        using var connection = database.OpenConnection();
        var linkCount = CrossRefLinkRecord.SelectCount(connection);
        var scanState = XrefScanStateRecord.SelectList(connection);
        var lastScan = scanState.OrderByDescending(s => s.LastScanAt).FirstOrDefault();

        var response = new ServicesStatusResponse
        {
            CrossRefLinks = linkCount,
        };

        if (lastScan is not null)
            response.LastXrefScanAt = Timestamp.FromDateTimeOffset(lastScan.LastScanAt);

        var healthStatus = healthMonitor.GetCurrentStatus();
        foreach (var (name, info) in healthStatus)
        {
            var health = new ServiceHealth
            {
                Name = info.Name,
                Status = info.Status,
                GrpcAddress = info.GrpcAddress,
                ItemCount = info.ItemCount,
                DbSizeBytes = info.DbSizeBytes,
                LastError = info.LastError ?? "",
            };
            if (info.LastSyncAt is DateTimeOffset lastSync)
                health.LastSyncAt = Timestamp.FromDateTimeOffset(lastSync);

            response.Services.Add(health);
        }

        return response;
    }

    public override async Task<TriggerXRefScanResponse> TriggerXRefScan(
        TriggerXRefScanRequest request, ServerCallContext context)
    {
        var newLinks = await crossRefLinker.ScanAllSourcesAsync(request.FullRescan, context.CancellationToken);

        return new TriggerXRefScanResponse
        {
            Status = "completed",
            ItemsToScan = newLinks,
        };
    }

    public override Task<IngestionCompleteAck> NotifyIngestionComplete(
        IngestionCompleteNotification request, ServerCallContext context)
    {
        logger.LogInformation(
            "Ingestion complete notification from {Source}: {Type}, {Items} items",
            request.Source, request.Type, request.ItemsIngested);

        // Trigger a cross-reference scan for newly ingested items
        xrefScanWorker.RequestScan();

        return Task.FromResult(new IngestionCompleteAck
        {
            XrefScanTriggered = true,
        });
    }
}
