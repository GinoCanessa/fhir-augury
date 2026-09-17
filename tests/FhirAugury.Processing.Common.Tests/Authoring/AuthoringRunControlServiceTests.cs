using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRunControlServiceTests
{
    [Fact]
    public async Task GetStatusAsync_ReportsRetryAndSupersedeMetadata()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:02:00",
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset failedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                failedAt.AddMinutes(-1)));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure",
            failedAt);
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        Assert.Equal(1, status.Run.FailedItems);
        Assert.Equal(1, status.Run.RetryableErrorItems);
        Assert.Equal(0, status.Run.SupersededItems);
        AuthoringRunItemStatus itemStatus = Assert.Single(status.Items);
        Assert.Equal(2, itemStatus.AttemptsRemaining);
        Assert.Equal(failedAt.AddMinutes(2), itemStatus.NextAutomaticRetryAt);
        Assert.Equal("worker failure", itemStatus.CurrentError);
        Assert.Null(itemStatus.SupersessionReason);
        Assert.True(itemStatus.AllowedActions!.CanRetryNow);
        Assert.True(itemStatus.AllowedActions.CanSupersede);
        Assert.Equal(
            failedAt.AddMinutes(2),
            status.Run.State!.NextAutomaticRecoveryAt);
    }

    [Fact]
    public async Task GetStatusAsync_ComputesAllowedActionsFromFenceReceiptAndBudget()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:02:00",
            AuthoringMaxAttempts = 3,
        });
        await database.ActivateAsync();
        AuthoringRunRecord run = await database.Store.CreateRunAsync(
            "test",
            [
                new("FHIR-1", "ticket", "revision-1"),
                new("FHIR-2", "ticket", "revision-2"),
                new("FHIR-3", "ticket", "revision-3"),
            ],
            requestJson: "{}");
        Assert.True(await database.Store.TryAcquireMutationFenceAsync(
            "test",
            run.Id));
        AuthoringRunItemRecord[] items =
            (await database.Store.GetRunItemsAsync(run.Id)).ToArray();
        DateTimeOffset failedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        AuthoringOperationClaim retryableClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    run.Id,
                    items[0].Id,
                    failedAt.AddMinutes(-1)));
        await database.Store.MarkClaimErrorAsync(
            items[0].Id,
            retryableClaim.OperationId,
            "authoring failure",
            failedAt);

        AuthoringOperationClaim receiptClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    run.Id,
                    items[1].Id,
                    failedAt.AddMinutes(-1)));
        AuthoringReceiptAcceptance receipt =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    run.Id,
                    items[1].Id,
                    receiptClaim.OperationId,
                    items[1].ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                receiptClaim.OperationToken,
                now: failedAt);
        string persistenceLease = Assert.IsType<string>(
            await database.Store.ClaimPersistedItemAsync(
                items[1].Id,
                receipt.Receipt.ReceiptId,
                receiptClaim.OperationId,
                failedAt));
        await database.Store.MarkClaimErrorAsync(
            items[1].Id,
            persistenceLease,
            "persistence failure",
            failedAt.AddSeconds(1));

        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = @status, AttemptCount = 3, CompletedAt = @completedAt,
                Error = 'attempts exhausted'
            WHERE Id = @itemId
            """,
            ("@status", AuthoringStatusValues.Items.Error),
            ("@completedAt", failedAt.AddSeconds(2).ToString("O")),
            ("@itemId", items[2].Id));
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        AuthoringRunItemStatus retryable =
            status.Items.Single(item => item.ItemId == items[0].Id);
        Assert.True(retryable.AllowedActions!.CanRetryNow);
        Assert.True(retryable.AllowedActions.CanSupersede);
        Assert.Equal("authoring failure", retryable.CurrentError);
        Assert.Null(retryable.SupersessionReason);
        Assert.Equal(2, retryable.AttemptsRemaining);
        Assert.Equal(failedAt.AddMinutes(2), retryable.NextAutomaticRetryAt);

        AuthoringRunItemStatus receiptBacked =
            status.Items.Single(item => item.ItemId == items[1].Id);
        Assert.True(receiptBacked.AllowedActions!.CanRetryNow);
        Assert.False(receiptBacked.AllowedActions.CanSupersede);
        Assert.Null(receiptBacked.AttemptsRemaining);
        Assert.Equal(
            failedAt.AddMinutes(2).AddSeconds(1),
            receiptBacked.NextAutomaticRetryAt);

        AuthoringRunItemStatus exhausted =
            status.Items.Single(item => item.ItemId == items[2].Id);
        Assert.False(exhausted.AllowedActions!.CanRetryNow);
        Assert.True(exhausted.AllowedActions.CanSupersede);
        Assert.Equal(0, exhausted.AttemptsRemaining);
        Assert.Null(exhausted.NextAutomaticRetryAt);
        Assert.Equal("attempts exhausted", exhausted.CurrentError);
        Assert.Equal(
            failedAt.AddMinutes(2),
            status.Run.State!.NextAutomaticRecoveryAt);

        await database.Store.ReleaseMutationFenceAsync("test", run.Id);
        AuthoringRunControlStatus unfenced =
            await service.GetStatusAsync("test", run.Id);
        Assert.All(
            unfenced.Items,
            item =>
            {
                Assert.False(item.AllowedActions!.CanRetryNow);
                Assert.False(item.AllowedActions.CanSupersede);
                Assert.Null(item.NextAutomaticRetryAt);
            });
        Assert.Null(unfenced.Run.State!.NextAutomaticRecoveryAt);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsErrorAsRecoverableAndNonTerminal()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        database.Execute(
            """
            UPDATE authoring_runs
            SET Status = @status, Error = 'finalization failed'
            WHERE Id = @runId
            """,
            ("@status", AuthoringStatusValues.Runs.Error),
            ("@runId", run.Id));
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        Assert.Equal(AuthoringStatusValues.Runs.Error, status.Run.Status);
        Assert.False(status.Run.State!.IsTerminal);
        Assert.True(status.Run.State.IsRecoverable);
        Assert.Equal("finalization failed", status.Run.Error);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsAbandonedAsTerminalAndNonRecoverable()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        database.Execute(
            """
            UPDATE authoring_runs
            SET Status = @status, CompletedAt = @completedAt,
                Error = 'canonical-unpublished'
            WHERE Id = @runId;
            DELETE FROM authoring_mutation_fences
            WHERE ProcessorKind = 'test' AND RunId = @runId;
            """,
            ("@status", AuthoringStatusValues.Runs.Abandoned),
            ("@completedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("@runId", run.Id));
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        Assert.Equal(AuthoringStatusValues.Runs.Abandoned, status.Run.Status);
        Assert.True(status.Run.State!.IsTerminal);
        Assert.False(status.Run.State.IsRecoverable);
        Assert.Null(status.Run.State.NextAutomaticRecoveryAt);
        Assert.All(
            status.Items,
            item =>
            {
                Assert.False(item.AllowedActions!.CanRetryNow);
                Assert.False(item.AllowedActions.CanSupersede);
            });
    }

    [Fact]
    public async Task SupersedeItemAsync_RequiresMatchingProcessorAndReason()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SupersedeItemAsync(
                "other",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest("not actionable")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SupersedeItemAsync(
                "test",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest(" ")));

        AuthoringItemSupersedeResult result =
            await service.SupersedeItemAsync(
                "test",
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest("not actionable"));
        Assert.Equal(AuthoringStatusValues.Items.Superseded, result.Status);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);
        Assert.Equal(1, status.Run.FailedItems);
        Assert.Equal(0, status.Run.RetryableErrorItems);
        Assert.Equal(1, status.Run.SupersededItems);
        AuthoringRunItemStatus supersededItem = Assert.Single(status.Items);
        Assert.Equal(0, supersededItem.AttemptsRemaining);
        Assert.Null(supersededItem.NextAutomaticRetryAt);
        Assert.Null(supersededItem.CurrentError);
        Assert.Equal("not actionable", supersededItem.SupersessionReason);
        Assert.False(supersededItem.AllowedActions!.CanRetryNow);
        Assert.False(supersededItem.AllowedActions.CanSupersede);
    }

    [Fact]
    public async Task ReconciliationItems_NeverAdvertiseOrPermitGenericSupersede()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "local source store has not caught up");
        database.Execute(
            """
            UPDATE authoring_runs
            SET Purpose = @purpose
            WHERE Id = @runId
            """,
            ("@purpose",
                AuthoringRunPurposeValues.PublicationReconciliation),
            ("@runId", run.Id));
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        AuthoringRunControlStatus status =
            await service.GetStatusAsync("test", run.Id);

        AuthoringRunItemStatus itemStatus = Assert.Single(status.Items);
        Assert.Null(itemStatus.AcceptedReceiptId);
        Assert.True(itemStatus.AllowedActions!.CanRetryNow);
        Assert.False(itemStatus.AllowedActions.CanSupersede);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => service.SupersedeItemAsync(
                    "test",
                    run.Id,
                    item.Id,
                    new AuthoringItemSupersedeRequest(
                        "source revision changed")));
        Assert.Equal(
            AuthoringConflictCode.ReconciliationCancelRequired,
            conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            Assert.Single(
                await database.Store.GetRunItemsAsync(run.Id)).Status);
    }

    [Fact]
    public async Task RetryItemAsync_RejectsCoordinatesFromAnotherRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");
        AuthoringRunControlService service =
            new(database.Store, database.RetryPolicy);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RetryItemAsync("test", "other-run", item.Id));
    }
}
