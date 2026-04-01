using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Fhiraugury;
using Google.Protobuf;
using Grpc.Net.Client;

namespace FhirAugury.DevUi.Services;

/// <summary>
/// Calls source services directly via gRPC or HTTP, bypassing the orchestrator.
/// </summary>
public sealed class SourceDirectClient : IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    private SourceService.SourceServiceClient GetGrpcClient(string grpcAddress)
    {
        GrpcChannel channel = _channels.GetOrAdd(grpcAddress, addr => GrpcChannel.ForAddress(addr));
        return new SourceService.SourceServiceClient(channel);
    }

    // ── gRPC operations ──────────────────────────────────────────

    public async Task<(IMessage Response, long ElapsedMs)> SearchGrpcAsync(
        string grpcAddress, string query, int limit, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        SearchResponse response = await client.SearchAsync(
            new SearchRequest { Query = query, Limit = limit }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetItemGrpcAsync(
        string grpcAddress, string id, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        ItemResponse response = await client.GetItemAsync(
            new GetItemRequest { Id = id, IncludeContent = true, IncludeComments = true },
            cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetRelatedGrpcAsync(
        string grpcAddress, string id, int limit, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        SearchResponse response = await client.GetRelatedAsync(
            new GetRelatedRequest { Id = id, Limit = limit }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetSnapshotGrpcAsync(
        string grpcAddress, string id, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        SnapshotResponse response = await client.GetSnapshotAsync(
            new GetSnapshotRequest { Id = id, IncludeComments = true }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetContentGrpcAsync(
        string grpcAddress, string id, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        ContentResponse response = await client.GetContentAsync(
            new GetContentRequest { Id = id }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetStatsGrpcAsync(
        string grpcAddress, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        StatsResponse response = await client.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> HealthCheckGrpcAsync(
        string grpcAddress, CancellationToken ct = default)
    {
        SourceService.SourceServiceClient client = GetGrpcClient(grpcAddress);
        Stopwatch sw = Stopwatch.StartNew();
        HealthCheckResponse response = await client.HealthCheckAsync(
            new HealthCheckRequest(), cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    // ── HTTP operations ──────────────────────────────────────────

    public async Task<(string Url, string Json, long ElapsedMs)> SearchHttpAsync(
        string httpBase, string query, int limit, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetItemHttpAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildItemPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetRelatedHttpAsync(
        string httpBase, string source, string id, int limit, CancellationToken ct = default)
    {
        string path = BuildRelatedPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}?limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetSnapshotHttpAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildSnapshotPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetContentHttpAsync(
        string httpBase, string source, string id, CancellationToken ct = default)
    {
        string path = BuildContentPath(source, id);
        string url = $"{httpBase.TrimEnd('/')}/api/v1/{path}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetStatsHttpAsync(
        string httpBase, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/stats";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> HealthCheckHttpAsync(
        string httpBase, CancellationToken ct = default)
    {
        string url = $"{httpBase.TrimEnd('/')}/api/v1/status";
        return await GetJsonAsync(url, ct);
    }

    // ── Formatting ───────────────────────────────────────────────

    public static string FormatAsJson(IMessage message)
    {
        string json = JsonFormatter.Default.Format(message);
        using JsonDocument doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, PrettyJsonOptions);
    }

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
        // GitHub uses /items/related/{*key} because catch-all must be the final segment.
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

    public void Dispose()
    {
        _httpClient.Dispose();
        foreach (GrpcChannel channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }
}
