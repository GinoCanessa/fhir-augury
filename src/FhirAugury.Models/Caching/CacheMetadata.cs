using System.Text.Json.Serialization;

namespace FhirAugury.Models.Caching;

/// <summary>
/// Sync metadata for Jira cache. Stored at {CacheRoot}/_meta_jira.json.
/// </summary>
public record JiraCacheMetadata
{
    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "xml";
}

/// <summary>
/// Sync metadata for a single Zulip stream.
/// Stored at {CacheRoot}/zulip/_meta_s{id}.json.
/// </summary>
public record ZulipStreamCacheMetadata
{
    [JsonPropertyName("streamId")]
    public int StreamId { get; init; }

    [JsonPropertyName("streamName")]
    public string? StreamName { get; init; }

    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("initialDownloadComplete")]
    public bool InitialDownloadComplete { get; init; }
}

/// <summary>
/// Sync metadata for Confluence cache. Stored at {CacheRoot}/_meta_confluence.json.
/// </summary>
public record ConfluenceCacheMetadata
{
    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "json";
}
