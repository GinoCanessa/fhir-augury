using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Processing.Common.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Routing;

/// <summary>
/// Routes proxied calls to configured Processing services via named HttpClients.
/// </summary>
public class ProcessingHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<ProcessingHttpClient> _logger;

    public ProcessingHttpClient(
        IHttpClientFactory httpClientFactory,
        IOptions<OrchestratorOptions> options,
        ILogger<ProcessingHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<string> GetEnabledProcessingServiceNames() => _options.ProcessingServices
        .Where(s => s.Value.Enabled)
        .Select(s => s.Key)
        .ToList();

    public bool IsProcessingServiceEnabled(string name) =>
        _options.ProcessingServices.TryGetValue(name, out ProcessingServiceConfig? config) && config.Enabled;

    public ProcessingServiceConfig? GetProcessingServiceConfig(string name) =>
        _options.ProcessingServices.TryGetValue(name, out ProcessingServiceConfig? config) ? config : null;

    public async Task<HealthCheckResponse?> HealthCheckAsync(string name, CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        return await client.GetFromJsonAsync<HealthCheckResponse>("/api/v1/health", ct);
    }

    public async Task<ProcessingStatusResponse?> GetStatusAsync(string name, CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        return await client.GetFromJsonAsync<ProcessingStatusResponse>("/api/v1/status", ct);
    }

    public async Task<ProcessingQueueStatsResponse?> GetQueueStatsAsync(string name, CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        return await client.GetFromJsonAsync<ProcessingQueueStatsResponse>("/api/v1/processing/queue", ct);
    }

    public async Task<ProcessingLifecycleResponse?> StartAsync(string name, CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        using HttpResponseMessage response = await client.PostAsync("/api/v1/processing/start", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProcessingLifecycleResponse>(ct);
    }

    public async Task<ProcessingLifecycleResponse?> StopAsync(string name, CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        using HttpResponseMessage response = await client.PostAsync("/api/v1/processing/stop", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProcessingLifecycleResponse>(ct);
    }

    private HttpClient GetClientForProcessingService(string serviceName)
    {
        _logger.LogDebug("Creating Processing service client for {ServiceName}", serviceName);
        return _httpClientFactory.CreateClient($"processing-{serviceName.ToLowerInvariant()}");
    }
}
