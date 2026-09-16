using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static FhirAugury.Processor.Jira.Fhir.Preparer.Tests.PreparedTicketPublicationTestFixture;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

[Collection(PreparedTicketPublicationTestCollection.Name)]
public sealed class PreparedTicketPublicationReconciliationTests
{
    [Fact]
    public async Task PlannerDiscoversCompleteMixedSetAndRejectsDuplicateStart()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054", "FHIR-13055");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-13055"] =
                    new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)
                        .ToString("O"),
            };
        ObservationFetcher fetcher = new(revisions, generation: 42);
        AuthoringRetryPolicy retryPolicy = new(
            Options.Create(new ProcessingServiceOptions()));
        JiraAuthoringRunCoordinator coordinator = new(
            fixture.Store,
            new JiraProcessingSourceTicketStore(
                fixture.Database.DatabasePath),
            new JiraProcessingFilterResolver(),
            Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
                TicketStatusesToProcess = ["Triaged"],
            }));
        PreparedTicketPublicationReconciliationPlanner planner = new(
            fixture.CreateBaselineReader(),
            fetcher,
            fixture.Database,
            new AuthoringRunControlService(fixture.Store, retryPolicy),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());

        PreparedTicketPublicationReconciliationStartResult result =
            await planner.StartAsync(source.Run.Id);

        Assert.Equal(2, result.Counts.AcceptedTicketCount);
        Assert.Equal(1, result.Counts.CarryForwardTicketCount);
        Assert.Equal(1, result.Counts.ReAuthorTicketCount);
        Assert.Equal(4, fetcher.CallCount);
        Assert.Collection(
            result.Items.OrderBy(item => item.BusinessKey),
            item => Assert.Equal(
                AuthoringStatusValues.Items.Complete,
                item.Status),
            item => Assert.Equal(
                AuthoringStatusValues.Items.Pending,
                item.Status));
        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => planner.StartAsync(source.Run.Id));
        Assert.Equal(
            AuthoringConflictCode.MutationFenceUnavailable,
            conflict.Code);
        Assert.Equal(6, fetcher.CallCount);
    }

    [Fact]
    public async Task ReconciliationAcceptanceStagesGraphAndReceiptAtomically()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-13054");
        string revisedSourceRevision =
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)
                .ToString("O");
        fixture.Execute(
            """
            UPDATE jira_processing_source_tickets
            SET LastUpdated = @revision
            WHERE Key = 'FHIR-13054'
            """,
            ("@revision", revisedSourceRevision));
        PreparedTicketPayload revised = Payload("FHIR-13054", "revised");
        string contentHash =
            PreparedTicketAuthoringDtos.ComputeContentHash(revised);
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            source.Run.Id,
            source.Descriptor.SnapshotId,
            source.Descriptor.Sha256,
            "1",
            DateTimeOffset.UtcNow,
            Hash('c'),
            [
                new(
                    "FHIR-13054",
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor,
                    source.ExpectedRevisions["FHIR-13054"],
                    revisedSourceRevision,
                    source.ReceiptIds["FHIR-13054"],
                    "baseline-item",
                    source.Run.Id,
                    Hash('a'),
                    Hash('b')),
            ]);
        AuthoringRunRecord run =
            await fixture.Database.CreatePublicationReconciliationAsync(
                comparison);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(run.Id));
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.Store.ClaimItemAsync(run.Id, item.Id));

        AuthoringReceiptAcceptance acceptance =
            await fixture.Store.AcceptResultWithReceiptAsync(
                new(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    contentHash),
                claim.OperationToken,
                async (connection, receiptId, ct) =>
                {
                    await JiraProcessingSourceTicketStore
                        .EnsureCurrentSourceRevisionAsync(
                            connection,
                            revised.Key,
                            item.ItemKind,
                            item.ExpectedSourceRevision,
                            ct);
                    await fixture.Database
                        .StagePublicationReconciliationTicketAsync(
                            connection,
                            run.Id,
                            item.Id,
                            claim.OperationId,
                            receiptId,
                            item.ExpectedSourceRevision,
                            contentHash,
                            revised,
                            Hydration(revised.Key),
                            ct: ct);
                });

        Assert.False(acceptance.IsReplay);
        Assert.Equal(
            1,
            fixture.Scalar<long>(
                "SELECT COUNT(*) FROM prepared_ticket_publication_staged_receipts"));
        Assert.Equal(
            "Request",
            fixture.Scalar<string>(
                "SELECT RequestSummary FROM prepared_tickets WHERE Key = 'FHIR-13054'"));
        PreparedTicketPublicationCorpusOverlay overlay =
            await fixture.Database.GetPublicationReconciliationCorpusAsync(
                run.Id);
        Assert.Equal(
            "revised",
            Assert.Single(overlay.Tickets).Payload.RequestSummary);
    }

    [Fact]
    public async Task PendingPromotionCanOnlyBeAbandonedWithDurableAudit()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-13054");
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            source.Run.Id,
            source.Descriptor.SnapshotId,
            source.Descriptor.Sha256,
            "42",
            DateTimeOffset.UtcNow,
            Hash('c'),
            [
                new(
                    "FHIR-13054",
                    PreparedTicketPublicationReconciliationDispositionValues
                        .CarryForward,
                    source.ExpectedRevisions["FHIR-13054"],
                    source.ExpectedRevisions["FHIR-13054"],
                    source.ReceiptIds["FHIR-13054"],
                    "baseline-item",
                    source.Run.Id,
                    Hash('a'),
                    Hash('b')),
            ]);
        AuthoringRunRecord run =
            await fixture.Database.CreatePublicationReconciliationAsync(
                comparison);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Database.AbandonPublicationReconciliationAsync(
                run.Id,
                "not promoted"));
        Assert.NotNull(await fixture.Store.GetFencedRunAsync("jira-fhir"));

        fixture.Execute(
            """
            UPDATE authoring_runs
            SET Status = 'finalizing'
            WHERE Id = @runId;
            UPDATE prepared_ticket_publication_reconciliations
            SET PromotionState = 'snapshot-publish-pending'
            WHERE RunId = @runId;
            UPDATE prepared_ticket_publication_reconciliation_journal
            SET State = 'snapshot-publish-pending'
            WHERE RunId = @runId;
            """,
            ("@runId", run.Id));

        DateTimeOffset abandonedAt =
            new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
        await fixture.Database.AbandonPublicationReconciliationAsync(
            run.Id,
            "operator accepted canonical-only state",
            abandonedAt);

        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .CanonicalUnpublished,
            fixture.Scalar<string>(
                $"""
                SELECT PromotionState
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{run.Id}'
                """));
        Assert.Equal(
            "operator accepted canonical-only state",
            fixture.Scalar<string>(
                $"""
                SELECT AbandonmentReason
                FROM prepared_ticket_publication_reconciliations
                WHERE RunId = '{run.Id}'
                """));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_reconciliation_fences
                WHERE RunId = '{run.Id}'
                """));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public void ContractSerializesFrozenDecisionsCountsAndRecoveryState()
    {
        DateTimeOffset capturedAt =
            new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        PreparedTicketPublicationReconciliationItemDecision decision = new(
            "FHIR-13054",
            PreparedTicketPublicationReconciliationDispositionValues.ReAuthor,
            "revision-1",
            "revision-2",
            "receipt-1",
            "item-1",
            "run-1",
            Hash('a'),
            Hash('b'));
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            "source-run",
            "source-snapshot",
            Hash('c'),
            "jira-generation-42",
            capturedAt,
            Hash('d'),
            [decision]);
        AuthoringRunReconciliationCounts counts = new(1, 0, 1);
        PreparedTicketPublicationReconciliationPromotionStatus promotion =
            new(
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending,
                "database-promoted",
                true,
                capturedAt);
        PreparedTicketPublicationReconciliationProof proof = new(
            1,
            PreparedTicketPublicationReconciliationContract.Purpose,
            "source-run",
            "source-snapshot",
            "jira-generation-42",
            1,
            0,
            1,
            Hash('d'),
            Hash('e'),
            capturedAt);

        string json = JsonSerializer.Serialize(
            new { comparison, counts, promotion, proof },
            JsonSerializerOptions.Web);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(
            "re-author",
            root.GetProperty("comparison")
                .GetProperty("items")[0]
                .GetProperty("disposition")
                .GetString());
        Assert.Equal(
            "jira-generation-42",
            root.GetProperty("comparison")
                .GetProperty("stableJiraGeneration")
                .GetString());
        Assert.Equal(
            "snapshot-publish-pending",
            root.GetProperty("promotion").GetProperty("state").GetString());
        Assert.True(root.GetProperty("promotion")
            .GetProperty("mutationFenceHeld").GetBoolean());
        Assert.Equal(
            "publication-reconciliation",
            root.GetProperty("proof").GetProperty("purpose").GetString());
        Assert.Equal(
            "publication-refresh",
            PreparedTicketPublicationContract.PublicationRefreshPurpose);
    }

    [Theory]
    [InlineData("carry-forward")]
    [InlineData("re-author")]
    public void DispositionValuesAreStable(string disposition)
    {
        Assert.True(
            PreparedTicketPublicationReconciliationDispositionValues
                .IsValid(disposition));
        PreparedTicketPublicationReconciliationDispositionValues.EnsureValid(
            disposition);
    }

    [Fact]
    public void FailureCodesAreStableAndUnknownCodesRemainUnknown()
    {
        string[] codes =
        [
            PreparedTicketPublicationReconciliationFailureCodes.InvalidBaseline,
            PreparedTicketPublicationReconciliationFailureCodes.UnstableJiraGeneration,
            PreparedTicketPublicationReconciliationFailureCodes.RevisionInvalidation,
            PreparedTicketPublicationReconciliationFailureCodes.StagingMismatch,
            PreparedTicketPublicationReconciliationFailureCodes.GroupingImpactMismatch,
            PreparedTicketPublicationReconciliationFailureCodes.RecoveryInProgress,
            PreparedTicketPublicationReconciliationFailureCodes.PromotionRecoveryFailure,
            PreparedTicketPublicationReconciliationFailureCodes.CanonicalUnpublishedRestriction,
        ];

        Assert.Equal(
            [
                "invalid-baseline",
                "unstable-jira-generation",
                "revision-invalidation",
                "staging-mismatch",
                "grouping-impact-mismatch",
                "recovery-in-progress",
                "promotion-recovery-failure",
                "canonical-unpublished-restriction",
            ],
            codes);
        Assert.All(
            codes,
            code => Assert.True(
                PreparedTicketPublicationReconciliationFailureCodes.IsKnown(
                    code)));
        Assert.False(
            PreparedTicketPublicationReconciliationFailureCodes.IsKnown(
                "source-revision-mismatch"));
    }

    [Fact]
    public void PromotionStatesIncludeOnlyAuditableLifecycleStates()
    {
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "staged"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "snapshot-publish-pending"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "ready"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "canonical-unpublished"));
        Assert.False(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "abandoned"));
    }

    private static string Hash(char value) => new(value, 64);

    private static PreparedTicketPayload Payload(string key, string summary)
        => new()
        {
            Key = key,
            RequestSummary = summary,
            ProposalA = "A",
            ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation = PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "Because",
        };

    private static HydrationBatch Hydration(string key)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            key,
            new HydrationTicketRow(
                key, null, null, null, "FHIR", null, null, null, null,
                null, null, null, now, "resolved", null),
            [
                new HydrationJiraRow(
                    key, key, "Title", "Triaged", "Change Request", null,
                    null, null, "FHIR-I", "FHIR", now, null, now,
                    "resolved", null),
            ],
            [],
            [],
            [],
            []);
    }

    private sealed class ObservationFetcher(
        IReadOnlyDictionary<string, string> revisions,
        long generation)
        : OrchestratorHydrationFetcher(
            new HttpClient(),
            NullLogger.Instance)
    {
        public int CallCount { get; private set; }

        public override Task<PublicationMetadataFetchResult>
            FetchPublicationMetadataAsync(
                string ticketKey,
                DateTimeOffset hydratedAt,
                CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new PublicationMetadataFetchResult(
                ticketKey,
                hydratedAt,
                revisions[ticketKey],
                null,
                null,
                [],
                "FHIR",
                hydratedAt,
                generation,
                true,
                1,
                Failure: null,
                UpdatedAt: hydratedAt));
        }
    }
}
