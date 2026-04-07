using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common.Api;

namespace FhirAugury.DevUi.Services;

public sealed class OrchestratorClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public string Address { get; } = configuration["DevUi:OrchestratorAddress"] ?? "http://localhost:5150";

    // ── Untimed methods (used by dashboard) ──────────────────────

    public async Task<List<ServiceHealthInfo>> GetServicesAsync(CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string url = $"{Address.TrimEnd('/')}/api/v1/services";
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        ServicesStatusResponse? result = await response.Content.ReadFromJsonAsync<ServicesStatusResponse>(ct);
        return result?.Services ?? [];
    }

    public async Task RebuildIndexAsync(string source, string indexType = "all", CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string url = $"{Address.TrimEnd('/')}/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}&sources={Uri.EscapeDataString(source)}";
        using HttpResponseMessage response = await client.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
    }

    // ── Timed HTTP operations (used by API test page) ────────────

    public async Task<(string Url, string Json, long ElapsedMs)> ContentSearchAsync(
        string valuesInput, int limit, CancellationToken ct = default)
    {
        string[] values = valuesInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string valuesQuery = string.Join("&", values.Select(v => $"values={Uri.EscapeDataString(v)}"));
        string url = $"{Address.TrimEnd('/')}/api/v1/content/search?{valuesQuery}&limit={limit}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> RefersToAsync(
        string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/content/refers-to?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> ReferredByAsync(
        string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/content/referred-by?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> CrossReferencedAsync(
        string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/content/cross-referenced?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetItemAsync(
        string source, string id, bool includeContent, bool includeComments, bool includeSnapshot,
        CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/content/item/{Uri.EscapeDataString(source)}/{EncodeId(source, id)}?includeContent={includeContent}&includeComments={includeComments}&includeSnapshot={includeSnapshot}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetServicesStatusAsync(
        CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/services";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetStatsAsync(
        CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/stats";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> RebuildIndexTimedAsync(
        string indexType, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}";
        return await PostTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> TriggerSyncAsync(
        string type, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/ingest/trigger?type={Uri.EscapeDataString(type)}";
        return await PostTimedJsonAsync(url, ct);
    }

    // ── Typed methods (used by Item Viewer) ─────────────────────

    private static readonly JsonSerializerOptions TypedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ContentItemResponse?> GetItemTypedAsync(
        string source, string id, bool includeContent = true, bool includeComments = true,
        bool includeSnapshot = false, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string url = $"{Address.TrimEnd('/')}/api/v1/content/item/{Uri.EscapeDataString(source)}/{EncodeId(source, id)}?includeContent={includeContent}&includeComments={includeComments}&includeSnapshot={includeSnapshot}";
        HttpResponseMessage response = await client.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContentItemResponse>(TypedJsonOptions, ct);
    }

    public async Task<CrossReferenceQueryResponse?> GetCrossReferencedTypedAsync(
        string value, string? sourceType = null, int? limit = null, CancellationToken ct = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string url = $"{Address.TrimEnd('/')}/api/v1/content/cross-referenced?value={Uri.EscapeDataString(value)}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        if (limit.HasValue)
            url += $"&limit={limit.Value}";
        HttpResponseMessage response = await client.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CrossReferenceQueryResponse>(TypedJsonOptions, ct);
    }

    internal static string EncodeId(string source, string id)
    {
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return id.Replace("#", "%23");
        return Uri.EscapeDataString(id);
    }

    // ── Formatting ───────────────────────────────────────────────

    public static string PrettyPrint(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, PrettyJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    // ── Internals ────────────────────────────────────────────────

    private async Task<(string Url, string Json, long ElapsedMs)> GetTimedJsonAsync(
        string url, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await client.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }

    private async Task<(string Url, string Json, long ElapsedMs)> PostTimedJsonAsync(
        string url, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostAsync(url, null, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }
}
