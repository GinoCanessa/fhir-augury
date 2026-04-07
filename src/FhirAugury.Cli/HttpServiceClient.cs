using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FhirAugury.Cli;

/// <summary>
/// HTTP client for communicating with the orchestrator and source services.
/// </summary>
public sealed class HttpServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _orchestratorClient;
    private readonly Dictionary<string, HttpClient> _sourceClients = new(StringComparer.OrdinalIgnoreCase);

    public HttpServiceClient(string orchestratorAddress = "http://localhost:5150")
    {
        _orchestratorClient = new HttpClient { BaseAddress = new Uri(orchestratorAddress) };
    }

    // ── Orchestrator calls ──────────────────────────────────────────────

    public async Task<JsonElement> UnifiedSearchAsync(string query, string? sources, int? limit, CancellationToken ct)
    {
        string url = $"/api/v1/search?q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrEmpty(sources))
            url += $"&sources={Uri.EscapeDataString(sources)}";
        if (limit.HasValue)
            url += $"&limit={limit.Value}";
        return await GetJsonAsync(_orchestratorClient, url, ct);
    }

    public async Task<JsonElement> GetItemAsync(string source, string id, CancellationToken ct)
    {
        string url = $"/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        return await GetJsonAsync(_orchestratorClient, url, ct);
    }

    public async Task<JsonElement> GetSnapshotAsync(string source, string id, CancellationToken ct)
    {
        string url = $"/api/v1/items/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}/snapshot";
        return await GetJsonAsync(_orchestratorClient, url, ct);
    }

    public async Task<JsonElement> FindRelatedAsync(string source, string id, int? limit, string? targetSources, CancellationToken ct)
    {
        string url = $"/api/v1/related/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        List<string> queryParams = [];
        if (limit.HasValue)
            queryParams.Add($"limit={limit.Value}");
        if (!string.IsNullOrEmpty(targetSources))
            queryParams.Add($"targetSources={Uri.EscapeDataString(targetSources)}");
        if (queryParams.Count > 0)
            url += "?" + string.Join("&", queryParams);
        return await GetJsonAsync(_orchestratorClient, url, ct);
    }

    public async Task<JsonElement> GetCrossReferencesAsync(string source, string id, string? direction, CancellationToken ct)
    {
        string url = $"/api/v1/xref/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(id)}";
        if (!string.IsNullOrEmpty(direction))
            url += $"?direction={Uri.EscapeDataString(direction)}";
        return await GetJsonAsync(_orchestratorClient, url, ct);
    }

    public async Task<JsonElement> TriggerSyncAsync(string? type, string? sources, CancellationToken ct)
    {
        string url = "/api/v1/ingest/trigger";
        List<string> queryParams = [];
        if (!string.IsNullOrEmpty(type))
            queryParams.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrEmpty(sources))
            queryParams.Add($"sources={Uri.EscapeDataString(sources)}");
        if (queryParams.Count > 0)
            url += "?" + string.Join("&", queryParams);
        return await PostJsonAsync(_orchestratorClient, url, null, ct);
    }

    public async Task<JsonElement> RebuildIndexAsync(string? type, string? sources, CancellationToken ct)
    {
        string url = "/api/v1/rebuild-index";
        List<string> queryParams = [];
        if (!string.IsNullOrEmpty(type))
            queryParams.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrEmpty(sources))
            queryParams.Add($"sources={Uri.EscapeDataString(sources)}");
        if (queryParams.Count > 0)
            url += "?" + string.Join("&", queryParams);
        return await PostJsonAsync(_orchestratorClient, url, null, ct);
    }

    public async Task<JsonElement> GetServicesStatusAsync(CancellationToken ct)
    {
        return await GetJsonAsync(_orchestratorClient, "/api/v1/services", ct);
    }

    public async Task<JsonElement> GetStatsAsync(CancellationToken ct)
    {
        return await GetJsonAsync(_orchestratorClient, "/api/v1/stats", ct);
    }

    public async Task<JsonElement> GetServiceEndpointsAsync(CancellationToken ct)
    {
        return await GetJsonAsync(_orchestratorClient, "/api/v1/endpoints", ct);
    }

    public async Task<JsonElement> QueryJiraAsync(string address, object queryParams, CancellationToken ct)
    {
        HttpClient client = GetSourceClient(address);
        return await PostJsonAsync(client, "/api/v1/query", queryParams, ct);
    }

    public async Task<JsonElement> QueryJiraViaOrchestratorAsync(object queryParams, CancellationToken ct)
    {
        return await PostJsonAsync(_orchestratorClient, "/api/v1/jira/query", queryParams, ct);
    }

    public async Task<JsonElement> QueryZulipAsync(string address, object queryParams, CancellationToken ct)
    {
        HttpClient client = GetSourceClient(address);
        return await PostJsonAsync(client, "/api/v1/query", queryParams, ct);
    }

    public async Task<JsonElement> QueryZulipViaOrchestratorAsync(object queryParams, CancellationToken ct)
    {
        return await PostJsonAsync(_orchestratorClient, "/api/v1/zulip/query", queryParams, ct);
    }

    // ── Content Query API ──────────────────────────────────────────────

    public async Task<JsonElement> ContentSearchAsync(List<string> values, string? sources, int? limit, CancellationToken ct)
    {
        StringBuilder url = new("/api/v1/content/search?");
        foreach (string v in values)
            url.Append($"values={Uri.EscapeDataString(v)}&");
        if (!string.IsNullOrEmpty(sources))
        {
            foreach (string s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                url.Append($"sources={Uri.EscapeDataString(s)}&");
        }
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await GetJsonAsync(_orchestratorClient, url.ToString().TrimEnd('&'), ct);
    }

    public async Task<JsonElement> ContentGetItemAsync(string source, string id,
        bool includeContent, bool includeComments, bool includeSnapshot, CancellationToken ct)
    {
        string encodedId = source.Equals("github", StringComparison.OrdinalIgnoreCase)
            ? id.Replace("#", "%23")
            : Uri.EscapeDataString(id);
        StringBuilder url = new($"/api/v1/content/item/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (includeContent) url.Append("includeContent=true&");
        if (includeComments) url.Append("includeComments=true&");
        if (includeSnapshot) url.Append("includeSnapshot=true&");
        return await GetJsonAsync(_orchestratorClient, url.ToString().TrimEnd('&', '?'), ct);
    }

    public async Task<JsonElement> ContentXRefAsync(string direction, string value, string? sourceType, int? limit, CancellationToken ct)
    {
        StringBuilder url = new($"/api/v1/content/{direction}?value={Uri.EscapeDataString(value)}");
        if (!string.IsNullOrEmpty(sourceType)) url.Append($"&sourceType={Uri.EscapeDataString(sourceType)}");
        if (limit.HasValue) url.Append($"&limit={limit.Value}");
        return await GetJsonAsync(_orchestratorClient, url.ToString(), ct);
    }

    // ── Source-direct calls ─────────────────────────────────────────────

    public async Task<JsonElement> ListItemsAsync(
        string sourceAddress, int? limit, int? offset, string? sortBy, string? sortOrder,
        Dictionary<string, string>? filters, CancellationToken ct)
    {
        HttpClient client = GetSourceClient(sourceAddress);
        List<string> queryParams = [];
        if (limit.HasValue)
            queryParams.Add($"limit={limit.Value}");
        if (offset.HasValue)
            queryParams.Add($"offset={offset.Value}");
        if (!string.IsNullOrEmpty(sortBy))
            queryParams.Add($"sort_by={Uri.EscapeDataString(sortBy)}");
        if (!string.IsNullOrEmpty(sortOrder))
            queryParams.Add($"sort_order={Uri.EscapeDataString(sortOrder)}");
        if (filters is not null)
        {
            foreach ((string key, string value) in filters)
                queryParams.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        string url = "/api/v1/items";
        if (queryParams.Count > 0)
            url += "?" + string.Join("&", queryParams);
        return await GetJsonAsync(client, url, ct);
    }

    public async Task<JsonElement> GetSourceStatsAsync(string sourceAddress, CancellationToken ct)
    {
        HttpClient client = GetSourceClient(sourceAddress);
        return await GetJsonAsync(client, "/api/v1/stats", ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or creates an HttpClient for a source service address.
    /// </summary>
    public HttpClient GetSourceClient(string address)
    {
        if (!_sourceClients.TryGetValue(address, out HttpClient? client))
        {
            client = new HttpClient { BaseAddress = new Uri(address) };
            _sourceClients[address] = client;
        }
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object? body, CancellationToken ct)
    {
        HttpResponseMessage response;
        if (body is not null)
        {
            response = await client.PostAsJsonAsync(url, body, JsonOptions, ct);
        }
        else
        {
            response = await client.PostAsync(url, null, ct);
        }
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Dispose()
    {
        _orchestratorClient.Dispose();
        foreach (HttpClient client in _sourceClients.Values)
            client.Dispose();
        _sourceClients.Clear();
    }
}
