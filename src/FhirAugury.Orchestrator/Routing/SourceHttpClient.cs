using System.Net.Http.Json;
using System.Text;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Routing;

/// <summary>
/// Routes proxied calls to source services via named HttpClients.
/// </summary>
public class SourceHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SourceHttpClient> _logger;

    public SourceHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OrchestratorOptions> options,
        ILogger<SourceHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<string> GetEnabledSourceNames() => _options.Services
        .Where(s => s.Value.Enabled)
        .Select(s => s.Key)
        .ToList();

    public bool IsSourceEnabled(string name) =>
        _options.Services.TryGetValue(name, out SourceServiceConfig? cfg) && cfg.Enabled;

    public SourceServiceConfig? GetSourceConfig(string name) =>
        _options.Services.TryGetValue(name, out SourceServiceConfig? cfg) ? cfg : null;

    private HttpClient GetClientForSource(string sourceName)
    {
        return _httpClientFactory.CreateClient($"source-{sourceName.ToLowerInvariant()}");
    }

    // ── Generic proxy ───────────────────────────────────────────────────

    private static readonly HashSet<string> s_forwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "If-None-Match",
        "If-Modified-Since",
        "Range",
        "User-Agent",
    };

    private const string AuguryHeaderPrefix = "X-Augury-";

    private static bool IsForwardedRequestHeader(string name) =>
        s_forwardedRequestHeaders.Contains(name)
        || name.StartsWith(AuguryHeaderPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool MethodAllowsBody(HttpMethod method) =>
        method == HttpMethod.Post
        || method == HttpMethod.Put
        || method == HttpMethod.Patch
        || method == HttpMethod.Delete;

    /// <summary>
    /// Forwards an arbitrary HTTP request to the named source service at
    /// <c>/api/v1/{rest}</c>. Streams the request and response bodies; the
    /// caller is responsible for relaying the returned <see cref="HttpResponseMessage"/>
    /// to the original client.
    /// </summary>
    public async Task<HttpResponseMessage> ForwardAsync(
        string sourceName, HttpRequest request, string rest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(request);
        rest ??= string.Empty;

        HttpClient client = GetClientForSource(sourceName);

        string path = $"/api/v1/{rest}";
        string? query = request.QueryString.Value;
        string targetUri = string.IsNullOrEmpty(query) ? path : path + query;

        HttpMethod method = new(request.Method);
        HttpRequestMessage forward = new(method, targetUri);

        if (MethodAllowsBody(method))
        {
            StreamContent content = new(request.Body);
            string? contentType = request.ContentType;
            if (!string.IsNullOrEmpty(contentType))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (request.ContentLength is long len)
            {
                content.Headers.ContentLength = len;
            }
            forward.Content = content;
        }

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in request.Headers)
        {
            if (!IsForwardedRequestHeader(header.Key))
            {
                continue;
            }

            string[] values = header.Value.ToArray()!;
            if (!forward.Headers.TryAddWithoutValidation(header.Key, values)
                && forward.Content is not null)
            {
                forward.Content.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }

        return await client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static string ItemBase() => "items";

    private static string EncodeId(string source, string id)
    {
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return id.Replace("#", "%23");

        return Uri.EscapeDataString(id);
    }

    private static string BuildItemPath(string source, string id) =>
        $"{ItemBase()}/{EncodeId(source, id)}";

    private static string BuildSubItemPath(string source, string id, string action)
    {
        string encoded = EncodeId(source, id);
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return $"items/{action}/{encoded}";

        return $"{ItemBase()}/{encoded}/{action}";
    }

    public async Task<SearchResponse?> SearchAsync(string sourceName, string query, int limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        return await client.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
    }

    public async Task<ItemResponse?> GetItemAsync(
        string sourceName, string id, CancellationToken ct,
        bool includeContent = false, bool includeComments = false)
    {
        HttpClient client = GetClientForSource(sourceName);
        string path = BuildItemPath(sourceName, id);
        string query = "";
        if (includeContent) query += "includeContent=true&";
        if (includeComments) query += "includeComments=true&";
        string url = string.IsNullOrEmpty(query)
            ? $"/api/v1/{path}"
            : $"/api/v1/{path}?{query.TrimEnd('&')}";
        return await client.GetFromJsonAsync<ItemResponse>(url, ct);
    }

    public async Task<FindRelatedResponse?> GetRelatedAsync(
        string sourceName, string seedSource, string seedId, int limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string path = BuildSubItemPath(sourceName, seedId, "related");
        return await client.GetFromJsonAsync<FindRelatedResponse>(
            $"/api/v1/{path}?limit={limit}&seedSource={Uri.EscapeDataString(seedSource)}&seedId={Uri.EscapeDataString(seedId)}", ct);
    }

    public async Task<SnapshotResponse?> GetSnapshotAsync(string sourceName, string id, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string path = BuildSubItemPath(sourceName, id, "snapshot");
        return await client.GetFromJsonAsync<SnapshotResponse>($"/api/v1/{path}", ct);
    }

    public async Task<ContentResponse?> GetContentAsync(string sourceName, string id, string format, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string path = BuildSubItemPath(sourceName, id, "content");
        return await client.GetFromJsonAsync<ContentResponse>(
            $"/api/v1/{path}?format={Uri.EscapeDataString(format)}", ct);
    }

    public async Task<CrossReferenceResponse?> GetCrossReferencesAsync(
        string sourceName, string id, string source, string direction, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        return await client.GetFromJsonAsync<CrossReferenceResponse>(
            $"/api/v1/xref/{Uri.EscapeDataString(id)}?source={Uri.EscapeDataString(source)}&direction={Uri.EscapeDataString(direction)}", ct);
    }

    public async Task<StatsResponse?> GetStatsAsync(string sourceName, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        return await client.GetFromJsonAsync<StatsResponse>("/api/v1/stats", ct);
    }

    public async Task<HealthCheckResponse?> HealthCheckAsync(string sourceName, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        return await client.GetFromJsonAsync<HealthCheckResponse>("/api/v1/health", ct);
    }

    public async Task<IngestionStatusResponse?> GetIngestionStatusAsync(string sourceName, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        return await client.GetFromJsonAsync<IngestionStatusResponse>("/api/v1/status", ct);
    }

    public async Task<IngestionStatusResponse?> TriggerIngestionAsync(
        string sourceName, string type, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/ingest?type={Uri.EscapeDataString(type)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionStatusResponse>(ct);
    }

    public async Task<PeerIngestionAck?> NotifyPeerAsync(
        string sourceName, PeerIngestionNotification notification, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/notify-peer", notification, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PeerIngestionAck>(ct);
    }

    public async Task<RebuildIndexResponse?> RebuildIndexAsync(
        string sourceName, string indexType, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RebuildIndexResponse>(ct);
    }

    // ── Content Query API ───────────────────────────────────────────────

    public async Task<CrossReferenceQueryResponse?> ContentRefersToAsync(
        string sourceName, string value, string? sourceType, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        StringBuilder url = new($"/api/v1/content/refers-to?value={Uri.EscapeDataString(value)}");
        if (!string.IsNullOrEmpty(sourceType)) url.Append($"&sourceType={Uri.EscapeDataString(sourceType)}");
        if (limit.HasValue) url.Append($"&limit={limit.Value}");
        return await client.GetFromJsonAsync<CrossReferenceQueryResponse>(url.ToString(), ct);
    }

    public async Task<CrossReferenceQueryResponse?> ContentReferredByAsync(
        string sourceName, string value, string? sourceType, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        StringBuilder url = new($"/api/v1/content/referred-by?value={Uri.EscapeDataString(value)}");
        if (!string.IsNullOrEmpty(sourceType)) url.Append($"&sourceType={Uri.EscapeDataString(sourceType)}");
        if (limit.HasValue) url.Append($"&limit={limit.Value}");
        return await client.GetFromJsonAsync<CrossReferenceQueryResponse>(url.ToString(), ct);
    }

    public async Task<CrossReferenceQueryResponse?> ContentCrossReferencedAsync(
        string sourceName, string value, string? sourceType, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        StringBuilder url = new($"/api/v1/content/cross-referenced?value={Uri.EscapeDataString(value)}");
        if (!string.IsNullOrEmpty(sourceType)) url.Append($"&sourceType={Uri.EscapeDataString(sourceType)}");
        if (limit.HasValue) url.Append($"&limit={limit.Value}");
        return await client.GetFromJsonAsync<CrossReferenceQueryResponse>(url.ToString(), ct);
    }

    public async Task<ContentSearchResponse?> ContentSearchAsync(
        string sourceName, List<string> values, List<string>? sources, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        StringBuilder url = new("/api/v1/content/search?");
        foreach (string v in values)
            url.Append($"values={Uri.EscapeDataString(v)}&");
        if (sources is { Count: > 0 })
        {
            foreach (string s in sources)
                url.Append($"sources={Uri.EscapeDataString(s)}&");
        }
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await client.GetFromJsonAsync<ContentSearchResponse>(url.ToString().TrimEnd('&'), ct);
    }

    public async Task<ContentItemResponse?> ContentGetItemAsync(
        string sourceName, string source, string id,
        bool includeContent, bool includeComments, bool includeSnapshot, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string encodedId = EncodeId(source, id);
        StringBuilder url = new($"/api/v1/content/item/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (includeContent) url.Append("includeContent=true&");
        if (includeComments) url.Append("includeComments=true&");
        if (includeSnapshot) url.Append("includeSnapshot=true&");
        return await client.GetFromJsonAsync<ContentItemResponse>(url.ToString().TrimEnd('&', '?'), ct);
    }

    public async Task<KeywordListResponse?> ContentKeywordsAsync(
        string sourceName, string source, string id,
        string? keywordType, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string encodedId = EncodeId(source, id);
        StringBuilder url = new($"/api/v1/content/keywords/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (!string.IsNullOrEmpty(keywordType)) url.Append($"keywordType={Uri.EscapeDataString(keywordType)}&");
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await client.GetFromJsonAsync<KeywordListResponse>(url.ToString().TrimEnd('&', '?'), ct);
    }

    public async Task<RelatedByKeywordResponse?> ContentRelatedByKeywordAsync(
        string sourceName, string source, string id,
        double? minScore, string? keywordType, int? limit, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string encodedId = EncodeId(source, id);
        StringBuilder url = new($"/api/v1/content/related-by-keyword/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (minScore.HasValue) url.Append($"minScore={minScore.Value}&");
        if (!string.IsNullOrEmpty(keywordType)) url.Append($"keywordType={Uri.EscapeDataString(keywordType)}&");
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await client.GetFromJsonAsync<RelatedByKeywordResponse>(url.ToString().TrimEnd('&', '?'), ct);
    }
}
