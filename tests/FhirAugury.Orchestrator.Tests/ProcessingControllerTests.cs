using System.Net;
using System.Net.Http.Headers;
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
        string location = controller.Response.Headers.Location.ToString();
        ContentResult list = Assert.IsType<ContentResult>(
            await controller.ListAuthoringRuns(
                "Planner",
                25,
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
        ProcessingProxyActionResult bytes =
            Assert.IsType<ProcessingProxyActionResult>(
            await controller.GetAuthoringSnapshotBytes(
                "Planner",
                "run-1",
                CancellationToken.None));
        MemoryStream streamedBytes = new();
        controller.Response.Body = streamedBytes;
        await bytes.ExecuteResultAsync(controller.ControllerContext);

        Assert.Equal(StatusCodes.Status202Accepted, created.StatusCode);
        Assert.Equal(
            "/api/v1/processing-services/Planner/authoring/runs/run-1",
            location);
        Assert.Contains("run-1", list.Content);
        Assert.Contains("run-1", status.Content);
        Assert.Contains("item-1", retry.Content);
        Assert.Equal(StatusCodes.Status409Conflict, supersede.StatusCode);
        Assert.Equal("application/json; charset=utf-8", supersede.ContentType);
        Assert.Contains("receipt-backed", supersede.Content);
        Assert.Equal(
            "11",
            controller.Response.Headers.RetryAfter.ToString());
        Assert.Contains("snapshot-1", descriptor.Content);
        Assert.Equal(StatusCodes.Status206PartialContent, controller.Response.StatusCode);
        Assert.Equal("bytes 0-3/4", controller.Response.Headers.ContentRange.ToString());
        Assert.Equal([1, 2, 3, 4], streamedBytes.ToArray());
    }

    [Fact]
    public async Task PublicationRefresh_PreservesAcceptedBodyAndHeaders()
    {
        ProcessingController controller = CreateController(enabled: true);

        ContentResult result = Assert.IsType<ContentResult>(
            await controller.StartPublicationRefresh(
                "Planner",
                "source-run",
                CancellationToken.None));
        JsonElement body = JsonDocument.Parse(result.Content!).RootElement;

        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.Equal(
            "refresh-run",
            body.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal(
            "publication-refresh",
            body.GetProperty("run").GetProperty("purpose").GetString());
        Assert.Equal(
            "source-run",
            body.GetProperty("run").GetProperty("sourceRunId").GetString());
        Assert.Equal(
            "/api/v1/processing-services/Planner/authoring/runs/refresh-run",
            controller.Response.Headers.Location.ToString());
        Assert.Equal(
            "5",
            controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task PublicationRefresh_PreservesStructuredConflict()
    {
        ProcessingController controller = CreateController(enabled: true);

        ContentResult result = Assert.IsType<ContentResult>(
            await controller.StartPublicationRefresh(
                "Planner",
                "busy-run",
                CancellationToken.None));
        JsonElement body = JsonDocument.Parse(result.Content!).RootElement;

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.Equal(
            "mutation-fence-unavailable",
            body.GetProperty("error").GetString());
        Assert.Equal(
            "active-run",
            body.GetProperty("conflictingRunIds")[0].GetString());
        Assert.Equal(
            "13",
            controller.Response.Headers.RetryAfter.ToString());
        Assert.Equal(
            string.Empty,
            controller.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task CanonicalRestriction_PreservesExactBodyAndCoordinates()
    {
        ProcessingController controller = CreateController(enabled: true);

        ContentResult result = Assert.IsType<ContentResult>(
            await controller.StartPublicationRefresh(
                "Planner",
                "restricted-run",
                CancellationToken.None));
        JsonElement body = JsonDocument.Parse(result.Content!).RootElement;

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal(
            "canonical-unpublished-restriction",
            body.GetProperty("error").GetString());
        Assert.Equal(
            ["abandoned-run", "recovery-run"],
            body.GetProperty("conflictingRunIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task PublicationReconciliation_PreservesLifecycleBodiesAndHeaders()
    {
        ProcessingController controller = CreateController(enabled: true);

        ContentResult started = Assert.IsType<ContentResult>(
            await controller.StartPublicationReconciliation(
                "Planner",
                "source-run",
                CancellationToken.None));
        ContentResult status = Assert.IsType<ContentResult>(
            await controller.GetPublicationReconciliation(
                "Planner",
                "reconciliation-run",
                CancellationToken.None));
        ContentResult retry = Assert.IsType<ContentResult>(
            await controller.RetryPublicationReconciliation(
                "Planner",
                "reconciliation-run",
                CancellationToken.None));
        ContentResult cancel = Assert.IsType<ContentResult>(
            await controller.CancelPublicationReconciliation(
                "Planner",
                "reconciliation-run",
                JsonDocument.Parse(
                    """{"reason":"frozen revision changed"}""")
                    .RootElement.Clone(),
                CancellationToken.None));
        ContentResult abandon = Assert.IsType<ContentResult>(
            await controller.AbandonPublicationReconciliation(
                "Planner",
                "reconciliation-run",
                JsonDocument.Parse(
                    """{"reason":"operator accepted risk"}""")
                    .RootElement.Clone(),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status202Accepted, started.StatusCode);
        Assert.Equal(
            "/api/v1/processing-services/Planner/authoring/runs/reconciliation-run/publication-reconciliation",
            controller.Response.Headers.Location.ToString());
        Assert.Contains("stableJiraGeneration", status.Content);
        Assert.Equal(StatusCodes.Status409Conflict, retry.StatusCode);
        Assert.Equal("17", controller.Response.Headers.RetryAfter.ToString());
        Assert.Contains("recovery-in-progress", retry.Content);
        Assert.Equal(StatusCodes.Status409Conflict, cancel.StatusCode);
        Assert.Contains("cancellation-not-allowed", cancel.Content);
        Assert.Equal(StatusCodes.Status200OK, abandon.StatusCode);
        Assert.Contains("canonical-unpublished", abandon.Content);
    }

    [Fact]
    public async Task CanonicalEpochRecovery_PreservesLifecycleBodiesAndHeaders()
    {
        ProcessingController controller = CreateController(enabled: true);

        ContentResult started = Assert.IsType<ContentResult>(
            await controller.StartCanonicalEpochRecovery(
                "Planner",
                "abandoned-run",
                CancellationToken.None));
        ContentResult status = Assert.IsType<ContentResult>(
            await controller.GetCanonicalEpochRecovery(
                "Planner",
                "recovery-run",
                CancellationToken.None));
        ContentResult retry = Assert.IsType<ContentResult>(
            await controller.RetryCanonicalEpochRecovery(
                "Planner",
                "recovery-run",
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status202Accepted, started.StatusCode);
        Assert.Equal(
            "/api/v1/processing-services/Planner/authoring/runs/recovery-run/canonical-epoch-recovery",
            controller.Response.Headers.Location.ToString());
        Assert.Contains("abandoned-run", started.Content);
        Assert.Contains("snapshot-publish-pending", status.Content);
        Assert.Equal(StatusCodes.Status409Conflict, retry.StatusCode);
        Assert.Contains("canonical-state-changed", retry.Content);
        Assert.Equal("23", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task SnapshotBytes_PreservesNotModifiedWithoutWritingBody()
    {
        ProcessingController controller = CreateController(
            enabled: true,
            snapshotStatus: HttpStatusCode.NotModified);
        controller.Request.Headers.IfNoneMatch = "\"snapshot\"";
        MemoryStream body = new();
        controller.Response.Body = body;

        ProcessingProxyActionResult result =
            Assert.IsType<ProcessingProxyActionResult>(
                await controller.GetAuthoringSnapshotBytes(
                    "Planner",
                    "run-1",
                    CancellationToken.None));
        await result.ExecuteResultAsync(controller.ControllerContext);

        Assert.Equal(StatusCodes.Status304NotModified, controller.Response.StatusCode);
        Assert.Equal("\"snapshot\"", controller.Response.Headers.ETag.ToString());
        Assert.Empty(body.ToArray());
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
        IActionResult refresh = await controller.StartPublicationRefresh(
            "Planner",
            "source-run",
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.IsType<NotFoundObjectResult>(refresh);
    }

    private static ProcessingController CreateController(
        bool enabled,
        HttpStatusCode snapshotStatus = HttpStatusCode.PartialContent)
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
        ProxyHandler handler = new(snapshotStatus);
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

    private sealed class ProxyHandler(HttpStatusCode snapshotStatus)
        : HttpMessageHandler
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
                "/processing/authoring/runs/source-run/publication-refresh" =>
                    RefreshRunEnvelope,
                "/processing/authoring/runs/busy-run/publication-refresh" =>
                    """{"error":"mutation-fence-unavailable","detail":"Another mutation is active.","conflictingRunIds":["active-run"],"runId":"active-run"}""",
                "/processing/authoring/runs/restricted-run/publication-refresh" =>
                    """{"error":"canonical-unpublished-restriction","detail":"A canonical epoch remains unpublished.","conflictingRunIds":["abandoned-run","recovery-run"],"runId":null}""",
                "/processing/authoring/runs/source-run/publication-reconciliation" =>
                    ReconciliationStartEnvelope,
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation" =>
                    ReconciliationStatusEnvelope,
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/retry" =>
                    """{"error":"recovery-in-progress","detail":"Snapshot publication is still pending.","conflictingRunIds":["reconciliation-run"],"runId":"reconciliation-run"}""",
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/cancel" =>
                    """{"error":"cancellation-not-allowed","detail":"Canonical promotion already started.","conflictingRunIds":[],"runId":"reconciliation-run"}""",
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/abandon" =>
                    ReconciliationAbandonEnvelope,
                "/processing/authoring/runs/abandoned-run/canonical-epoch-recovery" =>
                    """{"status":{"run":{"runId":"recovery-run","purpose":"canonical-epoch-recovery","sourceRunId":"abandoned-run"},"sourceAbandonment":{"runId":"abandoned-run"},"recovery":{"state":"materialization-pending"}},"existingRun":false}""",
                "/processing/authoring/runs/recovery-run/canonical-epoch-recovery" =>
                    """{"run":{"runId":"recovery-run","purpose":"canonical-epoch-recovery","sourceRunId":"abandoned-run"},"sourceAbandonment":{"runId":"abandoned-run"},"recovery":{"state":"snapshot-publish-pending"}}""",
                "/processing/authoring/runs/recovery-run/canonical-epoch-recovery/retry" =>
                    """{"error":"canonical-state-changed","detail":"The frozen canonical grouping changed.","conflictingRunIds":["abandoned-run","recovery-run"],"runId":"recovery-run"}""",
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
                : path == "/processing/authoring/runs/source-run/publication-refresh"
                    ? HttpStatusCode.Accepted
                : path == "/processing/authoring/runs/busy-run/publication-refresh"
                    ? HttpStatusCode.Conflict
                : path == "/processing/authoring/runs/restricted-run/publication-refresh"
                    ? HttpStatusCode.Conflict
                : path == "/processing/authoring/runs/source-run/publication-reconciliation"
                    ? HttpStatusCode.Accepted
                : path == "/processing/authoring/runs/abandoned-run/canonical-epoch-recovery"
                    ? HttpStatusCode.Accepted
                : path.EndsWith(
                    "/canonical-epoch-recovery/retry",
                    StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith(
                    "/publication-reconciliation/retry",
                    StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith(
                    "/publication-reconciliation/cancel",
                    StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith("/supersede", StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith("/snapshot/bytes", StringComparison.Ordinal)
                    ? snapshotStatus
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
            if (path == "/processing/authoring/runs" &&
                request.Method == HttpMethod.Post)
            {
                response.Headers.Location = new Uri(
                    "/processing/authoring/runs/run-1",
                    UriKind.Relative);
            }
            if (path == "/processing/authoring/runs/source-run/publication-refresh")
            {
                response.Headers.Location = new Uri(
                    "/processing/authoring/runs/refresh-run",
                    UriKind.Relative);
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(5));
            }
            if (path == "/processing/authoring/runs/busy-run/publication-refresh")
            {
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(13));
            }
            if (path == "/processing/authoring/runs/source-run/publication-reconciliation")
            {
                response.Headers.Location = new Uri(
                    "/processing/authoring/runs/reconciliation-run/publication-reconciliation",
                    UriKind.Relative);
            }
            if (path == "/processing/authoring/runs/abandoned-run/canonical-epoch-recovery")
            {
                response.Headers.Location = new Uri(
                    "/processing/authoring/runs/recovery-run/canonical-epoch-recovery",
                    UriKind.Relative);
            }
            if (path.EndsWith(
                "/canonical-epoch-recovery/retry",
                StringComparison.Ordinal))
            {
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(23));
            }
            else if (path.EndsWith(
                "/publication-reconciliation/retry",
                StringComparison.Ordinal))
            {
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(17));
            }
            if (path.EndsWith("/snapshot/bytes", StringComparison.Ordinal))
            {
                response.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/vnd.sqlite3");
                response.Content.Headers.ContentLength = 4;
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(0, 3, 4);
                response.Headers.AcceptRanges.Add("bytes");
                response.Headers.ETag =
                    new EntityTagHeaderValue("\"snapshot\"");
            }
            return Task.FromResult(response);
        }

        private const string RunEnvelope =
            """{"run":{"runId":"run-1","processorKind":"jira-fhir","authoringEpoch":1,"status":"running","databaseOnly":false,"totalItems":1,"completedItems":0,"failedItems":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null},"items":[{"itemId":"item-1","runId":"run-1","businessKey":"FHIR-1","itemKind":"jira-ticket","expectedSourceRevision":"rev-1","status":"pending","currentOperationId":null,"acceptedReceiptId":null,"attemptCount":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null}]}""";

        private const string RefreshRunEnvelope =
            """{"run":{"runId":"refresh-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"queued","databaseOnly":false,"totalItems":1,"completedItems":1,"failedItems":0,"createdAt":"2026-09-14T00:00:00Z","startedAt":null,"completedAt":null,"error":null,"purpose":"publication-refresh","sourceRunId":"source-run"},"items":[{"itemId":"item-1","runId":"refresh-run","businessKey":"FHIR-1","itemKind":"jira-ticket","expectedSourceRevision":"rev-1","status":"completed","currentOperationId":null,"acceptedReceiptId":"receipt-1","attemptCount":0,"createdAt":"2026-09-14T00:00:00Z","startedAt":null,"completedAt":"2026-09-14T00:00:00Z","error":null}]}""";

        private const string ReconciliationStartEnvelope =
            """{"run":{"runId":"reconciliation-run","purpose":"publication-reconciliation"},"comparison":{"stableJiraGeneration":"generation-1"}}""";

        private const string ReconciliationStatusEnvelope =
            """{"run":{"runId":"reconciliation-run","purpose":"publication-reconciliation"},"comparison":{"stableJiraGeneration":"generation-1"},"promotion":{"state":"snapshot-publish-pending"}}""";

        private const string ReconciliationAbandonEnvelope =
            """{"status":{"run":{"runId":"reconciliation-run"},"promotion":{"state":"canonical-unpublished"}},"reason":"operator accepted risk"}""";
    }
}
