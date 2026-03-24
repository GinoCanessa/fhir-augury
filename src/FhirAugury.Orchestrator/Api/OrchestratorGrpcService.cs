using Fhiraugury;
using FhirAugury.Common.Grpc;
using FhirAugury.Orchestrator.Configuration;
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
using Microsoft.Data.Sqlite;
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
    public override Task<SearchResponse> UnifiedSearch(UnifiedSearchRequest request, ServerCallContext context)
    {
        return GrpcErrorMapper.HandleAsync(async () =>
        {
            (List<ScoredItem>? results, List<string>? warnings) = await searchService.SearchAsync(
                request.Query,
                request.Sources.Count > 0 ? request.Sources.ToList() : null,
                request.Limit,
                context.CancellationToken);

            SearchResponse response = new SearchResponse { Query = request.Query };
            response.Warnings.AddRange(warnings);

            foreach (ScoredItem item in results)
            {
                SearchResultItem resultItem = new SearchResultItem
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
                foreach ((string? k, string? v) in item.Metadata)
                    resultItem.Metadata[k] = v;

                response.Results.Add(resultItem);
            }

            response.TotalResults = response.Results.Count;
            return response;
        }, logger, nameof(UnifiedSearch));
    }

    public override Task<FindRelatedResponse> FindRelated(FindRelatedRequest request, ServerCallContext context)
    {
        return GrpcErrorMapper.HandleAsync(() => relatedFinder.FindRelatedAsync(
            request.Source,
            request.Id,
            request.Limit,
            request.TargetSources.Count > 0 ? request.TargetSources.ToList() : null,
            context.CancellationToken), logger, nameof(FindRelated));
    }

    public override async Task<GetXRefResponse> GetCrossReferences(GetXRefRequest request, ServerCallContext context)
    {
        CancellationToken ct = context.CancellationToken;
        using SqliteConnection connection = database.OpenConnection();
        GetXRefResponse response = new GetXRefResponse();

        string direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (direction is "outgoing" or "both")
        {
            List<CrossRefLinkRecord> outgoing = CrossRefLinkRecord.SelectList(connection,
                SourceType: request.Source, SourceId: request.Id);
            foreach (CrossRefLinkRecord link in outgoing)
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
            List<CrossRefLinkRecord> incoming = CrossRefLinkRecord.SelectList(connection,
                TargetType: request.Source, TargetId: request.Id);
            foreach (CrossRefLinkRecord link in incoming)
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

        // Enrich references with target title and URL from source services
        List<(string TargetType, string TargetId)> targets = response.References
            .Select(r => (r.TargetType, r.TargetId))
            .Distinct()
            .ToList();

        Dictionary<(string, string), (string Title, string Url)> lookupCache = new Dictionary<(string, string), (string Title, string Url)>();
        foreach ((string? targetType, string? targetId) in targets)
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(targetType);
            if (client is null) continue;

            try
            {
                ItemResponse item = await client.GetItemAsync(
                    new GetItemRequest { Id = targetId }, cancellationToken: ct);
                lookupCache[(targetType, targetId)] = (item.Title, item.Url);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enrich xref target {TargetType}/{TargetId}", targetType, targetId);
            }
        }

        foreach (CrossReference? reference in response.References)
        {
            if (lookupCache.TryGetValue((reference.TargetType, reference.TargetId), out (string Title, string Url) info))
            {
                reference.TargetTitle = info.Title;
                reference.TargetUrl = info.Url;
            }
        }

        return response;
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        return GrpcErrorMapper.HandleAsync(async () =>
        {
            string source = !string.IsNullOrEmpty(request.SourceName)
                ? request.SourceName
                : context.RequestHeaders.GetValue("x-source") ?? "";
            SourceService.SourceServiceClient client = router.GetSourceClient(source)
                ?? throw new KeyNotFoundException($"Source '{source}' not found or disabled");

            return await client.GetItemAsync(request, cancellationToken: context.CancellationToken);
        }, logger, nameof(GetItem));
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        return GrpcErrorMapper.HandleAsync(async () =>
        {
            string source = !string.IsNullOrEmpty(request.SourceName)
                ? request.SourceName
                : context.RequestHeaders.GetValue("x-source") ?? "";
            SourceService.SourceServiceClient client = router.GetSourceClient(source)
                ?? throw new KeyNotFoundException($"Source '{source}' not found or disabled");

            return await client.GetSnapshotAsync(request, cancellationToken: context.CancellationToken);
        }, logger, nameof(GetSnapshot));
    }

    public override Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        return GrpcErrorMapper.HandleAsync(async () =>
        {
            string source = !string.IsNullOrEmpty(request.SourceName)
                ? request.SourceName
                : context.RequestHeaders.GetValue("x-source") ?? "";
            SourceService.SourceServiceClient client = router.GetSourceClient(source)
                ?? throw new KeyNotFoundException($"Source '{source}' not found or disabled");

            return await client.GetContentAsync(request, cancellationToken: context.CancellationToken);
        }, logger, nameof(GetContent));
    }

    public override async Task<TriggerSyncResponse> TriggerSync(TriggerSyncRequest request, ServerCallContext context)
    {
        TriggerSyncResponse response = new TriggerSyncResponse();
        List<string> targetSources = request.Sources.Count > 0
            ? request.Sources.ToList()
            : router.GetEnabledSources().ToList();

        foreach (string sourceName in targetSources)
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(sourceName);
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
                TriggerIngestionRequest ingestionRequest = new TriggerIngestionRequest { Type = request.Type };
                IngestionStatusResponse result = await client.TriggerIngestionAsync(ingestionRequest,
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

        using SqliteConnection connection = database.OpenConnection();
        int linkCount = CrossRefLinkRecord.SelectCount(connection);
        List<XrefScanStateRecord> scanState = XrefScanStateRecord.SelectList(connection);
        XrefScanStateRecord? lastScan = scanState.OrderByDescending(s => s.LastScanAt).FirstOrDefault();

        ServicesStatusResponse response = new ServicesStatusResponse
        {
            CrossRefLinks = linkCount,
        };

        if (lastScan is not null)
            response.LastXrefScanAt = Timestamp.FromDateTimeOffset(lastScan.LastScanAt);

        Dictionary<string, ServiceHealthInfo> healthStatus = healthMonitor.GetCurrentStatus();
        foreach ((string? name, ServiceHealthInfo? info) in healthStatus)
        {
            ServiceHealth health = new ServiceHealth
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
        int newLinks = await crossRefLinker.ScanAllSourcesAsync(request.FullRescan, context.CancellationToken);

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

    public override Task<ServiceEndpointsResponse> GetServiceEndpoints(
        ServiceEndpointsRequest request, ServerCallContext context)
    {
        ServiceEndpointsResponse response = new ServiceEndpointsResponse();

        foreach (string sourceName in router.GetEnabledSources())
        {
            SourceServiceConfig? config = router.GetSourceConfig(sourceName);
            if (config is null)
                continue;

            response.Endpoints.Add(new ServiceEndpoint
            {
                Name = sourceName,
                GrpcAddress = config.GrpcAddress,
                Enabled = config.Enabled,
            });
        }

        return Task.FromResult(response);
    }
}
