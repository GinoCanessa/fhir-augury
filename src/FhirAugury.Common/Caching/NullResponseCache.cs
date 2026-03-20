namespace FhirAugury.Common.Caching;

/// <summary>
/// No-op cache implementation used when caching is disabled.
/// All reads miss, all writes are discarded.
/// </summary>
public sealed class NullResponseCache : IResponseCache
{
    public static readonly NullResponseCache Instance = new();

    public string RootPath => string.Empty;

    public bool TryGet(string source, string key, out Stream content)
    {
        content = Stream.Null;
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
}
