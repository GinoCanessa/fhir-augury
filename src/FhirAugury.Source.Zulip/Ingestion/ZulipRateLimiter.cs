using FhirAugury.Source.Zulip.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// HTTP delegating handler that enforces <see cref="ZulipServiceOptions.RateLimiting"/>
/// by serializing requests and inserting delays to stay under MaxRequestsPerSecond.
/// </summary>
public class ZulipRateLimiter(
    IOptions<ZulipServiceOptions> options,
    ILogger<ZulipRateLimiter>? logger = null) : DelegatingHandler
{
    private readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(
        1000.0 / options.Value.RateLimiting.MaxRequestsPerSecond);
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
                logger?.LogDebug("Rate limiting: waiting {Delay:F0}ms before next Zulip request",
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
}
