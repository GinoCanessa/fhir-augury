using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Health;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
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
    public async Task AuthoringEndpoints_PreserveStatusJsonAndSnapshotBytes()
    {
        ProcessingController controller = CreateController(enabled: true);
        JsonElement request = JsonDocument.Parse(
            """{"ticketKeys":["FHIR-1"]}""").RootElement.Clone();

        ContentResult created = Assert.IsType<ContentResult>(
            await controller.CreateAuthoringRun(
                "Planner",
                request,
                CancellationToken.None));
        ContentResult status = Assert.IsType<ContentResult>(
            await controller.GetAuthoringRun(
                "Planner",
                "run-1",
                CancellationToken.None));
        ContentResult retry = Assert.IsType<ContentResult>(
            await controller.RetryAuthoringItem(
                "Planner",
                "run-1",
                "item-1",
                CancellationToken.None));
        ContentResult supersede = Assert.IsType<ContentResult>(
            await controller.SupersedeAuthoringItem(
                "Planner",
                "run-1",
                "item-1",
                JsonDocument.Parse(
                    """{"reason":"not actionable"}""").RootElement.Clone(),
                CancellationToken.None));
        ContentResult descriptor = Assert.IsType<ContentResult>(
            await controller.GetAuthoringSnapshot(
                "Planner",
                "run-1",
                CancellationToken.None));
        FileContentResult bytes = Assert.IsType<FileContentResult>(
            await controller.GetAuthoringSnapshotBytes(
                "Planner",
                "run-1",
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status202Accepted, created.StatusCode);
        Assert.Contains("run-1", status.Content);
        Assert.Contains("item-1", retry.Content);
        Assert.Equal(StatusCodes.Status409Conflict, supersede.StatusCode);
        Assert.Equal("application/json; charset=utf-8", supersede.ContentType);
        Assert.Contains("receipt-backed", supersede.Content);
        Assert.Equal(
            "11",
            controller.Response.Headers.RetryAfter.ToString());
        Assert.Contains("snapshot-1", descriptor.Content);
        Assert.Equal([1, 2, 3, 4], bytes.FileContents);
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
        return new ProcessingController(processingClient, monitor)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
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
                "/processing/authoring/runs" => RunEnvelope,
                "/processing/authoring/runs/run-1" => RunEnvelope,
                "/processing/authoring/runs/run-1/items/item-1/retry" =>
                    """{"itemId":"item-1","requiresAuthoring":true}""",
                "/processing/authoring/runs/run-1/items/item-1/supersede" =>
                    """{"error":"InvalidState","detail":"receipt-backed items cannot be superseded"}""",
                "/processing/authoring/runs/run-1/snapshot" =>
                    """{"processorKind":"jira-fhir","runId":"run-1","snapshotId":"snapshot-1","authoringEpoch":1,"sequence":1,"schemaVersion":1,"sha256":"x","sizeBytes":4,"itemCount":1,"receiptCount":1,"tableCounts":{},"fileName":"snapshot.db","createdAt":"2026-09-04T00:00:00Z"}""",
                _ => "{}",
            };
            HttpStatusCode status = path == "/processing/authoring/runs" &&
                request.Method == HttpMethod.Post
                ? HttpStatusCode.Accepted
                : path.EndsWith("/supersede", StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : HttpStatusCode.OK;
            HttpResponseMessage response = new(status)
            {
                Content = path.EndsWith(
                    "/snapshot/bytes",
                    StringComparison.Ordinal)
                    ? new ByteArrayContent([1, 2, 3, 4])
                    : new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),
            };
            if (path.EndsWith("/retry", StringComparison.Ordinal))
            {
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(7));
            }
            if (path.EndsWith("/supersede", StringComparison.Ordinal))
            {
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(11));
            }
            return Task.FromResult(response);
        }

        private const string RunEnvelope =
            """{"run":{"runId":"run-1","processorKind":"jira-fhir","authoringEpoch":1,"status":"running","databaseOnly":false,"totalItems":1,"completedItems":0,"failedItems":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null},"items":[{"itemId":"item-1","runId":"run-1","businessKey":"FHIR-1","itemKind":"jira-ticket","expectedSourceRevision":"rev-1","status":"pending","currentOperationId":null,"acceptedReceiptId":null,"attemptCount":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null}]}""";
    }
}
