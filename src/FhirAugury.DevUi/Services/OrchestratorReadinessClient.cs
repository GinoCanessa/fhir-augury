using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common.Api;

namespace FhirAugury.DevUi.Services;

public interface IOrchestratorReadinessClient
{
    Task<ServicesStatusResponse> GetAsync(
        CancellationToken ct = default);

    Task<ServicesStatusResponse> RefreshAsync(
        CancellationToken ct = default);
}

public sealed class OrchestratorReadinessClient(
    HttpClient httpClient) : IOrchestratorReadinessClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    public Task<ServicesStatusResponse> GetAsync(
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/v1/services", ct);

    public Task<ServicesStatusResponse> RefreshAsync(
        CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/v1/services/refresh", ct);

    private async Task<ServicesStatusResponse> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, path);
        using HttpResponseMessage response =
            await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<
                ServicesStatusResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException(
                $"Orchestrator readiness endpoint '{path}' returned an empty response.");
    }
}
