using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace FhirAugury.Orchestrator.Routing;

/// <summary>
/// Typed-proxy helpers added in Phase C. Forwards requests to source services
/// preserving query string, header allowlist, request body, response status,
/// response body, and ETag/Last-Modified round-tripping.
/// </summary>
public partial class SourceHttpClient
{
    private static readonly HashSet<string> s_typedForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "If-None-Match",
        "If-Modified-Since",
        "Range",
        "User-Agent",
        "Authorization",
    };

    private const string TypedXHeaderPrefix = "X-";

    private static bool IsTypedForwardedRequestHeader(string name) =>
        s_typedForwardedRequestHeaders.Contains(name)
        || name.StartsWith(TypedXHeaderPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool MethodAllowsBody(HttpMethod method) =>
        method == HttpMethod.Post
        || method == HttpMethod.Put
        || method == HttpMethod.Patch
        || method == HttpMethod.Delete;

    /// <summary>
    /// Forwards a request to the named source at <c>/api/v1/{path}</c>,
    /// returning an <see cref="IActionResult"/> that proxies the upstream
    /// response (status, body, ETag/Last-Modified) back to the original
    /// caller. Headers from the original <see cref="HttpRequest"/> are
    /// filtered through the typed-proxy allowlist (Accept*, If-None-Match,
    /// If-Modified-Since, Authorization, Range, User-Agent, and any
    /// X-prefixed custom headers).
    /// </summary>
    public async Task<IActionResult> ProxyAsync(
        string sourceName,
        HttpMethod method,
        string path,
        HttpRequest? sourceRequest,
        CancellationToken ct,
        HttpContent? body = null,
        string? overrideQueryString = null)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        if (!IsSourceEnabled(sourceName))
        {
            return new NotFoundObjectResult(new { error = $"{sourceName} service not configured or disabled" });
        }

        HttpClient client = GetClientForSource(sourceName);

        string normalizedPath = path.StartsWith('/') ? path : "/api/v1/" + path;
        string? query = overrideQueryString;
        if (query is null && sourceRequest is not null)
        {
            query = sourceRequest.QueryString.Value;
        }
        string targetUri = string.IsNullOrEmpty(query)
            ? normalizedPath
            : normalizedPath + (query.StartsWith('?') ? query : "?" + query);

        HttpRequestMessage forward = new(method, targetUri);
        if (body is not null)
        {
            forward.Content = body;
        }
        else if (sourceRequest is not null && MethodAllowsBody(method) && sourceRequest.ContentLength is not 0)
        {
            StreamContent stream = new(sourceRequest.Body);
            string? contentType = sourceRequest.ContentType;
            if (!string.IsNullOrEmpty(contentType))
            {
                stream.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (sourceRequest.ContentLength is long len)
            {
                stream.Headers.ContentLength = len;
            }
            forward.Content = stream;
        }

        if (sourceRequest is not null)
        {
            foreach (KeyValuePair<string, StringValues> header in sourceRequest.Headers)
            {
                if (!IsTypedForwardedRequestHeader(header.Key))
                    continue;
                string[] values = header.Value.ToArray()!;
                if (!forward.Headers.TryAddWithoutValidation(header.Key, values)
                    && forward.Content is not null)
                {
                    forward.Content.Headers.TryAddWithoutValidation(header.Key, values);
                }
            }
        }

        HttpResponseMessage response = await client.SendAsync(
            forward, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        return new SourceProxyActionResult(response);
    }

    /// <summary>
    /// Typed GET that deserializes the upstream response into <typeparamref name="T"/>.
    /// Returns the deserialized body, the upstream status code, and ETag if present.
    /// On non-success status codes, <c>Value</c> is <c>null</c>.
    /// </summary>
    public async Task<SourceJsonResponse<T>> GetJsonAsync<T>(
        string sourceName,
        string path,
        CancellationToken ct,
        HttpRequest? sourceRequest = null,
        string? overrideQueryString = null)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        HttpClient client = GetClientForSource(sourceName);

        string normalizedPath = path.StartsWith('/') ? path : "/api/v1/" + path;
        string? query = overrideQueryString ?? sourceRequest?.QueryString.Value;
        string targetUri = string.IsNullOrEmpty(query)
            ? normalizedPath
            : normalizedPath + (query.StartsWith('?') ? query : "?" + query);

        HttpRequestMessage request = new(HttpMethod.Get, targetUri);
        ApplyAllowlistedHeaders(request, sourceRequest);

        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        T? value = default;
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength != 0)
        {
            value = await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        return new SourceJsonResponse<T>(
            (int)response.StatusCode,
            value,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified);
    }

    /// <summary>
    /// Typed POST with JSON request body. Serializes <paramref name="body"/>
    /// and deserializes the response into <typeparamref name="TResp"/>.
    /// </summary>
    public async Task<SourceJsonResponse<TResp>> PostJsonAsync<TReq, TResp>(
        string sourceName,
        string path,
        TReq body,
        CancellationToken ct,
        HttpRequest? sourceRequest = null,
        string? overrideQueryString = null)
    {
        return await SendJsonAsync<TReq, TResp>(HttpMethod.Post, sourceName, path, body, ct, sourceRequest, overrideQueryString);
    }

    /// <summary>
    /// Typed PUT with JSON request body. Serializes <paramref name="body"/>
    /// and deserializes the response into <typeparamref name="TResp"/>.
    /// </summary>
    public async Task<SourceJsonResponse<TResp>> PutJsonAsync<TReq, TResp>(
        string sourceName,
        string path,
        TReq body,
        CancellationToken ct,
        HttpRequest? sourceRequest = null,
        string? overrideQueryString = null)
    {
        return await SendJsonAsync<TReq, TResp>(HttpMethod.Put, sourceName, path, body, ct, sourceRequest, overrideQueryString);
    }

    private async Task<SourceJsonResponse<TResp>> SendJsonAsync<TReq, TResp>(
        HttpMethod method,
        string sourceName,
        string path,
        TReq body,
        CancellationToken ct,
        HttpRequest? sourceRequest,
        string? overrideQueryString)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        HttpClient client = GetClientForSource(sourceName);

        string normalizedPath = path.StartsWith('/') ? path : "/api/v1/" + path;
        string? query = overrideQueryString ?? sourceRequest?.QueryString.Value;
        string targetUri = string.IsNullOrEmpty(query)
            ? normalizedPath
            : normalizedPath + (query.StartsWith('?') ? query : "?" + query);

        HttpRequestMessage request = new(method, targetUri)
        {
            Content = JsonContent.Create(body),
        };
        ApplyAllowlistedHeaders(request, sourceRequest);

        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        TResp? value = default;
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength != 0)
        {
            value = await response.Content.ReadFromJsonAsync<TResp>(ct).ConfigureAwait(false);
        }
        return new SourceJsonResponse<TResp>(
            (int)response.StatusCode,
            value,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified);
    }

    private static void ApplyAllowlistedHeaders(HttpRequestMessage request, HttpRequest? sourceRequest)
    {
        if (sourceRequest is null) return;
        foreach (KeyValuePair<string, StringValues> header in sourceRequest.Headers)
        {
            if (!IsTypedForwardedRequestHeader(header.Key))
                continue;
            string[] values = header.Value.ToArray()!;
            if (!request.Headers.TryAddWithoutValidation(header.Key, values)
                && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }
    }
}

/// <summary>
/// Typed JSON response from an upstream source service via a Phase C
/// typed-proxy helper.
/// </summary>
public record SourceJsonResponse<T>(int StatusCode, T? Value, string? ETag, DateTimeOffset? LastModified)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>
    /// Adapt to an <see cref="IActionResult"/>: success returns the typed
    /// value with ETag header propagated; non-success returns the raw status.
    /// </summary>
    public IActionResult ToActionResult()
    {
        if (!IsSuccess)
            return new StatusCodeResult(StatusCode);
        ObjectResult result = new(Value) { StatusCode = StatusCode };
        return new EtagWrappedResult(result, ETag, LastModified);
    }
}

internal sealed class EtagWrappedResult(IActionResult inner, string? etag, DateTimeOffset? lastModified) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        if (!string.IsNullOrEmpty(etag))
            context.HttpContext.Response.Headers.ETag = etag;
        if (lastModified is { } lm)
            context.HttpContext.Response.Headers.LastModified = lm.ToString("R");
        await inner.ExecuteResultAsync(context);
    }
}

/// <summary>
/// Streams an upstream <see cref="HttpResponseMessage"/> back to the original
/// caller, copying status, content-type, ETag, and Last-Modified.
/// </summary>
internal sealed class SourceProxyActionResult(HttpResponseMessage upstream) : IActionResult, IDisposable
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        HttpResponse outgoing = context.HttpContext.Response;
        outgoing.StatusCode = (int)upstream.StatusCode;

        string? contentType = upstream.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType))
            outgoing.ContentType = contentType;

        if (upstream.Headers.ETag is { } etag)
            outgoing.Headers.ETag = etag.Tag;
        if (upstream.Content.Headers.LastModified is { } lm)
            outgoing.Headers.LastModified = lm.ToString("R");

        if (upstream.StatusCode == HttpStatusCode.NotModified)
        {
            upstream.Dispose();
            return;
        }

        try
        {
            await using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
            await upstreamStream.CopyToAsync(outgoing.Body, context.HttpContext.RequestAborted);
        }
        finally
        {
            upstream.Dispose();
        }
    }

    public void Dispose() => upstream.Dispose();
}
