using System.Text.Json.Serialization;

namespace FhirAugury.Common.Caching;

/// <summary>Common interface for source cache metadata with shared sync tracking properties.</summary>
public interface ICacheMetadata
{
    /// <summary>Human-readable last sync date string.</summary>
    string? LastSyncDate { get; }

    /// <summary>Precise timestamp of the last sync.</summary>
    DateTimeOffset? LastSyncTimestamp { get; }
}

/// <summary>Sync metadata for Jira cache.</summary>
public record JiraCacheMetadata : ICacheMetadata
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

/// <summary>Sync metadata for a single Zulip stream.</summary>
public record ZulipStreamCacheMetadata : ICacheMetadata
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

/// <summary>Sync metadata for Confluence cache.</summary>
public record ConfluenceCacheMetadata : ICacheMetadata
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
