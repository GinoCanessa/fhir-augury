using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class ProcessingControllerTests
{
    [Fact]
    public async Task ProxyEndpoints_ReturnProcessingContracts()
    {
        ProcessingController controller = CreateController(enabled: true);

        OkObjectResult status = Assert.IsType<OkObjectResult>(await controller.GetStatus("Planner", CancellationToken.None));
        OkObjectResult queue = Assert.IsType<OkObjectResult>(await controller.GetQueue("Planner", CancellationToken.None));
        OkObjectResult start = Assert.IsType<OkObjectResult>(await controller.Start("Planner", CancellationToken.None));
        OkObjectResult stop = Assert.IsType<OkObjectResult>(await controller.Stop("Planner", CancellationToken.None));
        OkObjectResult health = Assert.IsType<OkObjectResult>(await controller.Health("Planner", CancellationToken.None));

        Assert.Equal("running", Json(status.Value).GetProperty("Status").GetString());
        Assert.Equal(7, Json(queue.Value).GetProperty("RemainingCount").GetInt32());
        Assert.Equal("running", Json(start.Value).GetProperty("Status").GetString());
        Assert.Equal("paused", Json(stop.Value).GetProperty("Status").GetString());
        Assert.Equal("ok", Json(health.Value).GetProperty("Status").GetString());
    }

    [Fact]
    public void GetServices_ListsConfiguredProcessingServices()
    {
        ProcessingController controller = CreateController(enabled: true);

        OkObjectResult result = Assert.IsType<OkObjectResult>(controller.GetServices());
        JsonElement json = Json(result.Value);
        JsonElement service = json.GetProperty("services")[0];

        Assert.Equal("Planner", service.GetProperty("name").GetString());
        Assert.True(service.GetProperty("enabled").GetBoolean());
        Assert.Equal("FHIR planning", service.GetProperty("description").GetString());
        Assert.Equal("http://planner", service.GetProperty("httpAddress").GetString());
    }

    [Fact]
    public async Task ProxyEndpoints_ReturnNotFound_ForDisabledService()
    {
        ProcessingController controller = CreateController(enabled: false);

        IActionResult result = await controller.GetStatus("Planner", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    private static ProcessingController CreateController(bool enabled)
    {
        OrchestratorOptions options = new()
        {
            ProcessingServices = new Dictionary<string, ProcessingServiceConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = new ProcessingServiceConfig
                {
                    HttpAddress = "http://planner",
                    Enabled = enabled,
                    Description = "FHIR planning",
                },
            },
        };
        IOptions<OrchestratorOptions> optionsAccessor = Options.Create(options);
        ProxyHandler handler = new();
        TestHttpClientFactory factory = new(handler);
        SourceHttpClient sourceClient = new(factory, optionsAccessor, NullLogger<SourceHttpClient>.Instance);
        ProcessingHttpClient processingClient = new(factory, optionsAccessor, NullLogger<ProcessingHttpClient>.Instance);
        ServiceHealthMonitor monitor = new(sourceClient, optionsAccessor, NullLogger<ServiceHealthMonitor>.Instance, processingClient);
        return new ProcessingController(processingClient, monitor);
    }

    private static JsonElement Json(object? value)
    {
        string json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed class TestHttpClientFactory(ProxyHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class ProxyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            string json = path switch
            {
                "/api/v1/status" => @"{""status"":""running"",""isRunning"":true,""isPaused"":false,""startedAt"":""2026-04-29T00:00:00Z"",""uptimeSeconds"":1,""lastPollAt"":null,""syncSchedule"":""00:05:00"",""maxConcurrentProcessingThreads"":2,""startProcessingOnStartup"":true}",
                "/api/v1/processing/queue" => @"{""processedCount"":3,""remainingCount"":7,""inFlightCount"":1,""errorCount"":0,""averageItemDurationMs"":12.5,""lastItemCompletedAt"":null}",
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

