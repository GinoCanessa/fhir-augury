namespace FhirAugury.Common.Caching;

/// <summary>Per-source cache statistics.</summary>
public record CacheStats(
    string Source,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> SubPaths);
