using System.ComponentModel.DataAnnotations;

namespace FhirAugury.Common.Caching;

/// <summary>Top-level cache configuration.</summary>
public class CacheConfiguration
{
    /// <summary>Root directory for all source caches.</summary>
    [Required]
    public string RootPath { get; set; } = "./cache";

    /// <summary>Default cache mode for all sources.</summary>
    public CacheMode DefaultMode { get; set; } = CacheMode.WriteThrough;
}

/// <summary>Per-source cache configuration override.</summary>
public class SourceCacheConfiguration
{
    /// <summary>Cache mode override for this source.</summary>
    public CacheMode? Mode { get; set; }

    /// <summary>
    /// Override the cache subdirectory for this source. When null, uses
    /// the source name as the subdirectory (e.g., "jira", "zulip").
    /// </summary>
    public string? Path { get; set; }
}
