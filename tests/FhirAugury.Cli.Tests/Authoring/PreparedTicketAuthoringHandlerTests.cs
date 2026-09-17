using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Cli.Dispatch;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Cli.Tests.Authoring;

[Collection(AuthoringEnvironmentCollection.Name)]
public sealed class PreparedTicketAuthoringHandlerTests
{
    private static readonly JsonSerializerOptions CliJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

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

        AuthoringMutationOutcomeUnknownException error =
            await Assert.ThrowsAsync<
                AuthoringMutationOutcomeUnknownException>(() =>
            PreparedTicketAuthoringHandler.HandleAsync(
                new PreparedTicketAuthoringRequest
                {
                    Action = "start",
                    TicketKeys = ["FHIR-1"],
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Equal("start", error.Operation);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RefreshPublicationUsesTypedOrchestratorRoute()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs/source-run/publication-refresh",
                request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(
                DelegateHttpHandler.Json(
                    RunEnvelope(
                        "refresh-run",
                        purpose: "publication-refresh",
                        sourceRunId: "source-run")));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "refresh-publication",
                RunId = "source-run",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        AuthoringRunEnvelope run =
            Assert.IsType<AuthoringRunEnvelope>(result);
        Assert.Equal("refresh-run", run.Run.RunId);
        Assert.Equal("publication-refresh", run.Run.Purpose);
        Assert.Equal("source-run", run.Run.SourceRunId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReconcilePublicationUsesDistinctTypedOrchestratorRoute()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs/source-run/publication-reconciliation",
                request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(
                DelegateHttpHandler.Json(ReconciliationStartResult()));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "reconcile-publication",
                SourceRunId = "source-run",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        PublicationReconciliationStartResult start =
            Assert.IsType<PublicationReconciliationStartResult>(result);
        Assert.Equal("reconciliation-run", start.Run.RunId);
        Assert.Equal("stable-generation", start.Comparison.StableJiraGeneration);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("reconciliation-status", "GET", "")]
    [InlineData("retry-reconciliation", "POST", "/retry")]
    public async Task ReconciliationStatusAndRetryReturnTypedState(
        string action,
        string method,
        string suffix)
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            Assert.Equal(method, request.Method.Method);
            Assert.Equal(
                $"/api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation{suffix}",
                request.RequestUri!.AbsolutePath);
            object response = action == "retry-reconciliation"
                ? new PublicationReconciliationRetryResult(
                    ReconciliationStatusResult(),
                    true)
                : ReconciliationStatusResult();
            return Task.FromResult(DelegateHttpHandler.Json(response));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = action,
                RunId = "reconciliation-run",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        if (action == "retry-reconciliation")
        {
            Assert.True(
                Assert.IsType<
                    PublicationReconciliationRetryResult>(
                    result).RecoveryStarted);
        }
        else
        {
            Assert.Equal(
                "snapshot-publish-pending",
                Assert.IsType<
                    PublicationReconciliationStatusResult>(
                    result).Promotion.State);
        }
    }

    [Fact]
    public async Task AbandonReconciliationSendsAuditedReason()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation/abandon",
                request.RequestUri!.AbsolutePath);
            JsonElement body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal(
                "snapshot cannot be recovered",
                body.GetProperty("reason").GetString());
            return DelegateHttpHandler.Json(
                new PublicationReconciliationAbandonResult(
                    ReconciliationStatusResult("canonical-unpublished"),
                    DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
                    "snapshot cannot be recovered"));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "abandon-reconciliation",
                RunId = "reconciliation-run",
                Reason = "snapshot cannot be recovered",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        PublicationReconciliationAbandonResult abandoned =
            Assert.IsType<PublicationReconciliationAbandonResult>(result);
        Assert.Equal("canonical-unpublished", abandoned.Status.Promotion.State);
    }

    [Fact]
    public async Task CancelReconciliationSendsAuditedReasonAndReturnsTypedState()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DateTimeOffset cancelledAt =
            DateTimeOffset.Parse("2026-09-17T12:00:00Z");
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs/reconciliation-run/publication-reconciliation/cancel",
                request.RequestUri!.AbsolutePath);
            JsonElement body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal(
                "frozen revision changed",
                body.GetProperty("reason").GetString());
            PublicationReconciliationStatusResult status =
                ReconciliationStatusResult("cancelled") with
                {
                    Run = ReconciliationRunStatus() with
                    {
                        Status = "superseded",
                    },
                    Promotion = new(
                        "cancelled",
                        "cancelled",
                        false,
                        CancelledAt: cancelledAt,
                        CancellationReason:
                            "frozen revision changed"),
                };
            return DelegateHttpHandler.Json(
                new PublicationReconciliationCancelResult(
                    status,
                    cancelledAt,
                    "frozen revision changed"));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "cancel-reconciliation",
                RunId = "reconciliation-run",
                Reason = "frozen revision changed",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        PublicationReconciliationCancelResult cancelled =
            Assert.IsType<PublicationReconciliationCancelResult>(result);
        Assert.Equal("cancelled", cancelled.Status.Promotion.State);
        Assert.Equal(cancelledAt, cancelled.CancelledAt);
        Assert.Equal(
            "frozen revision changed",
            cancelled.Reason);
    }

    [Fact]
    public async Task ReconciliationConflictPreservesMachineReadableFailureCode()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(
                DelegateHttpHandler.Json(
                    new PreparedTicketPublicationReconciliationFailure(
                        "recovery-in-progress",
                        "Promotion recovery owns the mutation fence.",
                        RunId: "pending-run"),
                    HttpStatusCode.Conflict)));

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(() =>
                PreparedTicketAuthoringHandler.HandleAsync(
                    new PreparedTicketAuthoringRequest
                    {
                        Action = "reconcile-publication",
                        SourceRunId = "source-run",
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler));

        Assert.Equal("recovery-in-progress", error.ErrorCode);
        Assert.Equal(["pending-run"], error.RelatedRunIds);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public async Task CanonicalRestrictionPreservesStableFailureAndRunCoordinates()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            Task.FromResult(
                DelegateHttpHandler.Json(
                    new AuthoringConflictResponse(
                        "canonical-unpublished-restriction",
                        "A canonical epoch remains unpublished.",
                        ["abandoned-run", "recovery-run"]),
                    HttpStatusCode.Conflict)));

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(() =>
                PreparedTicketAuthoringHandler.HandleAsync(
                    new PreparedTicketAuthoringRequest
                    {
                        Action = "start",
                        TicketKeys = ["FHIR-1"],
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler));

        Assert.Equal(
            "canonical-unpublished-restriction",
            error.ErrorCode);
        Assert.Equal(
            ["abandoned-run", "recovery-run"],
            error.RelatedRunIds);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Fact]
    public void DispatcherKeepsCanonicalRestrictionCoordinatesMachineReadable()
    {
        OutputEnvelope envelope =
            CommandDispatcher.CreateAuthoringControlFailure(
                "prepared-ticket-authoring",
                new AuthoringControlException(
                    HttpStatusCode.Conflict,
                    "canonical-unpublished-restriction",
                    "A canonical epoch remains unpublished.",
                    relatedRunIds:
                        ["abandoned-run", "recovery-run"]));

        Assert.Equal(
            "canonical-unpublished-restriction",
            envelope.Error!.Code);
        JsonElement data = JsonSerializer.SerializeToElement(
            envelope.Data,
            CliJsonOptions);
        Assert.Equal(
            ["abandoned-run", "recovery-run"],
            data.GetProperty("relatedRunIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task CancellationConflictPreservesMachineReadableFailureCode()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, _, _) =>
        {
            Assert.EndsWith(
                "/publication-reconciliation/cancel",
                request.RequestUri!.AbsolutePath,
                StringComparison.Ordinal);
            return Task.FromResult(
                DelegateHttpHandler.Json(
                    new PreparedTicketPublicationReconciliationFailure(
                        "cancellation-not-allowed",
                        "Trusted candidate already exists.",
                        RunId: "reconciliation-run"),
                    HttpStatusCode.Conflict));
        });

        AuthoringControlException error =
            await Assert.ThrowsAsync<AuthoringControlException>(() =>
                PreparedTicketAuthoringHandler.HandleAsync(
                    new PreparedTicketAuthoringRequest
                    {
                        Action = "cancel-reconciliation",
                        RunId = "reconciliation-run",
                        Reason = "source changed",
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler));

        Assert.Equal("cancellation-not-allowed", error.ErrorCode);
        Assert.Equal(["reconciliation-run"], error.RelatedRunIds);
    }

    [Fact]
    public async Task UnknownRefreshOutcomeReturnsReadOnlyCandidatesWithoutReplay()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DateTimeOffset submission =
            DateTimeOffset.Parse("2026-09-14T12:00:00Z");
        DelegateHttpHandler handler = new((request, call, _) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "/api/v1/processing-services/Preparer/authoring/runs/source-run/publication-refresh",
                    request.RequestUri!.AbsolutePath);
                throw new HttpRequestException("response lost");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs?limit=20",
                request.RequestUri!.PathAndQuery);
            return Task.FromResult(DelegateHttpHandler.Json(
                new AuthoringRunListResponse(
                [
                    RunStatus(
                        "matching-refresh",
                        submission.AddSeconds(1),
                        "publication-refresh",
                        "source-run"),
                    RunStatus(
                        "old-refresh",
                        submission.AddSeconds(-1),
                        "publication-refresh",
                        "source-run"),
                    RunStatus(
                        "wrong-purpose",
                        submission.AddSeconds(1),
                        "authoring",
                        "source-run"),
                    RunStatus(
                        "wrong-source",
                        submission.AddSeconds(1),
                        "publication-refresh",
                        "other-run"),
                ],
                Truncated: false)));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "refresh-publication",
                RunId = "source-run",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler,
            new FixedTimeProvider(submission));

        AuthoringPublicationRefreshReconciliationResponse reconciliation =
            Assert.IsType<
                AuthoringPublicationRefreshReconciliationResponse>(
                result);
        Assert.Equal("outcome-unknown", reconciliation.Outcome);
        Assert.Equal("succeeded", reconciliation.Reconciliation);
        Assert.Equal("source-run", reconciliation.SourceRunId);
        Assert.Equal(
            "matching-refresh",
            Assert.Single(reconciliation.Candidates).RunId);
        Assert.False(reconciliation.ListTruncated);
        Assert.Null(reconciliation.Error);
        Assert.Contains(
            "Select the single matching candidate run",
            reconciliation.Message);
        JsonElement serialized =
            JsonSerializer.SerializeToElement(
                result,
                CliJsonOptions);
        Assert.Equal(
            JsonValueKind.Null,
            serialized.GetProperty("error").ValueKind);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task UnknownRefreshOutcomeSerializesFailedReconciliationError()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new((request, call, _) =>
        {
            if (call == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                throw new HttpRequestException("response lost");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            throw new InvalidOperationException(
                "run list unavailable");
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "refresh-publication",
                RunId = "source-run",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        AuthoringPublicationRefreshReconciliationResponse reconciliation =
            Assert.IsType<
                AuthoringPublicationRefreshReconciliationResponse>(
                result);
        Assert.Equal("failed", reconciliation.Reconciliation);
        Assert.Empty(reconciliation.Candidates);
        Assert.Equal(
            "run list unavailable",
            reconciliation.Error);
        JsonElement serialized =
            JsonSerializer.SerializeToElement(
                result,
                CliJsonOptions);
        Assert.Equal(
            "run list unavailable",
            serialized.GetProperty("error").GetString());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SupersedeUsesTypedOrchestratorRouteAndReason()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            Assert.Equal(
                "/api/v1/processing-services/Preparer/authoring/runs/run-1/items/item-1/supersede",
                request.RequestUri!.AbsolutePath);
            JsonElement body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal("not actionable", body.GetProperty("reason").GetString());
            return DelegateHttpHandler.Json(
                new AuthoringItemSupersedeResult(
                    "run-1",
                    "item-1",
                    "superseded",
                    "not actionable"));
        });

        object result = await PreparedTicketAuthoringHandler.HandleAsync(
            new PreparedTicketAuthoringRequest
            {
                Action = "supersede",
                RunId = "run-1",
                ItemId = "item-1",
                Reason = "not actionable",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(
            "superseded",
            Assert.IsType<AuthoringItemSupersedeResult>(result).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SupersedeDoesNotReplayAfterTransportFailure()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new HttpRequestException("response lost"));

        AuthoringMutationOutcomeUnknownException error =
            await Assert.ThrowsAsync<
                AuthoringMutationOutcomeUnknownException>(() =>
            PreparedTicketAuthoringHandler.HandleAsync(
                new PreparedTicketAuthoringRequest
                {
                    Action = "supersede",
                    RunId = "run-1",
                    ItemId = "item-1",
                    Reason = "not actionable",
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Equal("supersede", error.Operation);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SnapshotMaterializesTrustedPreparerPair()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-preparer-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = [1, 2, 3, 4];
            string snapshotPath = Path.Combine(
                directory,
                "prepared-tickets.db");
            string descriptorPath = Path.Combine(
                directory,
                "prepared-tickets.json");
            string hash = Convert.ToHexString(
                    SHA256.HashData(bytes))
                .ToLowerInvariant();
            DelegateHttpHandler handler = new((request, _, _) =>
                Task.FromResult(
                    request.RequestUri!.AbsolutePath.EndsWith(
                        "/bytes",
                        StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes),
                        }
                        : DelegateHttpHandler.Json(
                            new AuthoringSnapshotDescriptor(
                                "jira-fhir",
                                "run-1",
                                "snapshot-1",
                                1,
                                1,
                                1,
                                hash,
                                bytes.Length,
                                1,
                                1,
                                new Dictionary<string, long>(),
                                "prepared-tickets.db",
                                DateTimeOffset.Parse(
                                    "2026-09-04T00:00:00Z")))));

            object result =
                await PreparedTicketAuthoringHandler.HandleAsync(
                    new PreparedTicketAuthoringRequest
                    {
                        Action = "snapshot",
                        RunId = "run-1",
                        SnapshotPath = snapshotPath,
                        DescriptorPath = descriptorPath,
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler);

            JsonElement json = JsonSerializer.SerializeToElement(result);
            Assert.Equal(
                snapshotPath,
                json.GetProperty("snapshotPath").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshotPath));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static PublicationReconciliationStartResult
        ReconciliationStartResult() => new(
            ReconciliationRunStatus(),
            [],
            ReconciliationComparison(),
            new AuthoringRunReconciliationCounts(1, 0, 1));

    private static PublicationReconciliationStatusResult
        ReconciliationStatusResult(
            string promotionState = "snapshot-publish-pending") => new(
                promotionState == "canonical-unpublished"
                    ? ReconciliationRunStatus() with
                    {
                        Status = "abandoned",
                        CompletedAt = DateTimeOffset.Parse(
                            "2026-09-16T12:00:00Z"),
                        State = new(
                            IsTerminal: true,
                            IsRecoverable: false),
                    }
                    : ReconciliationRunStatus(),
                [],
                ReconciliationComparison(),
                new AuthoringRunReconciliationCounts(1, 0, 1),
                [],
                new PublicationReconciliationPromotionStatus(
                    promotionState,
                    promotionState == "canonical-unpublished"
                        ? "canonical-unpublished"
                        : "database-promoted",
                    promotionState == "snapshot-publish-pending",
                    AbandonedAt:
                        promotionState == "canonical-unpublished"
                            ? DateTimeOffset.Parse(
                                "2026-09-16T12:00:00Z")
                            : null,
                    AbandonmentReason:
                        promotionState == "canonical-unpublished"
                            ? "snapshot cannot be recovered"
                            : null),
                []);

    private static AuthoringRunStatus ReconciliationRunStatus() => new(
        "reconciliation-run",
        "jira-fhir",
        1,
        "running",
        false,
        1,
        0,
        0,
        DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
        null,
        null,
        null,
        Purpose: "publication-reconciliation",
        SourceRunId: "source-run");

    private static PublicationReconciliationComparison
        ReconciliationComparison() => new(
            1,
            "source-run",
            "snapshot-1",
            new string('a', 64),
            "stable-generation",
            DateTimeOffset.Parse("2026-09-16T12:00:00Z"),
            new string('b', 64),
            [
                new PublicationReconciliationItemDecision(
                    "FHIR-1",
                    "re-author",
                    "revision-1",
                    "revision-2",
                    "receipt-1",
                    "source-item-1",
                    "source-run",
                    new string('c', 64),
                    new string('d', 64)),
            ]);

    private static object RunEnvelope(
        string runId = "run-1",
        string? purpose = null,
        string? sourceRunId = null) => new
    {
        run = new AuthoringRunStatus(
            runId,
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
            null,
            Purpose: purpose,
            SourceRunId: sourceRunId),
        items = new[]
        {
            new AuthoringRunItemStatus(
                "item-1",
                runId,
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

    private static AuthoringRunStatus RunStatus(
        string runId,
        DateTimeOffset createdAt,
        string purpose,
        string sourceRunId) => new(
            runId,
            "jira-fhir",
            1,
            "queued",
            false,
            1,
            1,
            0,
            createdAt,
            null,
            null,
            null,
            State: new AuthoringRunStateInfo(
                IsTerminal: false,
                IsRecoverable: false),
            Purpose: purpose,
            SourceRunId: sourceRunId);

    private sealed class FixedTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
