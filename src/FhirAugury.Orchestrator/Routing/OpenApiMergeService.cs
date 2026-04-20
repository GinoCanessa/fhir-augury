using System.Security.Cryptography;
using System.Text;
using FhirAugury.Common.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace FhirAugury.Orchestrator.Routing;

public sealed record MergedDocument(OpenApiDocument Document, string Json, string ETag);

/// <summary>
/// Builds and caches the orchestrator's merged OpenAPI document by combining
/// the orchestrator's own document with each enabled source service's document.
/// </summary>
public sealed class OpenApiMergeService
{
    private static readonly TimeSpan SourceFetchTimeout = TimeSpan.FromSeconds(5);

    private readonly SourceHttpClient _sources;
    private readonly IOpenApiDocumentProvider _provider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenApiMergeService> _logger;

    private readonly Dictionary<bool, MergedDocument> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    static OpenApiMergeService()
    {
        // The JSON reader is registered per-call via OpenApiReaderSettings.
    }

    public OpenApiMergeService(
        SourceHttpClient sources,
        [FromKeyedServices(AuguryOpenApiServiceCollectionExtensions.DocumentName)] IOpenApiDocumentProvider provider,
        IHttpClientFactory httpClientFactory,
        ILogger<OpenApiMergeService> logger)
    {
        _sources = sources;
        _provider = provider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _cache.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MergedDocument> GetMergedAsync(bool includeInternal, CancellationToken ct)
    {
        if (_cache.TryGetValue(includeInternal, out MergedDocument? cached))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(includeInternal, out cached))
            {
                return cached;
            }

            MergedDocument built = await BuildAsync(includeInternal, ct).ConfigureAwait(false);
            _cache[includeInternal] = built;
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetMergedJsonAsync(bool includeInternal, CancellationToken ct)
    {
        MergedDocument merged = await GetMergedAsync(includeInternal, ct).ConfigureAwait(false);
        return merged.Json;
    }

    public async Task<string> GetMergedYamlAsync(bool includeInternal, CancellationToken ct)
    {
        MergedDocument merged = await GetMergedAsync(includeInternal, ct).ConfigureAwait(false);
        return SerializeYaml(merged.Document);
    }

    private async Task<MergedDocument> BuildAsync(bool includeInternal, CancellationToken ct)
    {
        OpenApiDocument orchestrator = await _provider
            .GetOpenApiDocumentAsync(ct)
            .ConfigureAwait(false);

        Dictionary<string, OpenApiDocument?> sourceDocs = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> unavailable = new(StringComparer.OrdinalIgnoreCase);

        foreach (string sourceName in _sources.GetEnabledSourceNames())
        {
            (OpenApiDocument? doc, string? failure) = await TryFetchSourceDocAsync(sourceName, ct).ConfigureAwait(false);
            if (doc is not null)
            {
                sourceDocs[sourceName] = doc;
            }
            else
            {
                unavailable[sourceName] = failure ?? "unavailable";
            }
        }

        OpenApiDocument merged = OpenApiMerger.Merge(orchestrator, sourceDocs, includeInternal, unavailable);

        string json = SerializeJson(merged);
        string etag = ComputeETag(json);

        return new MergedDocument(merged, json, etag);
    }

    private async Task<(OpenApiDocument? Doc, string? Failure)> TryFetchSourceDocAsync(
        string sourceName, CancellationToken ct)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient($"source-{sourceName.ToLowerInvariant()}");
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SourceFetchTimeout);

            using HttpResponseMessage response = await client
                .GetAsync("/api/v1/openapi.json", cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"unavailable (HTTP {(int)response.StatusCode})");
            }

            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            OpenApiReaderSettings settings = new();
            if (!settings.Readers.ContainsKey(OpenApiConstants.Json))
            {
                settings.Readers[OpenApiConstants.Json] = new OpenApiJsonReader();
            }

            ReadResult result = OpenApiDocument.Parse(body, OpenApiConstants.Json, settings);
            if (result.Document is null)
            {
                return (null, "unavailable (invalid OpenAPI document)");
            }

            return (result.Document, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch OpenAPI document from source '{Source}'", sourceName);
            return (null, $"unavailable ({ex.GetType().Name}: {ex.Message})");
        }
    }

    internal static string SerializeJson(OpenApiDocument document)
    {
        using MemoryStream ms = new();
        using (StreamWriter sw = new(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            OpenApiJsonWriter writer = new(sw);
            document.SerializeAsV31(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string SerializeYaml(OpenApiDocument document)
    {
        using MemoryStream ms = new();
        using (StreamWriter sw = new(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            OpenApiYamlWriter writer = new(sw);
            document.SerializeAsV31(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ComputeETag(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] hash = SHA256.HashData(bytes);
        StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return $"W/\"{sb}\"";
    }
}
