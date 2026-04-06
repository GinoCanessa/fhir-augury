using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
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

    public async Task<ItemResponse?> GetItemAsync(string sourceName, string id, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        string path = BuildItemPath(sourceName, id);
        return await client.GetFromJsonAsync<ItemResponse>($"/api/v1/{path}", ct);
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
        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/ingest?type={Uri.EscapeDataString(type)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IngestionStatusResponse>(ct);
    }

    public async Task<PeerIngestionAck?> NotifyPeerAsync(
        string sourceName, PeerIngestionNotification notification, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/notify-peer", notification, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PeerIngestionAck>(ct);
    }

    public async Task<RebuildIndexResponse?> RebuildIndexAsync(
        string sourceName, string indexType, CancellationToken ct)
    {
        HttpClient client = GetClientForSource(sourceName);
        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RebuildIndexResponse>(ct);
    }
}
