using System.Net.Http.Json;
using System.Text.Json;

namespace FhirAugury.Cli;

/// <summary>HTTP client wrapper for the FHIR Augury service API.</summary>
public class ServiceClient(HttpClient httpClient) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<JsonElement> TriggerIngestionAsync(string source, string type = "Incremental", string? filter = null, CancellationToken ct = default)
    {
        var url = $"/api/v1/ingest/{Uri.EscapeDataString(source)}?type={Uri.EscapeDataString(type)}";
        if (!string.IsNullOrEmpty(filter)) url += $"&filter={Uri.EscapeDataString(filter)}";

        var response = await httpClient.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> SubmitItemAsync(string source, string identifier, CancellationToken ct = default)
    {
        var response = await httpClient.PostAsJsonAsync($"/api/v1/ingest/{Uri.EscapeDataString(source)}/item", new { identifier }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> TriggerSyncAllAsync(string? sources = null, CancellationToken ct = default)
    {
        var url = "/api/v1/ingest/sync";
        if (!string.IsNullOrEmpty(sources)) url += $"?sources={Uri.EscapeDataString(sources)}";

        var response = await httpClient.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> SearchAsync(string query, string? sources = null, int limit = 20, CancellationToken ct = default)
    {
        var url = $"/api/v1/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrEmpty(sources)) url += $"&sources={Uri.EscapeDataString(sources)}";

        var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync("/api/v1/ingest/status", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> GetStatsAsync(string? source = null, CancellationToken ct = default)
    {
        var url = source is null ? "/api/v1/stats" : $"/api/v1/stats/{Uri.EscapeDataString(source)}";
        var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> GetScheduleAsync(CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync("/api/v1/ingest/schedule", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> UpdateScheduleAsync(string source, string interval, CancellationToken ct = default)
    {
        var response = await httpClient.PutAsJsonAsync($"/api/v1/ingest/{Uri.EscapeDataString(source)}/schedule", new { syncInterval = interval }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> GetHealthAsync(CancellationToken ct = default)
    {
        var response = await httpClient.GetAsync("/health", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>Pretty-prints a JSON element to the console.</summary>
    public static void PrintJson(JsonElement json)
    {
        Console.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
    }

    public void Dispose()
    {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
