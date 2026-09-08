using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRunStoreTests
{
    [Fact]
    public async Task CreateRun_FreezesMembershipAndRequiresRunBackedMode()
    {
        using AuthoringTestDatabase database = new();

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.CreateRunAsync(
                "test",
                [new("FHIR-1", "ticket", "revision-1")]));
        Assert.Equal(AuthoringConflictCode.AuthoringNotActivated, conflict.Code);

        await database.ActivateAsync();
        AuthoringRunRecord run = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")]);
        AuthoringRunItemRecord item = Assert.Single(await database.Store.GetRunItemsAsync(run.Id));

        Assert.Equal(1, run.AuthoringEpoch);
        Assert.Equal("FHIR-1", item.BusinessKey);
        Assert.Equal("revision-1", item.ExpectedSourceRevision);
        Assert.Equal(AuthoringStatusValues.Items.Pending, item.Status);
    }

    [Fact]
    public async Task AcceptResult_IsAtomicAndReplaysTheOriginalReceipt()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken,
            async (connection, ct) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS domain_results(Id TEXT PRIMARY KEY, Value TEXT NOT NULL);
                    INSERT INTO domain_results(Id, Value) VALUES (@id, @value);
                    """;
                command.Parameters.AddWithValue("@id", item.Id);
                command.Parameters.AddWithValue("@value", "payload");
                await command.ExecuteNonQueryAsync(ct);
            });
        AuthoringReceiptAcceptance replay = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken,
            (_, _) => throw new InvalidOperationException("replay must not rewrite domain rows"));

        Assert.False(accepted.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(accepted.Receipt, replay.Receipt);
        Assert.Equal(1, database.Scalar<int>("SELECT COUNT(*) FROM domain_results"));
        Assert.DoesNotContain(
            claim.OperationToken,
            database.Scalar<string>(
                "SELECT TokenVerifier FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptResult_DifferentContentAfterAcceptanceConflicts()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission original = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("one"));
        await database.Store.AcceptResultAsync(original, claim.OperationToken);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(
                original with { ContentHash = AuthoringResultHasher.HashNormalizedUtf8("two") },
                claim.OperationToken));

        Assert.Equal(AuthoringConflictCode.ContentChanged, conflict.Code);
    }

    [Fact]
    public async Task AcceptResult_SourceRevisionMismatchRollsBackDomainMutation()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            "newer-revision",
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(
                submission,
                claim.OperationToken,
                async (connection, ct) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE should_not_exist(Id INTEGER);";
                    await command.ExecuteNonQueryAsync(ct);
                }));

        Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'should_not_exist'"));
    }

    [Fact]
    public async Task FailedPersistenceCanRetryTheSameOperation()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.AcceptResultAsync(
                submission,
                claim.OperationToken,
                (_, _) => throw new InvalidOperationException("write failed")));

        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken);
        Assert.False(accepted.IsReplay);
    }

    [Fact]
    public async Task RetryBeforeReceiptRotatesOperationButPostReceiptRetryDoesNot()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim first = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        await database.Store.MarkItemErrorAsync(item.Id, "agent failed");

        AuthoringRetryResult authoringRetry = await database.Store.RetryItemAsync(item.Id);
        AuthoringOperationClaim second = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        Assert.True(authoringRetry.RequiresAuthoring);
        Assert.NotEqual(first.OperationId, second.OperationId);

        AuthoringResultSubmission staleSubmission = new(
            run.Id,
            item.Id,
            first.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));
        AuthoringConflictException stale = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(staleSubmission, first.OperationToken));
        Assert.Equal(AuthoringConflictCode.StaleOperation, stale.Code);

        AuthoringResultSubmission acceptedSubmission = staleSubmission with { OperationId = second.OperationId };
        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            acceptedSubmission,
            second.OperationToken);
        await database.Store.MarkItemErrorAsync(item.Id, "hydration failed");
        AuthoringRetryResult postReceiptRetry = await database.Store.RetryItemAsync(item.Id);
        AuthoringRunItemRecord refreshed = Assert.Single(await database.Store.GetRunItemsAsync(run.Id));

        Assert.False(postReceiptRetry.RequiresAuthoring);
        Assert.Equal(second.OperationId, refreshed.CurrentOperationId);
        Assert.Equal(accepted.Receipt.ReceiptId, refreshed.AcceptedReceiptId);
        Assert.Equal(AuthoringStatusValues.Items.Persisted, refreshed.Status);
    }

    [Fact]
    public async Task MutationFenceAllowsOnlyOneActiveRun()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord first = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")]);
        AuthoringRunRecord second = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")]);

        Assert.True(await database.Store.TryAcquireMutationFenceAsync("test", first.Id));
        Assert.False(await database.Store.TryAcquireMutationFenceAsync("test", second.Id));
        Assert.Equal(AuthoringStatusValues.Runs.Queued, (await database.Store.GetRunAsync(second.Id))!.Status);
    }

    [Fact]
    public async Task MaintenanceRunReusesCurrentReceiptsWithoutAuthoringAttempts()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord sourceRun, AuthoringRunItemRecord sourceItem) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(sourceRun.Id, sourceItem.Id));
        AuthoringReceiptAcceptance receipt =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    sourceRun.Id,
                    sourceItem.Id,
                    claim.OperationId,
                    sourceItem.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(
            sourceItem.Id,
            receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(sourceRun.Id);
        await database.Store.CompleteRunAsync(sourceRun.Id, snapshotId: null);

        AuthoringRunRecord maintenance =
            await database.Store.CreateMaintenanceRunAsync(
                "test",
                [
                    new AuthoringMaintenanceRunItem(
                        sourceItem.BusinessKey,
                        sourceItem.ItemKind,
                        sourceItem.ExpectedSourceRevision,
                        receipt.Receipt.ReceiptId),
                ]);
        AuthoringRunItemRecord item = Assert.Single(
            await database.Store.GetRunItemsAsync(maintenance.Id));

        Assert.True(maintenance.DatabaseOnly);
        Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, item.AcceptedReceiptId);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunId = @runId",
                ("@runId", maintenance.Id)));
    }

    [Fact]
    public async Task RunStagesResumeAndRejectChangedFingerprint()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        AuthoringRunStageRecord stage = await database.Store.EnsureRunStageAsync(
            run.Id,
            "grouping",
            "fhir-core",
            "fingerprint-1");
        AuthoringRunStageLease firstLease = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(stage.Id));
        await database.Store.FailRunStageAsync(stage.Id, firstLease.LeaseId, "failed");
        AuthoringRunStageLease secondLease = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(stage.Id));
        await database.Store.CompleteRunStageAsync(stage.Id, secondLease.LeaseId);

        AuthoringRunStageRecord completed = Assert.Single(await database.Store.GetRunStagesAsync(run.Id));
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(AuthoringStatusValues.Stages.Complete, completed.Status);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.EnsureRunStageAsync(
                run.Id,
                "grouping",
                "fhir-core",
                "fingerprint-2"));
        Assert.Equal(AuthoringConflictCode.StageFingerprintMismatch, conflict.Code);
    }

    [Fact]
    public async Task RunStageLeasePreventsConcurrentCompletionAndCanBeReclaimed()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        AuthoringRunStageRecord stage = await database.Store.EnsureRunStageAsync(
            run.Id,
            "grouping",
            "fhir-core",
            "fingerprint-1");
        DateTimeOffset startedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        AuthoringRunStageLease first = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt));

        Assert.Null(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(9)));
        AuthoringRunStageLease reclaimed = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(11)));
        Assert.NotEqual(first.LeaseId, reclaimed.LeaseId);
        AuthoringConflictException lost = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.CompleteRunStageAsync(stage.Id, first.LeaseId));
        Assert.Equal(AuthoringConflictCode.StageLeaseLost, lost.Code);
        await database.Store.CompleteRunStageAsync(stage.Id, reclaimed.LeaseId);
    }

    [Fact]
    public async Task ProcessorModeIsOneWayAndAllocatesEpochOnActivation()
    {
        using AuthoringTestDatabase database = new();
        AuthoringProcessorModeRecord initial = await database.Store.EnsureProcessorModeAsync("test");
        AuthoringProcessorModeRecord cuttingOver = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        AuthoringProcessorModeRecord active = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);

        Assert.Equal(0, initial.Epoch);
        Assert.Equal(0, cuttingOver.Epoch);
        Assert.Equal(1, active.Epoch);
        AuthoringProcessorModeRecord replay = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        Assert.Equal(1, replay.Epoch);
        Assert.Throws<InvalidOperationException>(
            () => AuthoringStatusValues.EnsureModeTransition(
                AuthoringStatusValues.ProcessorModes.RunBacked,
                AuthoringStatusValues.ProcessorModes.Legacy));
    }

    [Fact]
    public async Task RetryCannotReopenCompletedRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(run.Id);
        await database.Store.CompleteRunAsync(run.Id, snapshotId: null);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.RetryItemAsync(item.Id));
        Assert.Equal(AuthoringConflictCode.MutationFenceUnavailable, conflict.Code);
    }

    [Fact]
    public async Task RecordClaimFailure_ClosesAttemptAndSupersedesAtExactLimit()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(run.Id, item.Id, now));
            Assert.Equal(attempt, claim.AttemptNumber);
            await database.Store.MarkClaimErrorAsync(
                item.Id,
                claim.OperationId,
                $"failure {attempt}",
                now.AddSeconds(1));

            AuthoringRunItemRecord failed = Assert.Single(
                await database.Store.GetRunItemsAsync(run.Id));
            Assert.Equal(
                attempt == 3
                    ? AuthoringStatusValues.Items.Superseded
                    : AuthoringStatusValues.Items.Error,
                failed.Status);
            Assert.Equal(
                AuthoringStatusValues.Attempts.Error,
                database.Scalar<string>(
                    "SELECT Status FROM authoring_run_attempts WHERE OperationId = @operationId",
                    ("@operationId", claim.OperationId)));

            if (attempt < 3)
            {
                now = now.AddMinutes(2);
                AuthoringErrorReconciliationResult reconciliation =
                    await database.Store.ReconcileErroredItemsAsync(run.Id, now);
                Assert.Equal(1, reconciliation.RetriedItems);
            }
        }

        Assert.Equal(
            3,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task ReconcileErroredItems_RepairsLegacyActiveAttemptAndPromotesWhenDue()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:01:00",
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset failedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id, failedAt.AddMinutes(-1)));
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', CompletedAt = @completedAt, Error = @error
            WHERE Id = @itemId
            """,
            ("@completedAt", failedAt.ToString("O")),
            ("@error", "legacy failure"),
            ("@itemId", item.Id));

        AuthoringErrorReconciliationResult result =
            await database.Store.ReconcileErroredItemsAsync(
                run.Id,
                failedAt.AddMinutes(1));

        Assert.Equal(1, result.RetriedItems);
        AuthoringRunItemRecord reconciled = Assert.Single(
            await database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Pending, reconciled.Status);
        Assert.Null(reconciled.CurrentOperationId);
        Assert.Equal(
            AuthoringStatusValues.Attempts.Error,
            database.Scalar<string>(
                "SELECT Status FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
        Assert.Equal(
            "legacy failure",
            database.Scalar<string>(
                "SELECT Error FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
    }

    [Fact]
    public async Task RetryItem_AtAttemptLimitCannotCreateFourthClaim()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', AttemptCount = 3, CompletedAt = @completedAt, Error = 'legacy failure'
            WHERE Id = @itemId
            """,
            ("@completedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("@itemId", item.Id));

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.RetryItemAsync(item.Id));

        Assert.Equal(AuthoringConflictCode.AttemptLimitReached, conflict.Code);
        Assert.Null(await database.Store.ClaimItemAsync(run.Id, item.Id));
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId AND AttemptNumber > 3",
                ("@itemId", item.Id)));
        Assert.Equal(claim.OperationId, (await database.Store.GetRunItemsAsync(run.Id))[0].CurrentOperationId);
    }

    [Fact]
    public async Task ManualRetryAndExhaustionRace_NeverCreatesAttemptFour()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:00:01",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        await database.Store.ClaimItemAsync(run.Id, item.Id);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', AttemptCount = 3, CompletedAt = @completedAt, Error = 'legacy failure'
            WHERE Id = @itemId
            """,
            ("@completedAt", failedAt.ToString("O")),
            ("@itemId", item.Id));

        Task<Exception?> retry = Record.ExceptionAsync(
            () => database.Store.RetryItemAsync(item.Id));
        Task<Exception?> reconcile = Record.ExceptionAsync(
            () => database.Store.ReconcileErroredItemsAsync(run.Id, DateTimeOffset.UtcNow));
        await Task.WhenAll(retry, reconcile);
        Exception? retryException = await retry;
        Exception? reconcileException = await reconcile;

        Assert.Null(reconcileException);
        AuthoringConflictException conflict =
            Assert.IsType<AuthoringConflictException>(retryException);
        Assert.Contains(
            conflict.Code,
            new[]
            {
                AuthoringConflictCode.AttemptLimitReached,
                AuthoringConflictCode.ItemNotClaimable,
            });
        Assert.Null(await database.Store.ClaimItemAsync(run.Id, item.Id));
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId AND AttemptNumber > 3",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task ReconcileErroredItems_ResumesAcceptedReceiptWithoutNewAttempt()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id, now));
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken,
            now: now.AddSeconds(1));
        string lease = Assert.IsType<string>(
            await database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId,
                now.AddSeconds(2)));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            lease,
            "post-receipt failure",
            now.AddSeconds(3));

        AuthoringErrorReconciliationResult result =
            await database.Store.ReconcileErroredItemsAsync(
                run.Id,
                now.AddMinutes(2));

        Assert.Equal(1, result.ResumedReceiptItems);
        AuthoringRunItemRecord resumed = Assert.Single(
            await database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Persisted, resumed.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, resumed.AcceptedReceiptId);
        Assert.Equal(1, resumed.AttemptCount);
        Assert.Equal(
            1,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task SupersedeErroredItem_LeavesRunFencedAndFinalizable()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");

        AuthoringItemSupersedeResult result =
            await database.Store.SupersedeErroredItemAsync(
                run.Id,
                item.Id,
                "not actionable");

        Assert.Equal(AuthoringStatusValues.Items.Superseded, result.Status);
        Assert.Equal(run.Id, (await database.Store.GetFencedRunAsync("test"))!.Id);
        Assert.True(await database.Store.AllItemsCompleteAsync(run.Id));
        Assert.Equal(
            "worker failure",
            database.Scalar<string>(
                "SELECT Error FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
        Assert.Equal(
            "not actionable",
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Error);
    }

    [Fact]
    public async Task SupersedeErroredItem_RejectsAcceptedReceipt()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        string lease = Assert.IsType<string>(
            await database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            lease,
            "post-receipt failure");

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.SupersedeErroredItemAsync(
                    run.Id,
                    item.Id,
                    "discard"));

        Assert.Equal(AuthoringConflictCode.ItemNotClaimable, conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Status);
    }
}

internal sealed class AuthoringTestDatabase : IDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;

    public AuthoringTestDatabase(ProcessingServiceOptions? options = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"fhir-augury-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "authoring.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        RetryPolicy = new AuthoringRetryPolicy(
            Options.Create(options ?? new ProcessingServiceOptions()));
        Store = new AuthoringRunStore(OpenConnection, retryPolicy: RetryPolicy);
        Store.Initialize();
    }

    public string DatabasePath { get; }
    public AuthoringRetryPolicy RetryPolicy { get; }
    public AuthoringRunStore Store { get; }

    public SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task ActivateAsync()
    {
        await Store.EnsureProcessorModeAsync("test");
        AuthoringProcessorModeRecord mode = await Store.GetProcessorModeAsync("test");
        if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await Store.TransitionProcessorModeAsync(
                "test",
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await Store.TransitionProcessorModeAsync(
                "test",
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }
    }

    public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateRunningRunAsync(
        bool databaseOnly = false)
    {
        await ActivateAsync();
        AuthoringRunRecord run = await Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            databaseOnly);
        Assert.True(await Store.TryAcquireMutationFenceAsync("test", run.Id));
        AuthoringRunItemRecord item = Assert.Single(await Store.GetRunItemsAsync(run.Id));
        return (run, item);
    }

    public T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
