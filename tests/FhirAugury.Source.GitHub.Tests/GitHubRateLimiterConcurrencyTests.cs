using System.Net;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubRateLimiterConcurrencyTests
{
    [Fact]
    public async Task SendAsync_ConcurrentRequests_AreSerializedWhenMaxIs1()
    {
        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            RateLimiting = new GitHubRateLimitConfiguration
            {
                MaxConcurrentRequests = 1,
                RespectRateLimitHeaders = false,
            },
        });

        GitHubRateLimiter handler = new GitHubRateLimiter(options)
        {
            InnerHandler = new ConcurrencyTrackingHandler(delay: TimeSpan.FromMilliseconds(100)),
        };

        using HttpClient client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, 3)
            .Select(_ => client.GetAsync("/test"))
            .ToArray();

        await Task.WhenAll(tasks);

        ConcurrencyTrackingHandler tracker = (ConcurrencyTrackingHandler)handler.InnerHandler;
        Assert.Equal(1, tracker.MaxConcurrent);
    }

    [Fact]
    public async Task SendAsync_ConcurrentRequests_AllowedWhenMaxIsHigher()
    {
        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            RateLimiting = new GitHubRateLimitConfiguration
            {
                MaxConcurrentRequests = 3,
                RespectRateLimitHeaders = false,
            },
        });

        GitHubRateLimiter handler = new GitHubRateLimiter(options)
        {
            InnerHandler = new ConcurrencyTrackingHandler(delay: TimeSpan.FromMilliseconds(200)),
        };

        using HttpClient client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, 3)
            .Select(_ => client.GetAsync("/test"))
            .ToArray();

        await Task.WhenAll(tasks);

        ConcurrencyTrackingHandler tracker = (ConcurrencyTrackingHandler)handler.InnerHandler;
        Assert.True(tracker.MaxConcurrent > 1,
            $"Expected concurrent execution with MaxConcurrentRequests=3, but max concurrent was {tracker.MaxConcurrent}");
    }

    [Fact]
    public async Task SendAsync_CancellationWhileWaiting_ThrowsOperationCanceled()
    {
        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            RateLimiting = new GitHubRateLimitConfiguration
            {
                MaxConcurrentRequests = 1,
                RespectRateLimitHeaders = false,
            },
        });

        GitHubRateLimiter handler = new GitHubRateLimiter(options)
        {
            InnerHandler = new ConcurrencyTrackingHandler(delay: TimeSpan.FromSeconds(5)),
        };

        using HttpClient client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using CancellationTokenSource cts = new CancellationTokenSource();

        // Start a long-running request to hold the gate
        Task<HttpResponseMessage> holdingTask = client.GetAsync("/hold", cts.Token);

        // Give it a moment to acquire the gate
        await Task.Delay(50);

        // Second request should block; cancel it quickly
        using CancellationTokenSource cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/blocked", cts2.Token));

        cts.Cancel();
        // Clean up the holding task
        try { await holdingTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Inner handler that tracks the maximum number of concurrent in-flight requests.
    /// </summary>
    private sealed class ConcurrencyTrackingHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _currentConcurrent;
        private int _maxConcurrent;

        public int MaxConcurrent => _maxConcurrent;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref _currentConcurrent);

            // Atomically update max
            int snapshot = _maxConcurrent;
            while (current > snapshot)
            {
                int original = Interlocked.CompareExchange(ref _maxConcurrent, current, snapshot);
                if (original == snapshot) break;
                snapshot = original;
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }
    }
}
