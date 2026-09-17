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
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
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
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);

        PreparedTicketPublicationReconciliationStartResult result =
            await planner.StartAsync(source.Run.Id);

        Assert.Equal(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            result.Comparison.ContractVersion);
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
        Assert.All(
            result.Comparison.Items,
            decision =>
            {
                Assert.Equal("fhir", decision.ItemKind);
                Assert.Equal(
                    decision.Disposition ==
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward
                        ? source.ExpectedRevisions[decision.TicketKey]
                        : revisions[decision.TicketKey],
                    decision.ExpectedSourceRevision);
            });
        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => planner.StartAsync(source.Run.Id));
        Assert.Equal(
            AuthoringConflictCode.MutationFenceUnavailable,
            conflict.Code);
        Assert.Equal(6, fetcher.CallCount);
    }

    [Fact]
    public async Task AllCarryForwardReconciliation_RejectsGenerationAdvanceBeforePromotion()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13055", "FHIR-13054");
        ObservationFetcher fetcher = new(
            new Dictionary<string, string>(
                source.ExpectedRevisions,
                StringComparer.OrdinalIgnoreCase),
            generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        Assert.All(
            start.Comparison.Items,
            item => Assert.Equal(
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward,
                item.Disposition));

        string[] expected =
            ["FHIR-13054", "FHIR-13055"];
        int callsBeforeStatus = fetcher.CallCount;
        fetcher.Generation = 43;

        PreparedTicketPublicationReconciliationStatusResult status =
            await planner.GetStatusAsync(start.Run.RunId);

        Assert.Equal(expected, status.InvalidatedTicketKeys);
        Assert.Equal(expected.Length, status.Counts.InvalidatedTicketCount);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            status.FailureCode);
        Assert.Equal(
            expected,
            fetcher.RequestedTicketKeys
                .Skip(callsBeforeStatus)
                .ToArray());
        PreparedTicketGroupingDeltaDispatcher grouping = new(
            fixture.Database);
        IOptions<PreparerServiceOptions> options =
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            });
        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            options);
        PreparedTicketRunWorkflowRegistry workflows = new(
            fixture.Store,
            fixture.Database,
            fetcher,
            planner,
            grouping,
            materializer,
            new PreparedTicketPublicationRecoveryService(
                fixture.Database,
                fixture.Store,
                NullLogger<PreparedTicketPublicationRecoveryService>.Instance),
            options,
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance);
        AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(start.Run.RunId));
        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => workflows.FinalizeReconciliationAsync(run));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            error.FailureCode);
        Assert.Equal(expected, error.TicketKeys);
        Assert.Equal(
            expected,
            fetcher.RequestedTicketKeys
                .Skip(callsBeforeStatus + expected.Length)
                .ToArray());
        string temporaryPath = Path.Combine(
            fixture.SnapshotDirectory,
            $"jira-fhir-{start.Run.RunId}.reconciliation.tmp");
        string finalPath = Path.Combine(
            fixture.SnapshotDirectory,
            $"jira-fhir-{start.Run.RunId}.db");
        Assert.False(File.Exists(temporaryPath));
        Assert.False(File.Exists(finalPath));
        Assert.All(
            new[]
            {
                "prepared_ticket_publication_snapshot_descriptors",
                "prepared_ticket_publication_reconciliation_proofs",
                "authoring_review_snapshots",
            },
            table => Assert.Equal(
                0,
                fixture.Scalar<long>(
                    $"SELECT COUNT(*) FROM {table} WHERE RunId = '{start.Run.RunId}'")));
        Assert.Equal(
            0,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_authoring_state
                WHERE RunId = '{start.Run.RunId}'
                """));
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetRunAsync(start.Run.RunId)).Status);
        Assert.NotNull(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task MixedReconciliation_RejectsGenerationAdvanceBeforePromotion()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13055", "FHIR-13054");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-13055"] =
                    new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)
                        .ToString("O"),
            };
        ObservationFetcher fetcher = new(revisions, generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        Assert.Equal(1, start.Counts.CarryForwardTicketCount);
        Assert.Equal(1, start.Counts.ReAuthorTicketCount);

        revisions["FHIR-13054"] =
            new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero)
                .ToString("O");
        fetcher.MissingTicketKeys.Add("FHIR-13055");
        fetcher.Generation = 43;
        string[] expected =
            ["FHIR-13054", "FHIR-13055"];
        int callsBeforeStatus = fetcher.CallCount;

        PreparedTicketPublicationReconciliationStatusResult status =
            await planner.GetStatusAsync(start.Run.RunId);

        Assert.Equal(expected, status.InvalidatedTicketKeys);
        Assert.Equal(expected.Length, status.Counts.InvalidatedTicketCount);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            status.FailureCode);
        Assert.Equal(
            expected,
            fetcher.RequestedTicketKeys
                .Skip(callsBeforeStatus)
                .ToArray());
        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => planner.EnsureFrozenCorpusCurrentAsync(
                    start.Run.RunId));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            error.FailureCode);
        Assert.Equal(expected, error.TicketKeys);
        Assert.Equal(
            expected,
            fetcher.RequestedTicketKeys
                .Skip(callsBeforeStatus + expected.Length)
                .ToArray());
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
                    Hash('b'),
                    "fhir",
                    revisedSourceRevision),
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
        PreparedTicketPublicationCorpusTicket ticket =
            Assert.Single(overlay.Tickets);
        Assert.Equal(
            "revised",
            ticket.Payload.RequestSummary);
        Assert.Equal("fhir", ticket.ItemKind);
        Assert.Equal(revisedSourceRevision, ticket.ExpectedSourceRevision);
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                [ticket.ToPublicationCorpusItem()]),
            overlay.CorpusFingerprint);
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
                    Hash('b'),
                    "fhir",
                    source.ExpectedRevisions["FHIR-13054"]),
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
            Hash('b'),
            "fhir",
            "revision-2");
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
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
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
            "fhir",
            root.GetProperty("comparison")
                .GetProperty("items")[0]
                .GetProperty("itemKind")
                .GetString());
        Assert.Equal(
            "revision-2",
            root.GetProperty("comparison")
                .GetProperty("items")[0]
                .GetProperty("expectedSourceRevision")
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

    [Fact]
    public void CanonicalMixedCorpusSerialization_IsOrderIndependent()
    {
        PreparedTicketPublicationReconciliationItemDecision carried = new(
            "FHIR-100",
            PreparedTicketPublicationReconciliationDispositionValues
                .CarryForward,
            "revision-1",
            "revision-1",
            "receipt-1",
            "item-1",
            "run-1",
            Hash('a'),
            Hash('b'),
            "fhir",
            "revision-1");
        PreparedTicketPublicationReconciliationItemDecision reAuthored = new(
            "FHIR-200",
            PreparedTicketPublicationReconciliationDispositionValues.ReAuthor,
            "revision-1",
            "revision-2",
            "baseline-receipt-2",
            "baseline-item-2",
            "baseline-run-2",
            Hash('c'),
            Hash('d'),
            "fhir",
            "revision-2");
        PreparedTicketPublicationCorpusItem[] corpus =
        [
            new(
                carried.TicketKey,
                carried.BaselineReceiptId,
                carried.BaselineRunItemId,
                carried.BaselineContributingRunId,
                carried.ItemKind!,
                carried.ExpectedSourceRevision!),
            new(
                reAuthored.TicketKey,
                "reconciliation-receipt-2",
                "reconciliation-item-2",
                "reconciliation-run",
                reAuthored.ItemKind!,
                reAuthored.ExpectedSourceRevision!),
        ];

        Assert.Equal(
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus),
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                corpus.Reverse()));
        Assert.Equal(
            PreparedTicketPublicationContract.SerializeCorpus(corpus),
            PreparedTicketPublicationContract.SerializeCorpus(
                corpus.Reverse()));
        Assert.Equal(
            2,
            PreparedTicketPublicationReconciliationContract.CurrentVersion);
        Assert.Equal(1, PreparedTicketPublicationContract.CurrentVersion);
    }

    [Fact]
    public async Task VersionOneComparison_RemainsReadableButCannotResumeExecution()
    {
        using Fixture fixture = new();
        string runId = $"legacy-reconciliation-{Guid.NewGuid():N}";
        DateTimeOffset capturedAt =
            new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        string comparisonJson = JsonSerializer.Serialize(new
        {
            ContractVersion = 1,
            SourceRunId = "source-run",
            SourceSnapshotId = "source-snapshot",
            SourceSnapshotSha256 = Hash('a'),
            StableJiraGeneration = "42",
            CapturedAt = capturedAt,
            CorpusFingerprint = Hash('b'),
            Items = new[]
            {
                new
                {
                    TicketKey = "FHIR-100",
                    Disposition =
                        PreparedTicketPublicationReconciliationDispositionValues
                            .CarryForward,
                    BaselineSourceRevision = "revision-1",
                    CurrentSourceRevision = "revision-1",
                    BaselineReceiptId = "receipt-1",
                    BaselineRunItemId = "item-1",
                    BaselineContributingRunId = "run-1",
                    BaselineAuthoredFingerprint = Hash('c'),
                    BaselineGroupingFingerprint = Hash('d'),
                },
            },
        });
        fixture.Execute(
            """
            INSERT INTO prepared_ticket_publication_reconciliations(
                RunId, SourceRunId, SourceSnapshotId, SourceSnapshotSha256,
                StableJiraGeneration, CorpusFingerprint, ComparisonJson,
                PromotionState, CapturedAt)
            VALUES(
                @runId, 'source-run', 'source-snapshot', @snapshotSha,
                '42', @corpusFingerprint, @comparisonJson,
                'snapshot-publish-pending', @capturedAt);
            INSERT INTO prepared_ticket_publication_reconciliation_journal(
                RunId, State, SnapshotDescriptorJson, UpdatedAt)
            VALUES(
                @runId, 'snapshot-publish-pending', '{}', @capturedAt);
            """,
            ("@runId", runId),
            ("@snapshotSha", Hash('a')),
            ("@corpusFingerprint", Hash('b')),
            ("@comparisonJson", comparisonJson),
            ("@capturedAt", capturedAt.ToString("O")));

        PreparedTicketPublicationReconciliationComparison readable =
            Assert.IsType<PreparedTicketPublicationReconciliationComparison>(
                await fixture.Database
                    .GetPublicationReconciliationComparisonAsync(runId));
        Assert.Equal(1, readable.ContractVersion);
        PreparedTicketPublicationReconciliationItemDecision legacy =
            Assert.Single(readable.Items);
        Assert.Null(legacy.ItemKind);
        Assert.Null(legacy.ExpectedSourceRevision);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => fixture.Database
                .GetPublicationReconciliationCorpusAsync(runId));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new PreparedTicketGroupingDeltaDispatcher(fixture.Database)
                .PrepareAsync(runId));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => fixture.Database
                .GetPendingPublicationReconciliationAsync(runId));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => fixture.Database.PromotePublicationReconciliationAsync(
                runId,
                Path.Combine(fixture.DirectoryPath, "legacy.db")));

        PreparedTicketSnapshotMaterializer materializer = new(
            fixture.Database,
            fixture.Store,
            new SqliteReviewSnapshotReconciler(fixture.Store),
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            }));
        AuthoringRunRecord run = new()
        {
            Id = runId,
            ProcessorKind = "jira-fhir",
            Status = AuthoringStatusValues.Runs.Finalizing,
            Purpose =
                PreparedTicketPublicationReconciliationContract.Purpose,
            SourceRunId = "source-run",
            CreatedAt = capturedAt,
        };
        PreparedTicketPublicationReconciliationProof proof = new(
            1,
            PreparedTicketPublicationReconciliationContract.Purpose,
            "source-run",
            "source-snapshot",
            "42",
            1,
            1,
            0,
            Hash('b'),
            Hash('e'),
            capturedAt);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => materializer.MaterializeReconciliationCandidateAsync(
                run,
                proof));
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

    private static PreparedTicketPublicationReconciliationPlanner
        CreatePlanner(
            Fixture fixture,
            OrchestratorHydrationFetcher fetcher)
    {
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
        return new(
            fixture.CreateBaselineReader(),
            fetcher,
            fixture.Database,
            new AuthoringRunControlService(fixture.Store, retryPolicy),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());
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
        public long Generation { get; set; } = generation;
        public HashSet<string> MissingTicketKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> RequestedTicketKeys { get; } = [];

        public override Task<PublicationMetadataFetchResult>
            FetchPublicationMetadataAsync(
                string ticketKey,
                DateTimeOffset hydratedAt,
                CancellationToken ct)
        {
            CallCount++;
            RequestedTicketKeys.Add(ticketKey);
            if (MissingTicketKeys.Contains(ticketKey))
            {
                return Task.FromResult(new PublicationMetadataFetchResult(
                    ticketKey,
                    hydratedAt,
                    null,
                    null,
                    null,
                    [],
                    null,
                    null,
                    null,
                    null,
                    null,
                    new(
                        PublicationMetadataFetchFailureReason.TicketNotFound,
                        "Synthetic missing ticket.")));
            }
            return Task.FromResult(new PublicationMetadataFetchResult(
                ticketKey,
                hydratedAt,
                revisions[ticketKey],
                null,
                null,
                [],
                "FHIR",
                hydratedAt,
                Generation,
                true,
                1,
                Failure: null,
                UpdatedAt: hydratedAt));
        }
    }
}
