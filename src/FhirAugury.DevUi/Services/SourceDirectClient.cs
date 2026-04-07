using System.Diagnostics;
using System.Text.Json;

namespace FhirAugury.DevUi.Services;

/// <summary>
/// Calls source services directly via HTTP, bypassing the orchestrator.
/// </summary>
public sealed class SourceDirectClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    // ── HTTP operations ──────────────────────────────────────────

    public async Task<(string Url, string Json, long ElapsedMs)> ContentSearchAsync(
        string httpBase, string valuesInput, int limit, CancellationToken ct = default)
    {
        string[] values = valuesInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string valuesQuery = string.Join("&", values.Select(v => $"values={Uri.EscapeDataString(v)}"));
        string url = $"{httpBase.TrimEnd('/')}/api/v1/content/search?{valuesQuery}&limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> RefersToAsync(
        string httpBase, string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/content/refers-to?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> ReferredByAsync(
        string httpBase, string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/content/referred-by?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> CrossReferencedAsync(
        string httpBase, string value, string? sourceType, int limit, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/content/cross-referenced?value={Uri.EscapeDataString(value)}&limit={limit}";
        if (!string.IsNullOrEmpty(sourceType))
            url += $"&sourceType={Uri.EscapeDataString(sourceType)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> ContentItemAsync(
        string httpBase, string source, string id, bool includeContent, bool includeComments,
        bool includeSnapshot, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/content/item/{Uri.EscapeDataString(source)}/{EncodeId(source, id)}?includeContent={includeContent}&includeComments={includeComments}&includeSnapshot={includeSnapshot}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetStatsAsync(
        string httpBase, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/stats";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> HealthCheckAsync(
        string httpBase, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/status";
        return await GetJsonAsync(url, ct);
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

    // ── URL helpers ─────────────────────────────────────────────

    private static string EncodeId(string source, string id)
    {
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return id.Replace("#", "%23");
        return Uri.EscapeDataString(id);
    }

    // ── Internals ────────────────────────────────────────────────

    private async Task<(string Url, string Json, long ElapsedMs)> GetJsonAsync(
        string url, CancellationToken ct)
    {
        HttpClient client = httpClientFactory.CreateClient("source-direct");
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await client.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }
}
