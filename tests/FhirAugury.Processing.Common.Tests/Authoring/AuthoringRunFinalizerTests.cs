using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRunFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_DatabaseOnlyCompletesAndReleasesFence()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database, databaseOnly: true);
        int stageRuns = 0;
        int snapshotRuns = 0;
        database.Execute(
            """
            UPDATE authoring_runs
            SET Purpose = @purpose,
                SourceRunId = 'source-run'
            WHERE Id = @runId
            """,
            ("@purpose", AuthoringRunPurposeValues.PublicationRefresh),
            ("@runId", run.Id));
        AuthoringRunFinalizer finalizer = new(database.Store);

        AuthoringSnapshotDescriptor? descriptor = await finalizer.FinalizeAsync(
            run.Id,
            [
                new(
                    "hydration",
                    "",
                    "input-1",
                    (_, _) =>
                    {
                        stageRuns++;
                        return Task.CompletedTask;
                    }),
            ],
            _ =>
            {
                snapshotRuns++;
                return Task.FromException<AuthoringSnapshotDescriptor>(
                    new InvalidOperationException(
                        "Database-only runs must not invoke the snapshot factory."));
            });

        Assert.Null(descriptor);
        Assert.Equal(1, stageRuns);
        Assert.Equal(0, snapshotRuns);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await database.Store.GetRunAsync(run.Id))!.Status);

        AuthoringRunRecord next = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")]);
        Assert.True(await database.Store.TryAcquireMutationFenceAsync("test", next.Id));
    }

    [Fact]
    public async Task FinalizeAsync_RetryResumesOnlyFailedStage()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database, databaseOnly: true);
        int firstRuns = 0;
        int secondRuns = 0;
        bool failSecond = true;
        AuthoringFinalizationStage[] stages =
        [
            new(
                "hydration",
                "",
                "input-1",
                (_, _) =>
                {
                    firstRuns++;
                    return Task.CompletedTask;
                }),
            new(
                "grouping",
                "fhir-core",
                "input-2",
                (_, _) =>
                {
                    secondRuns++;
                    if (failSecond)
                    {
                        throw new InvalidOperationException("grouping failed");
                    }
                    return Task.CompletedTask;
                }),
        ];
        AuthoringRunFinalizer finalizer = new(database.Store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => finalizer.FinalizeAsync(run.Id, stages));
        failSecond = false;
        await finalizer.FinalizeAsync(run.Id, stages);

        Assert.Equal(1, firstRuns);
        Assert.Equal(2, secondRuns);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await database.Store.GetRunAsync(run.Id))!.Status);
        AuthoringRunStageRecord[] persistedStages =
            (await database.Store.GetRunStagesAsync(run.Id)).ToArray();
        Assert.All(
            persistedStages,
            stage => Assert.Equal(AuthoringStatusValues.Stages.Complete, stage.Status));
        Assert.Equal(2, persistedStages.Single(stage => stage.StageName == "grouping").AttemptCount);
    }

    [Fact]
    public async Task FinalizeAsync_ConcurrentCallerDoesNotPoisonActiveRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database, databaseOnly: true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AuthoringFinalizationStage[] stages =
        [
            new(
                "hydration",
                "",
                "input-1",
                async (_, _) =>
                {
                    started.TrySetResult();
                    await release.Task;
                }),
        ];
        AuthoringRunFinalizer first = new(database.Store);
        AuthoringRunFinalizer second = new(database.Store);

        Task firstRun = first.FinalizeAsync(run.Id, stages);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => second.FinalizeAsync(run.Id, stages));
        Assert.Equal(AuthoringConflictCode.StageAlreadyInProgress, conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Runs.Finalizing,
            (await database.Store.GetRunAsync(run.Id))!.Status);

        release.SetResult();
        await firstRun;
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await database.Store.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task FinalizeAsync_ReclaimedLeaseCannotPoisonNewOwner()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database, databaseOnly: true);
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocation = 0;
        List<string> leaseIds = [];
        AuthoringFinalizationStage[] stages =
        [
            new(
                "hydration",
                "",
                "input-1",
                async (lease, _) =>
                {
                    lock (leaseIds)
                    {
                        leaseIds.Add(lease.LeaseId);
                    }
                    if (Interlocked.Increment(ref invocation) == 1)
                    {
                        firstStarted.SetResult();
                        await releaseFirst.Task;
                    }
                }),
        ];
        AuthoringRunFinalizer first = new(
            database.Store,
            orphanedStageThreshold: TimeSpan.FromMilliseconds(10));
        AuthoringRunFinalizer second = new(
            database.Store,
            orphanedStageThreshold: TimeSpan.FromMilliseconds(10));

        Task firstRun = first.FinalizeAsync(run.Id, stages);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(30);
        await second.FinalizeAsync(run.Id, stages);
        releaseFirst.SetResult();

        AuthoringConflictException lost = await Assert.ThrowsAsync<AuthoringConflictException>(
            async () => await firstRun);
        Assert.Equal(AuthoringConflictCode.StageLeaseLost, lost.Code);
        Assert.Equal(2, leaseIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await database.Store.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task FinalizeAsync_SnapshotRunRequiresMatchingDescriptor()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database, databaseOnly: false);
        AuthoringRunFinalizer finalizer = new(database.Store);
        string outputDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, database.Store);

        AuthoringSnapshotDescriptor? result = await finalizer.FinalizeAsync(
            run.Id,
            [],
            ct => writer.WriteAsync(
                new SqliteReviewSnapshotRequest(
                    "test",
                    run.Id,
                    outputDirectory,
                    1,
                    1,
                    1,
                    new Dictionary<string, long>(),
                    AuthoringSnapshotSanitizer.CreateCore()),
                ct));

        Assert.NotNull(result);
        AuthoringRunRecord completed = (await database.Store.GetRunAsync(run.Id))!;
        Assert.Equal(AuthoringStatusValues.Runs.Completed, completed.Status);
        Assert.Equal(result.SnapshotId, completed.SnapshotId);
    }

    [Fact]
    public async Task FinalizeAsync_AllSupersededItemsCompletesAndReleasesFence()
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
        await database.Store.SupersedeErroredItemAsync(
            run.Id,
            item.Id,
            "not actionable");

        AuthoringRunFinalizer finalizer = new(database.Store);
        AuthoringSnapshotDescriptor? descriptor =
            await finalizer.FinalizeAsync(run.Id, []);

        Assert.Null(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await database.Store.GetRunAsync(run.Id))!.Status);
        Assert.Null(await database.Store.GetFencedRunAsync("test"));
        AuthoringRunRecord next = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")]);
        Assert.True(await database.Store.TryAcquireMutationFenceAsync("test", next.Id));
    }

    private static async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> PrepareCompletedItemAsync(
        AuthoringTestDatabase database,
        bool databaseOnly)
    {
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly);
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
        return (run, item);
    }
}
