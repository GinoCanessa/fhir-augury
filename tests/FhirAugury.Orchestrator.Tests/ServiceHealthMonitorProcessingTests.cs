using System.Net;
using System.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class ServiceHealthMonitorProcessingTests
{
    [Fact]
    public async Task CheckAllAsync_IncludesProcessingServices_WithoutRegressingSources()
    {
        OrchestratorOptions options = new()
        {
            Services = new Dictionary<string, SourceServiceConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["jira"] = new SourceServiceConfig { HttpAddress = "http://jira", Enabled = true },
            },
            ProcessingServices = new Dictionary<string, ProcessingServiceConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["planner"] = new ProcessingServiceConfig { HttpAddress = "http://planner", Enabled = true },
            },
        };
        IOptions<OrchestratorOptions> optionsAccessor = Options.Create(options);
        TestHttpClientFactory factory = new();
        SourceHttpClient sourceClient = new(factory, optionsAccessor, NullLogger<SourceHttpClient>.Instance);
        ProcessingHttpClient processingClient = new(factory, optionsAccessor, NullLogger<ProcessingHttpClient>.Instance);
        ServiceHealthMonitor monitor = new(sourceClient, optionsAccessor, NullLogger<ServiceHealthMonitor>.Instance, processingClient);

        await monitor.CheckAllAsync(CancellationToken.None);

        Dictionary<string, ServiceHealthInfo> status = monitor.GetCurrentStatus();
        Assert.Equal("source", status["jira"].ServiceKind);
        Assert.Equal("ok", status["jira"].Status);
        Assert.Equal(10, status["jira"].ItemCount);
        Assert.Equal("processing", status["planner"].ServiceKind);
        Assert.Equal("running", status["planner"].ProcessingStatus);
        Assert.Equal(7, status["planner"].ProcessingRemainingCount);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new MultiplexHandler(name)) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class MultiplexHandler(string clientName) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            string json = clientName.StartsWith("source-", StringComparison.Ordinal)
                ? SourceJson(path)
                : ProcessingJson(path);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }

        private static string SourceJson(string path) => path switch
        {
            "/api/v1/health" => @"{""status"":""ok"",""version"":null,""uptimeSeconds"":1,""message"":null}",
            "/api/v1/stats" => @"{""source"":""jira"",""totalItems"":10,""totalComments"":0,""databaseSizeBytes"":100,""cacheSizeBytes"":0,""cacheFiles"":0,""lastSyncAt"":null,""oldestItem"":null,""newestItem"":null,""additionalCounts"":null}",
            "/api/v1/status" => @"{""source"":""jira"",""status"":""ok"",""lastSyncAt"":null,""itemsTotal"":10,""itemsProcessed"":10,""lastError"":null,""syncSchedule"":""01:00:00"",""indexes"":[]}",
            _ => "{}",
        };

        private static string ProcessingJson(string path) => path switch
        {
            "/api/v1/health" => @"{""status"":""ok"",""version"":null,""uptimeSeconds"":2,""message"":null}",
            "/api/v1/status" => @"{""status"":""running"",""isRunning"":true,""isPaused"":false,""startedAt"":""2026-04-29T00:00:00Z"",""uptimeSeconds"":2,""lastPollAt"":null,""syncSchedule"":""00:05:00"",""maxConcurrentProcessingThreads"":2,""startProcessingOnStartup"":true}",
            "/api/v1/processing/queue" => @"{""processedCount"":3,""remainingCount"":7,""inFlightCount"":1,""errorCount"":0,""averageItemDurationMs"":12.5,""lastItemCompletedAt"":null}",
            _ => "{}",
        };
    }
}


