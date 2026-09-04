using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class SqliteReviewSnapshotWriterTests
{
    [Fact]
    public async Task WriteAsync_IncludesCommittedWalAndStripsSecrets()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await PrepareCompletedItemAsync(database);
        string outputDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, database.Store);
        AuthoringSnapshotDescriptor descriptor;
        using (SqliteConnection connection = database.OpenConnection())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE public_domain(Id TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE secret_domain(Id TEXT PRIMARY KEY, Secret TEXT NOT NULL);
                INSERT INTO public_domain(Id, Value) VALUES ('one', 'committed-in-wal');
                INSERT INTO secret_domain(Id, Secret) VALUES ('one', 'operation-token-secret');
                """;
            command.ExecuteNonQuery();
            string walPath = database.DatabasePath + "-wal";
            Assert.True(File.Exists(walPath));
            Assert.True(new FileInfo(walPath).Length > 0);

            descriptor = await writer.WriteAsync(
                new SqliteReviewSnapshotRequest(
                    "test",
                    run.Id,
                    outputDirectory,
                    SchemaVersion: 1,
                    ItemCount: 1,
                    ReceiptCount: 1,
                    new Dictionary<string, long> { ["public_domain"] = 1 },
                    AuthoringSnapshotSanitizer.CreateCore([new("public_domain")])));
        }

        string snapshotPath = Path.Combine(outputDirectory, descriptor.FileName);
        Assert.True(File.Exists(snapshotPath));
        Assert.Equal(
            descriptor.Sha256,
            await SqliteReviewSnapshotWriter.ComputeSha256Async(snapshotPath));
        using SqliteConnection snapshot = OpenReadOnly(snapshotPath);
        Assert.Equal("committed-in-wal", Scalar<string>(snapshot, "SELECT Value FROM public_domain"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('secret_domain', 'authoring_run_attempts', 'authoring_mutation_fences')"));
        Assert.Equal(
            item.Id,
            Scalar<string>(snapshot, "SELECT Id FROM authoring_run_items"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM pragma_table_info('authoring_run_items') WHERE name = 'Error'"));
        Assert.Equal(
            descriptor.Sequence,
            Scalar<long>(snapshot, "SELECT Sequence FROM authoring_snapshot_provenance"));
    }

    [Fact]
    public async Task WriteAsync_AllocatesMonotonicProcessorSequence()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database);
        string outputDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, database.Store);
        SqliteReviewSnapshotRequest request = new(
            "test",
            run.Id,
            outputDirectory,
            1,
            1,
            1,
            new Dictionary<string, long>(),
            AuthoringSnapshotSanitizer.CreateCore());

        AuthoringSnapshotDescriptor first = await writer.WriteAsync(request);
        AuthoringSnapshotDescriptor second = await writer.WriteAsync(request);

        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.NotEqual(first.Sha256, second.Sha256);
    }

    [Fact]
    public async Task ReadySnapshotLookupUsesSnapshotSelectedByCompletedRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database);
        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(database.DatabasePath)!,
            "snapshots");
        SqliteReviewSnapshotWriter writer = new(
            database.OpenConnection,
            database.Store);
        SqliteReviewSnapshotRequest request = new(
            "test",
            run.Id,
            outputDirectory,
            1,
            1,
            1,
            new Dictionary<string, long>(),
            AuthoringSnapshotSanitizer.CreateCore());
        AuthoringSnapshotDescriptor selected = await writer.WriteAsync(request);
        AuthoringSnapshotDescriptor later = await writer.WriteAsync(request);
        await database.Store.CompleteRunAsync(run.Id, selected.SnapshotId);

        AuthoringReviewSnapshotRecord snapshot =
            Assert.IsType<AuthoringReviewSnapshotRecord>(
                await database.Store.GetReadySnapshotRecordAsync(run.Id));

        Assert.Equal(selected.SnapshotId, snapshot.Id);
        Assert.NotEqual(later.SnapshotId, snapshot.Id);
    }

    [Fact]
    public async Task WriteAsync_SanitizationFailureRemovesUnsanitizedStagingDatabase()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database);
        string outputDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, database.Store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync(
                new SqliteReviewSnapshotRequest(
                    "test",
                    run.Id,
                    outputDirectory,
                    1,
                    1,
                    1,
                    new Dictionary<string, long>(),
                    new AuthoringSnapshotSanitizer(
                    [
                        new("authoring_runs", ["MissingRequiredColumn"]),
                    ]))));

        AuthoringReviewSnapshotRecord failed =
            Assert.Single(await database.Store.GetSnapshotRecordsAsync());
        Assert.Equal(AuthoringStatusValues.Snapshots.Error, failed.Status);
        Assert.False(File.Exists(failed.TempPath));
        Assert.False(File.Exists(failed.TempPath + "-wal"));
        Assert.False(File.Exists(failed.TempPath + "-shm"));
        Assert.False(File.Exists(failed.TempPath + "-journal"));
    }

    [Fact]
    public async Task ReconcileAsync_RecoversPromotedFileAndMarksTempOnlyAttemptError()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database);
        string directory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        Directory.CreateDirectory(directory);

        string promotedPath = Path.Combine(directory, "promoted.db");
        string promotedTemp = promotedPath + ".tmp";
        AuthoringReviewSnapshotRecord promoted = await database.Store.BeginSnapshotAsync(
            "test",
            run.Id,
            1,
            promotedTemp,
            promotedPath,
            1,
            1,
            new Dictionary<string, long>());
        CreateValidSqliteFile(promotedPath, promoted);
        File.WriteAllText(promotedTemp + "-journal", "orphan rollback journal");
        File.WriteAllText(promotedTemp + "-wal", "orphan wal");
        File.WriteAllText(promotedTemp + "-shm", "orphan shm");

        string abandonedPath = Path.Combine(directory, "abandoned.db");
        string abandonedTemp = abandonedPath + ".tmp";
        AuthoringReviewSnapshotRecord abandoned = await database.Store.BeginSnapshotAsync(
            "test",
            run.Id,
            1,
            abandonedTemp,
            abandonedPath,
            1,
            1,
            new Dictionary<string, long>());
        CreateValidSqliteFile(abandonedTemp);
        File.WriteAllText(abandonedTemp + "-journal", "orphan rollback journal");

        string untrustedPath = Path.Combine(directory, "untrusted.db");
        string untrustedTemp = untrustedPath + ".tmp";
        AuthoringReviewSnapshotRecord untrusted = await database.Store.BeginSnapshotAsync(
            "test",
            run.Id,
            1,
            untrustedTemp,
            untrustedPath,
            1,
            1,
            new Dictionary<string, long>());
        CreateValidSqliteFile(untrustedPath);

        string malformedPath = Path.Combine(directory, "malformed.db");
        AuthoringReviewSnapshotRecord malformed = await database.Store.BeginSnapshotAsync(
            "test",
            run.Id,
            1,
            malformedPath + ".tmp",
            malformedPath,
            1,
            1,
            new Dictionary<string, long>());
        CreateValidSqliteFile(malformedPath, malformed, tableCountsJson: "{");

        string laterValidPath = Path.Combine(directory, "later-valid.db");
        AuthoringReviewSnapshotRecord laterValid = await database.Store.BeginSnapshotAsync(
            "test",
            run.Id,
            1,
            laterValidPath + ".tmp",
            laterValidPath,
            1,
            1,
            new Dictionary<string, long>());
        CreateValidSqliteFile(laterValidPath, laterValid);

        SqliteReviewSnapshotReconciler reconciler = new(database.Store);
        IReadOnlyList<SnapshotReconciliationResult> results = await reconciler.ReconcileAsync();

        Assert.Contains(
            results,
            result => result.SnapshotId == promoted.Id &&
                result.Status == AuthoringStatusValues.Snapshots.Ready);
        Assert.Contains(
            results,
            result => result.SnapshotId == abandoned.Id &&
                result.Status == AuthoringStatusValues.Snapshots.Error);
        Assert.Contains(
            results,
            result => result.SnapshotId == untrusted.Id &&
                result.Status == AuthoringStatusValues.Snapshots.Error);
        Assert.Contains(
            results,
            result => result.SnapshotId == malformed.Id &&
                result.Status == AuthoringStatusValues.Snapshots.Error);
        Assert.Contains(
            results,
            result => result.SnapshotId == laterValid.Id &&
                result.Status == AuthoringStatusValues.Snapshots.Ready);
        Assert.False(File.Exists(promotedTemp + "-journal"));
        Assert.False(File.Exists(promotedTemp + "-wal"));
        Assert.False(File.Exists(promotedTemp + "-shm"));
        Assert.False(File.Exists(abandonedTemp));
        Assert.False(File.Exists(abandonedTemp + "-journal"));
        Assert.NotNull(await database.Store.GetSnapshotDescriptorAsync(promoted.Id));
        Assert.NotNull(await database.Store.GetSnapshotDescriptorAsync(laterValid.Id));
    }

    [Fact]
    public async Task ReconcileAsync_DetectsMissingAndChangedReadyFiles()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await PrepareCompletedItemAsync(database);
        string outputDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "snapshots");
        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, database.Store);
        SqliteReviewSnapshotRequest request = new(
            "test",
            run.Id,
            outputDirectory,
            1,
            1,
            1,
            new Dictionary<string, long>(),
            AuthoringSnapshotSanitizer.CreateCore());
        AuthoringSnapshotDescriptor missing = await writer.WriteAsync(request);
        AuthoringSnapshotDescriptor changed = await writer.WriteAsync(request);
        File.Delete(Path.Combine(outputDirectory, missing.FileName));
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(outputDirectory, changed.FileName),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE changed_after_ready(Value TEXT);";
            command.ExecuteNonQuery();
        }

        IReadOnlyList<SnapshotReconciliationResult> results =
            await new SqliteReviewSnapshotReconciler(database.Store).ReconcileAsync();

        Assert.Contains(
            results,
            result => result.SnapshotId == missing.SnapshotId &&
                result.Status == AuthoringStatusValues.Snapshots.Error);
        Assert.Contains(
            results,
            result => result.SnapshotId == changed.SnapshotId &&
                result.Status == AuthoringStatusValues.Snapshots.Error);
    }

    private static async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> PrepareCompletedItemAsync(
        AuthoringTestDatabase database)
    {
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
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
        return (run, item);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
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

    private static void CreateValidSqliteFile(
        string path,
        AuthoringReviewSnapshotRecord? record = null,
        string? tableCountsJson = null)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE data(Value TEXT); INSERT INTO data(Value) VALUES ('ok');";
        command.ExecuteNonQuery();
        if (record is null)
        {
            return;
        }

        command.CommandText =
            """
            CREATE TABLE authoring_snapshot_provenance(
                SnapshotId TEXT NOT NULL,
                ProcessorKind TEXT NOT NULL,
                RunId TEXT NOT NULL,
                AuthoringEpoch INTEGER NOT NULL,
                Sequence INTEGER NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                ItemCount INTEGER NOT NULL,
                ReceiptCount INTEGER NOT NULL,
                TableCountsJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            INSERT INTO authoring_snapshot_provenance(
                SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion,
                ItemCount, ReceiptCount, TableCountsJson, CreatedAt)
            VALUES(
                @snapshotId, @processorKind, @runId, @authoringEpoch, @sequence, @schemaVersion,
                @itemCount, @receiptCount, @tableCountsJson, @createdAt);
            """;
        command.Parameters.AddWithValue("@snapshotId", record.Id);
        command.Parameters.AddWithValue("@processorKind", record.ProcessorKind);
        command.Parameters.AddWithValue("@runId", record.RunId);
        command.Parameters.AddWithValue("@authoringEpoch", record.AuthoringEpoch);
        command.Parameters.AddWithValue("@sequence", record.Sequence);
        command.Parameters.AddWithValue("@schemaVersion", record.SchemaVersion);
        command.Parameters.AddWithValue("@itemCount", record.ItemCount);
        command.Parameters.AddWithValue("@receiptCount", record.ReceiptCount);
        command.Parameters.AddWithValue("@tableCountsJson", tableCountsJson ?? record.TableCountsJson);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }
}
