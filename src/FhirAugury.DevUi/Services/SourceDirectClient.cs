using System.Diagnostics;
using System.Text.Json;

namespace FhirAugury.DevUi.Services;

/// <summary>
/// Calls source services directly via HTTP, bypassing the orchestrator.
/// </summary>
public sealed class SourceDirectClient : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    // ── HTTP operations ──────────────────────────────────────────

    public async Task<(string Url, string Json, long ElapsedMs)> SearchAsync(
        string httpBase, string query, int limit, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetItemAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildItemPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetRelatedAsync(
        string httpBase, string source, string id, int limit, CancellationToken ct = default)
    {
        string path = BuildRelatedPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}?limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetSnapshotAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildSnapshotPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetContentAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildContentPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
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

    // ── URL path builders (source-specific patterns) ─────────────

    private static string EncodeId(string source, string id)
    {
        // GitHub keys contain slashes (e.g. "HL7/fhir#4006"); the catch-all
        // route expects raw slashes but needs '#' encoded.
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return id.Replace("#", "%23");

        return Uri.EscapeDataString(id);
    }

    private static string ItemBase(string source) => source.ToLowerInvariant() switch
    {
        "zulip" => "messages",
        "confluence" => "pages",
        _ => "items",
    };

    private static string BuildItemPath(string source, string id) =>
        $"{ItemBase(source)}/{EncodeId(source, id)}";

    private static string BuildRelatedPath(string source, string id)
    {
        string encoded = EncodeId(source, id);
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return $"items/related/{encoded}";

        return $"{ItemBase(source)}/{encoded}/related";
    }

    private static string BuildSnapshotPath(string source, string id)
    {
        string encoded = EncodeId(source, id);
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return $"items/snapshot/{encoded}";

        return $"{ItemBase(source)}/{encoded}/snapshot";
    }

    private static string BuildContentPath(string source, string id)
    {
        string encoded = EncodeId(source, id);
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return $"items/content/{encoded}";

        return $"{ItemBase(source)}/{encoded}/content";
    }

    // ── Internals ────────────────────────────────────────────────

    private async Task<(string Url, string Json, long ElapsedMs)> GetJsonAsync(
        string url, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }

    public void Dispose() => _httpClient.Dispose();
}
