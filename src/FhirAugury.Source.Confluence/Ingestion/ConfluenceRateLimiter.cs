using FhirAugury.Source.Confluence.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// HTTP delegating handler that enforces <see cref="ConfluenceServiceOptions.RateLimiting"/>
/// by serializing requests and inserting delays to stay under MaxRequestsPerSecond.
/// </summary>
/// <remarks>
/// Modelled on <c>ZulipRateLimiter</c>. Registered <em>outside</em> the standard
/// resilience handler so the gate observes every physical send, including the
/// retries that handler issues on its own.
/// </remarks>
public class ConfluenceRateLimiter(
    IOptions<ConfluenceServiceOptions> options,
    ILogger<ConfluenceRateLimiter>? logger = null) : DelegatingHandler
{
    private readonly TimeSpan _minInterval = ResolveInterval(options.Value.RateLimiting.MaxRequestsPerSecond);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < _minInterval)
            {
                TimeSpan delay = _minInterval - elapsed;
                logger?.LogDebug("Rate limiting: waiting {Delay:F0}ms before next Confluence request",
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// A non-positive rate would produce an infinite or negative interval, so it
    /// is treated as "no throttling" rather than as a configuration crash.
    /// </summary>
    private static TimeSpan ResolveInterval(int maxRequestsPerSecond) =>
        maxRequestsPerSecond > 0
            ? TimeSpan.FromMilliseconds(1000.0 / maxRequestsPerSecond)
            : TimeSpan.Zero;
}
