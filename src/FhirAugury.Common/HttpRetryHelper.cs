using System.Net;

namespace FhirAugury.Common;

/// <summary>
/// Lightweight HTTP retry helper with exponential backoff and jitter.
/// </summary>
public static class HttpRetryHelper
{
    public const int DefaultMaxRetries = 3;
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private static readonly HashSet<HttpStatusCode> AuthFailureCodes =
    [
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
    ];

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
                await DelayWithJitter(backoff, ct);
                backoff = NextBackoff(backoff);
                continue;
            }

            if (AuthFailureCodes.Contains(response.StatusCode))
            {
                var source = sourceName ?? "source";
                var msg = response.StatusCode == HttpStatusCode.Unauthorized
                    ? $"Authentication failed for {source} (HTTP 401). Check your credentials — API tokens, cookies, or passwords may be expired or invalid."
                    : $"Access forbidden for {source} (HTTP 403). Check your credentials and permissions. For GitHub, verify your PAT has the required scopes.";
                response.Dispose();
                throw new HttpRequestException(msg, null, response.StatusCode);
            }

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

    public static bool IsAuthFailure(HttpStatusCode statusCode) => AuthFailureCodes.Contains(statusCode);
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
