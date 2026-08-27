using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Fetches one Confluence URL and returns its response body. The single
/// injectable seam that makes discovery and the sweep testable offline.
/// </summary>
public delegate Task<string> ConfluenceFetch(string url, CancellationToken ct);

/// <summary>
/// Enumerates the spaces this service tracks and records them in a durable
/// catalog.
/// </summary>
/// <remarks>
/// Per-space manifests can never answer a question about a space they do not
/// mention, so without the catalog a space that is later archived — or simply
/// dropped from an explicit <c>Spaces</c> list — would keep its stale manifest
/// and replay forever.
/// </remarks>
public class ConfluenceSpaceDiscovery
{
    private readonly ConfluenceServiceOptions _options;
    private readonly IResponseCache _cache;
    private readonly ILogger<ConfluenceSpaceDiscovery> _logger;
    private readonly ConfluenceFetch _fetch;

    public ConfluenceSpaceDiscovery(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        IHttpClientFactory httpClientFactory,
        IResponseCache cache,
        ILogger<ConfluenceSpaceDiscovery> logger)
        : this(optionsAccessor, cache, logger,
            ConfluenceHttp.CreateFetch(httpClientFactory, optionsAccessor.Value))
    {
    }

    /// <summary>Test seam: supply the fetch directly.</summary>
    public ConfluenceSpaceDiscovery(
        IOptions<ConfluenceServiceOptions> optionsAccessor,
        IResponseCache cache,
        ILogger<ConfluenceSpaceDiscovery> logger,
        ConfluenceFetch fetch)
    {
        _options = optionsAccessor.Value;
        _cache = cache;
        _logger = logger;
        _fetch = fetch;
    }

    /// <summary>
    /// Resolves the tracked space set and writes the catalog, which is persisted
    /// <b>only</b> when enumeration ran to exhaustion.
    /// </summary>
    public async Task<ConfluenceSpaceCatalog> DiscoverAsync(CancellationToken ct)
    {
        // Empty-as-explicit: track nothing, but still record that decision on
        // disk so the tracked set is always answerable without configuration.
        if (_options.HasExplicitEmptySpaces)
        {
            _logger.LogInformation("Confluence Spaces is explicitly empty; tracking no spaces");
            return await WriteCatalogAsync(new ConfluenceSpaceCatalog
            {
                DiscoveredAt = DateTimeOffset.UtcNow,
                Complete = true,
                Spaces = [],
            }, ct);
        }

        List<ConfluenceCatalogedSpace> spaces = _options.Spaces is { Count: > 0 } configured
            ? await FetchNamedSpacesAsync(configured, ct)
            : await EnumerateAllSpacesAsync(ct);

        return await WriteCatalogAsync(new ConfluenceSpaceCatalog
        {
            DiscoveredAt = DateTimeOffset.UtcNow,
            Complete = true,
            Spaces = spaces,
        }, ct);
    }

    private async Task<List<ConfluenceCatalogedSpace>> EnumerateAllSpacesAsync(CancellationToken ct)
    {
        // status=current is honoured by this instance and archived spaces are
        // out of scope; type=global excludes personal spaces.
        string url = $"{_options.BaseUrl}/rest/api/space" +
                     $"?type=global&status=current&limit={_options.SweepPageSize}";

        List<ConfluenceCatalogedSpace> spaces = [];

        await foreach (JsonElement element in EnumerateAsync(url, ct))
        {
            string? key = JsonElementHelper.GetString(element, "key");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            spaces.Add(new ConfluenceCatalogedSpace
            {
                Key = key,
                Name = JsonElementHelper.GetString(element, "name") ?? key,
            });

            await CacheSpaceAsync(key, element, ct);
        }

        _logger.LogInformation("Confluence discovery found {Count} non-archived global spaces", spaces.Count);
        return spaces;
    }

    private async Task<List<ConfluenceCatalogedSpace>> FetchNamedSpacesAsync(
        List<string> keys, CancellationToken ct)
    {
        List<ConfluenceCatalogedSpace> spaces = [];

        foreach (string key in keys)
        {
            ct.ThrowIfCancellationRequested();

            string json;
            try
            {
                json = await _fetch($"{_options.BaseUrl}/rest/api/space/{Uri.EscapeDataString(key)}", ct);
            }
            catch (Exception ex)
            {
                ConfluenceAuthFailure.ThrowIfAuthFailure(ex);
                _logger.LogWarning(ex, "Failed to fetch metadata for configured space {SpaceKey}", key);

                // A configured space stays tracked even when its metadata call
                // fails; dropping it would silently tombstone everything in it.
                spaces.Add(new ConfluenceCatalogedSpace { Key = key, Name = key });
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            spaces.Add(new ConfluenceCatalogedSpace
            {
                Key = key,
                Name = JsonElementHelper.GetString(document.RootElement, "name") ?? key,
            });

            await CacheSpaceAsync(key, document.RootElement, ct);
        }

        return spaces;
    }

    private async IAsyncEnumerable<JsonElement> EnumerateAsync(
        string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (JsonElement element in ConfluenceHttp.EnumerateResultsAsync(_fetch, _options.BaseUrl, url, ct))
        {
            yield return element;
        }
    }

    private async Task CacheSpaceAsync(string key, JsonElement element, CancellationToken ct)
    {
        // Cached so replay can reconstruct ConfluenceSpaceRecord exactly rather
        // than guessing at a name and description.
        byte[] bytes = Encoding.UTF8.GetBytes(element.GetRawText());
        using MemoryStream stream = new(bytes);
        await _cache.PutAsync(ConfluenceCacheLayout.SourceName, ConfluenceCacheLayout.GetSpaceCacheKey(key), stream, ct);
    }

    private async Task<ConfluenceSpaceCatalog> WriteCatalogAsync(ConfluenceSpaceCatalog catalog, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(catalog.ToJson());
        using MemoryStream stream = new(bytes);
        await _cache.PutAsync(
            ConfluenceCacheLayout.SourceName, ConfluenceCacheLayout.GetSpaceCatalogCacheKey(), stream, ct);
        return catalog;
    }
}

/// <summary>Shared HTTP plumbing for discovery and the sweep.</summary>
internal static class ConfluenceHttp
{
    /// <summary>The production fetch: the rate-limited "confluence" client.</summary>
    public static ConfluenceFetch CreateFetch(
        IHttpClientFactory httpClientFactory, ConfluenceServiceOptions options) =>
        async (url, ct) =>
        {
            HttpClient client = httpClientFactory.CreateClient("confluence");
            using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                client, url, ct, options.RateLimiting.MaxRetries, ConfluenceCacheLayout.SourceName);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        };

    /// <summary>
    /// Walks a paginated Confluence collection to exhaustion via
    /// <c>_links.next</c>, which this instance returns as a site-relative path.
    /// </summary>
    public static async IAsyncEnumerable<JsonElement> EnumerateResultsAsync(
        ConfluenceFetch fetch,
        string baseUrl,
        string startUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = startUrl;

        while (!string.IsNullOrEmpty(next))
        {
            ct.ThrowIfCancellationRequested();

            string json = await fetch(Absolute(baseUrl, next), ct);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("results", out JsonElement results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in results.EnumerateArray())
                {
                    yield return element.Clone();
                }
            }

            next = root.TryGetProperty("_links", out JsonElement links)
                && links.TryGetProperty("next", out JsonElement nextLink)
                && nextLink.ValueKind == JsonValueKind.String
                ? nextLink.GetString()
                : null;
        }
    }

    private static string Absolute(string baseUrl, string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{baseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
}
