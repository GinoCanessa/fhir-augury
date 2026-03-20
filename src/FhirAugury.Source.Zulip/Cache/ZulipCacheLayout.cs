namespace FhirAugury.Source.Zulip.Cache;

/// <summary>Constants for Zulip cache file layout and naming conventions.</summary>
public static class ZulipCacheLayout
{
    /// <summary>The source name used as the cache subdirectory.</summary>
    public const string SourceName = "zulip";

    /// <summary>Extension for JSON API responses.</summary>
    public const string JsonExtension = "json";

    /// <summary>Returns a per-stream subdirectory name: s{streamId}.</summary>
    public static string StreamDirectory(int zulipStreamId) => $"s{zulipStreamId}";

    /// <summary>Returns the metadata file name for a stream.</summary>
    public static string StreamMetadataFile(int zulipStreamId) => $"_meta_s{zulipStreamId}.json";
}
