using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace FhirAugury.Cli.OpenApi;

/// <summary>
/// Fetches and caches the orchestrator's merged OpenAPI document on disk
/// using ETag-based conditional GETs.
/// </summary>
public sealed class OpenApiDocumentCache
{
    private const string MergedJsonPath = "/api/v1/openapi.json";

    private readonly string _orchestratorAddr;
    private readonly string _cacheDir;
    private readonly string _key;

    public OpenApiDocumentCache(string orchestratorAddr)
    {
        _orchestratorAddr = orchestratorAddr.TrimEnd('/');
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "fhir-augury",
            "openapi");
        _key = ComputeKey(_orchestratorAddr);
    }

    public async Task<OpenApiDocument> GetMergedAsync(bool refresh, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDir);

        string jsonPath = Path.Combine(_cacheDir, $"{_key}-merged.json");
        string etagPath = Path.Combine(_cacheDir, $"{_key}-merged.etag");

        string? cachedJson = !refresh && File.Exists(jsonPath)
            ? await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false)
            : null;
        string? cachedEtag = !refresh && File.Exists(etagPath)
            ? (await File.ReadAllTextAsync(etagPath, ct).ConfigureAwait(false)).Trim()
            : null;

        using HttpClient client = new() { BaseAddress = new Uri(_orchestratorAddr) };
        using HttpRequestMessage request = new(HttpMethod.Get, MergedJsonPath);
        if (cachedJson is not null && !string.IsNullOrEmpty(cachedEtag))
        {
            if (EntityTagHeaderValue.TryParse(cachedEtag, out EntityTagHeaderValue? etagValue))
            {
                request.Headers.IfNoneMatch.Add(etagValue);
            }
        }

        using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && cachedJson is not null)
        {
            return Parse(cachedJson);
        }

        response.EnsureSuccessStatusCode();
        string fetched = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? newEtag = response.Headers.ETag?.ToString();

        await File.WriteAllTextAsync(jsonPath, fetched, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(newEtag))
        {
            await File.WriteAllTextAsync(etagPath, newEtag, ct).ConfigureAwait(false);
        }
        else if (File.Exists(etagPath))
        {
            File.Delete(etagPath);
        }

        return Parse(fetched);
    }

    private static OpenApiDocument Parse(string json)
    {
        OpenApiReaderSettings settings = new();
        if (!settings.Readers.ContainsKey(OpenApiConstants.Json))
        {
            settings.Readers[OpenApiConstants.Json] = new OpenApiJsonReader();
        }
        ReadResult result = OpenApiDocument.Parse(json, OpenApiConstants.Json, settings);
        return result.Document
            ?? throw new InvalidOperationException("Failed to parse merged OpenAPI document.");
    }

    private static string ComputeKey(string address)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(address));
        StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
