namespace FhirAugury.Models.Caching;

/// <summary>
/// Controls how a data source interacts with the file-system cache.
/// </summary>
public enum CacheMode
{
    /// <summary>No caching — always fetch from API (current behaviour).</summary>
    Disabled,

    /// <summary>Read from cache if fresh → otherwise fetch from API → write to cache.</summary>
    WriteThrough,

    /// <summary>Read from cache only — no network calls, no credentials required.</summary>
    CacheOnly,

    /// <summary>Always fetch from API → write to cache (build cache without using it).</summary>
    WriteOnly,
}
