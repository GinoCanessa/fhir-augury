using FhirAugury.Common;

namespace FhirAugury.Source.Confluence.Cache;

/// <summary>Constants for Confluence cache file layout and naming conventions.</summary>
public static class ConfluenceCacheLayout
{
    /// <summary>The source name used as the cache subdirectory.</summary>
    public const string SourceName = SourceSystems.Confluence;

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Metadata file name for Confluence cache state.</summary>
    public const string MetadataFileName = "_meta_confluence.json";

    /// <summary>Gets the cache key for a page within a space.</summary>
    public static string GetPageCacheKey(string spaceKey, string pageId)
        => $"spaces/{spaceKey}/{pageId}.{JsonExtension}";

    /// <summary>Gets the cache key for space metadata.</summary>
    public static string GetSpaceCacheKey(string spaceKey)
        => $"spaces/{spaceKey}.{JsonExtension}";
}
