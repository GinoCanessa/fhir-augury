using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Processing.Common.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    public async Task AuthoringControlAndList_ForwardStatusBodiesAndHeaders()
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
        ProcessingProxyResponse list =
            await client.ListAuthoringRunsAsync(
                "Planner",
                37,
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
                CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal(
            "/api/v1/processing-services/Planner/authoring/runs/run-1",
            created.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("run-1", Encoding.UTF8.GetString(list.Content));
        Assert.Contains(
            handler.RequestTargets,
            target => target.EndsWith("?limit=37", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("7", retry.RetryAfter);
        Assert.Equal(HttpStatusCode.Conflict, supersede.StatusCode);
        Assert.Equal("11", supersede.RetryAfter);
        Assert.Equal(
            "application/json; charset=utf-8",
            supersede.ContentType);
        Assert.Contains(
            "receipt-backed",
            Encoding.UTF8.GetString(supersede.Content));
        Assert.Equal("application/json; charset=utf-8", descriptor.ContentType);
        Assert.Contains("/processing/authoring/runs", handler.Paths);
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
    public async Task PublicationRefresh_ForwardsExactPathAndRewritesLocation()
    {
        RecordingHandler handler = new();
        ProcessingHttpClient client = CreateClient(handler);

        ProcessingProxyResponse response =
            await client.StartPublicationRefreshAsync(
                "Preparer",
                "source-run",
                CancellationToken.None);
        JsonElement body = JsonDocument.Parse(response.Content).RootElement;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("refresh-run", body.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal(
            "publication-refresh",
            body.GetProperty("run").GetProperty("purpose").GetString());
        Assert.Equal(
            "source-run",
            body.GetProperty("run").GetProperty("sourceRunId").GetString());
        Assert.Equal("5", response.RetryAfter);
        Assert.Equal(
            "/api/v1/processing-services/Preparer/authoring/runs/refresh-run",
            response.Location);
        Assert.Equal(
            ["POST /processing/authoring/runs/source-run/publication-refresh"],
            handler.Requests);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task PublicationReconciliation_ForwardsTypedLifecycleRoutes()
    {
        RecordingHandler handler = new();
        ProcessingHttpClient client = CreateClient(handler);
        JsonElement abandon = JsonDocument.Parse(
            """{"reason":"operator accepted risk"}""")
            .RootElement.Clone();
        JsonElement cancel = JsonDocument.Parse(
            """{"reason":"frozen revision changed"}""")
            .RootElement.Clone();

        ProcessingProxyResponse started =
            await client.StartPublicationReconciliationAsync(
                "Preparer",
                "source-run",
                CancellationToken.None);
        ProcessingProxyResponse status =
            await client.GetPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                CancellationToken.None);
        ProcessingProxyResponse retried =
            await client.RetryPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                CancellationToken.None);
        ProcessingProxyResponse cancelled =
            await client.CancelPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                cancel,
                CancellationToken.None);
        ProcessingProxyResponse abandoned =
            await client.AbandonPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                abandon,
                CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal(
            "/api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation",
            started.Location);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, retried.StatusCode);
        Assert.Equal("17", retried.RetryAfter);
        Assert.Contains("recovery-in-progress", Encoding.UTF8.GetString(retried.Content));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Contains(
            "\"state\":\"cancelled\"",
            Encoding.UTF8.GetString(cancelled.Content));
        Assert.Equal(HttpStatusCode.OK, abandoned.StatusCode);
        Assert.Equal(
            [
                "POST /processing/authoring/runs/source-run/publication-reconciliation",
                "GET /processing/authoring/runs/reconciliation-run/publication-reconciliation",
                "POST /processing/authoring/runs/reconciliation-run/publication-reconciliation/retry",
                "POST /processing/authoring/runs/reconciliation-run/publication-reconciliation/cancel",
                "POST /processing/authoring/runs/reconciliation-run/publication-reconciliation/abandon",
            ],
            handler.Requests);
        Assert.Contains(
            handler.Bodies,
            body => JsonDocument.Parse(body).RootElement
                .GetProperty("reason").GetString() ==
                "operator accepted risk");
        Assert.Contains(
            handler.Bodies,
            body => JsonDocument.Parse(body).RootElement
                .GetProperty("reason").GetString() ==
                "frozen revision changed");
    }

    [Fact]
    public async Task SnapshotBytes_AreStreamedWithConditionalHeadersAndDisposed()
    {
        RecordingHandler handler = new();
        ProcessingHttpClient client = CreateClient(handler);
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers.Range = "bytes=1-2";
        httpContext.Request.Headers.IfRange = "\"old\"";
        httpContext.Request.Headers.IfNoneMatch = "\"cached\"";
        httpContext.Request.Headers.IfModifiedSince =
            "Wed, 09 Sep 2026 12:00:00 GMT";
        httpContext.Request.Headers.Connection = "keep-alive";
        MemoryStream outgoing = new();
        httpContext.Response.Body = outgoing;

        ProcessingProxyActionResult result =
            await client.GetAuthoringSnapshotBytesAsync(
                "Planner",
                "run-1",
                httpContext.Request,
                CancellationToken.None);

        Assert.NotNull(handler.SnapshotStream);
        Assert.Equal(0, handler.SnapshotStream.ReadCount);
        Assert.False(handler.SnapshotStream.Disposed);

        await result.ExecuteResultAsync(new ActionContext
        {
            HttpContext = httpContext,
        });

        Assert.Equal(StatusCodes.Status206PartialContent, httpContext.Response.StatusCode);
        Assert.Equal("application/vnd.sqlite3", httpContext.Response.ContentType);
        Assert.Equal(4, httpContext.Response.ContentLength);
        Assert.Equal("bytes", httpContext.Response.Headers.AcceptRanges.ToString());
        Assert.Equal("bytes 0-3/4", httpContext.Response.Headers.ContentRange.ToString());
        Assert.Equal("\"snapshot\"", httpContext.Response.Headers.ETag.ToString());
        Assert.Equal(
            "Wed, 09 Sep 2026 12:00:00 GMT",
            httpContext.Response.Headers.LastModified.ToString());
        Assert.Equal("9", httpContext.Response.Headers.RetryAfter.ToString());
        Assert.Equal(
            "attachment; filename=snapshot.db",
            httpContext.Response.Headers.ContentDisposition.ToString());
        Assert.Equal([1, 2, 3, 4], outgoing.ToArray());
        Assert.True(handler.SnapshotStream.ReadCount > 0);
        Assert.True(handler.SnapshotStream.Disposed);
        Assert.Equal("bytes=1-2", handler.SnapshotRequestHeaders["Range"]);
        Assert.Equal("\"old\"", handler.SnapshotRequestHeaders["If-Range"]);
        Assert.Equal("\"cached\"", handler.SnapshotRequestHeaders["If-None-Match"]);
        Assert.Equal(
            "Wed, 09 Sep 2026 12:00:00 GMT",
            handler.SnapshotRequestHeaders["If-Modified-Since"]);
        Assert.DoesNotContain("Connection", handler.SnapshotRequestHeaders.Keys);
    }

    [Fact]
    public async Task ProcessingProxyActionResult_DisposesUpstreamWhenStreamBreaks()
    {
        ThrowingReadStream stream = new();
        HttpResponseMessage upstream = new(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        ProcessingProxyActionResult result = new(upstream);
        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(
            () => result.ExecuteResultAsync(new ActionContext
            {
                HttpContext = httpContext,
            }));

        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ProcessingProxyActionResult_DisposesUpstreamOnCallerCancellation()
    {
        BlockingReadStream stream = new();
        HttpResponseMessage upstream = new(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        ProcessingProxyActionResult result = new(upstream);
        using CancellationTokenSource cts =
            new(TimeSpan.FromMilliseconds(25));
        DefaultHttpContext httpContext = new();
        httpContext.RequestAborted = cts.Token;
        httpContext.Response.Body = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => result.ExecuteResultAsync(new ActionContext
            {
                HttpContext = httpContext,
            }));

        Assert.True(stream.Disposed);
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
        public List<string> RequestTargets { get; } = [];
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public Dictionary<string, string> SnapshotRequestHeaders { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public TrackingStream? SnapshotStream { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            Paths.Add(path);
            RequestTargets.Add(request.RequestUri?.PathAndQuery ?? "");
            Requests.Add(
                $"{request.Method} {request.RequestUri?.PathAndQuery}");
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
                "/processing/authoring/runs/source-run/publication-refresh" =>
                    RefreshRunEnvelope,
                "/processing/authoring/runs/source-run/publication-reconciliation" =>
                    ReconciliationStartEnvelope,
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation" =>
                    ReconciliationStatusEnvelope,
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/retry" =>
                    """{"error":"recovery-in-progress","detail":"Snapshot publication is still pending.","conflictingRunIds":["reconciliation-run"],"runId":"reconciliation-run"}""",
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/cancel" =>
                    ReconciliationCancelEnvelope,
                "/processing/authoring/runs/reconciliation-run/publication-reconciliation/abandon" =>
                    ReconciliationAbandonEnvelope,
                "/processing/authoring/runs/run-1/items/item-1/retry" =>
                    """{"itemId":"item-1","requiresAuthoring":true}""",
                "/processing/authoring/runs/run-1/items/item-1/supersede" =>
                    """{"error":"InvalidState","detail":"receipt-backed items cannot be superseded","conflictingRunIds":[],"runId":"run-1"}""",
                "/processing/authoring/runs/run-1/snapshot" =>
                    """{"processorKind":"jira-fhir","runId":"run-1","snapshotId":"snapshot-1","authoringEpoch":1,"sequence":1,"schemaVersion":1,"sha256":"x","sizeBytes":4,"itemCount":1,"receiptCount":1,"tableCounts":{},"fileName":"snapshot.db","createdAt":"2026-09-04T00:00:00Z"}""",
                _ => "{}",
            };
            HttpStatusCode status = path == "/processing/authoring/runs" &&
                request.Method == HttpMethod.Post
                ? HttpStatusCode.Accepted
                : path == "/processing/authoring/runs/source-run/publication-refresh"
                    ? HttpStatusCode.Accepted
                : path == "/processing/authoring/runs/source-run/publication-reconciliation"
                    ? HttpStatusCode.Accepted
                : path.EndsWith(
                    "/publication-reconciliation/retry",
                    StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith("/supersede", StringComparison.Ordinal)
                    ? HttpStatusCode.Conflict
                : path.EndsWith("/snapshot/bytes", StringComparison.Ordinal)
                    ? HttpStatusCode.PartialContent
                : HttpStatusCode.OK;
            bool snapshotBytes = path.EndsWith(
                "/snapshot/bytes",
                StringComparison.Ordinal);
            if (snapshotBytes)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in
                    request.Headers)
                {
                    SnapshotRequestHeaders[header.Key] =
                        string.Join(", ", header.Value);
                }
                SnapshotStream = new TrackingStream([1, 2, 3, 4]);
            }
            HttpResponseMessage response = new(status)
            {
                Content = snapshotBytes
                    ? new StreamContent(SnapshotStream!)
                    : new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"),
            };
            if (snapshotBytes)
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(
                    "application/vnd.sqlite3");
                response.Content.Headers.ContentLength = 4;
                response.Content.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("attachment")
                    {
                        FileName = "snapshot.db",
                    };
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(0, 3, 4);
                response.Content.Headers.LastModified =
                    new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
                response.Headers.AcceptRanges.Add("bytes");
                response.Headers.ETag =
                    new EntityTagHeaderValue("\"snapshot\"");
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(9));
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
            if (path == "/processing/authoring/runs/source-run/publication-reconciliation")
            {
                response.Headers.Location = new Uri(
                    "/processing/authoring/runs/reconciliation-run/publication-reconciliation",
                    UriKind.Relative);
            }
            if (path.EndsWith(
                "/publication-reconciliation/retry",
                StringComparison.Ordinal))
            {
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(17));
            }
            else if (path.EndsWith("/retry", StringComparison.Ordinal))
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

        private const string RefreshRunEnvelope =
            """{"run":{"runId":"refresh-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"queued","databaseOnly":false,"totalItems":1,"completedItems":1,"failedItems":0,"createdAt":"2026-09-14T00:00:00Z","startedAt":null,"completedAt":null,"error":null,"purpose":"publication-refresh","sourceRunId":"source-run"},"items":[{"itemId":"item-1","runId":"refresh-run","businessKey":"FHIR-1","itemKind":"jira-ticket","expectedSourceRevision":"rev-1","status":"completed","currentOperationId":null,"acceptedReceiptId":"receipt-1","attemptCount":0,"createdAt":"2026-09-14T00:00:00Z","startedAt":null,"completedAt":"2026-09-14T00:00:00Z","error":null}]}""";

        private const string ReconciliationStartEnvelope =
            """{"run":{"runId":"reconciliation-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"queued","databaseOnly":false,"totalItems":1,"completedItems":0,"failedItems":0,"createdAt":"2026-09-16T00:00:00Z","startedAt":null,"completedAt":null,"error":null,"purpose":"publication-reconciliation","sourceRunId":"source-run"},"items":[],"comparison":{"contractVersion":1,"sourceRunId":"source-run","sourceSnapshotId":"snapshot-1","sourceSnapshotSha256":"abc","stableJiraGeneration":"generation-1","capturedAt":"2026-09-16T00:00:00Z","corpusFingerprint":"def","items":[]},"counts":{"acceptedTicketCount":1,"carryForwardTicketCount":0,"reAuthorTicketCount":1,"invalidatedTicketCount":0}}""";

        private const string ReconciliationStatusEnvelope =
            """{"run":{"runId":"reconciliation-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"finalizing","databaseOnly":false,"totalItems":1,"completedItems":1,"failedItems":0,"createdAt":"2026-09-16T00:00:00Z","startedAt":null,"completedAt":null,"error":null,"purpose":"publication-reconciliation","sourceRunId":"source-run"},"items":[],"comparison":{"contractVersion":1,"sourceRunId":"source-run","sourceSnapshotId":"snapshot-1","sourceSnapshotSha256":"abc","stableJiraGeneration":"generation-1","capturedAt":"2026-09-16T00:00:00Z","corpusFingerprint":"def","items":[]},"counts":{"acceptedTicketCount":1,"carryForwardTicketCount":0,"reAuthorTicketCount":1,"invalidatedTicketCount":0},"groupingImpacts":[],"promotion":{"state":"snapshot-publish-pending","journalState":"database-promoted","mutationFenceHeld":true},"invalidatedTicketKeys":[]}""";

        private const string ReconciliationAbandonEnvelope =
            """{"status":{"run":{"runId":"reconciliation-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"failed","databaseOnly":false,"totalItems":1,"completedItems":1,"failedItems":0,"createdAt":"2026-09-16T00:00:00Z","startedAt":null,"completedAt":null,"error":null,"purpose":"publication-reconciliation","sourceRunId":"source-run"},"items":[],"comparison":{"contractVersion":1,"sourceRunId":"source-run","sourceSnapshotId":"snapshot-1","sourceSnapshotSha256":"abc","stableJiraGeneration":"generation-1","capturedAt":"2026-09-16T00:00:00Z","corpusFingerprint":"def","items":[]},"counts":{"acceptedTicketCount":1,"carryForwardTicketCount":0,"reAuthorTicketCount":1,"invalidatedTicketCount":0},"groupingImpacts":[],"promotion":{"state":"canonical-unpublished","journalState":"abandoned","mutationFenceHeld":false},"invalidatedTicketKeys":[]},"abandonedAt":"2026-09-16T18:00:00Z","reason":"operator accepted risk"}""";

        private const string ReconciliationCancelEnvelope =
            """{"status":{"run":{"runId":"reconciliation-run","processorKind":"jira-fhir","authoringEpoch":1,"status":"superseded","databaseOnly":false,"totalItems":1,"completedItems":0,"failedItems":1,"createdAt":"2026-09-16T00:00:00Z","startedAt":null,"completedAt":"2026-09-17T18:00:00Z","error":"cancelled","purpose":"publication-reconciliation","sourceRunId":"source-run"},"items":[],"comparison":{"contractVersion":1,"sourceRunId":"source-run","sourceSnapshotId":"snapshot-1","sourceSnapshotSha256":"abc","stableJiraGeneration":"generation-1","capturedAt":"2026-09-16T00:00:00Z","corpusFingerprint":"def","items":[]},"counts":{"acceptedTicketCount":1,"carryForwardTicketCount":0,"reAuthorTicketCount":1,"invalidatedTicketCount":0},"groupingImpacts":[],"promotion":{"state":"cancelled","journalState":"cancelled","mutationFenceHeld":false,"cancelledAt":"2026-09-17T18:00:00Z","cancellationReason":"frozen revision changed"},"invalidatedTicketKeys":[]},"cancelledAt":"2026-09-17T18:00:00Z","reason":"frozen revision changed"}""";
    }

    private sealed class TrackingStream(byte[] buffer) : MemoryStream(buffer)
    {
        public bool Disposed { get; private set; }
        public int ReadCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("broken stream");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new IOException("broken stream"));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
