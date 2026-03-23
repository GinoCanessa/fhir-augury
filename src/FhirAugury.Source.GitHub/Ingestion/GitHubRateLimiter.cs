using System.Net.Http.Headers;
using FhirAugury.Source.GitHub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// HTTP delegating handler that monitors GitHub API rate limits
/// and delays requests when approaching the limit.
/// </summary>
public class GitHubRateLimiter(IOptions<GitHubServiceOptions> optionsAccessor, ILogger<GitHubRateLimiter>? logger = null) : DelegatingHandler
{
    private readonly Lock _lock = new();
    private int _remaining = int.MaxValue;
    private DateTimeOffset _resetTime = DateTimeOffset.MinValue;
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;

    /// <summary>Configures an existing HttpClient with GitHub default headers (non-auth).</summary>
    public static void ConfigureHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Check if we should wait for rate limit reset
        TimeSpan delay;
        lock (_lock)
        {
            if (_remaining <= 10 && _resetTime > DateTimeOffset.UtcNow)
            {
                delay = _resetTime - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            }
            else
            {
                delay = TimeSpan.Zero;
            }
        }

        if (delay > TimeSpan.Zero)
        {
            logger?.LogWarning(
                "Rate limit approaching ({Remaining} remaining). Waiting {Delay:F0}s until reset.",
                _remaining, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        // Add auth header per-request if not set globally
        var token = _options.Auth.ResolveToken();
        if (!string.IsNullOrEmpty(token) && request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Update rate limit tracking from response headers
        if (_options.RateLimiting.RespectRateLimitHeaders)
        {
            lock (_lock)
            {
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                    int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
                {
                    _remaining = remaining;
                }

                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                    long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
                {
                    _resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
                }
            }
        }

        logger?.LogDebug("GitHub API: {Status}, Rate limit remaining: {Remaining}", response.StatusCode, _remaining);

        return response;
    }
}
