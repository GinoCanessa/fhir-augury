using System.Diagnostics.CodeAnalysis;

namespace FhirAugury.Common.Caching;

/// <summary>
/// No-op cache implementation used when caching is disabled.
/// All reads miss, all writes are discarded.
/// </summary>
public sealed class NullResponseCache : IResponseCache
{
    public static readonly NullResponseCache Instance = new();

    public string RootPath => string.Empty;

    public bool TryGet(string source, string key, [NotNullWhen(true)] out Stream? content)
    {
        content = null;
        return false;
    }

    public Task PutAsync(string source, string key, Stream content, CancellationToken ct)
        => Task.CompletedTask;

    public void Remove(string source, string key) { }
    public IEnumerable<string> EnumerateKeys(string source) => [];
    public IEnumerable<string> EnumerateKeys(string source, string subPath) => [];
    public void Clear(string source) { }
    public void ClearAll() { }
    public CacheStats GetStats(string source) => new(source, 0, 0, []);

    public Task<Stream?> TryGetAsync(string source, string key, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);

    public Task RemoveAsync(string source, string key, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearAsync(string source, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearAllAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
