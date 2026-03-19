using System.Net;

namespace FhirAugury.Models;

/// <summary>
/// Lightweight HTTP retry helper with exponential backoff and jitter.
/// Retries on transient status codes (429, 500, 502, 503) and respects Retry-After headers.
/// </summary>
public static class HttpRetryHelper
{
    /// <summary>Default maximum number of retry attempts.</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>Default initial backoff delay.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>Default maximum backoff delay.</summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.TooManyRequests,        // 429
        HttpStatusCode.InternalServerError,     // 500
        HttpStatusCode.BadGateway,              // 502
        HttpStatusCode.ServiceUnavailable,      // 503
        HttpStatusCode.GatewayTimeout,          // 504
    ];

    private static readonly HashSet<HttpStatusCode> AuthFailureCodes =
    [
        HttpStatusCode.Unauthorized,            // 401
        HttpStatusCode.Forbidden,               // 403
    ];

    /// <summary>
    /// Sends an HTTP GET request with retry logic for transient failures.
    /// Throws <see cref="HttpRequestException"/> with clear messaging for auth failures.
    /// </summary>
    public static Task<HttpResponseMessage> GetWithRetryAsync(
        HttpClient httpClient,
        string url,
        CancellationToken ct,
        int maxRetries = DefaultMaxRetries,
        string? sourceName = null)
    {
        return ExecuteWithRetryAsync(
            token => httpClient.GetAsync(url, token),
            ct, maxRetries, sourceName);
    }

    /// <summary>
    /// Executes an HTTP operation with retry logic for transient failures.
    /// </summary>
    public static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> operation,
        CancellationToken ct,
        int maxRetries = DefaultMaxRetries,
        string? sourceName = null)
    {
        var backoff = DefaultInitialBackoff;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                response = await operation(ct);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await DelayWithJitter(backoff, ct);
                backoff = NextBackoff(backoff);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxRetries)
            {
                // Timeout, not user cancellation
                await DelayWithJitter(backoff, ct);
                backoff = NextBackoff(backoff);
                continue;
            }

            // Auth failure — don't retry, throw with clear message
            if (AuthFailureCodes.Contains(response.StatusCode))
            {
                var source = sourceName ?? "source";
                var msg = response.StatusCode == HttpStatusCode.Unauthorized
                    ? $"Authentication failed for {source} (HTTP 401). Check your credentials — API tokens, cookies, or passwords may be expired or invalid."
                    : $"Access forbidden for {source} (HTTP 403). Check your credentials and permissions. For GitHub, verify your PAT has the required scopes.";
                response.Dispose();
                throw new HttpRequestException(msg, null, response.StatusCode);
            }

            // Transient failure — retry if attempts remain
            if (TransientStatusCodes.Contains(response.StatusCode) && attempt < maxRetries)
            {
                var retryAfter = GetRetryAfter(response);
                var delay = retryAfter ?? backoff;
                await DelayWithJitter(delay, ct);
                backoff = NextBackoff(backoff);
                response.Dispose();
                continue;
            }

            return response;
        }
    }

    /// <summary>Checks if the status code indicates an authentication or authorization failure.</summary>
    public static bool IsAuthFailure(HttpStatusCode statusCode) => AuthFailureCodes.Contains(statusCode);

    /// <summary>Checks if the status code is a transient/retryable error.</summary>
    public static bool IsTransient(HttpStatusCode statusCode) => TransientStatusCodes.Contains(statusCode);

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }

    private static async Task DelayWithJitter(TimeSpan baseDelay, CancellationToken ct)
    {
        // ±20% jitter
        var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
        if (delay > DefaultMaxBackoff)
            delay = DefaultMaxBackoff;

        await Task.Delay(delay, ct);
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > DefaultMaxBackoff ? DefaultMaxBackoff : next;
    }
}
