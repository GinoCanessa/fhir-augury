using System.Net;
using System.Text;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Processing.Common.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class ProcessingHttpClientTests
{
    [Fact]
    public async Task StatusQueueAndLifecycle_UseConfiguredProcessingClient()
    {
        RecordingHandler handler = new();
        ProcessingHttpClient client = CreateClient(handler);

        ProcessingStatusResponse? status = await client.GetStatusAsync("Planner", CancellationToken.None);
        ProcessingQueueStatsResponse? queue = await client.GetQueueStatsAsync("Planner", CancellationToken.None);
        ProcessingLifecycleResponse? start = await client.StartAsync("Planner", CancellationToken.None);
        ProcessingLifecycleResponse? stop = await client.StopAsync("Planner", CancellationToken.None);
        HealthCheckResponse? health = await client.HealthCheckAsync("Planner", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("running", status.Status);
        Assert.NotNull(queue);
        Assert.Equal(7, queue.RemainingCount);
        Assert.NotNull(start);
        Assert.Equal("running", start.Status);
        Assert.NotNull(stop);
        Assert.Equal("paused", stop.Status);
        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Contains("processing-planner", handler.ClientNames);
        Assert.Contains("/api/v1/processing/queue", handler.Paths);
    }

    [Fact]
    public void DisabledProcessingService_IsNotEnabled()
    {
        ProcessingHttpClient client = CreateClient(new RecordingHandler(), enabled: false);

        Assert.False(client.IsProcessingServiceEnabled("Planner"));
        Assert.Empty(client.GetEnabledProcessingServiceNames());
    }

    private static ProcessingHttpClient CreateClient(RecordingHandler handler, bool enabled = true)
    {
        OrchestratorOptions options = new()
        {
            ProcessingServices = new Dictionary<string, ProcessingServiceConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = new ProcessingServiceConfig { HttpAddress = "http://planner", Enabled = enabled },
            },
        };
        TestHttpClientFactory factory = new(handler);
        return new ProcessingHttpClient(factory, Options.Create(options), NullLogger<ProcessingHttpClient>.Instance);
    }

    private sealed class TestHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            handler.ClientNames.Add(name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> ClientNames { get; } = [];
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            Paths.Add(path);
            string json = path switch
            {
                "/api/v1/status" => """
                    {"status":"running","isRunning":true,"isPaused":false,"startedAt":"2026-04-29T00:00:00Z","uptimeSeconds":1,"lastPollAt":null,"syncSchedule":"00:05:00","maxConcurrentProcessingThreads":2,"startProcessingOnStartup":true}
                    """,
                "/api/v1/processing/queue" => """
                    {"processedCount":3,"remainingCount":7,"inFlightCount":1,"errorCount":0,"averageItemDurationMs":12.5,"lastItemCompletedAt":null}
                    """,
                "/api/v1/processing/start" => @"{""status"":""running"",""isRunning"":true,""message"":""started""}",
                "/api/v1/processing/stop" => @"{""status"":""paused"",""isRunning"":false,""message"":""stopped""}",
                "/api/v1/health" => @"{""status"":""ok"",""version"":null,""uptimeSeconds"":1,""message"":null}",
                _ => "{}",
            };
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}

