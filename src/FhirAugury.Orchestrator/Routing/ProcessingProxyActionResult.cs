using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Routing;

/// <summary>
/// Streams an upstream processing-service response and owns its lifetime.
/// </summary>
public sealed class ProcessingProxyActionResult : IActionResult, IDisposable
{
    private HttpResponseMessage? _upstream;

    public ProcessingProxyActionResult(HttpResponseMessage upstream)
    {
        _upstream = upstream;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        HttpResponseMessage upstream = _upstream ??
            throw new ObjectDisposedException(nameof(ProcessingProxyActionResult));

        try
        {
            HttpResponse outgoing = context.HttpContext.Response;
            outgoing.StatusCode = (int)upstream.StatusCode;

            if (upstream.Content.Headers.ContentType is { } contentType)
            {
                outgoing.ContentType = contentType.ToString();
            }
            if (upstream.Content.Headers.ContentLength is long contentLength)
            {
                outgoing.ContentLength = contentLength;
            }

            CopyHeader(upstream.Headers, outgoing, "Accept-Ranges");
            CopyHeader(upstream.Headers, outgoing, "ETag");
            CopyHeader(upstream.Headers, outgoing, "Retry-After");
            CopyHeader(upstream.Content.Headers, outgoing, "Content-Range");
            CopyHeader(upstream.Content.Headers, outgoing, "Content-Disposition");
            CopyHeader(upstream.Content.Headers, outgoing, "Last-Modified");

            if (!HasResponseBody(upstream.StatusCode))
            {
                return;
            }

            await using Stream upstreamStream =
                await upstream.Content.ReadAsStreamAsync(
                    context.HttpContext.RequestAborted);
            await upstreamStream.CopyToAsync(
                outgoing.Body,
                context.HttpContext.RequestAborted);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _upstream, null)?.Dispose();

    private static void CopyHeader(
        HttpHeaders source,
        HttpResponse destination,
        string name)
    {
        if (source.TryGetValues(name, out IEnumerable<string>? values))
        {
            destination.Headers[name] = values.ToArray();
        }
    }

    private static bool HasResponseBody(HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return status >= 200 &&
            status is not StatusCodes.Status204NoContent and
                not StatusCodes.Status205ResetContent and
                not StatusCodes.Status304NotModified;
    }
}
