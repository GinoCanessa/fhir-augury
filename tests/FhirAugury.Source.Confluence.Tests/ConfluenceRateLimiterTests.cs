using System.Diagnostics;
using FhirAugury.Common.Configuration;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins that <see cref="ConfluenceServiceOptions.RateLimiting"/> actually
/// throttles. Before slot 0827-01 the option existed but was read by nothing,
/// so a full-instance sweep would have hammered Confluence at whatever rate the
/// socket allowed.
/// </summary>
public class ConfluenceRateLimiterTests
{
    /// <summary>Inner handler that records send times and answers immediately.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<TimeSpan> Offsets { get; } = [];

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Lock _sync = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Offsets.Add(_stopwatch.Elapsed);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static ConfluenceRateLimiter CreateLimiter(int maxRequestsPerSecond, HttpMessageHandler inner)
    {
        ConfluenceServiceOptions options = new()
        {
            RateLimiting = new RateLimitConfiguration { MaxRequestsPerSecond = maxRequestsPerSecond },
        };

        return new ConfluenceRateLimiter(Options.Create(options)) { InnerHandler = inner };
    }

    [Fact]
    public async Task SendAsync_SpacesRequestsByConfiguredRate()
    {
        const int Rate = 20;      // 50 ms apart
        const int Requests = 5;   // >= 200 ms total

        using RecordingHandler inner = new();
        using ConfluenceRateLimiter limiter = CreateLimiter(Rate, inner);
        using HttpClient client = new(limiter);

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < Requests; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("https://confluence.invalid/probe");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        stopwatch.Stop();

        TimeSpan expectedFloor = TimeSpan.FromMilliseconds((Requests - 1) * 1000.0 / Rate);
        Assert.Equal(Requests, inner.Offsets.Count);
        Assert.True(
            stopwatch.Elapsed >= expectedFloor,
            $"{Requests} requests at {Rate}/s should take at least {expectedFloor.TotalMilliseconds}ms, took {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task SendAsync_ConsecutiveRequestsAreNeverCloserThanTheInterval()
    {
        const int Rate = 20;
        TimeSpan interval = TimeSpan.FromMilliseconds(1000.0 / Rate);

        using RecordingHandler inner = new();
        using ConfluenceRateLimiter limiter = CreateLimiter(Rate, inner);
        using HttpClient client = new(limiter);

        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("https://confluence.invalid/probe");
        }

        // The first send is unthrottled; every later one must clear the interval.
        // A small tolerance absorbs timer granularity on Windows.
        TimeSpan tolerance = TimeSpan.FromMilliseconds(15);
        for (int i = 1; i < inner.Offsets.Count; i++)
        {
            TimeSpan gap = inner.Offsets[i] - inner.Offsets[i - 1];
            Assert.True(
                gap >= interval - tolerance,
                $"gap {i} was {gap.TotalMilliseconds:F0}ms, expected >= {interval.TotalMilliseconds:F0}ms");
        }
    }

    [Fact]
    public async Task SendAsync_SerializesConcurrentCallers()
    {
        const int Rate = 20;

        using RecordingHandler inner = new();
        using ConfluenceRateLimiter limiter = CreateLimiter(Rate, inner);
        using HttpClient client = new(limiter);

        // The gate is what makes the sweep polite even when callers fan out.
        Task[] calls = [.. Enumerable.Range(0, 4).Select(async _ =>
        {
            using HttpResponseMessage response = await client.GetAsync("https://confluence.invalid/probe");
        })];

        Stopwatch stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(calls);
        stopwatch.Stop();

        Assert.Equal(4, inner.Offsets.Count);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(3 * 1000.0 / Rate),
            $"4 concurrent requests at {Rate}/s should still be spaced; took {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task SendAsync_NonPositiveRateDisablesThrottlingRatherThanHanging()
    {
        using RecordingHandler inner = new();
        using ConfluenceRateLimiter limiter = CreateLimiter(0, inner);
        using HttpClient client = new(limiter);

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("https://confluence.invalid/probe");
        }

        stopwatch.Stop();

        Assert.Equal(5, inner.Offsets.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DefaultOptions_ThrottleRatherThanRunUngated()
    {
        ConfluenceServiceOptions options = new();

        // The shipped appsettings.json narrows this to 5; what matters here is
        // that an unconfigured service still throttles instead of running flat out.
        Assert.True(options.RateLimiting.MaxRequestsPerSecond > 0);
    }
}
