using System.Net;
using System.Text.Json;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Cli.Tests.Authoring;

[Collection(AuthoringEnvironmentCollection.Name)]
public sealed class PreparedTicketAuthoringHandlerTests
{
    [Fact]
    public async Task StartUsesTypedOrchestratorRoute()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs",
                request.RequestUri!.AbsolutePath);
            return Task.FromResult(DelegateHttpHandler.Json(RunEnvelope()));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "start",
                TicketKeys = ["FHIR-1"],
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal("run-1", Assert.IsType<AuthoringRunEnvelope>(result).Run.RunId);
    }

    [Fact]
    public async Task ScheduledStartReturnsTypedNoCandidatesResult()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "start",
                TicketKeys = [],
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(
            "no-candidates",
            Assert.IsType<AuthoringNoCandidatesResponse>(result).Status);
    }

    [Fact]
    public async Task StartDoesNotReplayMutatingRequestAfterTransportFailure()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new HttpRequestException("response lost"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            PreparedTicketAuthoringHandler.HandleAsync(
                new PreparedTicketAuthoringRequest
                {
                    Action = "start",
                    TicketKeys = ["FHIR-1"],
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WorkerSubmitRetriesSamePayloadAndKeepsTokenOutOfBodyAndUrl()
    {
        const string token = "secret-token-value";
        using AuthoringEnvironmentScope environment =
            new(operationToken: token);
        string? firstBody = null;
        DelegateHttpHandler handler = new(async (request, call, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            firstBody ??= body;
            Assert.Equal(firstBody, body);
            Assert.DoesNotContain(token, body);
            Assert.DoesNotContain(token, request.RequestUri!.AbsoluteUri);
            Assert.Equal(
                token,
                request.Headers.GetValues(
                    AuthoringHttpClient.OperationTokenHeader).Single());
            if (call == 1)
            {
                HttpResponseMessage transient =
                    DelegateHttpHandler.Json(
                        new { error = "temporary" },
                        HttpStatusCode.ServiceUnavailable);
                transient.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.Zero);
                return transient;
            }

            JsonElement json = JsonDocument.Parse(body).RootElement;
            JsonElement submission = json.GetProperty("submission");
            return DelegateHttpHandler.Json(new
            {
                receipt = new AuthoringResultReceipt(
                    "receipt-1",
                    submission.GetProperty("runId").GetString()!,
                    submission.GetProperty("itemId").GetString()!,
                    submission.GetProperty("operationId").GetString()!,
                    "FHIR-1",
                    submission.GetProperty("contentHash").GetString()!,
                    "revision-1",
                    submission.GetProperty("observedSourceRevision").GetString()!,
                    1,
                    DateTimeOffset.Parse("2026-09-04T00:00:00Z")),
                isReplay = false,
            });
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "submit",
                ObservedSourceRevision = "revision-1",
                Payload = Payload(),
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(2, handler.Calls);
        Assert.False(Assert.IsType<AuthoringSubmitResponse>(result).IsReplay);
    }

    [Fact]
    public async Task WorkerSubmitRequiresActuallyObservedSourceRevision()
    {
        using AuthoringEnvironmentScope environment = new();
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new InvalidOperationException("HTTP should not be called."));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => PreparedTicketAuthoringHandler.HandleAsync(
                new PreparedTicketAuthoringRequest
                {
                    Action = "submit",
                    Payload = Payload(),
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Contains("observedSourceRevision", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WorkerSubmitCanonicalizesJsonTimestampRevision()
    {
        const string canonical =
            "2026-09-04T00:00:00.1230000+00:00";
        using AuthoringEnvironmentScope environment =
            new(sourceRevision: canonical);
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            JsonElement submission = JsonDocument.Parse(body)
                .RootElement.GetProperty("submission");
            Assert.Equal(
                canonical,
                submission.GetProperty(
                    "observedSourceRevision").GetString());
            return DelegateHttpHandler.Json(new
            {
                receipt = new AuthoringResultReceipt(
                    "receipt-1",
                    "run-1",
                    "item-1",
                    "operation-1",
                    "FHIR-1",
                    submission.GetProperty("contentHash").GetString()!,
                    canonical,
                    canonical,
                    1,
                    DateTimeOffset.Parse("2026-09-04T00:00:00Z")),
                isReplay = false,
            });
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "submit",
                ObservedSourceRevision =
                    "2026-09-04T00:00:00.123+00:00",
                Payload = Payload(),
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.False(Assert.IsType<AuthoringSubmitResponse>(result).IsReplay);
    }

    private static PreparedTicketPayload Payload() => new()
    {
        Key = "FHIR-1",
        RequestSummary = "Request",
        ProposalA = "A",
        ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
        ProposalB = "B",
        ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
        ProposalC = "C",
        Recommendation = PreparedTicketRecommendationValues.ProposalA,
        RecommendationJustification = "Because",
    };

    private static object RunEnvelope() => new
    {
        run = new AuthoringRunStatus(
            "run-1",
            "jira-fhir",
            1,
            "queued",
            false,
            1,
            0,
            0,
            DateTimeOffset.Parse("2026-09-04T00:00:00Z"),
            null,
            null,
            null),
        items = new[]
        {
            new AuthoringRunItemStatus(
                "item-1",
                "run-1",
                "FHIR-1",
                "jira-ticket",
                "revision-1",
                "pending",
                null,
                null,
                0,
                DateTimeOffset.Parse("2026-09-04T00:00:00Z"),
                null,
                null,
                null),
        },
    };
}
