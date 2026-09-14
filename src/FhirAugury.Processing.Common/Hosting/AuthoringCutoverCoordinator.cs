using System.Globalization;
using System.Security.Cryptography;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Hosting;

public sealed record AuthoringCutoverRequest(
    string ProcessorKind,
    string DatabasePath,
    string BackupPath);

public sealed record AuthoringCutoverPreparation(
    IReadOnlyList<AuthoringRunItemDefinition> Items,
    IReadOnlyList<AuthoringRunInputProvenanceDefinition>? InputProvenance = null);

public interface IAuthoringCutoverParticipant
{
    Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
        SqliteConnection connection,
        CancellationToken ct);
}

public sealed class AuthoringCutoverCoordinator(
    Func<SqliteConnection> openConnection)
{
    public async Task<AuthoringProcessorModeRecord> ActivateAsync(
        AuthoringCutoverRequest request,
        IAuthoringCutoverParticipant participant,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcessorKind);
        string databasePath = Path.GetFullPath(request.DatabasePath);
        string backupPath = Path.GetFullPath(request.BackupPath);
        if (string.Equals(
            databasePath,
            backupPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The pre-cutover backup path must differ from the live database path.",
                nameof(request));
        }

        await EnsureModeExistsAsync(request.ProcessorKind, ct);
        AuthoringProcessorModeRecord current =
            await GetModeAsync(request.ProcessorKind, ct);
        if (current.Mode == AuthoringStatusValues.ProcessorModes.RunBacked)
        {
            return current;
        }

        if (current.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await EnterCuttingOverAsync(
                request.ProcessorKind,
                backupPath,
                ct);
        }
        else if (current.Mode == AuthoringStatusValues.ProcessorModes.CuttingOver)
        {
            await VerifyBackupAsync(
                backupPath,
                request.ProcessorKind,
                ct);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown authoring mode '{current.Mode}'.");
        }

        return await CompleteActivationAsync(
            request.ProcessorKind,
            participant,
            ct);
    }

    private async Task EnsureModeExistsAsync(
        string processorKind,
        CancellationToken ct)
    {
        await using SqliteConnection connection = openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            int transitioned = await ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO authoring_processor_modes(
                    ProcessorKind, Mode, Epoch, RevalidationRequired,
                    RevalidationRunId, UpdatedAt)
                VALUES(@processorKind, @mode, 0, 0, NULL, @updatedAt)
                """,
                ct,
                ("@processorKind", processorKind),
                ("@mode", AuthoringStatusValues.ProcessorModes.Legacy),
                ("@updatedAt", Format(DateTimeOffset.UtcNow)));
            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    private async Task EnterCuttingOverAsync(
        string processorKind,
        string backupPath,
        CancellationToken ct)
    {
        await using SqliteConnection connection = openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord mode =
                await ReadModeAsync(connection, processorKind, ct)
                ?? throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' was not initialized.");
            if (mode.Mode == AuthoringStatusValues.ProcessorModes.CuttingOver)
            {
                await CommitAsync(connection, ct);
                await VerifyBackupAsync(backupPath, processorKind, ct);
                return;
            }
            if (mode.Mode != AuthoringStatusValues.ProcessorModes.Legacy)
            {
                throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' is '{mode.Mode}', expected legacy.");
            }

            await CreateOrVerifyBackupAsync(
                backupPath,
                processorKind,
                ct);
            int transitioned = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_processor_modes
                SET Mode = @mode, UpdatedAt = @updatedAt
                WHERE ProcessorKind = @processorKind AND Mode = @legacy
                """,
                ct,
                ("@mode", AuthoringStatusValues.ProcessorModes.CuttingOver),
                ("@updatedAt", Format(DateTimeOffset.UtcNow)),
                ("@processorKind", processorKind),
                ("@legacy", AuthoringStatusValues.ProcessorModes.Legacy));
            if (transitioned != 1)
            {
                throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' changed before cutover admission closed.");
            }
            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    private async Task<AuthoringProcessorModeRecord> CompleteActivationAsync(
        string processorKind,
        IAuthoringCutoverParticipant participant,
        CancellationToken ct)
    {
        await using SqliteConnection connection = openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord current =
                await ReadModeAsync(connection, processorKind, ct)
                ?? throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' was not initialized.");
            if (current.Mode == AuthoringStatusValues.ProcessorModes.RunBacked)
            {
                await CommitAsync(connection, ct);
                return current;
            }
            if (current.Mode != AuthoringStatusValues.ProcessorModes.CuttingOver)
            {
                throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' is '{current.Mode}', expected cutting-over.");
            }

            AuthoringCutoverPreparation preparation =
                await participant.PrepareCutoverAsync(connection, ct);
            long epoch = checked(current.Epoch + 1);
            string? runId = preparation.Items.Count == 0
                ? null
                : Guid.NewGuid().ToString("N");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (runId is not null)
            {
                await InsertRunAsync(
                    connection,
                    processorKind,
                    epoch,
                    runId,
                    preparation.Items,
                    preparation.InputProvenance,
                    now,
                    ct);
            }

            int activationUpdates = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_processor_modes
                SET Mode = @mode,
                    Epoch = @epoch,
                    RevalidationRequired = @required,
                    RevalidationRunId = @runId,
                    UpdatedAt = @updatedAt
                WHERE ProcessorKind = @processorKind
                  AND Mode = @expectedMode
                  AND Epoch = @expectedEpoch
                """,
                ct,
                ("@mode", AuthoringStatusValues.ProcessorModes.RunBacked),
                ("@epoch", epoch),
                ("@required", runId is null ? 0 : 1),
                ("@runId", runId),
                ("@updatedAt", Format(now)),
                ("@processorKind", processorKind),
                ("@expectedMode", AuthoringStatusValues.ProcessorModes.CuttingOver),
                ("@expectedEpoch", current.Epoch));
            if (activationUpdates != 1)
            {
                throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' changed before activation completed.");
            }

            AuthoringProcessorModeRecord activated =
                await ReadModeAsync(connection, processorKind, ct)
                ?? throw new InvalidOperationException(
                    $"Failed to activate run-backed authoring for '{processorKind}'.");
            await CommitAsync(connection, ct);
            return activated;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    private static async Task InsertRunAsync(
        SqliteConnection connection,
        string processorKind,
        long epoch,
        string runId,
        IReadOnlyList<AuthoringRunItemDefinition> items,
        IReadOnlyList<AuthoringRunInputProvenanceDefinition>? inputProvenance,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                SourceRunId, DatabaseOnly, TotalItems, CreatedAt, StartedAt)
            VALUES(
                @id, @processorKind, @epoch, @status, @purpose,
                NULL, 0, @totalItems, @createdAt, @startedAt)
            """,
            ct,
            ("@id", runId),
            ("@processorKind", processorKind),
            ("@epoch", epoch),
            ("@status", AuthoringStatusValues.Runs.Running),
            ("@purpose", AuthoringRunPurposeValues.InitialRevalidation),
            ("@totalItems", items.Count),
            ("@createdAt", Format(now)),
            ("@startedAt", Format(now)));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO authoring_mutation_fences(
                ProcessorKind, RunId, LeaseId, AcquiredAt)
            VALUES(@processorKind, @runId, @leaseId, @acquiredAt)
            """,
            ct,
            ("@processorKind", processorKind),
            ("@runId", runId),
            ("@leaseId", Guid.NewGuid().ToString("N")),
            ("@acquiredAt", Format(now)));
        foreach (AuthoringRunItemDefinition item in items)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                    Status, AttemptCount, CreatedAt)
                VALUES(
                    @id, @runId, @businessKey, @itemKind,
                    @expectedSourceRevision, @status, 0, @createdAt)
                """,
                ct,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@runId", runId),
                ("@businessKey", item.BusinessKey),
                ("@itemKind", item.ItemKind),
                ("@expectedSourceRevision", item.ExpectedSourceRevision),
                ("@status", AuthoringStatusValues.Items.Pending),
                ("@createdAt", Format(now)));
        }
        await AuthoringRunStore.InsertRunInputProvenanceAsync(
            connection,
            runId,
            inputProvenance,
            now,
            ct);
    }

    private async Task CreateOrVerifyBackupAsync(
        string backupPath,
        string processorKind,
        CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string tempPath = $"{backupPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using SqliteConnection backupSource = openConnection();
            await using (SqliteConnection destination = OpenDatabase(
                tempPath,
                SqliteOpenMode.ReadWriteCreate))
            {
                backupSource.BackupDatabase(destination);
            }
            await VerifyBackupAsync(tempPath, processorKind, ct);
            if (File.Exists(backupPath))
            {
                await VerifyBackupAsync(backupPath, processorKind, ct);
                string existingHash = await ComputeSha256Async(
                    backupPath,
                    ct);
                string candidateHash = await ComputeSha256Async(
                    tempPath,
                    ct);
                if (!string.Equals(
                        existingHash,
                        candidateHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "An existing pre-cutover backup does not match the current committed legacy database.");
                }
                return;
            }
            File.Move(tempPath, backupPath, overwrite: false);
        }
        finally
        {
            foreach (string residue in new[]
                     {
                         tempPath,
                         $"{tempPath}-wal",
                         $"{tempPath}-shm",
                         $"{tempPath}-journal",
                     })
            {
                if (File.Exists(residue))
                {
                    File.Delete(residue);
                }
            }
        }

        static async Task<string> ComputeSha256Async(
            string path,
            CancellationToken ct)
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, ct));
        }
    }

    private static async Task VerifyBackupAsync(
        string backupPath,
        string processorKind,
        CancellationToken ct)
    {
        if (!File.Exists(backupPath))
        {
            throw new InvalidOperationException(
                $"The verified pre-cutover backup is missing: {backupPath}");
        }
        await using SqliteConnection connection = OpenDatabase(
            backupPath,
            SqliteOpenMode.ReadOnly);
        await using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            string? result = Convert.ToString(
                await integrity.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Pre-cutover backup integrity check failed: {result}");
            }
        }
        AuthoringProcessorModeRecord mode =
            await ReadModeAsync(connection, processorKind, ct)
            ?? throw new InvalidOperationException(
                "Pre-cutover backup does not contain processor mode metadata.");
        if (mode.Mode != AuthoringStatusValues.ProcessorModes.Legacy ||
            mode.Epoch != 0 ||
            mode.RevalidationRequired ||
            mode.RevalidationRunId is not null)
        {
            throw new InvalidOperationException(
                "Pre-cutover backup is not an operational legacy database.");
        }
    }

    private async Task<AuthoringProcessorModeRecord> GetModeAsync(
        string processorKind,
        CancellationToken ct)
    {
        await using SqliteConnection connection = openConnection();
        return await ReadModeAsync(connection, processorKind, ct)
            ?? throw new InvalidOperationException(
                $"Authoring mode for '{processorKind}' was not initialized.");
    }

    private static async Task<AuthoringProcessorModeRecord?> ReadModeAsync(
        SqliteConnection connection,
        string processorKind,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, ProcessorKind, Mode, Epoch, RevalidationRequired,
                   RevalidationRunId, UpdatedAt
            FROM authoring_processor_modes
            WHERE ProcessorKind = @processorKind
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AuthoringProcessorModeRecord
            {
                RowId = reader.GetInt32(0),
                ProcessorKind = reader.GetString(1),
                Mode = reader.GetString(2),
                Epoch = reader.GetInt64(3),
                RevalidationRequired = reader.GetBoolean(4),
                RevalidationRunId = reader.IsDBNull(5)
                    ? null
                    : reader.GetString(5),
                UpdatedAt = DateTimeOffset.Parse(
                    reader.GetString(6),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            }
            : null;
    }

    private static SqliteConnection OpenDatabase(
        string path,
        SqliteOpenMode mode)
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static Task BeginImmediateAsync(
        SqliteConnection connection,
        CancellationToken ct) =>
        ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);

    private static Task CommitAsync(
        SqliteConnection connection,
        CancellationToken ct) =>
        ExecuteRawAsync(connection, "COMMIT", ct);

    private static Task RollbackAsync(SqliteConnection connection) =>
        ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);

    private static async Task ExecuteRawAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);
}
