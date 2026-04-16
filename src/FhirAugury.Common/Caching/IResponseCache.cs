using System.Diagnostics.CodeAnalysis;

namespace FhirAugury.Common.Caching;

/// <summary>
/// File-system cache for raw API responses.
/// </summary>
public interface IResponseCache
{
    /// <summary>The resolved root path of this cache (empty for null caches).</summary>
    string RootPath { get; }

    /// <summary>
    /// Check whether a cached entry exists. If so, returns a readable stream
    /// positioned at the start of the cached content. Caller must dispose the stream when true.
    /// </summary>
    bool TryGet(string source, string key, [NotNullWhen(true)] out Stream? content);

    /// <summary>
    /// Write a response to the cache. Creates intermediate directories as needed.
    /// </summary>
    Task PutAsync(string source, string key, Stream content, CancellationToken ct);

    /// <summary>Delete a single cached entry.</summary>
    void Remove(string source, string key);

    /// <summary>
    /// Enumerate all cached keys for a source, returned in the correct
    /// ingestion order (oldest → newest for date-based batch sources).
    /// </summary>
    IEnumerable<string> EnumerateKeys(string source);

    /// <summary>
    /// Enumerate all cached keys for a source and sub-path (e.g., a Zulip
    /// stream directory), returned in ingestion order.
    /// </summary>
    IEnumerable<string> EnumerateKeys(string source, string subPath);

    /// <summary>Delete all cached entries for a source.</summary>
    void Clear(string source);

    /// <summary>Delete all cached entries for all sources.</summary>
    void ClearAll();

    /// <summary>
    /// Get cache statistics: total files and total bytes per source.
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <param name="forceRefresh">If true, bypass any cached snapshot and recompute from disk.</param>
    CacheStats GetStats(string source, bool forceRefresh = false);

    /// <summary>Async version of <see cref="TryGet"/>. Returns null on cache miss.</summary>
    Task<Stream?> TryGetAsync(string source, string key, CancellationToken ct = default);

    /// <summary>Async version of <see cref="Remove"/>.</summary>
    Task RemoveAsync(string source, string key, CancellationToken ct = default);

    /// <summary>Async version of <see cref="Clear"/>.</summary>
    Task ClearAsync(string source, CancellationToken ct = default);

    /// <summary>Async version of <see cref="ClearAll"/>.</summary>
    Task ClearAllAsync(CancellationToken ct = default);
}
