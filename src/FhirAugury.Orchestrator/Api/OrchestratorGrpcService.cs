using Fhiraugury;
using FhirAugury.Common.Grpc;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Related;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
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
        GetXRefResponse response = new GetXRefResponse();

        List<Task<GetItemXRefResponse>> tasks = [];
        foreach (string source in router.GetEnabledSources())
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null) continue;

            tasks.Add(client.GetItemCrossReferencesAsync(new GetItemXRefRequest
            {
                Source = request.Source,
                Id = request.Id,
                Direction = request.Direction ?? "both",
            }, cancellationToken: ct).ResponseAsync);
        }

        foreach (Task<GetItemXRefResponse> task in tasks)
        {
            try
            {
                GetItemXRefResponse result = await task;
                foreach (SourceCrossReference xref in result.References)
                {
                    response.References.Add(new CrossReference
                    {
                        SourceType = xref.SourceType,
                        SourceId = xref.SourceId,
                        TargetType = xref.TargetType,
                        TargetId = xref.TargetId,
                        LinkType = xref.LinkType,
                        Context = xref.Context,
                        TargetTitle = xref.SourceTitle,
                        TargetUrl = xref.SourceUrl,
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GetItemCrossReferences failed for a source");
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
        List<XrefScanStateRecord> scanState = XrefScanStateRecord.SelectList(connection);
        XrefScanStateRecord? lastScan = scanState.OrderByDescending(s => s.LastScanAt).FirstOrDefault();

        ServicesStatusResponse response = new ServicesStatusResponse();

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

            foreach (ServiceIndexInfo idx in info.Indexes)
            {
                IndexStatus indexStatus = new IndexStatus
                {
                    Name = idx.Name,
                    Description = idx.Description,
                    IsRebuilding = idx.IsRebuilding,
                    RecordCount = idx.RecordCount,
                    LastError = idx.LastError ?? "",
                };
                if (idx.LastRebuildStartedAt is DateTimeOffset started)
                    indexStatus.LastRebuildStartedAt = Timestamp.FromDateTimeOffset(started);
                if (idx.LastRebuildCompletedAt is DateTimeOffset completed)
                    indexStatus.LastRebuildCompletedAt = Timestamp.FromDateTimeOffset(completed);
                health.Indexes.Add(indexStatus);
            }

            response.Services.Add(health);
        }

        return response;
    }

    public override async Task<IngestionCompleteAck> NotifyIngestionComplete(
        IngestionCompleteNotification request, ServerCallContext context)
    {
        logger.LogInformation(
            "Ingestion complete from {Source}: {Type}, {Items} items",
            request.Source, request.Type, request.ItemsIngested);

        using SqliteConnection connection = database.OpenConnection();
        XrefScanStateRecord? existing = XrefScanStateRecord.SelectSingle(
            connection, SourceName: request.Source);

        XrefScanStateRecord record = new XrefScanStateRecord
        {
            Id = existing?.Id ?? XrefScanStateRecord.GetIndex(),
            SourceName = request.Source,
            LastCursor = null,
            LastScanAt = request.CompletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
        };

        if (existing is not null)
            XrefScanStateRecord.Update(connection, record);
        else
            XrefScanStateRecord.Insert(connection, record);

        // Fan out to all OTHER sources
        PeerIngestionNotification peerNotification = new()
        {
            Source = request.Source,
            Type = request.Type,
            ItemsIngested = request.ItemsIngested,
        };

        List<string> targets = router.GetEnabledSources()
            .Where(s => !s.Equals(request.Source, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Task[] fanOutTasks = targets.Select(async targetSource =>
        {
            try
            {
                SourceService.SourceServiceClient? client = router.GetSourceClient(targetSource);
                if (client is not null)
                    await client.NotifyPeerIngestionCompleteAsync(peerNotification);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to notify {Target} of {Source} ingestion",
                    targetSource, request.Source);
            }
        }).ToArray();

        await Task.WhenAll(fanOutTasks);

        return new IngestionCompleteAck { Acknowledged = true };
    }

    public override async Task<OrchestratorRebuildIndexResponse> RebuildIndex(
        OrchestratorRebuildIndexRequest request, ServerCallContext context)
    {
        List<string> targets = request.Sources.Count > 0
            ? request.Sources.ToList()
            : router.GetEnabledSources().ToList();

        RebuildIndexRequest sourceRequest = new() { IndexType = request.IndexType };
        List<SourceRebuildIndexStatus> results = [];

        await Task.WhenAll(targets.Select(async source =>
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null)
            {
                lock (results) results.Add(new SourceRebuildIndexStatus
                    { Source = source, Success = false, Error = "source not found" });
                return;
            }

            try
            {
                RebuildIndexResponse resp = await client.RebuildIndexAsync(sourceRequest);
                lock (results) results.Add(new SourceRebuildIndexStatus
                {
                    Source = source, Success = resp.Success,
                    ActionTaken = resp.ActionTaken, ElapsedSeconds = resp.ElapsedSeconds,
                });
            }
            catch (Exception ex)
            {
                lock (results) results.Add(new SourceRebuildIndexStatus
                    { Source = source, Success = false, Error = ex.Message });
            }
        }));

        OrchestratorRebuildIndexResponse response = new();
        response.Results.AddRange(results);
        return response;
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
