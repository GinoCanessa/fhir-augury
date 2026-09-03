using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

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
}

internal sealed class AuthoringTestDatabase : IDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;

    public AuthoringTestDatabase()
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
        Store = new AuthoringRunStore(OpenConnection);
        Store.Initialize();
    }

    public string DatabasePath { get; }
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
