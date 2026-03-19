using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.GitHub;

/// <summary>
/// HTTP delegating handler that monitors GitHub API rate limits
/// and delays requests when approaching the limit.
/// </summary>
public class GitHubRateLimiter(GitHubSourceOptions options, ILogger? logger = null) : DelegatingHandler
{
    private readonly Lock _lock = new();
    private int _remaining = int.MaxValue;
    private DateTimeOffset _resetTime = DateTimeOffset.MinValue;

    /// <summary>Creates an HttpClient configured with GitHub authentication and rate limiting.</summary>
    public static HttpClient CreateHttpClient(GitHubSourceOptions options, ILogger? logger = null)
    {
        var handler = new GitHubRateLimiter(options, logger) { InnerHandler = new HttpClientHandler() };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        ConfigureHttpClient(client, options);
        return client;
    }

    /// <summary>Configures an existing HttpClient with GitHub default headers and auth.</summary>
    public static void ConfigureHttpClient(HttpClient client, GitHubSourceOptions options)
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrEmpty(options.PersonalAccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.PersonalAccessToken);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Check if we should wait for rate limit reset
        TimeSpan delay;
        lock (_lock)
        {
            if (_remaining <= options.RateLimitBuffer && _resetTime > DateTimeOffset.UtcNow)
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
            logger?.LogWarning("Rate limit approaching ({Remaining} remaining). Waiting {Delay:F0}s until reset.", _remaining, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        // Add auth header per-request if not set globally
        if (!string.IsNullOrEmpty(options.PersonalAccessToken) && request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.PersonalAccessToken);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Update rate limit tracking
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

        logger?.LogDebug("GitHub API: {Status}, Rate limit remaining: {Remaining}", response.StatusCode, _remaining);

        return response;
    }
}
