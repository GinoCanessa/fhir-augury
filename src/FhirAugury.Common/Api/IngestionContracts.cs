namespace FhirAugury.Common.Api;

/// <summary>Ingestion status for a single source service.</summary>
public record IngestionStatusResponse(
    string Source,
    string Status,
    DateTimeOffset? LastSyncAt,
    int ItemsTotal,
    int ItemsProcessed,
    string? LastError,
    string? SyncSchedule,
    List<IndexStatusInfo> Indexes,
    List<string>? SupportedIndexTypes = null);

/// <summary>Status of a single index within a source service.</summary>
public record IndexStatusInfo(
    string Name,
    string Description,
    bool IsRebuilding,
    DateTimeOffset? LastRebuildStartedAt,
    DateTimeOffset? LastRebuildCompletedAt,
    int RecordCount,
    string? LastError);

/// <summary>Result of a rebuild-from-cache operation.</summary>
public record RebuildResponse(
    bool Success,
    int ItemsLoaded,
    double ElapsedSeconds,
    string? Error);

/// <summary>Result of a rebuild-index operation on a single source.</summary>
public record RebuildIndexResponse(
    bool Success,
    string? ActionTaken,
    double? ElapsedSeconds,
    string? Error);

/// <summary>Aggregated trigger-sync response from the orchestrator.</summary>
public record TriggerSyncResponse(
    string Type,
    List<SourceSyncStatus> Statuses);

/// <summary>Sync status for a single source after a trigger.</summary>
public record SourceSyncStatus(
    string Source,
    string Status,
    string? Message,
    int? ItemsTotal);

/// <summary>Peer ingestion notification sent between source services via the orchestrator.</summary>
public record PeerIngestionNotification(
    string Source,
    string? CompletedAt);

/// <summary>Acknowledgement of a peer ingestion notification.</summary>
public record PeerIngestionAck(
    bool Acknowledged);

/// <summary>Aggregated rebuild-index response from the orchestrator.</summary>
public record OrchestratorRebuildIndexResponse(
    string IndexType,
    List<SourceRebuildStatus> Results);

/// <summary>Rebuild-index result for a single source.</summary>
public record SourceRebuildStatus(
    string Source,
    bool Success,
    string? ActionTaken,
    string? Error);


