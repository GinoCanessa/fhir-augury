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

    public async Task<(string Url, string Json, long ElapsedMs)> UnifiedSearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> FindRelatedAsync(
        string source, string id, int limit, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/related/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}?limit={limit}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetCrossReferencesAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/xref/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetItemAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetSnapshotAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/snapshot/{Uri.EscapeDataString(id)}";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetContentAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/content/{Uri.EscapeDataString(id)}";
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
