using FhirAugury.Common.OpenApi;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1")]
[ApiExplorerSettings(IgnoreApi = true)]
public class OpenApiController(
    OpenApiMergeService merge,
    SourceHttpClient sources,
    [FromKeyedServices(AuguryOpenApiServiceCollectionExtensions.DocumentName)] IOpenApiDocumentProvider provider,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const string JsonContentType = "application/json";
    private const string YamlContentType = "application/yaml; charset=utf-8";

    [HttpGet("openapi.json")]
    public async Task<IActionResult> GetMergedJson(
        [FromQuery(Name = "include")] string? include,
        CancellationToken ct)
    {
        bool includeInternal = string.Equals(include, "internal", StringComparison.OrdinalIgnoreCase);
        MergedDocument merged = await merge.GetMergedAsync(includeInternal, ct).ConfigureAwait(false);

        if (RequestMatchesETag(merged.ETag))
        {
            Response.Headers.ETag = merged.ETag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = merged.ETag;
        return Content(merged.Json, JsonContentType);
    }

    [HttpGet("openapi.yaml")]
    public async Task<IActionResult> GetMergedYaml(
        [FromQuery(Name = "include")] string? include,
        CancellationToken ct)
    {
        bool includeInternal = string.Equals(include, "internal", StringComparison.OrdinalIgnoreCase);
        MergedDocument merged = await merge.GetMergedAsync(includeInternal, ct).ConfigureAwait(false);

        if (RequestMatchesETag(merged.ETag))
        {
            Response.Headers.ETag = merged.ETag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        string yaml = OpenApiMergeService.SerializeYaml(merged.Document);
        Response.Headers.ETag = merged.ETag;
        return Content(yaml, YamlContentType);
    }

    [HttpGet("source/orchestrator/openapi.json")]
    public async Task<IActionResult> GetOrchestratorOwnJson(CancellationToken ct)
    {
        OpenApiDocument document = await provider
            .GetOpenApiDocumentAsync(ct)
            .ConfigureAwait(false);
        string json = OpenApiMergeService.SerializeJson(document);
        return Content(json, JsonContentType);
    }

    [HttpGet("source/{name}/openapi.json")]
    public async Task<IActionResult> GetSourceJson(string name, CancellationToken ct)
    {
        if (string.Equals(name, "orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return await GetOrchestratorOwnJson(ct).ConfigureAwait(false);
        }

        if (!sources.IsSourceEnabled(name))
        {
            return NotFound(new { error = $"Source '{name}' not configured or disabled" });
        }

        HttpClient client = httpClientFactory.CreateClient($"source-{name.ToLowerInvariant()}");
        try
        {
            using HttpResponseMessage upstream = await client
                .GetAsync("/api/v1/openapi.json", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? JsonContentType;

            await using Stream body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await body.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Source '{name}' is unreachable: {ex.Message}" });
        }
    }

    private bool RequestMatchesETag(string etag)
    {
        if (!Request.Headers.TryGetValue("If-None-Match", out StringValues values))
        {
            return false;
        }
        foreach (string? value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }
            foreach (string part in value.Split(','))
            {
                if (string.Equals(part.Trim(), etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
