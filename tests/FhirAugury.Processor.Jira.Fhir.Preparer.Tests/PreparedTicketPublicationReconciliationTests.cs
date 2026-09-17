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
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;
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
    public async Task LocalSourceLag_RemainsRetryableAndIsNotGenericallySuperseded()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054");
        Dictionary<string, string> authoritativeRevisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-13054"] =
                    new DateTimeOffset(
                        2026,
                        9,
                        3,
                        0,
                        0,
                        0,
                        TimeSpan.Zero).ToString("O"),
            };
        ObservationFetcher fetcher = new(
            authoritativeRevisions,
            generation: 42);
        JiraAuthoringRunCoordinator coordinator =
            CreateCoordinator(fixture);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher, coordinator);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(start.Run.RunId));
        Assert.Equal(
            authoritativeRevisions[item.BusinessKey],
            item.ExpectedSourceRevision);
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.Store.ClaimItemAsync(
                    start.Run.RunId,
                    item.Id));
        AuthoringConflictException localLag =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Store.AcceptResultWithReceiptAsync(
                    new(
                        start.Run.RunId,
                        item.Id,
                        claim.OperationId,
                        item.ExpectedSourceRevision,
                        Hash('a')),
                    claim.OperationToken,
                    async (connection, _, cancellationToken) =>
                        await JiraProcessingSourceTicketStore
                            .EnsureCurrentSourceRevisionAsync(
                                connection,
                                item.BusinessKey,
                                item.ItemKind,
                                item.ExpectedSourceRevision,
                                cancellationToken)));
        Assert.Equal(
            AuthoringConflictCode.SourceRevisionMismatch,
            localLag.Code);
        await fixture.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "the local Jira source store has not reached generation 42");

        Assert.False(
            await coordinator.SupersedeStaleItemsAsync(
                start.Run.RunId));

        AuthoringRunControlStatus genericStatus =
            await new AuthoringRunControlService(
                fixture.Store,
                new AuthoringRetryPolicy(
                    Options.Create(new ProcessingServiceOptions())))
            .GetStatusAsync(
                coordinator.ProcessorKind,
                start.Run.RunId);
        AuthoringRunItemStatus statusItem =
            Assert.Single(genericStatus.Items);
        Assert.True(statusItem.AllowedActions!.CanRetryNow);
        Assert.False(statusItem.AllowedActions.CanSupersede);
        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            statusItem.Status);
        Assert.Empty(
            (await planner.GetStatusAsync(start.Run.RunId))
            .InvalidatedTicketKeys);

        AuthoringRetryResult retry =
            await fixture.Store.RetryItemAsync(item.Id);
        Assert.True(retry.RequiresAuthoring);
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            Assert.Single(
                await fixture.Store.GetRunItemsAsync(
                    start.Run.RunId)).Status);
    }

    [Fact]
    public async Task AuthoritativeInvalidation_CanCancelStagedRunIdempotently()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase);
        ObservationFetcher fetcher = new(revisions, generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        revisions["FHIR-13054"] =
            new DateTimeOffset(
                2026,
                9,
                4,
                0,
                0,
                0,
                TimeSpan.Zero).ToString("O");
        fetcher.Generation = 43;
        Assert.Equal(
            ["FHIR-13054"],
            (await planner.GetStatusAsync(start.Run.RunId))
            .InvalidatedTicketKeys);

        PreparedTicketPublicationReconciliationCancelResult cancelled =
            await planner.CancelAsync(
                start.Run.RunId,
                "  frozen Jira revision advanced  ");
        PreparedTicketPublicationReconciliationCancelResult replay =
            await planner.CancelAsync(
                start.Run.RunId,
                "replayed request must preserve the first audit");

        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Cancelled,
            cancelled.Status.Promotion.State);
        Assert.Equal(
            "frozen Jira revision advanced",
            cancelled.Reason);
        Assert.Equal(cancelled.CancelledAt, replay.CancelledAt);
        Assert.Equal(cancelled.Reason, replay.Reason);
        Assert.Equal(
            cancelled.CancelledAt,
            cancelled.Status.Promotion.CancelledAt);
        Assert.Equal(
            cancelled.Reason,
            cancelled.Status.Promotion.CancellationReason);
        Assert.False(cancelled.Status.Promotion.MutationFenceHeld);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            cancelled.Status.Run.Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.NotNull(
            await fixture.Database
                .GetPublicationReconciliationComparisonAsync(
                    start.Run.RunId));

        PreparedTicketPublicationReconciliationStartResult next =
            await planner.StartAsync(source.Run.Id);
        Assert.NotEqual(start.Run.RunId, next.Run.RunId);
        Assert.Equal(
            next.Run.RunId,
            Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetFencedRunAsync("jira-fhir")).Id);
    }

    [Fact]
    public async Task Cancellation_SupersedesCompletedReAuthorAndAdmitsSameRevisionAtNewGeneration()
    {
        using Fixture fixture = new();
        const string ticketKey = "FHIR-13054";
        SourceResult source =
            await fixture.CreateSourceRunAsync(ticketKey);
        string revisedSourceRevision =
            new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)
                .ToString("O");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                [ticketKey] = revisedSourceRevision,
            };
        fixture.Execute(
            """
            UPDATE jira_processing_source_tickets
            SET LastUpdated = @revision
            WHERE Key = @ticketKey
            """,
            ("@revision", revisedSourceRevision),
            ("@ticketKey", ticketKey));
        ObservationFetcher fetcher = new(revisions, generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        PreparedTicketPublicationReconciliationItemDecision decision =
            Assert.Single(start.Comparison.Items);
        Assert.Equal(
            PreparedTicketPublicationReconciliationDispositionValues
                .ReAuthor,
            decision.Disposition);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(start.Run.RunId));
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.Store.ClaimItemAsync(
                    start.Run.RunId,
                    item.Id));
        PreparedTicketPayload revised = Payload(ticketKey, "revised");
        string contentHash =
            PreparedTicketAuthoringDtos.ComputeContentHash(revised);
        AuthoringReceiptAcceptance acceptance =
            await fixture.Store.AcceptResultWithReceiptAsync(
                new(
                    start.Run.RunId,
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
                            ticketKey,
                            item.ItemKind,
                            item.ExpectedSourceRevision,
                            ct);
                    await fixture.Database
                        .StagePublicationReconciliationTicketAsync(
                            connection,
                            start.Run.RunId,
                            item.Id,
                            claim.OperationId,
                            receiptId,
                            item.ExpectedSourceRevision,
                            contentHash,
                            revised,
                            Hydration(ticketKey),
                            ct: ct);
                });
        await fixture.Store.MarkItemCompleteAsync(
            item.Id,
            acceptance.Receipt.ReceiptId);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            Assert.Single(
                await fixture.Store.GetRunItemsAsync(
                    start.Run.RunId)).Status);
        Assert.Equal(
            1,
            fixture.Scalar<long>(
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_publication_staged_receipts
                WHERE RunId = '{start.Run.RunId}'
                """));

        fetcher.Generation = 43;
        PreparedTicketPublicationReconciliationStatusResult invalidated =
            await planner.GetStatusAsync(start.Run.RunId);
        Assert.Equal([ticketKey], invalidated.InvalidatedTicketKeys);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .RevisionInvalidation,
            invalidated.FailureCode);

        await planner.CancelAsync(
            start.Run.RunId,
            "Jira generation advanced without a ticket revision change");

        AuthoringRunItemRecord cancelledItem = Assert.Single(
            await fixture.Store.GetRunItemsAsync(start.Run.RunId));
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            cancelledItem.Status);
        Assert.Equal(
            acceptance.Receipt.ReceiptId,
            cancelledItem.AcceptedReceiptId);
        Assert.Equal(
            acceptance.Receipt,
            await fixture.Store.GetReceiptByOperationAsync(
                claim.OperationId));

        PreparedTicketPublicationReconciliationStartResult replacement =
            await planner.StartAsync(source.Run.Id);
        AuthoringRunItemRecord replacementItem = Assert.Single(
            await fixture.Store.GetRunItemsAsync(replacement.Run.RunId));
        Assert.NotEqual(start.Run.RunId, replacement.Run.RunId);
        Assert.Equal("43", replacement.Comparison.StableJiraGeneration);
        Assert.Equal(
            PreparedTicketPublicationReconciliationDispositionValues
                .ReAuthor,
            Assert.Single(replacement.Comparison.Items).Disposition);
        Assert.Equal(item.ItemKind, replacementItem.ItemKind);
        Assert.Equal(
            item.ExpectedSourceRevision,
            replacementItem.ExpectedSourceRevision);
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            replacementItem.Status);
    }

    [Fact]
    public async Task CancellationController_ReturnsTypedResultAndStableConflict()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054");
        ObservationFetcher fetcher = new(
            new Dictionary<string, string>(
                source.ExpectedRevisions,
                StringComparer.OrdinalIgnoreCase),
            generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult first =
            await planner.StartAsync(source.Run.Id);
        PreparedTicketPublicationMaintenanceController controller = new(
            fixture.CreateRefreshService(fetcher),
            planner,
            new PreparedTicketPublicationRecoveryService(
                fixture.Database,
                fixture.Store,
                NullLogger<
                    PreparedTicketPublicationRecoveryService>.Instance),
            fixture.Database);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(
            await controller.CancelReconciliation(
                first.Run.RunId,
                new("operator invalidated staged work"),
                CancellationToken.None));
        PreparedTicketPublicationReconciliationCancelResult result =
            Assert.IsType<
                PreparedTicketPublicationReconciliationCancelResult>(
                ok.Value);
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Cancelled,
            result.Status.Promotion.State);

        PreparedTicketPublicationReconciliationStartResult second =
            await planner.StartAsync(source.Run.Id);
        fixture.Execute(
            """
            UPDATE prepared_ticket_publication_reconciliations
            SET PromotionState = @pending
            WHERE RunId = @runId;
            UPDATE prepared_ticket_publication_reconciliation_journal
            SET State = @pending
            WHERE RunId = @runId;
            """,
            ("@pending",
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending),
            ("@runId", second.Run.RunId));

        ConflictObjectResult conflict =
            Assert.IsType<ConflictObjectResult>(
                await controller.CancelReconciliation(
                    second.Run.RunId,
                    new("too late"),
                    CancellationToken.None));
        PreparedTicketPublicationReconciliationFailure failure =
            Assert.IsType<PreparedTicketPublicationReconciliationFailure>(
                conflict.Value);
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .CancellationNotAllowed,
            failure.Error);
    }

    [Fact]
    public async Task Cancellation_RacingItemRetryAlwaysLeavesTerminalReleasedRun()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-13054"] =
                    new DateTimeOffset(
                        2026,
                        9,
                        3,
                        0,
                        0,
                        0,
                        TimeSpan.Zero).ToString("O"),
            };
        ObservationFetcher fetcher = new(revisions, generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(start.Run.RunId));
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.Store.ClaimItemAsync(
                    start.Run.RunId,
                    item.Id));
        await fixture.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "transient local source lag");
        using Barrier barrier = new(2);
        Task<PreparedTicketPublicationReconciliationCancelResult> cancel =
            Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await planner.CancelAsync(
                    start.Run.RunId,
                    "operator cancelled invalidated work");
            });
        Task<Exception> retry = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await Record.ExceptionAsync(
                () => fixture.Store.RetryItemAsync(item.Id));
        });

        await Task.WhenAll(cancel, retry);
        PreparedTicketPublicationReconciliationCancelResult cancelResult =
            await cancel;
        Exception? retryError = await retry;

        if (retryError is not null)
        {
            Assert.IsType<AuthoringConflictException>(retryError);
        }
        Assert.Equal(
            PreparedTicketPublicationReconciliationPromotionStateValues
                .Cancelled,
            cancelResult.Status.Promotion.State);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            Assert.IsType<AuthoringRunRecord>(
                await fixture.Store.GetRunAsync(
                    start.Run.RunId)).Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.Single(
                await fixture.Store.GetRunItemsAsync(
                    start.Run.RunId)).Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
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
        Assert.False(
            await fixture.Database
                .IsPublicationReconciliationStagingCompleteAsync(run.Id));
        await fixture.Store.MarkItemCompleteAsync(
            item.Id,
            acceptance.Receipt.ReceiptId);
        Assert.True(
            await fixture.Store.AllItemsStrictlyCompleteAsync(run.Id));
        Assert.True(
            await fixture.Database
                .IsPublicationReconciliationStagingCompleteAsync(run.Id));
    }

    [Fact]
    public async Task ReconciliationReadiness_RejectsSupersededOrUnstagedReAuthorItem()
    {
        using Fixture fixture = new();
        SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-13054");
        Dictionary<string, string> revisions =
            new(source.ExpectedRevisions, StringComparer.OrdinalIgnoreCase)
            {
                ["FHIR-13054"] =
                    new DateTimeOffset(
                        2026,
                        9,
                        3,
                        0,
                        0,
                        0,
                        TimeSpan.Zero).ToString("O"),
            };
        ObservationFetcher fetcher = new(revisions, generation: 42);
        PreparedTicketPublicationReconciliationPlanner planner =
            CreatePlanner(fixture, fetcher);
        PreparedTicketPublicationReconciliationStartResult start =
            await planner.StartAsync(source.Run.Id);
        AuthoringRunItemRecord item = Assert.Single(
            await fixture.Store.GetRunItemsAsync(start.Run.RunId));
        fixture.Execute(
            """
            UPDATE authoring_run_items
            SET Status = @status, CompletedAt = @completedAt
            WHERE Id = @itemId
            """,
            ("@status", AuthoringStatusValues.Items.Superseded),
            ("@completedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("@itemId", item.Id));

        Assert.True(
            await fixture.Store.AllItemsCompleteAsync(start.Run.RunId));
        Assert.False(
            await fixture.Store.AllItemsStrictlyCompleteAsync(
                start.Run.RunId));
        Assert.False(
            await fixture.Database
                .IsPublicationReconciliationStagingCompleteAsync(
                    start.Run.RunId));

        fixture.Execute(
            """
            UPDATE authoring_run_items
            SET Status = @status, AcceptedReceiptId = 'missing-stage-receipt'
            WHERE Id = @itemId
            """,
            ("@status", AuthoringStatusValues.Items.Complete),
            ("@itemId", item.Id));
        Assert.True(
            await fixture.Store.AllItemsStrictlyCompleteAsync(
                start.Run.RunId));
        Assert.False(
            await fixture.Database
                .IsPublicationReconciliationStagingCompleteAsync(
                    start.Run.RunId));

        AuthoringRunRecord currentRun = Assert.IsType<AuthoringRunRecord>(
            await fixture.Store.GetRunAsync(start.Run.RunId));
        PreparedTicketPublicationReconciliationException error =
            await Assert.ThrowsAsync<
                PreparedTicketPublicationReconciliationException>(
                () => CreateWorkflows(
                    fixture,
                    fetcher,
                    planner).FinalizeReconciliationAsync(currentRun));
        Assert.Equal(
            PreparedTicketPublicationReconciliationFailureCodes
                .StagingMismatch,
            error.FailureCode);
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
            SET Status = 'error'
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
        Assert.Equal(
            AuthoringStatusValues.Runs.Abandoned,
            (await fixture.Store.GetRunAsync(run.Id))!.Status);
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
            PreparedTicketPublicationReconciliationFailureCodes.CancellationNotAllowed,
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
                "cancellation-not-allowed",
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
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "cancelled"));
        Assert.False(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "abandoned"));
    }

    private static PreparedTicketPublicationReconciliationPlanner
        CreatePlanner(
            Fixture fixture,
            OrchestratorHydrationFetcher fetcher,
            JiraAuthoringRunCoordinator? coordinator = null)
    {
        AuthoringRetryPolicy retryPolicy = new(
            Options.Create(new ProcessingServiceOptions()));
        coordinator ??= CreateCoordinator(fixture);
        return new(
            fixture.CreateBaselineReader(),
            fetcher,
            fixture.Database,
            new AuthoringRunControlService(fixture.Store, retryPolicy),
            coordinator,
            new AuthoringRunSchedulerWakeSignal());
    }

    private static JiraAuthoringRunCoordinator CreateCoordinator(
        Fixture fixture)
        => new(
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

    private static PreparedTicketRunWorkflowRegistry CreateWorkflows(
        Fixture fixture,
        OrchestratorHydrationFetcher fetcher,
        PreparedTicketPublicationReconciliationPlanner planner)
    {
        IOptions<PreparerServiceOptions> options =
            Options.Create(new PreparerServiceOptions
            {
                SnapshotDirectory = fixture.SnapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            });
        return new(
            fixture.Store,
            fixture.Database,
            fetcher,
            planner,
            new PreparedTicketGroupingDeltaDispatcher(fixture.Database),
            new PreparedTicketSnapshotMaterializer(
                fixture.Database,
                fixture.Store,
                new SqliteReviewSnapshotReconciler(fixture.Store),
                options),
            new PreparedTicketPublicationRecoveryService(
                fixture.Database,
                fixture.Store,
                NullLogger<
                    PreparedTicketPublicationRecoveryService>.Instance),
            options,
            NullLogger<PreparedTicketRunWorkflowRegistry>.Instance);
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
