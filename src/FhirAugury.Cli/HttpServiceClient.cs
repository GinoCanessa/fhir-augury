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

    public async Task<JsonElement> TriggerSyncAsync(string? type, string? sources, string? jiraProject, CancellationToken ct)
    {
        string url = "/api/v1/ingest/trigger";
        List<string> queryParams = [];
        if (!string.IsNullOrEmpty(type))
            queryParams.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrEmpty(sources))
            queryParams.Add($"sources={Uri.EscapeDataString(sources)}");
        if (!string.IsNullOrEmpty(jiraProject))
            queryParams.Add($"jira-project={Uri.EscapeDataString(jiraProject)}");
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

    public async Task<JsonElement> GetFromOrchestratorAsync(string path, CancellationToken ct)
    {
        return await GetJsonAsync(_orchestratorClient, path, ct);
    }

    public async Task<JsonElement> PostToOrchestratorAsync(string path, object? body, CancellationToken ct)
    {
        return await PostJsonAsync(_orchestratorClient, path, body, ct);
    }

    public async Task<JsonElement> PutToOrchestratorAsync(string path, string? bodyJson, CancellationToken ct)
    {
        using HttpResponseMessage response = bodyJson is not null
            ? await _orchestratorClient.PutAsync(path, new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"), ct)
            : await _orchestratorClient.PutAsync(path, content: null, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
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
        string encodedId = Uri.EscapeDataString(id);
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

    public async Task<JsonElement> ContentKeywordsAsync(
        string source, string id, string? keywordType, int? limit, CancellationToken ct = default)
    {
        string encodedId = Uri.EscapeDataString(id);
        StringBuilder url = new($"/api/v1/content/keywords/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (!string.IsNullOrEmpty(keywordType)) url.Append($"keywordType={Uri.EscapeDataString(keywordType)}&");
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await GetJsonAsync(_orchestratorClient, url.ToString().TrimEnd('&', '?'), ct);
    }

    public async Task<JsonElement> ContentRelatedByKeywordAsync(
        string source, string id, double? minScore, string? keywordType, int? limit,
        CancellationToken ct = default)
    {
        string encodedId = Uri.EscapeDataString(id);
        StringBuilder url = new($"/api/v1/content/related-by-keyword/{Uri.EscapeDataString(source)}/{encodedId}?");
        if (minScore.HasValue) url.Append($"minScore={minScore.Value}&");
        if (!string.IsNullOrEmpty(keywordType)) url.Append($"keywordType={Uri.EscapeDataString(keywordType)}&");
        if (limit.HasValue) url.Append($"limit={limit.Value}&");
        return await GetJsonAsync(_orchestratorClient, url.ToString().TrimEnd('&', '?'), ct);
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
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object? body, CancellationToken ct)
    {
        using HttpResponseMessage response = body is not null
            ? await client.PostAsJsonAsync(url, body, JsonOptions, ct)
            : await client.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        _orchestratorClient.Dispose();
        foreach (HttpClient client in _sourceClients.Values)
            client.Dispose();
        _sourceClients.Clear();
    }
}
