using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Common.Tests.Authoring;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Queue;

public sealed class AuthoringRunSchedulerTests
{
    [Fact]
    public async Task RetryWaitsUntilCompletedAtPlusDelay()
    {
        ProcessingServiceOptions options = CreateOptions();
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        DateTimeOffset failedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                failedAt.AddMinutes(-1)));
        await fixture.Database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure",
            failedAt);
        fixture.Lifecycle.Stop();
        fixture.Now = failedAt.AddSeconds(30);

        TimeSpan wait = await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), wait);
        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            Assert.Single(await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);

        fixture.Now = failedAt.AddMinutes(1);
        await fixture.Scheduler.RunCycleAsync();
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            Assert.Single(await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task RestartBeforeOrphanThreshold_RecoversWhenThresholdElapses()
    {
        ProcessingServiceOptions options = CreateOptions();
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        DateTimeOffset startedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        _ = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                startedAt));
        fixture.Lifecycle.Stop();
        fixture.Now = startedAt.AddMinutes(5);

        TimeSpan wait = await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(TimeSpan.FromMinutes(5), wait);
        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);

        fixture.Now = startedAt.AddMinutes(10);
        wait = await fixture.Scheduler.RunCycleAsync();

        AuthoringRunItemRecord recovered = Assert.Single(
            await fixture.Database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Error, recovered.Status);
        Assert.Equal(startedAt.AddMinutes(10), recovered.CompletedAt);
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task ApplyResultFailure_AfterClaim_RecoversSameRun()
    {
        ProcessingServiceOptions options = CreateOptions();
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, _) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        fixture.QueueStore.ApplyException =
            new InvalidOperationException("result transition failed");

        await fixture.Scheduler.RunCycleAsync();
        await fixture.Runner.DrainAsync();

        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(1, fixture.Handler.InvocationCount);

        fixture.QueueStore.ApplyException = null;
        fixture.Lifecycle.Stop();
        fixture.Now = fixture.Now.AddMinutes(5);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await fixture.Scheduler.RunCycleAsync());

        fixture.Now = fixture.Now.AddMinutes(5);
        await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            Assert.Single(
                await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(1, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task OrphanedFinalAttempt_SupersedesAndFinalizes()
    {
        ProcessingServiceOptions options = CreateOptions();
        options.AuthoringMaxAttempts = 1;
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        DateTimeOffset startedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        _ = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                startedAt));
        fixture.Now = startedAt.AddMinutes(10);

        Assert.Equal(TimeSpan.Zero, await fixture.Scheduler.RunCycleAsync());

        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.Single(
                await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.Database.Store.GetRunAsync(run.Id))!.Status);
        Assert.Null(await fixture.Database.Store.GetFencedRunAsync("test"));
        Assert.Equal(1, fixture.Finalization.FinalizeCount);
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task PostReceiptLease_RecoversToPersistedWithoutNewAuthoringAttempt()
    {
        ProcessingServiceOptions options = CreateOptions();
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        DateTimeOffset authoringStartedAt =
            new(2026, 9, 8, 11, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(
                run.Id,
                item.Id,
                authoringStartedAt));
        AuthoringReceiptAcceptance receipt =
            await fixture.Database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken,
                now: authoringStartedAt.AddMinutes(1));
        DateTimeOffset leaseStartedAt = authoringStartedAt.AddHours(1);
        _ = Assert.IsType<string>(
            await fixture.Database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId,
                leaseStartedAt));
        fixture.Lifecycle.Stop();
        fixture.Now = leaseStartedAt.AddMinutes(5);

        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await fixture.Scheduler.RunCycleAsync());
        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.Database.Store.GetRunItemsAsync(run.Id)).Status);

        fixture.Now = leaseStartedAt.AddMinutes(10);
        await fixture.Scheduler.RunCycleAsync();

        AuthoringRunItemRecord recovered = Assert.Single(
            await fixture.Database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Persisted, recovered.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, recovered.AcceptedReceiptId);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task ExhaustedFailureFinalizesBeforeActivatingOldestQueuedRun()
    {
        ProcessingServiceOptions options = CreateOptions();
        options.AuthoringMaxAttempts = 1;
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord first, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(first.Id, item.Id));
        await fixture.Database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "terminal failure");
        AuthoringRunRecord second = await fixture.Database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")],
            databaseOnly: true);

        Assert.Equal(TimeSpan.Zero, await fixture.Scheduler.RunCycleAsync());
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.Database.Store.GetRunAsync(first.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            (await fixture.Database.Store.GetRunAsync(second.Id))!.Status);
        Assert.Null(await fixture.Database.Store.GetFencedRunAsync("test"));

        Assert.Equal(TimeSpan.Zero, await fixture.Scheduler.RunCycleAsync());
        Assert.Equal(second.Id, (await fixture.Database.Store.GetFencedRunAsync("test"))!.Id);
    }

    [Fact]
    public async Task PausedLifecycleReconcilesAndFinalizesWithoutDispatchOrActivation()
    {
        ProcessingServiceOptions options = CreateOptions();
        options.AuthoringMaxAttempts = 1;
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord first, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(first.Id, item.Id));
        await fixture.Database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "terminal failure");
        AuthoringRunRecord second = await fixture.Database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")],
            databaseOnly: true);
        fixture.Lifecycle.Stop();

        await fixture.Scheduler.RunCycleAsync();
        TimeSpan pausedWait = await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.Database.Store.GetRunAsync(first.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            (await fixture.Database.Store.GetRunAsync(second.Id))!.Status);
        Assert.Null(await fixture.Database.Store.GetFencedRunAsync("test"));
        Assert.Equal(options.SyncSchedule, pausedWait.ToString());
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task AcceptedReceiptErrorReturnsToPersistedWithoutReauthoring()
    {
        ProcessingServiceOptions options = CreateOptions();
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        DateTimeOffset now =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(run.Id, item.Id, now));
        AuthoringReceiptAcceptance receipt =
            await fixture.Database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken,
                now: now.AddSeconds(1));
        string lease = Assert.IsType<string>(
            await fixture.Database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId,
                now.AddSeconds(2)));
        await fixture.Database.Store.MarkClaimErrorAsync(
            item.Id,
            lease,
            "post-receipt failure",
            now.AddSeconds(3));
        fixture.Lifecycle.Stop();
        fixture.Now = now.AddMinutes(2);

        await fixture.Scheduler.RunCycleAsync();

        AuthoringRunItemRecord resumed = Assert.Single(
            await fixture.Database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Persisted, resumed.Status);
        Assert.Equal(1, resumed.AttemptCount);
        Assert.Equal(0, fixture.Handler.InvocationCount);
    }

    [Fact]
    public async Task InFlightCompletionWakesBeforeSyncSchedule()
    {
        ProcessingServiceOptions options = CreateOptions();
        TaskCompletionSource<AuthoringWorkResult> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SchedulerFixture fixture = new(
            options,
            (_, _) => release.Task,
            (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await fixture.Database.CreateRunningRunAsync(databaseOnly: true);

        TimeSpan wait = await fixture.Scheduler.RunCycleAsync();
        Assert.Equal(1, fixture.Handler.InvocationCount);
        Task wake = fixture.Scheduler.WaitForWakeForTestAsync(wait);

        release.SetResult(AuthoringWorkResult.Retry("worker failure"));
        await wake.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Runner.DrainAsync();

        Assert.Equal(1, fixture.QueueStore.ApplyCount);
    }

    [Fact]
    public async Task FinalizationRetry_FromRunErrorCompletesNormally()
    {
        ProcessingServiceOptions options = CreateOptions();
        options.AuthoringMaxAttempts = 1;
        using SchedulerFixture fixture = new(options);
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.Database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.Database.Store.ClaimItemAsync(run.Id, item.Id));
        await fixture.Database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "terminal failure");
        await fixture.Database.Store.MarkRunFinalizingAsync(run.Id);
        await fixture.Database.Store.MarkRunErrorAsync(run.Id, "snapshot failed");

        await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.Database.Store.GetRunAsync(run.Id))!.Status);
        Assert.Equal(1, fixture.Finalization.FinalizeCount);
    }

    [Fact]
    public async Task OldestQueuedRunIsActivatedByCreatedAtThenRowId()
    {
        using SchedulerFixture fixture = new(CreateOptions());
        await fixture.Database.ActivateAsync();
        DateTimeOffset timestamp =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringRunRecord first = await fixture.Database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            databaseOnly: true,
            now: timestamp);
        await fixture.Database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")],
            databaseOnly: true,
            now: timestamp);

        await fixture.Scheduler.RunCycleAsync();

        Assert.Equal(first.Id, (await fixture.Database.Store.GetFencedRunAsync("test"))!.Id);
    }

    private static ProcessingServiceOptions CreateOptions()
        => new()
        {
            SyncSchedule = "00:05:00",
            OrphanedInProgressThreshold = "00:10:00",
            AuthoringRetryDelay = "00:01:00",
            AuthoringMaxAttempts = 3,
            MaxConcurrentProcessingThreads = 2,
            StartProcessingOnStartup = true,
            ReconcileSnapshotsOnStartup = true,
        };

    private sealed record TestItem(AuthoringRunItemRecord RunItem);

    private sealed class SchedulerFixture : IDisposable
    {
        public SchedulerFixture(
            ProcessingServiceOptions options,
            Func<TestItem, AuthoringQueueClaim, Task<AuthoringWorkResult>>? handler = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            Database = new AuthoringTestDatabase(options);
            Lifecycle = new ProcessingLifecycleService(Options.Create(options));
            QueueStore = new TestQueueStore(Database.Store);
            Handler = new TestHandler(
                handler ?? ((_, _) => Task.FromResult(AuthoringWorkResult.Retry("worker failure"))));
            Runner = new TestRunner(
                QueueStore,
                Handler,
                Lifecycle,
                Options.Create(options),
                () => Now);
            Adapter = new TestAdapter();
            Finalization = new TestFinalizationStrategy(Database.Store);
            Scheduler = new TestScheduler(
                Database.Store,
                Adapter,
                Finalization,
                Runner,
                Lifecycle,
                Options.Create(options),
                () => Now,
                delay ?? Task.Delay);
        }

        public AuthoringTestDatabase Database { get; }
        public ProcessingLifecycleService Lifecycle { get; }
        public TestQueueStore QueueStore { get; }
        public TestHandler Handler { get; }
        public TestRunner Runner { get; }
        public TestAdapter Adapter { get; }
        public TestFinalizationStrategy Finalization { get; }
        public TestScheduler Scheduler { get; }
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        public void Dispose()
        {
            Database.Dispose();
        }
    }

    private sealed class TestAdapter : IAuthoringRunLifecycleAdapter
    {
        public string ProcessorKind => "test";

        public Task<AuthoringRunReconciliationResult> ReconcileRunAsync(
            AuthoringRunRecord run,
            CancellationToken ct)
            => Task.FromResult(AuthoringRunReconciliationResult.Current);
    }

    private sealed class TestFinalizationStrategy(AuthoringRunStore store)
        : IAuthoringRunFinalizationStrategy
    {
        public int FinalizeCount { get; private set; }
        public int SnapshotReconciliationCount { get; private set; }

        public Task ReconcileSnapshotsOnStartupAsync(CancellationToken ct)
        {
            SnapshotReconciliationCount++;
            return Task.CompletedTask;
        }

        public async Task FinalizeRunAsync(string runId, CancellationToken ct)
        {
            FinalizeCount++;
            await new AuthoringRunFinalizer(store).FinalizeAsync(runId, [], ct: ct);
        }
    }

    private sealed class TestQueueStore(AuthoringRunStore store)
        : IAuthoringQueueStore<TestItem>
    {
        public int ApplyCount { get; private set; }
        public Exception? ApplyException { get; set; }

        public async Task<IReadOnlyList<TestItem>> GetPendingAsync(
            string runId,
            int maxItems,
            CancellationToken ct)
            => (await store.GetRunItemsAsync(runId, ct))
                .Where(item => item.Status is
                    AuthoringStatusValues.Items.Pending or
                    AuthoringStatusValues.Items.Persisted)
                .Take(maxItems)
                .Select(item => new TestItem(item))
                .ToArray();

        public async Task<AuthoringQueueClaim?> TryClaimAsync(
            TestItem item,
            DateTimeOffset startedAt,
            CancellationToken ct)
        {
            if (item.RunItem.AcceptedReceiptId is not null &&
                item.RunItem.CurrentOperationId is not null)
            {
                string? lease = await store.ClaimPersistedItemAsync(
                    item.RunItem.Id,
                    item.RunItem.AcceptedReceiptId,
                    item.RunItem.CurrentOperationId,
                    startedAt,
                    ct);
                return lease is null
                    ? null
                    : new AuthoringQueueClaim(
                        lease,
                        string.Empty,
                        item.RunItem.AttemptCount);
            }

            AuthoringOperationClaim? claim = await store.ClaimItemAsync(
                item.RunItem.RunId,
                item.RunItem.Id,
                startedAt,
                ct);
            return claim is null
                ? null
                : new AuthoringQueueClaim(
                    claim.OperationId,
                    claim.OperationToken,
                    claim.AttemptNumber);
        }

        public async Task ApplyResultAsync(
            TestItem item,
            AuthoringQueueClaim claim,
            AuthoringWorkResult result,
            DateTimeOffset completedAt,
            CancellationToken ct)
        {
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            ApplyCount++;
            if (result.Disposition == AuthoringWorkDisposition.Complete)
            {
                await store.MarkClaimCompleteAsync(
                    item.RunItem.Id,
                    result.ReceiptId!,
                    claim.OperationId,
                    completedAt,
                    ct);
                return;
            }
            if (result.Disposition is
                AuthoringWorkDisposition.RetryableError or
                AuthoringWorkDisposition.PermanentError)
            {
                await store.MarkClaimErrorAsync(
                    item.RunItem.Id,
                    claim.OperationId,
                    result.Error ?? "worker failure",
                    completedAt,
                    ct);
            }
        }

        public async Task<AuthoringOrphanRecoveryResult> ResetOrphanedItemsAsync(
            string runId,
            TimeSpan olderThan,
            DateTimeOffset now,
            CancellationToken ct)
        {
            IReadOnlyList<AuthoringRunItemRecord> items =
                await store.GetRunItemsAsync(runId, ct);
            int recovered = 0;
            DateTimeOffset? nextRecoveryAt = null;
            foreach (AuthoringRunItemRecord item in items.Where(value =>
                         string.Equals(
                             value.Status,
                             AuthoringStatusValues.Items.InProgress,
                             StringComparison.Ordinal)))
            {
                DateTimeOffset claimStartedAt =
                    item.PostPersistenceLeaseAcquiredAt ??
                    item.StartedAt ??
                    throw new InvalidOperationException(
                        $"In-progress item '{item.Id}' has no claim timestamp.");
                DateTimeOffset recoveryAt = claimStartedAt + olderThan;
                if (recoveryAt > now)
                {
                    nextRecoveryAt = nextRecoveryAt is null ||
                        recoveryAt < nextRecoveryAt
                        ? recoveryAt
                        : nextRecoveryAt;
                    continue;
                }

                string claimId = item.PostPersistenceLeaseId ??
                    item.CurrentOperationId ??
                    throw new InvalidOperationException(
                        $"In-progress item '{item.Id}' has no current claim.");
                try
                {
                    await store.RecoverOrphanedClaimAsync(
                        item.Id,
                        claimId,
                        now,
                        ct);
                    recovered++;
                }
                catch (AuthoringConflictException ex)
                    when (ex.Code is
                        AuthoringConflictCode.StaleOperation or
                        AuthoringConflictCode.MutationFenceUnavailable)
                {
                }
            }
            return new AuthoringOrphanRecoveryResult(
                recovered,
                nextRecoveryAt);
        }
    }

    private sealed class TestHandler(
        Func<TestItem, AuthoringQueueClaim, Task<AuthoringWorkResult>> handler)
        : IAuthoringWorkItemHandler<TestItem>
    {
        public int InvocationCount { get; private set; }

        public Task<AuthoringWorkResult> ProcessAsync(
            TestItem item,
            AuthoringQueueClaim claim,
            CancellationToken ct)
        {
            InvocationCount++;
            return handler(item, claim);
        }
    }

    private sealed class TestRunner(
        IAuthoringQueueStore<TestItem> store,
        IAuthoringWorkItemHandler<TestItem> handler,
        ProcessingLifecycleService lifecycle,
        IOptions<ProcessingServiceOptions> options,
        Func<DateTimeOffset> utcNow)
        : AuthoringQueueRunner<TestItem>(
            store,
            handler,
            lifecycle,
            options,
            NullLogger<AuthoringQueueRunner<TestItem>>.Instance)
    {
        protected override DateTimeOffset UtcNow => utcNow();
    }

    private sealed class TestScheduler(
        AuthoringRunStore store,
        IAuthoringRunLifecycleAdapter adapter,
        IAuthoringRunFinalizationStrategy finalizationStrategy,
        AuthoringQueueRunner<TestItem> runner,
        ProcessingLifecycleService lifecycle,
        IOptions<ProcessingServiceOptions> options,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
        : AuthoringRunScheduler<TestItem>(
            store,
            adapter,
            finalizationStrategy,
            runner,
            lifecycle,
            options,
            NullLogger<AuthoringRunScheduler<TestItem>>.Instance)
    {
        protected override DateTimeOffset UtcNow => utcNow();

        protected override Task DelayAsync(TimeSpan wait, CancellationToken ct)
            => delay(wait, ct);

        public Task WaitForWakeForTestAsync(
            TimeSpan wait,
            CancellationToken ct = default)
            => WaitForWakeAsync(wait, ct);
    }
}
