using System.Net;
using System.Text;
using System.Text.Json;
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
    public async Task AuthoringControlAndSnapshot_ForwardWithoutRewritingPayloads()
    {
        RecordingHandler handler = new();
        ProcessingHttpClient client = CreateClient(handler);
        JsonElement request = JsonDocument.Parse(
            """{"ticketKeys":["FHIR-1"],"databaseOnly":false}""")
            .RootElement.Clone();

        ProcessingProxyResponse created =
            await client.CreateAuthoringRunAsync(
                "Planner",
                request,
                CancellationToken.None);
        ProcessingProxyResponse status =
            await client.GetAuthoringRunAsync(
                "Planner",
                "run-1",
                CancellationToken.None);
        ProcessingProxyResponse retry =
            await client.RetryAuthoringItemAsync(
                "Planner",
                "run-1",
                "item-1",
                CancellationToken.None);
        ProcessingProxyResponse supersede =
            await client.SupersedeAuthoringItemAsync(
                "Planner",
                "run-1",
                "item-1",
                JsonDocument.Parse(
                    """{"reason":"not actionable"}""").RootElement.Clone(),
                CancellationToken.None);
        ProcessingProxyResponse descriptor =
            await client.GetAuthoringSnapshotAsync(
                "Planner",
                "run-1",
                bytes: false,
                CancellationToken.None);
        ProcessingProxyResponse bytes =
            await client.GetAuthoringSnapshotAsync(
                "Planner",
                "run-1",
                bytes: true,
                CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("7", retry.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, supersede.StatusCode);
        Assert.Equal("11", supersede.RetryAfter);
        Assert.Equal(
            "application/json; charset=utf-8",
            supersede.ContentType);
        Assert.Contains(
            "not actionable",
            Encoding.UTF8.GetString(supersede.Content));
        Assert.Equal("application/json; charset=utf-8", descriptor.ContentType);
        Assert.Equal([1, 2, 3, 4], bytes.Content);
        Assert.Contains("/processing/authoring/runs", handler.Paths);
        Assert.Contains(
            "/processing/authoring/runs/run-1/snapshot/bytes",
            handler.Paths);
        Assert.Contains(
            "/processing/authoring/runs/run-1/items/item-1/supersede",
            handler.Paths);
        Assert.Contains(
            handler.Bodies,
            body => body.Contains("FHIR-1", StringComparison.Ordinal));
        Assert.Contains(
            handler.Bodies,
            body => JsonDocument.Parse(body).RootElement
                .TryGetProperty("reason", out JsonElement reason) &&
                reason.GetString() == "not actionable");
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
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            Paths.Add(path);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
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
                "/processing/authoring/runs" => RunEnvelope,
                "/processing/authoring/runs/run-1" => RunEnvelope,
                "/processing/authoring/runs/run-1/items/item-1/retry" =>
                    """{"itemId":"item-1","requiresAuthoring":true}""",
                "/processing/authoring/runs/run-1/items/item-1/supersede" =>
                    """{"runId":"run-1","itemId":"item-1","status":"superseded","reason":"not actionable"}""",
                "/processing/authoring/runs/run-1/snapshot" =>
                    """{"processorKind":"jira-fhir","runId":"run-1","snapshotId":"snapshot-1","authoringEpoch":1,"sequence":1,"schemaVersion":1,"sha256":"x","sizeBytes":4,"itemCount":1,"receiptCount":1,"tableCounts":{},"fileName":"snapshot.db","createdAt":"2026-09-04T00:00:00Z"}""",
                _ => "{}",
            };
            HttpStatusCode status = path == "/processing/authoring/runs" &&
                request.Method == HttpMethod.Post
                ? HttpStatusCode.Accepted
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
            if (path.EndsWith("/snapshot/bytes", StringComparison.Ordinal))
            {
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(
                        "application/vnd.sqlite3");
            }
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
            return response;
        }

        private const string RunEnvelope =
            """{"run":{"runId":"run-1","processorKind":"jira-fhir","authoringEpoch":1,"status":"running","databaseOnly":false,"totalItems":1,"completedItems":0,"failedItems":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null},"items":[{"itemId":"item-1","runId":"run-1","businessKey":"FHIR-1","itemKind":"jira-ticket","expectedSourceRevision":"rev-1","status":"pending","currentOperationId":null,"acceptedReceiptId":null,"attemptCount":0,"createdAt":"2026-09-04T00:00:00Z","startedAt":null,"completedAt":null,"error":null}]}""";
    }
}
