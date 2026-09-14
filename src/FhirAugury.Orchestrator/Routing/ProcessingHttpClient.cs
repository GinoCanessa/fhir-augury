using System.Net.Http.Json;
using System.Net;
using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Processing.Common.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Routing;

public sealed record ProcessingProxyResponse(
    HttpStatusCode StatusCode,
    string? ContentType,
    byte[] Content,
    string? RetryAfter,
    string? Location = null);

/// <summary>
/// Routes proxied calls to configured Processing services via named HttpClients.
/// </summary>
public class ProcessingHttpClient
{
    private static readonly HashSet<string> s_snapshotRequestHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Range",
            "If-Range",
            "If-None-Match",
            "If-Modified-Since",
        };

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

    public Task<ProcessingProxyResponse> CreateAuthoringRunAsync(
        string name,
        JsonElement request,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Post,
            GetAuthoringPath(name),
            request,
            ct);

    public Task<ProcessingProxyResponse> StartPublicationRefreshAsync(
        string name,
        string sourceRunId,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Post,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(sourceRunId)}/publication-refresh",
            body: null,
            ct);

    public Task<ProcessingProxyResponse> GetAuthoringRunAsync(
        string name,
        string runId,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Get,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(runId)}",
            body: null,
            ct);

    public Task<ProcessingProxyResponse> ListAuthoringRunsAsync(
        string name,
        int? limit,
        CancellationToken ct)
    {
        string path = GetAuthoringPath(name);
        if (limit.HasValue)
        {
            path += $"?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return ForwardAsync(
            name,
            HttpMethod.Get,
            path,
            body: null,
            ct);
    }

    public Task<ProcessingProxyResponse> RetryAuthoringItemAsync(
        string name,
        string runId,
        string itemId,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Post,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/retry",
            body: null,
            ct);

    public Task<ProcessingProxyResponse> SupersedeAuthoringItemAsync(
        string name,
        string runId,
        string itemId,
        JsonElement request,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Post,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/supersede",
            request,
            ct);

    public Task<ProcessingProxyResponse> GetAuthoringSnapshotAsync(
        string name,
        string runId,
        CancellationToken ct) =>
        ForwardAsync(
            name,
            HttpMethod.Get,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(runId)}/snapshot",
            body: null,
            ct);

    public async Task<ProcessingProxyActionResult> GetAuthoringSnapshotBytesAsync(
        string name,
        string runId,
        HttpRequest sourceRequest,
        CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"{GetAuthoringPath(name)}/{Uri.EscapeDataString(runId)}/snapshot/bytes");

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in
            sourceRequest.Headers)
        {
            if (s_snapshotRequestHeaders.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(
                    header.Key,
                    header.Value.ToArray());
            }
        }

        HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return new ProcessingProxyActionResult(response);
    }

    private async Task<ProcessingProxyResponse> ForwardAsync(
        string name,
        HttpMethod method,
        string path,
        JsonElement? body,
        CancellationToken ct)
    {
        HttpClient client = GetClientForProcessingService(name);
        using HttpRequestMessage request = new(method, path);
        if (body is JsonElement json)
        {
            request.Content = JsonContent.Create(json);
        }

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return new ProcessingProxyResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsByteArrayAsync(ct),
            response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
                ? string.Join(", ", values)
                : null,
            RewriteLocation(name, response.Headers.Location));
    }

    private static string GetAuthoringPath(string serviceName) =>
        string.Equals(serviceName, "BallotNotes", StringComparison.OrdinalIgnoreCase)
            ? "/api/v1/ballot-notes/authoring/runs"
            : "/processing/authoring/runs";

    private static string? RewriteLocation(string serviceName, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        string original = location.IsAbsoluteUri
            ? location.PathAndQuery + location.Fragment
            : location.OriginalString;
        string normalized = original.StartsWith('/')
            ? original
            : "/" + original;
        string authoringPath = GetAuthoringPath(serviceName);
        int suffixStart = normalized.IndexOfAny(['?', '#']);
        string path = suffixStart < 0
            ? normalized
            : normalized[..suffixStart];
        if (!path.Equals(authoringPath, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(authoringPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return location.ToString();
        }

        string suffix = normalized[authoringPath.Length..];
        return $"/api/v1/processing-services/{Uri.EscapeDataString(serviceName)}/authoring/runs{suffix}";
    }

    private HttpClient GetClientForProcessingService(string serviceName)
    {
        _logger.LogDebug("Creating Processing service client for {ServiceName}", serviceName);
        return _httpClientFactory.CreateClient($"processing-{serviceName.ToLowerInvariant()}");
    }
}
