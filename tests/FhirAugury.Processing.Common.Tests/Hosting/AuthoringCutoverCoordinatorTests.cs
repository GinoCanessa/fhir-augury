using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Tests.Authoring;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Tests.Hosting;

public sealed class AuthoringCutoverCoordinatorTests
{
    [Fact]
    public async Task ActivateCreatesVerifiedLegacyBackupAndInitialRun()
    {
        using AuthoringTestDatabase database = new();
        string backupPath = $"{database.DatabasePath}.pre-cutover";
        AuthoringCutoverCoordinator coordinator = new(database.OpenConnection);

        AuthoringProcessorModeRecord active = await coordinator.ActivateAsync(
            new AuthoringCutoverRequest(
                "test",
                database.DatabasePath,
                backupPath),
            new StaticParticipant(
                [new("FHIR-1", "ticket", "revision-1")]));

        Assert.Equal(AuthoringStatusValues.ProcessorModes.RunBacked, active.Mode);
        Assert.Equal(1, active.Epoch);
        Assert.True(active.RevalidationRequired);
        Assert.NotNull(active.RevalidationRunId);
        AuthoringRunRecord run =
            (await database.Store.GetRunAsync(active.RevalidationRunId!))!;
        Assert.False(run.DatabaseOnly);
        Assert.Equal(
            AuthoringRunPurposeValues.InitialRevalidation,
            run.Purpose);
        Assert.Null(run.SourceRunId);
        AuthoringRunItemRecord item = Assert.Single(
            await database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal("revision-1", item.ExpectedSourceRevision);
        using SqliteConnection backup = OpenReadOnly(backupPath);
        Assert.Equal(
            AuthoringStatusValues.ProcessorModes.Legacy,
            Scalar<string>(
                backup,
                "SELECT Mode FROM authoring_processor_modes WHERE ProcessorKind = 'test'"));
        Assert.Equal(
            0L,
            Scalar<long>(
                backup,
                "SELECT Epoch FROM authoring_processor_modes WHERE ProcessorKind = 'test'"));
    }

    [Fact]
    public async Task ActivationPersistsParticipantProvenanceWithInitialRun()
    {
        using AuthoringTestDatabase database = new();
        DateTimeOffset refreshedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "test",
                        database.DatabasePath,
                        $"{database.DatabasePath}.pre-cutover"),
                    new StaticParticipant(
                        [new("FHIR-1", "ticket", "revision-1")],
                        [new("jira", refreshedAt, 42)]));

        AuthoringRunRecord run =
            (await database.Store.GetRunAsync(active.RevalidationRunId!))!;
        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await database.Store.GetRunInputProvenanceAsync(run.Id));
        Assert.Equal(refreshedAt, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(42, provenance.ContentRevision);
        Assert.Equal(run.CreatedAt, provenance.CapturedAt);
    }

    [Fact]
    public async Task InvalidParticipantProvenanceRollsBackInitialRunAndActivation()
    {
        using AuthoringTestDatabase database = new();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "test",
                        database.DatabasePath,
                        $"{database.DatabasePath}.pre-cutover"),
                    new StaticParticipant(
                        [new("FHIR-1", "ticket", "revision-1")],
                        [
                            new("jira", null, null),
                            new("JIRA", null, null),
                        ])));

        AuthoringProcessorModeRecord mode =
            await database.Store.GetProcessorModeAsync("test");
        Assert.Equal(
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            mode.Mode);
        Assert.Null(mode.RevalidationRunId);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_input_provenance"));
    }

    [Fact]
    public async Task ActivationIsIdempotentAndOrdinaryRunsWaitForRevalidation()
    {
        using AuthoringTestDatabase database = new();
        string backupPath = $"{database.DatabasePath}.pre-cutover";
        AuthoringCutoverCoordinator coordinator = new(database.OpenConnection);
        StaticParticipant participant =
            new([new("FHIR-1", "ticket", "revision-1")]);

        AuthoringProcessorModeRecord first = await coordinator.ActivateAsync(
            new AuthoringCutoverRequest(
                "test",
                database.DatabasePath,
                backupPath),
            participant);
        AuthoringProcessorModeRecord replay = await coordinator.ActivateAsync(
            new AuthoringCutoverRequest(
                "test",
                database.DatabasePath,
                backupPath),
            participant);

        Assert.Equal(first.Epoch, replay.Epoch);
        Assert.Equal(first.RevalidationRunId, replay.RevalidationRunId);
        Assert.Equal(1, participant.Calls);
        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(() =>
                database.Store.CreateRunAsync(
                    "test",
                    [new("FHIR-2", "ticket", "revision-2")]));
        Assert.Equal(AuthoringConflictCode.RevalidationRequired, conflict.Code);
    }

    [Fact]
    public async Task FailedPreparationLeavesCuttingOverAndRetryCompletes()
    {
        using AuthoringTestDatabase database = new();
        string backupPath = $"{database.DatabasePath}.pre-cutover";
        AuthoringCutoverCoordinator coordinator = new(database.OpenConnection);
        AuthoringCutoverRequest request = new(
            "test",
            database.DatabasePath,
            backupPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ActivateAsync(
                request,
                new ThrowingParticipant()));

        AuthoringProcessorModeRecord cutting =
            await database.Store.GetProcessorModeAsync("test");
        Assert.Equal(
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            cutting.Mode);
        Assert.True(File.Exists(backupPath));

        AuthoringProcessorModeRecord active = await coordinator.ActivateAsync(
            request,
            new StaticParticipant([]));
        Assert.Equal(AuthoringStatusValues.ProcessorModes.RunBacked, active.Mode);
        Assert.False(active.RevalidationRequired);
    }

    [Fact]
    public async Task InitialRevalidationAdmissionUsesSnapshotWorkflowGuard()
    {
        using AuthoringTestDatabase database = new();
        RecordingSnapshotWorkflowGuard guard = new(reject: true);
        AuthoringCutoverCoordinator coordinator =
            new(database.OpenConnection, guard);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => coordinator.ActivateAsync(
                    new AuthoringCutoverRequest(
                        "test",
                        database.DatabasePath,
                        $"{database.DatabasePath}.pre-cutover"),
                    new StaticParticipant(
                        [new("FHIR-1", "ticket", "revision-1")])));

        Assert.Equal(
            AuthoringConflictCode.CanonicalUnpublishedRestriction,
            conflict.Code);
        AuthoringSnapshotWorkflowIntent intent =
            Assert.Single(guard.Intents);
        Assert.Equal("test", intent.ProcessorKind);
        Assert.False(intent.DatabaseOnly);
        Assert.Equal(
            AuthoringRunPurposeValues.InitialRevalidation,
            intent.Purpose);
        Assert.False(string.IsNullOrWhiteSpace(intent.RunId));
        AuthoringProcessorModeRecord mode =
            await database.Store.GetProcessorModeAsync("test");
        Assert.Equal(
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            mode.Mode);
        Assert.Equal(
            0,
            database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Null(await database.Store.GetFencedRunAsync("test"));
    }

    [Fact]
    public async Task PreparationRunsWhileIndependentWritesAreBlocked()
    {
        using AuthoringTestDatabase database = new();
        using (SqliteConnection setup = database.OpenConnection())
        using (SqliteCommand command = setup.CreateCommand())
        {
            command.CommandText =
                "CREATE TABLE cutover_probe(Value TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        LockProbeParticipant participant = new(database.DatabasePath);

        await new AuthoringCutoverCoordinator(database.OpenConnection)
            .ActivateAsync(
                new AuthoringCutoverRequest(
                    "test",
                    database.DatabasePath,
                    $"{database.DatabasePath}.pre-cutover"),
                participant);

        Assert.True(participant.WriteWasBlocked);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM cutover_probe"));
    }

    [Fact]
    public async Task SuccessfulInitialRunClearsRevalidationGate()
    {
        using AuthoringTestDatabase database = new();
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "test",
                        database.DatabasePath,
                        $"{database.DatabasePath}.pre-cutover"),
                    new StaticParticipant(
                        [new("FHIR-1", "ticket", "revision-1")]));
        string runId = active.RevalidationRunId!;
        Assert.True(await database.Store.TryAcquireMutationFenceAsync(
            "test",
            runId));
        AuthoringRunItemRecord item = Assert.Single(
            await database.Store.GetRunItemsAsync(runId));
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(runId, item.Id));
        AuthoringReceiptAcceptance receipt =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    runId,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(runId);
        AuthoringSnapshotDescriptor descriptor =
            await new SqliteReviewSnapshotWriter(
                database.OpenConnection,
                database.Store).WriteAsync(
                new SqliteReviewSnapshotRequest(
                    "test",
                    runId,
                    Path.Combine(
                        Path.GetDirectoryName(database.DatabasePath)!,
                        "snapshots"),
                    1,
                    1,
                    1,
                    new Dictionary<string, long>(),
                    AuthoringSnapshotSanitizer.CreateCore()));
        await database.Store.CompleteRunAsync(
            runId,
            descriptor.SnapshotId);

        AuthoringProcessorModeRecord completed =
            await database.Store.GetProcessorModeAsync("test");
        Assert.False(completed.RevalidationRequired);
        Assert.Null(completed.RevalidationRunId);
        Assert.NotNull(await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")]));
    }

    [Fact]
    public async Task StaleInitialRunIsAtomicallyReplacedAndRefenced()
    {
        using AuthoringTestDatabase database = new();
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "test",
                        database.DatabasePath,
                        $"{database.DatabasePath}.pre-cutover"),
                    new StaticParticipant(
                    [
                        new("FHIR-1", "ticket", "revision-1"),
                        new("FHIR-2", "ticket", "revision-2"),
                    ]));
        string firstRunId = active.RevalidationRunId!;
        AuthoringRunItemRecord acceptedItem =
            (await database.Store.GetRunItemsAsync(firstRunId))
            .Single(item => item.BusinessKey == "FHIR-1");
        AuthoringOperationClaim acceptedClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    firstRunId,
                    acceptedItem.Id));
        AuthoringReceiptAcceptance accepted =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    firstRunId,
                    acceptedItem.Id,
                    acceptedClaim.OperationId,
                    acceptedItem.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("first")),
                acceptedClaim.OperationToken);
        await database.Store.MarkItemCompleteAsync(
            acceptedItem.Id,
            accepted.Receipt.ReceiptId);

        AuthoringRunRecord replacement =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                firstRunId,
                [
                    new("FHIR-2", "ticket", "revision-3"),
                ]);

        Assert.Equal(
            AuthoringRunPurposeValues.InitialRevalidation,
            replacement.Purpose);
        Assert.Null(replacement.SourceRunId);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await database.Store.GetRunAsync(firstRunId))!.Status);
        AuthoringProcessorModeRecord refreshed =
            await database.Store.GetProcessorModeAsync("test");
        Assert.Equal(replacement.Id, refreshed.RevalidationRunId);
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            replacement.Status);
        Assert.Equal(
            replacement.Id,
            database.Scalar<string>(
                "SELECT RunId FROM authoring_mutation_fences WHERE ProcessorKind = 'test'"));
        Assert.Single(
            await database.Store.GetRunItemsAsync(replacement.Id));
        IReadOnlyList<AuthoringRunItemRecord> predecessorItems =
            await database.Store.GetRunItemsAsync(firstRunId);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            predecessorItems.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            predecessorItems.Single(item => item.BusinessKey == "FHIR-2").Status);
        Assert.Equal(
            ["FHIR-1", "FHIR-2"],
            (await database.Store.GetRevalidationCorpusItemsAsync(replacement.Id))
            .Select(item => item.BusinessKey)
            .Order(StringComparer.Ordinal)
            .ToArray());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private sealed class StaticParticipant(
        IReadOnlyList<AuthoringRunItemDefinition> items,
        IReadOnlyList<AuthoringRunInputProvenanceDefinition>? inputProvenance = null)
        : IAuthoringCutoverParticipant
    {
        public int Calls { get; private set; }

        public Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
            SqliteConnection connection,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(
                new AuthoringCutoverPreparation(
                    items,
                    inputProvenance));
        }
    }

    private sealed class ThrowingParticipant : IAuthoringCutoverParticipant
    {
        public Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
            SqliteConnection connection,
            CancellationToken ct) =>
            throw new InvalidOperationException("classification failed");
    }

    private sealed class RecordingSnapshotWorkflowGuard(bool reject)
        : IAuthoringSnapshotWorkflowGuard
    {
        public List<AuthoringSnapshotWorkflowIntent> Intents { get; } = [];

        public Task EnsureSnapshotWorkflowAllowedAsync(
            AuthoringSnapshotWorkflowIntent intent,
            CancellationToken ct = default)
        {
            Intents.Add(intent);
            return reject
                ? Task.FromException(
                    AuthoringConflictException
                        .ForCanonicalUnpublishedRestriction(
                            relatedRunIds: ["abandoned-run"]))
                : Task.CompletedTask;
        }
    }

    private sealed class LockProbeParticipant(string databasePath)
        : IAuthoringCutoverParticipant
    {
        public bool WriteWasBlocked { get; private set; }

        public async Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
            SqliteConnection connection,
            CancellationToken ct)
        {
            await using SqliteConnection competing = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                    DefaultTimeout = 1,
                }.ToString());
            await competing.OpenAsync(ct);
            await using SqliteCommand write = competing.CreateCommand();
            write.CommandText =
                "INSERT INTO cutover_probe(Value) VALUES('unexpected')";
            try
            {
                await write.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
            {
                WriteWasBlocked = true;
            }
            return new AuthoringCutoverPreparation([]);
        }
    }
}
