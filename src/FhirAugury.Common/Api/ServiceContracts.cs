namespace FhirAugury.Common.Api;

/// <summary>Statistics from a single source service.</summary>
public record StatsResponse
{
    public required string Source { get; init; }
    public int TotalItems { get; init; }
    public int TotalComments { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public long CacheSizeBytes { get; init; }
    public int CacheFiles { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    public DateTimeOffset? OldestItem { get; init; }
    public DateTimeOffset? NewestItem { get; init; }
    public Dictionary<string, int>? AdditionalCounts { get; init; }
}

/// <summary>Health check response from a service.</summary>
public record HealthCheckResponse(
    string Status,
    string? Version,
    double UptimeSeconds,
    string? Message);

/// <summary>Aggregated services status from the orchestrator.</summary>
public record ServicesStatusResponse(
    List<ServiceHealthInfo> Services);

/// <summary>Health and status of a single source service.</summary>
public record ServiceHealthInfo
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? HttpAddress { get; init; }
    public double? UptimeSeconds { get; init; }
    public string? Version { get; init; }
    public int ItemCount { get; init; }
    public long DbSizeBytes { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    public string? LastError { get; init; }
    public List<IndexStatusInfo>? Indexes { get; init; }
}

/// <summary>Available service endpoints from the orchestrator.</summary>
public record ServiceEndpointsResponse(
    List<ServiceEndpointInfo> Endpoints);

/// <summary>A single service endpoint descriptor.</summary>
public record ServiceEndpointInfo(
    string Name,
    string HttpAddress,
    bool Enabled);
