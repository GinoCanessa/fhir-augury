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
        const int expected = 3;
        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            RateLimiting = new GitHubRateLimitConfiguration
            {
                MaxConcurrentRequests = expected,
                RespectRateLimitHeaders = false,
            },
        });

        RendezvousTrackingHandler tracker =
            new RendezvousTrackingHandler(expected, TimeSpan.FromSeconds(20));
        GitHubRateLimiter handler = new GitHubRateLimiter(options) { InnerHandler = tracker };
        using HttpClient client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, expected)
            .Select(_ => client.GetAsync("/test"))
            .ToArray();

        // Fail fast if a regressed gate serializes instead of hanging the assembly.
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(expected, tracker.MaxConcurrent);
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

    /// <summary>
    /// Inner handler that holds each in-flight request until <c>expected</c> requests are
    /// concurrently in the gate, then releases them all at once. A correct gate rendezvouses
    /// in milliseconds; a regressed (serializing) gate never reaches the rendezvous and the
    /// per-waiter timeout fails the test fast instead of hanging the assembly.
    /// </summary>
    private sealed class RendezvousTrackingHandler : HttpMessageHandler
    {
        private readonly int _expected;
        private readonly TimeSpan _timeout;
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _currentConcurrent;
        private int _maxConcurrent;

        public RendezvousTrackingHandler(int expected, TimeSpan timeout)
        {
            _expected = expected;
            _timeout = timeout;
        }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref _currentConcurrent);
            int snapshot = Volatile.Read(ref _maxConcurrent);
            while (current > snapshot)
            {
                int original = Interlocked.CompareExchange(ref _maxConcurrent, current, snapshot);
                if (original == snapshot) break;
                snapshot = original;
            }

            try
            {
                if (Interlocked.Increment(ref _arrived) == _expected)
                    _allArrived.TrySetResult();

                // Releases instantly once all expected requests are concurrently
                // in-flight. If the gate serializes (regression), this times out
                // -> deterministic failure, never a hang.
                await _allArrived.Task.WaitAsync(_timeout, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }
    }
}
