using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client.Tests;

public sealed class AuthoringControlClientTests
{
    [Fact]
    public async Task TypedMethodsUseOrchestratorAuthoringRoutes()
    {
        List<string> targets = [];
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            targets.Add(
                $"{request.Method} {request.RequestUri!.PathAndQuery}");
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/retry", StringComparison.Ordinal))
            {
                return DelegateHttpHandler.Json(
                    new AuthoringRetryResponse(
                        "item-1",
                        true));
            }
            if (path.EndsWith("/supersede", StringComparison.Ordinal))
            {
                JsonElement body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(ct))
                    .RootElement;
                Assert.Equal(
                    "obsolete",
                    body.GetProperty("reason").GetString());
                return DelegateHttpHandler.Json(
                    new AuthoringItemSupersedeResult(
                        "run-1",
                        "item-1",
                        "superseded",
                        "obsolete"));
            }
            if (request.Method == HttpMethod.Get &&
                request.RequestUri.Query.Length > 0)
            {
                return DelegateHttpHandler.Json(
                    new AuthoringRunListResponse(
                        [AuthoringClientTestData.RunResponse().Run],
                        false));
            }
            return DelegateHttpHandler.Json(
                AuthoringClientTestData.RunResponse(),
                request.Method == HttpMethod.Post
                    ? HttpStatusCode.Accepted
                    : HttpStatusCode.OK);
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringStartResult started = await client.StartAsync(
            "preparer",
            new { ticketKeys = new[] { "FHIR-1" } },
            CancellationToken.None);
        AuthoringRunListResponse listed = await client.ListAsync(
            "Preparer",
            17,
            CancellationToken.None);
        AuthoringRunResponse status = await client.GetAsync(
            "Preparer",
            "run-1",
            CancellationToken.None);
        AuthoringRetryResponse retry = await client.RetryAsync(
            "Preparer",
            "run-1",
            "item-1",
            CancellationToken.None);
        AuthoringItemSupersedeResult superseded =
            await client.SupersedeAsync(
                "Preparer",
                "run-1",
                "item-1",
                "obsolete",
                CancellationToken.None);

        Assert.False(started.NoCandidates);
        Assert.Equal("run-1", started.Run!.Run.RunId);
        Assert.Single(listed.Runs);
        Assert.Equal("run-1", status.Run.RunId);
        Assert.True(retry.RequiresAuthoring);
        Assert.Equal("superseded", superseded.Status);
        Assert.Contains(
            "POST /api/v1/processing-services/Preparer/authoring/runs",
            targets);
        Assert.Contains(
            "GET /api/v1/processing-services/Preparer/authoring/runs?limit=17",
            targets);
        Assert.Contains(
            "GET /api/v1/processing-services/Preparer/authoring/runs/run-1",
            targets);
        Assert.Contains(
            "POST /api/v1/processing-services/Preparer/authoring/runs/run-1/items/item-1/retry",
            targets);
        Assert.Contains(
            "POST /api/v1/processing-services/Preparer/authoring/runs/run-1/items/item-1/supersede",
            targets);
    }

    [Fact]
    public async Task StartMapsNoContentToTypedNoCandidatesResult()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NoContent)));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringStartResult result = await client.StartAsync(
            "Planner",
            new { ticketKeys = Array.Empty<string>() },
            CancellationToken.None);

        Assert.True(result.NoCandidates);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task PublicationRefreshUsesTypedOrchestratorRoute()
    {
        List<string> targets = [];
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            targets.Add(
                $"{request.Method} {request.RequestUri!.PathAndQuery}");
            Assert.Null(request.Content);
            return Task.FromResult(
                DelegateHttpHandler.Json(
                    PublicationRefreshResponse(),
                    HttpStatusCode.Accepted));
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringRunResponse response =
            await client.StartPublicationRefreshAsync(
                "preparer",
                "source-run",
                CancellationToken.None);

        Assert.Equal("refresh-run", response.Run.RunId);
        Assert.Equal("publication-refresh", response.Run.Purpose);
        Assert.Equal("source-run", response.Run.SourceRunId);
        Assert.Equal("refresh-run", Assert.Single(response.Items).RunId);
        Assert.Equal(
            ["POST /api/v1/processing-services/Preparer/authoring/runs/source-run/publication-refresh"],
            targets);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicationReconciliationUsesTypedOrchestratorRoutes()
    {
        List<string> targets = [];
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            targets.Add(
                $"{request.Method} {request.RequestUri!.PathAndQuery}");
            string path = request.RequestUri.AbsolutePath;
            PublicationReconciliationStatusResult status =
                PublicationReconciliationStatus();
            if (path.EndsWith("/retry", StringComparison.Ordinal))
            {
                return DelegateHttpHandler.Json(
                    new PublicationReconciliationRetryResult(status, true));
            }
            if (path.EndsWith("/abandon", StringComparison.Ordinal))
            {
                JsonElement body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(ct))
                    .RootElement;
                Assert.Equal(
                    "operator accepted risk",
                    body.GetProperty("reason").GetString());
                return DelegateHttpHandler.Json(
                    new PublicationReconciliationAbandonResult(
                        status with
                        {
                            Promotion = status.Promotion with
                            {
                                State = "canonical-unpublished",
                            },
                        },
                        new DateTimeOffset(
                            2026, 9, 16, 18, 0, 0, TimeSpan.Zero),
                        "operator accepted risk"));
            }
            if (request.Method == HttpMethod.Post)
            {
                return DelegateHttpHandler.Json(
                    new PublicationReconciliationStartResult(
                        status.Run,
                        status.Items,
                        status.Comparison,
                        status.Counts),
                    HttpStatusCode.Accepted);
            }
            return DelegateHttpHandler.Json(status);
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        PublicationReconciliationStartResult started =
            await client.StartPublicationReconciliationAsync(
                "preparer",
                "source-run",
                CancellationToken.None);
        PublicationReconciliationStatusResult status =
            await client.GetPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                CancellationToken.None);
        PublicationReconciliationRetryResult retried =
            await client.RetryPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                CancellationToken.None);
        PublicationReconciliationAbandonResult abandoned =
            await client.AbandonPublicationReconciliationAsync(
                "Preparer",
                "reconciliation-run",
                "operator accepted risk",
                CancellationToken.None);

        Assert.Equal("jira-generation-9", started.Comparison.StableJiraGeneration);
        Assert.Equal(1, status.Counts.ReAuthorTicketCount);
        Assert.True(retried.RecoveryStarted);
        Assert.Equal("canonical-unpublished", abandoned.Status.Promotion.State);
        Assert.Equal(
            [
                "POST /api/v1/processing-services/Preparer/authoring/runs/source-run/publication-reconciliation",
                "GET /api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation",
                "POST /api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation/retry",
                "POST /api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation/abandon",
            ],
            targets);
    }

    [Fact]
    public async Task PublicationReconciliationConflictRetainsTypedFailure()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
        {
            HttpResponseMessage response = DelegateHttpHandler.Json(
                new AuthoringConflictResponse(
                    "recovery-in-progress",
                    "Snapshot publication is still pending.",
                    ["reconciliation-run"],
                    "reconciliation-run"),
                HttpStatusCode.Conflict);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(TimeSpan.FromSeconds(21));
            return Task.FromResult(response);
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(
                () => client.RetryPublicationReconciliationAsync(
                    "Preparer",
                    "reconciliation-run",
                    CancellationToken.None));

        Assert.Equal("recovery-in-progress", error.ErrorCode);
        Assert.Equal(["reconciliation-run"], error.RelatedRunIds);
        Assert.Equal(TimeSpan.FromSeconds(21), error.RetryAfter);
        Assert.EndsWith(
            "/publication-reconciliation/retry",
            error.Endpoint,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("source-run")]
    [InlineData("run")]
    [InlineData("item")]
    [InlineData("processor")]
    public async Task PublicationRefreshValidatesResponseCoordinatesAndService(
        string invalidField)
    {
        AuthoringRunResponse response = PublicationRefreshResponse();
        response = invalidField switch
        {
            "purpose" => response with
            {
                Run = response.Run with { Purpose = "authoring" },
            },
            "source-run" => response with
            {
                Run = response.Run with { SourceRunId = "other-run" },
            },
            "run" => response with
            {
                Run = response.Run with { RunId = "" },
            },
            "item" => response with
            {
                Items =
                [
                    response.Items[0] with { RunId = "other-run" },
                ],
            },
            "processor" => response with
            {
                Run = response.Run with
                {
                    ProcessorKind = "github-fhir-ballot-notes",
                },
            },
            _ => throw new UnreachableException(),
        };
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(
                DelegateHttpHandler.Json(
                    response,
                    HttpStatusCode.Accepted)));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartPublicationRefreshAsync(
                "Preparer",
                "source-run",
                CancellationToken.None));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicationRefreshStructuredConflictRetainsCoordinates()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
        {
            HttpResponseMessage response = DelegateHttpHandler.Json(
                new AuthoringConflictResponse(
                    "mutation-fence-unavailable",
                    "Another mutation is active.",
                    ["active-run"],
                    "active-run"),
                HttpStatusCode.Conflict);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(13));
            return Task.FromResult(response);
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(
                () => client.StartPublicationRefreshAsync(
                    "Preparer",
                    "source-run",
                    CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("mutation-fence-unavailable", error.ErrorCode);
        Assert.Equal("Another mutation is active.", error.Detail);
        Assert.Equal(["active-run"], error.RelatedRunIds);
        Assert.Equal(TimeSpan.FromSeconds(13), error.RetryAfter);
        Assert.Equal("13", error.RetryAfterHeader);
        Assert.Equal(
            "api/v1/processing-services/Preparer/authoring/runs/source-run/publication-refresh",
            error.Endpoint);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicationRefreshCallerCancellationIsNotReclassified()
    {
        DelegateHttpHandler handler = new(
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new UnreachableException();
            });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 3);
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StartPublicationRefreshAsync(
                "Preparer",
                "source-run",
                cancellation.Token));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicationRefreshTransportLossIsNotReplayed()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new HttpRequestException("response lost"));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 3);

        AuthoringMutationOutcomeUnknownException error =
            await Assert.ThrowsAsync<
                AuthoringMutationOutcomeUnknownException>(
                () => client.StartPublicationRefreshAsync(
                    "Preparer",
                    "source-run",
                    CancellationToken.None));

        Assert.Equal("publication-refresh", error.Operation);
        Assert.Equal("Preparer", error.ServiceName);
        Assert.Equal("source-run", error.RunId);
        Assert.Null(error.ItemId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PublicationRefreshResponseBodyLossIsNotReplayed()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StreamContent(
                        new InterruptedReadStream()),
                }));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 3);

        AuthoringMutationOutcomeUnknownException error =
            await Assert.ThrowsAsync<
                AuthoringMutationOutcomeUnknownException>(
                () => client.StartPublicationRefreshAsync(
                    "Preparer",
                    "source-run",
                    CancellationToken.None));

        Assert.Equal("publication-refresh", error.Operation);
        Assert.Equal("Preparer", error.ServiceName);
        Assert.Equal("source-run", error.RunId);
        Assert.Null(error.ItemId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task IdempotentReadRetriesTransientResponse()
    {
        DelegateHttpHandler handler = new((_, call, _) =>
        {
            if (call == 1)
            {
                HttpResponseMessage transient = DelegateHttpHandler.Json(
                    new { error = "temporary" },
                    HttpStatusCode.ServiceUnavailable);
                transient.Headers.RetryAfter =
                    new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(transient);
            }
            return Task.FromResult(
                DelegateHttpHandler.Json(
                    AuthoringClientTestData.RunResponse()));
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 1);

        AuthoringRunResponse response = await client.GetAsync(
            "Planner",
            "run-1",
            CancellationToken.None);

        Assert.Equal("run-1", response.Run.RunId);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task IdempotentReadRetriesInterruptedResponseBody()
    {
        DelegateHttpHandler handler = new((_, call, _) =>
            Task.FromResult(
                call == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(
                            new InterruptedReadStream()),
                    }
                    : DelegateHttpHandler.Json(
                        AuthoringClientTestData.RunResponse())));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 1);

        AuthoringRunResponse response = await client.GetAsync(
            "Planner",
            "run-1",
            CancellationToken.None);

        Assert.Equal("run-1", response.Run.RunId);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("retry")]
    [InlineData("supersede")]
    public async Task MutationTransportLossIsNotReplayed(
        string operation)
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new HttpRequestException("response lost"));
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 3);

        AuthoringMutationOutcomeUnknownException error =
            await Assert.ThrowsAsync<
                AuthoringMutationOutcomeUnknownException>(
                () => operation switch
                {
                    "start" => AsTask(client.StartAsync(
                        "Preparer",
                        new { ticketKeys = new[] { "FHIR-1" } },
                        CancellationToken.None)),
                    "retry" => AsTask(client.RetryAsync(
                        "Preparer",
                        "run-1",
                        "item-1",
                        CancellationToken.None)),
                    _ => AsTask(client.SupersedeAsync(
                        "Preparer",
                        "run-1",
                        "item-1",
                        "obsolete",
                        CancellationToken.None)),
                });

        Assert.Equal(operation, error.Operation);
        Assert.Equal("Preparer", error.ServiceName);
        Assert.Null(error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StructuredConflictRetainsCoordinatesAndRetryAfter()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
        {
            HttpResponseMessage response = DelegateHttpHandler.Json(
                new AuthoringConflictResponse(
                    "revision-already-scheduled",
                    "Revision is already present.",
                    ["run-2", "run-1"],
                    "run-1"),
                HttpStatusCode.Conflict);
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(9));
            return Task.FromResult(response);
        });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(handler);

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(
                () => client.StartAsync(
                    "Planner",
                    new { ticketKeys = new[] { "FHIR-1" } },
                    CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(
            "revision-already-scheduled",
            error.ErrorCode);
        Assert.Equal(
            "Revision is already present.",
            error.Detail);
        Assert.Equal(TimeSpan.FromSeconds(9), error.RetryAfter);
        Assert.Equal("9", error.RetryAfterHeader);
        Assert.Equal(["run-2", "run-1"], error.RelatedRunIds);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotRetriedOrReclassified()
    {
        DelegateHttpHandler handler = new(
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new UnreachableException();
            });
        AuthoringControlClient client =
            AuthoringClientTestData.CreateClient(
                handler,
                maxReadRetries: 3);
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(
                "Preparer",
                "run-1",
                cancellation.Token));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void PublicClientSurfaceHasNoWorkerSubmissionCapability()
    {
        string[] assemblyReferences =
            typeof(AuthoringControlClient).Assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .ToArray();
        string[] publicMethods = typeof(IAuthoringControlClient)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains(
            "FhirAugury.Processing.Contracts",
            assemblyReferences);
        Assert.DoesNotContain(
            assemblyReferences,
            name => name.StartsWith(
                "FhirAugury.Processor.",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            publicMethods,
            name => name.Contains(
                "Submit",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(AuthoringControlClient).Assembly
                .GetExportedTypes()
                .SelectMany(type => type.GetMembers())
                .Select(member => member.Name),
            name => name.Contains(
                "OperationToken",
                StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<object?> AsTask<T>(Task<T> task)
        => await task;

    private static AuthoringRunResponse PublicationRefreshResponse()
    {
        AuthoringRunResponse response =
            AuthoringClientTestData.RunResponse("refresh-run");
        return response with
        {
            Run = response.Run with
            {
                Purpose = "publication-refresh",
                SourceRunId = "source-run",
            },
        };
    }

    private static PublicationReconciliationStatusResult
        PublicationReconciliationStatus()
    {
        AuthoringRunResponse response =
            AuthoringClientTestData.RunResponse("reconciliation-run");
        AuthoringRunStatus run = response.Run with
        {
            Purpose = "publication-reconciliation",
            SourceRunId = "source-run",
            ReconciliationCounts = new(1, 0, 1),
            Recovery = new(
                "snapshot-publish-pending",
                "database-promoted",
                true),
        };
        AuthoringRunItemStatus[] items =
        [
            response.Items[0] with
            {
                RunId = run.RunId,
                Reconciliation = new(
                    "re-author",
                    "revision-1",
                    "revision-2",
                    new string('a', 64),
                    new string('b', 64)),
            },
        ];
        PublicationReconciliationComparison comparison = new(
            1,
            "source-run",
            "source-snapshot",
            new string('c', 64),
            "jira-generation-9",
            new DateTimeOffset(2026, 9, 16, 17, 0, 0, TimeSpan.Zero),
            new string('d', 64),
            [
                new(
                    "FHIR-1",
                    "re-author",
                    "revision-1",
                    "revision-2",
                    "receipt-1",
                    "item-1",
                    "source-run",
                    new string('a', 64),
                    new string('b', 64)),
            ]);
        return new(
            run,
            items,
            comparison,
            new(1, 0, 1),
            [],
            new(
                "snapshot-publish-pending",
                "database-promoted",
                true),
            []);
    }

    private sealed class InterruptedReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new IOException("connection interrupted");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(
                new IOException("connection interrupted"));

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }
}
