using System.Diagnostics;
using System.Text.Json;

namespace FhirAugury.DevUi.Services;

public sealed class OrchestratorClient : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public string Address { get; }

    public OrchestratorClient(IConfiguration configuration)
    {
        Address = configuration["DevUi:OrchestratorAddress"] ?? "http://localhost:5150";
    }

    // ── Untimed methods (used by dashboard) ──────────────────────

    public async Task<List<ServiceInfo>> GetServicesAsync(CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/services";
        using HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        List<ServiceInfo> services = [];
        foreach (JsonElement svc in doc.RootElement.GetProperty("services").EnumerateArray())
        {
            services.Add(new ServiceInfo
            {
                Name = GetStr(svc, "name"),
                Status = GetStr(svc, "status"),
                Address = GetNullStr(svc, "grpcAddress") ?? GetNullStr(svc, "httpAddress"),
                ItemCount = svc.TryGetProperty("itemCount", out JsonElement icEl) ? icEl.GetInt32() : 0,
                DbSizeBytes = svc.TryGetProperty("dbSizeBytes", out JsonElement dbEl) ? dbEl.GetInt64() : 0,
                LastSyncAt = GetNullStr(svc, "lastSyncAt"),
                LastError = GetNullStr(svc, "lastError"),
                Indexes = ParseIndexes(svc),
            });
        }
        return services;
    }

    public async Task RebuildIndexAsync(string source, string indexType = "all", CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}&sources={Uri.EscapeDataString(source)}";
        using HttpResponseMessage response = await _httpClient.PostAsync(url, null, ct);
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
        string url = $"{Address.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}/snapshot";
        return await GetTimedJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetContentAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{Address.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}/content";
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
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }

    private async Task<(string Url, string Json, long ElapsedMs)> PostTimedJsonAsync(
        string url, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        HttpResponseMessage response = await _httpClient.PostAsync(url, null, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");

        return (url, PrettyPrint(body), elapsed);
    }

    private static List<IndexInfo> ParseIndexes(JsonElement svc)
    {
        if (!svc.TryGetProperty("indexes", out JsonElement idxs) || idxs.ValueKind != JsonValueKind.Array)
            return [];

        List<IndexInfo> result = [];
        foreach (JsonElement idx in idxs.EnumerateArray())
        {
            result.Add(new IndexInfo
            {
                Name = GetStr(idx, "name"),
                Description = GetStr(idx, "description"),
                IsRebuilding = idx.TryGetProperty("isRebuilding", out JsonElement rebEl) && rebEl.GetBoolean(),
                LastRebuildCompletedAt = GetNullStr(idx, "lastRebuildCompletedAt"),
                RecordCount = idx.TryGetProperty("recordCount", out JsonElement rcEl) ? rcEl.GetInt32() : 0,
                LastError = GetNullStr(idx, "lastError"),
            });
        }
        return result;
    }

    private static string GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? GetNullStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _httpClient.Dispose();

    // ── DTOs ──────────────────────────────────────────────────────

    public record ServiceInfo
    {
        public string Name { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Address { get; init; }
        public int ItemCount { get; init; }
        public long DbSizeBytes { get; init; }
        public string? LastSyncAt { get; init; }
        public string? LastError { get; init; }
        public List<IndexInfo> Indexes { get; init; } = [];
    }

    public record IndexInfo
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsRebuilding { get; init; }
        public string? LastRebuildCompletedAt { get; init; }
        public int RecordCount { get; init; }
        public string? LastError { get; init; }
    }
}
