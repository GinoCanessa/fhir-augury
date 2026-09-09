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
