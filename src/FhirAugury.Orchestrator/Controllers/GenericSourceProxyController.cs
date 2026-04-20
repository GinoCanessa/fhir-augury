using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1/source/{name}")]
[ApiExplorerSettings(IgnoreApi = true)]
public class GenericSourceProxyController(
    SourceHttpClient httpClient,
    ILogger<GenericSourceProxyController> logger) : ControllerBase
{
    private static readonly HashSet<string> s_forwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
        "Content-Disposition",
        "ETag",
        "Cache-Control",
        "Last-Modified",
        "Vary",
    };

    private static readonly HashSet<string> s_hopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Transfer-Encoding",
        "Keep-Alive",
        "Upgrade",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
    };

    private const string AuguryHeaderPrefix = "X-Augury-";

    private static bool IsForwardedResponseHeader(string name)
    {
        if (s_hopByHopHeaders.Contains(name))
        {
            return false;
        }
        return s_forwardedResponseHeaders.Contains(name)
            || name.StartsWith(AuguryHeaderPrefix, StringComparison.OrdinalIgnoreCase);
    }

    [Route("{**rest}")]
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD")]
    public async Task<IActionResult> Forward(string name, string? rest, CancellationToken ct)
    {
        if (string.Equals(name, "orchestrator", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { error = "Use /api/v1/... directly for orchestrator routes." });
        }

        if (!httpClient.IsSourceEnabled(name))
        {
            return NotFound(new { error = $"Source '{name}' not configured or disabled" });
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.ForwardAsync(name, Request, rest ?? string.Empty, ct).ConfigureAwait(false);

            Response.StatusCode = (int)response.StatusCode;

            CopyResponseHeaders(response, Response);

            await using Stream upstream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await upstream.CopyToAsync(Response.Body, ct).ConfigureAwait(false);

            return new EmptyResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Source '{Source}' unreachable while proxying '{Path}'", name, rest);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Source '{name}' is unreachable: {ex.Message}" });
        }
        catch (TaskCanceledException ex)
        {
            logger.LogWarning(ex, "Source '{Source}' timed out while proxying '{Path}'", name, rest);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Source '{name}' is unreachable: {ex.Message}" });
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source.Headers)
        {
            if (!IsForwardedResponseHeader(header.Key))
            {
                continue;
            }
            destination.Headers[header.Key] = header.Value.ToArray();
        }

        if (source.Content is not null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in source.Content.Headers)
            {
                if (!IsForwardedResponseHeader(header.Key))
                {
                    continue;
                }
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
