using System.Diagnostics;
using System.Text.Json;
using Fhiraugury;
using Google.Protobuf;
using Grpc.Net.Client;

namespace FhirAugury.DevUi.Services;

public sealed class OrchestratorClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly OrchestratorService.OrchestratorServiceClient _client;
    private readonly HttpClient _httpClient = new();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public string GrpcAddress { get; }
    public string HttpAddress { get; }

    public OrchestratorClient(IConfiguration configuration)
    {
        GrpcAddress = configuration["DevUi:OrchestratorGrpcAddress"] ?? "http://localhost:5151";
        HttpAddress = configuration["DevUi:OrchestratorHttpAddress"] ?? "http://localhost:5150";
        _channel = GrpcChannel.ForAddress(GrpcAddress);
        _client = new OrchestratorService.OrchestratorServiceClient(_channel);
    }

    // ── Untimed methods (used by dashboard) ──────────────────────

    public async Task<ServicesStatusResponse> GetServicesStatusAsync(CancellationToken ct = default)
    {
        return await _client.GetServicesStatusAsync(new ServicesStatusRequest(), cancellationToken: ct);
    }

    public async Task<ServiceEndpointsResponse> GetServiceEndpointsAsync(CancellationToken ct = default)
    {
        return await _client.GetServiceEndpointsAsync(new ServiceEndpointsRequest(), cancellationToken: ct);
    }

    public async Task<OrchestratorRebuildIndexResponse> RebuildIndexAsync(
        string source, string indexType = "all", CancellationToken ct = default)
    {
        OrchestratorRebuildIndexRequest request = new() { IndexType = indexType };
        request.Sources.Add(source);
        return await _client.RebuildIndexAsync(request, cancellationToken: ct);
    }

    public async Task<FindRelatedResponse> FindRelatedAsync(
        string source, string id, int limit = 20, CancellationToken ct = default)
    {
        return await _client.FindRelatedAsync(
            new FindRelatedRequest { Source = source, Id = id, Limit = limit },
            cancellationToken: ct);
    }

    // ── Timed gRPC operations (used by API test page) ────────────

    public async Task<(IMessage Response, long ElapsedMs)> UnifiedSearchGrpcAsync(
        string query, int limit, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SearchResponse response = await _client.UnifiedSearchAsync(
            new UnifiedSearchRequest { Query = query, Limit = limit }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> FindRelatedGrpcAsync(
        string source, string id, int limit, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        FindRelatedResponse response = await _client.FindRelatedAsync(
            new FindRelatedRequest { Source = source, Id = id, Limit = limit }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetCrossReferencesGrpcAsync(
        string source, string id, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        GetXRefResponse response = await _client.GetCrossReferencesAsync(
            new GetXRefRequest { Source = source, Id = id, Direction = "both" }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetItemGrpcAsync(
        string sourceName, string id, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ItemResponse response = await _client.GetItemAsync(
            new GetItemRequest { Id = id, SourceName = sourceName, IncludeContent = true, IncludeComments = true },
            cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetSnapshotGrpcAsync(
        string sourceName, string id, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SnapshotResponse response = await _client.GetSnapshotAsync(
            new GetSnapshotRequest { Id = id, SourceName = sourceName, IncludeComments = true },
            cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetContentGrpcAsync(
        string sourceName, string id, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ContentResponse response = await _client.GetContentAsync(
            new GetContentRequest { Id = id, SourceName = sourceName }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> GetServicesStatusGrpcAsync(
        CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        ServicesStatusResponse response = await _client.GetServicesStatusAsync(
            new ServicesStatusRequest(), cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> RebuildIndexGrpcAsync(
        string indexType, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        OrchestratorRebuildIndexResponse response = await _client.RebuildIndexAsync(
            new OrchestratorRebuildIndexRequest { IndexType = indexType }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    public async Task<(IMessage Response, long ElapsedMs)> TriggerSyncGrpcAsync(
        string type, CancellationToken ct = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        TriggerSyncResponse response = await _client.TriggerSyncAsync(
            new TriggerSyncRequest { Type = type }, cancellationToken: ct);
        return (response, sw.ElapsedMilliseconds);
    }

    // ── HTTP operations ──────────────────────────────────────────

    public async Task<(string Url, string Json, long ElapsedMs)> UnifiedSearchHttpAsync(
        string query, int limit, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> FindRelatedHttpAsync(
        string source, string id, int limit, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/related/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}?limit={limit}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetCrossReferencesHttpAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/xref/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetItemHttpAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetSnapshotHttpAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}/snapshot";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetContentHttpAsync(
        string source, string id, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}/content";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetServicesStatusHttpAsync(
        CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/services";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> GetStatsHttpAsync(
        CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/stats";
        return await GetJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> RebuildIndexHttpAsync(
        string indexType, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}";
        return await PostJsonAsync(url, ct);
    }

    public async Task<(string Url, string Json, long ElapsedMs)> TriggerSyncHttpAsync(
        string type, CancellationToken ct = default)
    {
        string url = $"{HttpAddress.TrimEnd('/')}/api/v1/ingest/trigger?type={Uri.EscapeDataString(type)}";
        return await PostJsonAsync(url, ct);
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

    private async Task<(string Url, string Json, long ElapsedMs)> PostJsonAsync(
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

    public void Dispose()
    {
        _httpClient.Dispose();
        _channel.Dispose();
    }
}
