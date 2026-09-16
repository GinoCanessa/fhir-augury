using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Database;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processing.Common.Database;

public sealed record AuthoringMaintenanceRunItem(
    string BusinessKey,
    string ItemKind,
    string ExpectedSourceRevision,
    string ReceiptId);

public sealed record AuthoringMaintenanceRunSelection(
    IReadOnlyList<AuthoringMaintenanceRunItem> Items,
    string? RequestJson);

public sealed record AuthoringMixedRunItem(
    string BusinessKey,
    string ItemKind,
    string ExpectedSourceRevision,
    string Status,
    string? AcceptedReceiptId = null);

public sealed record AuthoringErrorReconciliationResult(
    int RetriedItems,
    int ResumedReceiptItems,
    int SupersededItems,
    DateTimeOffset? NextRetryAt);

public sealed record AuthoringOperatorRunSummary(
    AuthoringRunRecord Run,
    int CompletedItems,
    int RetryableErrorItems,
    int SupersededItems,
    DateTimeOffset? NextAutomaticRetryAt);

public sealed record AuthoringOperatorRunList(
    IReadOnlyList<AuthoringOperatorRunSummary> Runs,
    bool Truncated);

public sealed class AuthoringRunStore
{
    public const int DefaultOperatorRunLimit = 20;
    public const int MaximumOperatorRunLimit = 100;

    private readonly Func<SqliteConnection> _openConnection;
    private readonly ILogger<AuthoringRunStore> _logger;
    private readonly AuthoringRetryPolicy _retryPolicy;

    public AuthoringRunStore(
        Func<SqliteConnection> openConnection,
        ILogger<AuthoringRunStore>? logger = null,
        AuthoringRetryPolicy? retryPolicy = null)
    {
        _openConnection = openConnection ?? throw new ArgumentNullException(nameof(openConnection));
        _logger = logger ?? NullLogger<AuthoringRunStore>.Instance;
        _retryPolicy = retryPolicy ?? AuthoringRetryPolicy.CreateDefault();
    }

    public AuthoringRunStore(
        ProcessingDatabase database,
        ILogger<AuthoringRunStore>? logger = null,
        AuthoringRetryPolicy? retryPolicy = null)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database))).OpenConnection,
            logger,
            retryPolicy)
    {
    }

    public void Initialize()
    {
        using SqliteConnection connection = _openConnection();
        EnsureSchema(connection);
    }

    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_run_items",
            "PostPersistenceLeaseId",
            "TEXT NULL");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_run_items",
            "PostPersistenceLeaseAcquiredAt",
            "TEXT NULL");

        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_runs",
            "Purpose",
            "TEXT NOT NULL DEFAULT 'authoring'");
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_runs",
            "SourceRunId",
            "TEXT NULL");
        using (SqliteCommand createRuns = connection.CreateCommand())
        {
            // The generator cannot express SQL defaults. Create the fresh-table
            // shape here so older writers may continue omitting Purpose.
            createRuns.CommandText =
                """
                CREATE TABLE IF NOT EXISTS authoring_runs (
                    RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                    Id TEXT UNIQUE NOT NULL,
                    ProcessorKind TEXT NOT NULL,
                    AuthoringEpoch INTEGER NOT NULL,
                    Status TEXT NOT NULL,
                    Purpose TEXT NOT NULL DEFAULT 'authoring',
                    SourceRunId TEXT,
                    DatabaseOnly INTEGER NOT NULL,
                    TotalItems INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    StartedAt TEXT,
                    CompletedAt TEXT,
                    Error TEXT,
                    SnapshotId TEXT,
                    RequestJson TEXT
                )
                """;
            createRuns.ExecuteNonQuery();
        }
        AuthoringRunRecord.CreateTable(connection);
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_runs",
            "RequestJson",
            "TEXT NULL");
        AuthoringRunInputProvenanceRecord.CreateTable(connection);
        AuthoringRunItemRecord.CreateTable(connection);
        AuthoringRunAttemptRecord.CreateTable(connection);
        AuthoringRunStageRecord.CreateTable(connection);
        AuthoringResultReceiptRecord.CreateTable(connection);
        AuthoringMutationFenceRecord.CreateTable(connection);
        AuthoringProcessorModeRecord.CreateTable(connection);
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "authoring_review_snapshots",
            "PublicationProofJson",
            "TEXT NULL");
        AuthoringReviewSnapshotRecord.CreateTable(connection);
        using (SqliteCommand lineage = connection.CreateCommand())
        {
            lineage.CommandText =
                """
                CREATE TABLE IF NOT EXISTS authoring_revalidation_lineage(
                    RunId TEXT NOT NULL PRIMARY KEY,
                    PreviousRunId TEXT NOT NULL UNIQUE
                );
                """;
            lineage.ExecuteNonQuery();
        }

        using (SqliteCommand backfillMaintenancePurpose = connection.CreateCommand())
        {
            backfillMaintenancePurpose.CommandText =
                """
                UPDATE authoring_runs AS run
                SET Purpose = @groupingMaintenance
                WHERE run.Purpose = @authoring
                  AND run.DatabaseOnly = 1
                  AND EXISTS (
                      SELECT 1
                      FROM authoring_run_items item
                      WHERE item.RunId = run.Id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM authoring_run_items item
                      WHERE item.RunId = run.Id
                        AND (
                            item.Status <> @complete
                            OR item.ItemKind NOT LIKE 'maintenance:%'))
                """;
            backfillMaintenancePurpose.Parameters.AddWithValue(
                "@groupingMaintenance",
                AuthoringRunPurposeValues.GroupingMaintenance);
            backfillMaintenancePurpose.Parameters.AddWithValue(
                "@authoring",
                AuthoringRunPurposeValues.Authoring);
            backfillMaintenancePurpose.Parameters.AddWithValue(
                "@complete",
                AuthoringStatusValues.Items.Complete);
            backfillMaintenancePurpose.ExecuteNonQuery();
        }

        using (SqliteCommand backfillRevalidationPurpose = connection.CreateCommand())
        {
            backfillRevalidationPurpose.CommandText =
                """
                UPDATE authoring_runs AS run
                SET Purpose = @initialRevalidation
                WHERE run.Purpose = @authoring
                  AND (
                      EXISTS (
                          SELECT 1
                          FROM authoring_processor_modes mode
                          WHERE mode.RevalidationRunId = run.Id)
                      OR EXISTS (
                          SELECT 1
                          FROM authoring_revalidation_lineage lineage
                          WHERE lineage.RunId = run.Id
                             OR lineage.PreviousRunId = run.Id))
                """;
            backfillRevalidationPurpose.Parameters.AddWithValue(
                "@initialRevalidation",
                AuthoringRunPurposeValues.InitialRevalidation);
            backfillRevalidationPurpose.Parameters.AddWithValue(
                "@authoring",
                AuthoringRunPurposeValues.Authoring);
            backfillRevalidationPurpose.ExecuteNonQuery();
        }

        using (SqliteCommand dropIdentityIndexes = connection.CreateCommand())
        {
            dropIdentityIndexes.CommandText =
                """
                DROP INDEX IF EXISTS idx_authoring_run_items_identity;
                DROP INDEX IF EXISTS idx_authoring_run_items_revision;
                """;
            dropIdentityIndexes.ExecuteNonQuery();
        }

        string[] indexes =
        [
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_run_items_identity ON authoring_run_items(RunId, ItemKind COLLATE NOCASE, BusinessKey COLLATE NOCASE);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_run_items_revision ON authoring_run_items(ItemKind COLLATE NOCASE, BusinessKey COLLATE NOCASE, ExpectedSourceRevision) WHERE Status <> 'superseded';",
            "CREATE INDEX IF NOT EXISTS idx_authoring_run_items_lifecycle ON authoring_run_items(RunId, Status, CompletedAt, RowId);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_run_input_provenance_identity ON authoring_run_input_provenance(RunId, Source COLLATE NOCASE);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_run_attempts_number ON authoring_run_attempts(RunItemId, AttemptNumber);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_run_stages_identity ON authoring_run_stages(RunId, StageName, PartitionKey);",
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_authoring_review_snapshots_sequence ON authoring_review_snapshots(ProcessorKind, Sequence);",
        ];

        foreach (string sql in indexes)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public async Task<AuthoringProcessorModeRecord> EnsureProcessorModeAsync(
        string processorKind,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            await ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO authoring_processor_modes
                    (ProcessorKind, Mode, Epoch, RevalidationRequired, RevalidationRunId, UpdatedAt)
                VALUES
                    (@processorKind, @mode, 0, 0, NULL, @updatedAt)
                """,
                ct,
                ("@processorKind", processorKind),
                ("@mode", AuthoringStatusValues.ProcessorModes.Legacy),
                ("@updatedAt", Format(timestamp)));

            AuthoringProcessorModeRecord record = await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new InvalidOperationException($"Failed to initialize authoring mode for '{processorKind}'.");
            await CommitAsync(connection, ct);
            return record;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringProcessorModeRecord> GetProcessorModeAsync(
        string processorKind,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        return await ReadProcessorModeAsync(connection, processorKind, ct)
            ?? await EnsureProcessorModeAsync(processorKind, ct: ct);
    }

    public async Task<AuthoringProcessorModeRecord> TransitionProcessorModeAsync(
        string processorKind,
        string expectedMode,
        string nextMode,
        bool revalidationRequired = false,
        string? revalidationRunId = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        AuthoringStatusValues.EnsureModeTransition(expectedMode, nextMode);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord current = await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? new AuthoringProcessorModeRecord
                {
                    ProcessorKind = processorKind,
                    Mode = AuthoringStatusValues.ProcessorModes.Legacy,
                    Epoch = 0,
                    RevalidationRequired = false,
                    UpdatedAt = timestamp,
                };

            if (current.RowId != 0 &&
                string.Equals(current.Mode, nextMode, StringComparison.Ordinal))
            {
                bool sameMetadata = current.RevalidationRequired == revalidationRequired &&
                    string.Equals(current.RevalidationRunId, revalidationRunId, StringComparison.Ordinal);
                if (!sameMetadata)
                {
                    throw new InvalidOperationException(
                        $"Authoring mode '{nextMode}' is already active with different revalidation metadata.");
                }

                await CommitAsync(connection, ct);
                return current;
            }

            if (!string.Equals(current.Mode, expectedMode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Authoring mode for '{processorKind}' is '{current.Mode}', expected '{expectedMode}'.");
            }

            AuthoringStatusValues.EnsureModeTransition(current.Mode, nextMode);
            long nextEpoch = string.Equals(current.Mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal) &&
                string.Equals(nextMode, AuthoringStatusValues.ProcessorModes.RunBacked, StringComparison.Ordinal)
                ? checked(current.Epoch + 1)
                : current.Epoch;

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_processor_modes
                    (ProcessorKind, Mode, Epoch, RevalidationRequired, RevalidationRunId, UpdatedAt)
                VALUES
                    (@processorKind, @mode, @epoch, @revalidationRequired, @revalidationRunId, @updatedAt)
                ON CONFLICT(ProcessorKind) DO UPDATE SET
                    Mode = excluded.Mode,
                    Epoch = excluded.Epoch,
                    RevalidationRequired = excluded.RevalidationRequired,
                    RevalidationRunId = excluded.RevalidationRunId,
                    UpdatedAt = excluded.UpdatedAt
                """,
                ct,
                ("@processorKind", processorKind),
                ("@mode", nextMode),
                ("@epoch", nextEpoch),
                ("@revalidationRequired", revalidationRequired),
                ("@revalidationRunId", revalidationRunId),
                ("@updatedAt", Format(timestamp)));

            AuthoringProcessorModeRecord updated = await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new InvalidOperationException($"Failed to update authoring mode for '{processorKind}'.");
            await CommitAsync(connection, ct);
            return updated;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRunRecord> CreateRunAsync(
        string processorKind,
        IReadOnlyCollection<AuthoringRunItemDefinition> items,
        bool databaseOnly = false,
        string? runId = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default,
        string? requestJson = null,
        int maxActiveRuns = int.MaxValue,
        IReadOnlyCollection<AuthoringRunInputProvenanceDefinition>? inputProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("An authoring run must contain at least one item.", nameof(items));
        }
        if (maxActiveRuns < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActiveRuns),
                "Active authoring run capacity must be greater than or equal to 1.");
        }

        foreach (AuthoringRunItemDefinition item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.BusinessKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemKind);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ExpectedSourceRevision);
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string id = runId ?? Guid.NewGuid().ToString("N");

        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord mode = await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new AuthoringConflictException(
                    AuthoringConflictCode.AuthoringNotActivated,
                    $"Authoring is not activated for processor '{processorKind}'.");
            EnsureRunBacked(mode);
            if (mode.RevalidationRequired)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RevalidationRequired,
                    $"Initial revalidation run '{mode.RevalidationRunId}' must complete before ordinary authoring runs can start.",
                    mode.RevalidationRunId is null
                        ? null
                        : [mode.RevalidationRunId]);
            }

            await EnsureActiveRunCapacityAvailableAsync(
                connection,
                processorKind,
                maxActiveRuns,
                ct);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_runs
                    (Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                     SourceRunId, DatabaseOnly, TotalItems, CreatedAt,
                     RequestJson)
                VALUES
                    (@id, @processorKind, @epoch, @status, @purpose,
                     NULL, @databaseOnly, @totalItems, @createdAt,
                     @requestJson)
                """,
                ct,
                ("@id", id),
                ("@processorKind", processorKind),
                ("@epoch", mode.Epoch),
                ("@status", AuthoringStatusValues.Runs.Queued),
                ("@purpose", AuthoringRunPurposeValues.Authoring),
                ("@databaseOnly", databaseOnly),
                ("@totalItems", items.Count),
                ("@createdAt", Format(timestamp)),
                ("@requestJson", requestJson));

            foreach (AuthoringRunItemDefinition item in items)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO authoring_run_items
                        (Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status, AttemptCount, CreatedAt)
                    VALUES
                        (@id, @runId, @businessKey, @itemKind, @expectedSourceRevision, @status, 0, @createdAt)
                    """,
                    ct,
                    ("@id", Guid.NewGuid().ToString("N")),
                    ("@runId", id),
                    ("@businessKey", item.BusinessKey),
                    ("@itemKind", item.ItemKind),
                    ("@expectedSourceRevision", item.ExpectedSourceRevision),
                    ("@status", AuthoringStatusValues.Items.Pending),
                    ("@createdAt", Format(timestamp)));
            }
            await InsertRunInputProvenanceAsync(
                connection,
                id,
                inputProvenance,
                timestamp,
                ct);

            AuthoringRunRecord run = await ReadRunAsync(connection, id, ct)
                ?? throw new InvalidOperationException($"Failed to create authoring run '{id}'.");
            await CommitAsync(connection, ct);
            return run;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    /// <summary>
    /// Creates a run inside a transaction owned by the caller. This is used
    /// when a purpose-specific durable recipe must become visible atomically
    /// with the authoring run that consumes it.
    /// </summary>
    public async Task<AuthoringRunRecord> CreateMixedRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string processorKind,
        string purpose,
        string sourceRunId,
        IReadOnlyCollection<AuthoringMixedRunItem> items,
        string? runId = null,
        DateTimeOffset? now = null,
        string? requestJson = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException(
                "A mixed authoring run must contain at least one item.",
                nameof(items));
        }

        foreach (AuthoringMixedRunItem item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.BusinessKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemKind);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ExpectedSourceRevision);
            if (item.Status is not (
                    AuthoringStatusValues.Items.Pending or
                    AuthoringStatusValues.Items.Complete))
            {
                throw new ArgumentException(
                    $"Mixed run item '{item.BusinessKey}' has unsupported status '{item.Status}'.",
                    nameof(items));
            }
            if (item.Status == AuthoringStatusValues.Items.Complete)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.AcceptedReceiptId);
            }
            else if (item.AcceptedReceiptId is not null)
            {
                throw new ArgumentException(
                    $"Pending mixed run item '{item.BusinessKey}' cannot carry a receipt.",
                    nameof(items));
            }
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string id = runId ?? Guid.NewGuid().ToString("N");
        AuthoringProcessorModeRecord mode;
        await using (SqliteCommand readMode = connection.CreateCommand())
        {
            readMode.Transaction = transaction;
            readMode.CommandText =
                """
                SELECT ProcessorKind, Mode, Epoch, RevalidationRequired,
                       RevalidationRunId, UpdatedAt
                FROM authoring_processor_modes
                WHERE ProcessorKind = @processorKind
                """;
            readMode.Parameters.AddWithValue("@processorKind", processorKind);
            await using SqliteDataReader reader =
                await readMode.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.AuthoringNotActivated,
                    $"Authoring is not activated for processor '{processorKind}'.");
            }
            mode = new AuthoringProcessorModeRecord
            {
                ProcessorKind = reader.GetString(0),
                Mode = reader.GetString(1),
                Epoch = reader.GetInt64(2),
                RevalidationRequired = reader.GetBoolean(3),
                RevalidationRunId = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedAt = reader.GetDateTimeOffset(5),
            };
        }
        EnsureRunBacked(mode);
        if (mode.RevalidationRequired)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.RevalidationRequired,
                $"Initial revalidation run '{mode.RevalidationRunId}' must complete before reconciliation.");
        }

        await using (SqliteCommand activeFence = connection.CreateCommand())
        {
            activeFence.Transaction = transaction;
            activeFence.CommandText =
                "SELECT RunId FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind";
            activeFence.Parameters.AddWithValue("@processorKind", processorKind);
            string? activeRunId =
                (string?)await activeFence.ExecuteScalarAsync(ct);
            if (activeRunId is not null)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.MutationFenceUnavailable,
                    $"Mutating run '{activeRunId}' is already active.",
                    [activeRunId]);
            }
        }

        async Task ExecuteInTransactionAsync(
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            await command.ExecuteNonQueryAsync(ct);
        }

        await ExecuteInTransactionAsync(
            """
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                SourceRunId, DatabaseOnly, TotalItems, CreatedAt, StartedAt,
                RequestJson)
            VALUES(
                @id, @processorKind, @epoch, @status, @purpose,
                @sourceRunId, 0, @totalItems, @createdAt, @startedAt,
                @requestJson)
            """,
            ("@id", id),
            ("@processorKind", processorKind),
            ("@epoch", mode.Epoch),
            ("@status", AuthoringStatusValues.Runs.Running),
            ("@purpose", purpose),
            ("@sourceRunId", sourceRunId),
            ("@totalItems", items.Count),
            ("@createdAt", Format(timestamp)),
            ("@startedAt", Format(timestamp)),
            ("@requestJson", requestJson));
        await ExecuteInTransactionAsync(
            """
            INSERT INTO authoring_mutation_fences(
                ProcessorKind, RunId, LeaseId, AcquiredAt)
            VALUES(@processorKind, @runId, @leaseId, @acquiredAt)
            """,
            ("@processorKind", processorKind),
            ("@runId", id),
            ("@leaseId", Guid.NewGuid().ToString("N")),
            ("@acquiredAt", Format(timestamp)));
        foreach (AuthoringMixedRunItem item in items)
        {
            await ExecuteInTransactionAsync(
                """
                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind,
                    ExpectedSourceRevision, Status, AcceptedReceiptId,
                    AttemptCount, CreatedAt, CompletedAt)
                VALUES(
                    @id, @runId, @businessKey, @itemKind,
                    @expectedSourceRevision, @status, @receiptId,
                    0, @createdAt, @completedAt)
                """,
                ("@id", Guid.NewGuid().ToString("N")),
                ("@runId", id),
                ("@businessKey", item.BusinessKey),
                ("@itemKind", item.ItemKind),
                ("@expectedSourceRevision", item.ExpectedSourceRevision),
                ("@status", item.Status),
                ("@receiptId", item.AcceptedReceiptId),
                ("@createdAt", Format(timestamp)),
                ("@completedAt", item.Status == AuthoringStatusValues.Items.Complete
                    ? Format(timestamp)
                    : null));
        }

        return new AuthoringRunRecord
        {
            Id = id,
            ProcessorKind = processorKind,
            AuthoringEpoch = mode.Epoch,
            Status = AuthoringStatusValues.Runs.Running,
            Purpose = purpose,
            SourceRunId = sourceRunId,
            DatabaseOnly = false,
            TotalItems = items.Count,
            CreatedAt = timestamp,
            StartedAt = timestamp,
            RequestJson = requestJson,
        };
    }

    public async Task<AuthoringRunRecord> CreateMaintenanceRunAsync(
        string processorKind,
        IReadOnlyCollection<AuthoringMaintenanceRunItem> items,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
        => await CreateMaintenanceRunAsync(
            processorKind,
            (_, _) => Task.FromResult<IReadOnlyList<AuthoringMaintenanceRunItem>>(
                items.ToArray()),
            AuthoringRunPurposeValues.GroupingMaintenance,
            databaseOnly: true,
            sourceRunId: null,
            now: now,
            ct: ct);

    public async Task<AuthoringRunRecord> CreateMaintenanceRunAsync(
        string processorKind,
        IReadOnlyCollection<AuthoringMaintenanceRunItem> items,
        string purpose,
        bool databaseOnly,
        string? sourceRunId = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
        => await CreateMaintenanceRunAsync(
            processorKind,
            (_, _) => Task.FromResult<IReadOnlyList<AuthoringMaintenanceRunItem>>(
                items.ToArray()),
            purpose,
            databaseOnly,
            sourceRunId,
            now,
            ct);

    public async Task<AuthoringRunRecord> CreateMaintenanceRunAsync(
        string processorKind,
        Func<
            SqliteConnection,
            CancellationToken,
            Task<IReadOnlyList<AuthoringMaintenanceRunItem>>> itemFactory,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
        => await CreateMaintenanceRunAsync(
            processorKind,
            itemFactory,
            AuthoringRunPurposeValues.GroupingMaintenance,
            databaseOnly: true,
            sourceRunId: null,
            now: now,
            ct: ct);

    public async Task<AuthoringRunRecord> CreateMaintenanceRunAsync(
        string processorKind,
        Func<
            SqliteConnection,
            CancellationToken,
            Task<IReadOnlyList<AuthoringMaintenanceRunItem>>> itemFactory,
        string purpose,
        bool databaseOnly,
        string? sourceRunId = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemFactory);
        return await CreateMaintenanceRunAsync(
            processorKind,
            async (connection, cancellationToken) =>
                new AuthoringMaintenanceRunSelection(
                    await itemFactory(connection, cancellationToken),
                    RequestJson: null),
            purpose,
            databaseOnly,
            sourceRunId,
            now,
            ct);
    }

    public async Task<AuthoringRunRecord> CreateMaintenanceRunAsync(
        string processorKind,
        Func<
            SqliteConnection,
            CancellationToken,
            Task<AuthoringMaintenanceRunSelection>> selectionFactory,
        string purpose,
        bool databaseOnly,
        string? sourceRunId = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        ArgumentNullException.ThrowIfNull(selectionFactory);
        AuthoringRunPurposeValues.EnsureValid(purpose);
        if (!AuthoringRunPurposeValues.IsMaintenance(purpose))
        {
            throw new ArgumentException(
                $"Purpose '{purpose}' is not a maintenance run purpose.",
                nameof(purpose));
        }
        if (string.Equals(
                purpose,
                AuthoringRunPurposeValues.GroupingMaintenance,
                StringComparison.Ordinal) &&
            !databaseOnly)
        {
            throw new ArgumentException(
                "Grouping maintenance runs must be database-only.",
                nameof(databaseOnly));
        }
        if (string.Equals(
                purpose,
                AuthoringRunPurposeValues.PublicationRefresh,
                StringComparison.Ordinal))
        {
            if (databaseOnly)
            {
                throw new ArgumentException(
                    "Publication refresh runs must produce a snapshot.",
                    nameof(databaseOnly));
            }
            if (string.IsNullOrWhiteSpace(sourceRunId))
            {
                throw new ArgumentException(
                    "Publication refresh runs require a source run.",
                    nameof(sourceRunId));
            }
        }
        else if (sourceRunId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string runId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord mode =
                await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new AuthoringConflictException(
                    AuthoringConflictCode.AuthoringNotActivated,
                    $"Authoring is not activated for processor '{processorKind}'.");
            EnsureRunBacked(mode);
            if (mode.RevalidationRequired)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RevalidationRequired,
                    $"Initial revalidation run '{mode.RevalidationRunId}' must complete before maintenance work.",
                    mode.RevalidationRunId is null
                        ? null
                        : [mode.RevalidationRunId]);
            }
            await using (SqliteCommand fence = connection.CreateCommand())
            {
                fence.CommandText =
                    "SELECT RunId FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind";
                fence.Parameters.AddWithValue("@processorKind", processorKind);
                string? activeRunId =
                    (string?)await fence.ExecuteScalarAsync(ct);
                if (activeRunId is not null)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.MutationFenceUnavailable,
                        $"Mutating run '{activeRunId}' is already active.");
                }
            }
            await ValidateMaintenanceSourceAsync(
                connection,
                processorKind,
                purpose,
                sourceRunId,
                ct);
            AuthoringMaintenanceRunSelection selection =
                await selectionFactory(connection, ct);
            ArgumentNullException.ThrowIfNull(selection);
            IReadOnlyList<AuthoringMaintenanceRunItem> items = selection.Items;
            ArgumentNullException.ThrowIfNull(items);
            _ = AuthoringMaintenanceRunRequest.Parse(selection.RequestJson);
            if (items.Count == 0)
            {
                throw new ArgumentException(
                    "A maintenance run requires at least one current receipt-backed item.",
                    nameof(selectionFactory));
            }
            foreach (AuthoringMaintenanceRunItem item in items)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.BusinessKey);
                ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemKind);
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    item.ExpectedSourceRevision);
                ArgumentException.ThrowIfNullOrWhiteSpace(item.ReceiptId);
            }

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_runs(
                    Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                    SourceRunId, DatabaseOnly, TotalItems, CreatedAt, StartedAt,
                    RequestJson)
                VALUES(
                    @id, @processorKind, @epoch, @status, @purpose,
                    @sourceRunId, @databaseOnly,
                    @totalItems, @createdAt, @startedAt, @requestJson)
                """,
                ct,
                ("@id", runId),
                ("@processorKind", processorKind),
                ("@epoch", mode.Epoch),
                ("@status", AuthoringStatusValues.Runs.Running),
                ("@purpose", purpose),
                ("@sourceRunId", sourceRunId),
                ("@databaseOnly", databaseOnly),
                ("@totalItems", items.Count),
                ("@createdAt", Format(timestamp)),
                ("@startedAt", Format(timestamp)),
                ("@requestJson", selection.RequestJson));
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
                ("@acquiredAt", Format(timestamp)));
            foreach (AuthoringMaintenanceRunItem item in items)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO authoring_run_items(
                        Id, RunId, BusinessKey, ItemKind,
                        ExpectedSourceRevision, Status, AcceptedReceiptId,
                        AttemptCount, CreatedAt, CompletedAt)
                    VALUES(
                        @id, @runId, @businessKey, @itemKind,
                        @expectedSourceRevision, @status, @receiptId,
                        0, @createdAt, @completedAt)
                    """,
                    ct,
                    ("@id", Guid.NewGuid().ToString("N")),
                    ("@runId", runId),
                    ("@businessKey", item.BusinessKey),
                    ("@itemKind", $"maintenance:{runId}:{item.ItemKind}"),
                    ("@expectedSourceRevision", item.ExpectedSourceRevision),
                    ("@status", AuthoringStatusValues.Items.Complete),
                    ("@receiptId", item.ReceiptId),
                    ("@createdAt", Format(timestamp)),
                    ("@completedAt", Format(timestamp)));
            }

            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new InvalidOperationException(
                    $"Failed to create maintenance run '{runId}'.");
            await CommitAsync(connection, ct);
            return run;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRunRecord> ReplaceRevalidationRunAsync(
        string processorKind,
        string expectedRunId,
        IReadOnlyCollection<AuthoringRunItemDefinition> items,
        DateTimeOffset? now = null,
        CancellationToken ct = default,
        IReadOnlyCollection<AuthoringRunInputProvenanceDefinition>? inputProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRunId);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException(
                "A replacement revalidation run requires at least one item.",
                nameof(items));
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string replacementRunId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord mode =
                await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new AuthoringConflictException(
                    AuthoringConflictCode.AuthoringNotActivated,
                    $"Authoring is not activated for processor '{processorKind}'.");
            EnsureRunBacked(mode);
            if (!mode.RevalidationRequired ||
                !string.Equals(
                    mode.RevalidationRunId,
                    expectedRunId,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{expectedRunId}' is no longer the active initial revalidation run.");
            }

            AuthoringRunRecord current =
                await ReadRunAsync(connection, expectedRunId, ct)
                ?? throw new KeyNotFoundException(
                    $"Revalidation run '{expectedRunId}' was not found.");
            if (current.AuthoringEpoch != mode.Epoch)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Revalidation run '{expectedRunId}' is not in the active epoch.");
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_attempts
                SET Status = @superseded,
                    CompletedAt = @completedAt,
                    Error = @error
                WHERE RunId = @runId AND Status = @active
                """,
                ct,
                ("@superseded", AuthoringStatusValues.Attempts.Superseded),
                ("@completedAt", Format(timestamp)),
                ("@error", "Initial revalidation source revisions changed."),
                ("@runId", expectedRunId),
                ("@active", AuthoringStatusValues.Attempts.Active));
            foreach (AuthoringRunItemDefinition item in items
                         .DistinctBy(
                             item => (
                                 item.BusinessKey.ToUpperInvariant(),
                                 item.ItemKind.ToLowerInvariant())))
            {
                await ExecuteAsync(
                    connection,
                    """
                    WITH RECURSIVE run_chain(Id) AS (
                        SELECT @runId
                        UNION
                        SELECT lineage.PreviousRunId
                        FROM authoring_revalidation_lineage lineage
                        INNER JOIN run_chain chain ON lineage.RunId = chain.Id
                    )
                    UPDATE authoring_run_items
                    SET Status = @superseded,
                        CompletedAt = @completedAt,
                        Error = @error
                    WHERE RunId IN (SELECT Id FROM run_chain)
                      AND BusinessKey = @businessKey COLLATE NOCASE
                      AND ItemKind = @itemKind COLLATE NOCASE
                      AND Status <> @alreadySuperseded
                    """,
                    ct,
                    ("@superseded", AuthoringStatusValues.Items.Superseded),
                    ("@completedAt", Format(timestamp)),
                    ("@error", "Initial revalidation source revisions changed."),
                    ("@runId", expectedRunId),
                    ("@businessKey", item.BusinessKey),
                    ("@itemKind", item.ItemKind),
                    ("@alreadySuperseded", AuthoringStatusValues.Items.Superseded));
            }
            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_runs
                SET Status = @status,
                    Purpose = @purpose,
                    CompletedAt = @completedAt,
                    Error = @error
                WHERE Id = @runId
                """,
                ct,
                ("@status", AuthoringStatusValues.Runs.Superseded),
                ("@purpose", AuthoringRunPurposeValues.InitialRevalidation),
                ("@completedAt", Format(timestamp)),
                ("@error", "Initial revalidation source revisions changed."),
                ("@runId", expectedRunId));
            await ExecuteAsync(
                connection,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind AND RunId = @runId
                """,
                ct,
                ("@processorKind", processorKind),
                ("@runId", expectedRunId));

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_runs(
                    Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                    SourceRunId, DatabaseOnly, TotalItems, CreatedAt, StartedAt)
                VALUES(
                    @id, @processorKind, @epoch, @status, @purpose,
                    NULL, 0,
                    @totalItems, @createdAt, @startedAt)
                """,
                ct,
                ("@id", replacementRunId),
                ("@processorKind", processorKind),
                ("@epoch", mode.Epoch),
                ("@status", AuthoringStatusValues.Runs.Running),
                ("@purpose", AuthoringRunPurposeValues.InitialRevalidation),
                ("@totalItems", items.Count),
                ("@createdAt", Format(timestamp)),
                ("@startedAt", Format(timestamp)));
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_revalidation_lineage(RunId, PreviousRunId)
                VALUES(@runId, @previousRunId)
                """,
                ct,
                ("@runId", replacementRunId),
                ("@previousRunId", expectedRunId));
            foreach (AuthoringRunItemDefinition item in items)
            {
                int attemptCount =
                    await CountRevalidationAuthoringAttemptsAsync(
                        connection,
                        expectedRunId,
                        item,
                        ct);
                bool attemptLimitReached =
                    !_retryPolicy.CanStartAnotherAuthoringAttempt(
                        attemptCount);
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO authoring_run_items(
                        Id, RunId, BusinessKey, ItemKind,
                        ExpectedSourceRevision, Status, AttemptCount, CreatedAt,
                        CompletedAt, Error)
                    VALUES(
                        @id, @runId, @businessKey, @itemKind,
                        @expectedSourceRevision, @status, @attemptCount, @createdAt,
                        @completedAt, @error)
                    """,
                    ct,
                    ("@id", Guid.NewGuid().ToString("N")),
                    ("@runId", replacementRunId),
                    ("@businessKey", item.BusinessKey),
                    ("@itemKind", item.ItemKind),
                    ("@expectedSourceRevision", item.ExpectedSourceRevision),
                    ("@status", attemptLimitReached
                        ? AuthoringStatusValues.Items.Superseded
                        : AuthoringStatusValues.Items.Pending),
                    ("@attemptCount", attemptCount),
                    ("@createdAt", Format(timestamp)),
                    ("@completedAt", attemptLimitReached
                        ? Format(timestamp)
                        : null),
                    ("@error", attemptLimitReached
                        ? $"Authoring attempt limit of {_retryPolicy.MaxAttempts} was reached."
                        : null));
            }
            await InsertRunInputProvenanceAsync(
                connection,
                replacementRunId,
                inputProvenance,
                timestamp,
                ct);
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(@processorKind, @runId, @leaseId, @acquiredAt)
                """,
                ct,
                ("@processorKind", processorKind),
                ("@runId", replacementRunId),
                ("@leaseId", Guid.NewGuid().ToString("N")),
                ("@acquiredAt", Format(timestamp)));
            int modeUpdates = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_processor_modes
                SET RevalidationRunId = @replacementRunId,
                    UpdatedAt = @updatedAt
                WHERE ProcessorKind = @processorKind
                  AND RevalidationRequired = 1
                  AND RevalidationRunId = @expectedRunId
                """,
                ct,
                ("@replacementRunId", replacementRunId),
                ("@updatedAt", Format(timestamp)),
                ("@processorKind", processorKind),
                ("@expectedRunId", expectedRunId));
            if (modeUpdates != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    "Initial revalidation ownership changed before replacement.");
            }

            AuthoringRunRecord replacement =
                await ReadRunAsync(connection, replacementRunId, ct)
                ?? throw new InvalidOperationException(
                    "Failed to create replacement revalidation run.");
            await CommitAsync(connection, ct);
            return replacement;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRunRecord?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        return await ReadRunAsync(connection, runId, ct);
    }

    public async Task<IReadOnlyList<AuthoringRunInputProvenanceRecord>>
        GetRunInputProvenanceAsync(
            string runId,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        return await ReadRunInputProvenanceAsync(connection, runId, ct);
    }

    public static async Task<IReadOnlyList<AuthoringRunInputProvenanceRecord>>
        ReadRunInputProvenanceAsync(
            SqliteConnection connection,
            string runId,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, RunId, Source, LatestSuccessfulRefreshAt,
                   ContentRevision, CapturedAt
            FROM authoring_run_input_provenance
            WHERE RunId = @runId
            ORDER BY Source COLLATE NOCASE, RowId
            """;
        command.Parameters.AddWithValue("@runId", runId);

        List<AuthoringRunInputProvenanceRecord> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AuthoringRunInputProvenanceRecord
            {
                RowId = reader.GetInt32(0),
                RunId = reader.GetString(1),
                Source = reader.GetString(2),
                LatestSuccessfulRefreshAt = ReadNullableDate(reader, 3),
                ContentRevision = reader.IsDBNull(4)
                    ? null
                    : reader.GetInt64(4),
                CapturedAt = ParseDate(reader.GetString(5)),
            });
        }
        return rows;
    }

    public async Task<AuthoringOperatorRunList> ListOperatorRunsAsync(
        string processorKind,
        int limit = DefaultOperatorRunLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        if (limit is < 1 or > MaximumOperatorRunLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"Operator authoring run limit must be between 1 and {MaximumOperatorRunLimit}.");
        }

        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH candidate_runs AS (
                SELECT RowId, Id, ProcessorKind, AuthoringEpoch, Status,
                       DatabaseOnly, TotalItems, CreatedAt, StartedAt,
                       CompletedAt, Error, SnapshotId, RequestJson,
                       Purpose, SourceRunId,
                       CASE
                           WHEN Status IN (@queued, @running, @finalizing, @runError)
                               THEN 0
                           ELSE 1
                       END AS Priority
                FROM authoring_runs
                WHERE ProcessorKind = @processorKind
                  AND (
                      RequestJson IS NOT NULL
                      OR Purpose NOT IN (@authoring, @initialRevalidation))
                ORDER BY Priority, CreatedAt DESC, RowId DESC
                LIMIT @candidateLimit
            )
            SELECT r.RowId, r.Id, r.ProcessorKind, r.AuthoringEpoch, r.Status,
                   r.DatabaseOnly, r.TotalItems, r.CreatedAt, r.StartedAt,
                   r.CompletedAt, r.Error, r.SnapshotId, r.RequestJson,
                   r.Purpose, r.SourceRunId,
                   SUM(CASE WHEN i.Status = @complete THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.Status = @itemError THEN 1 ELSE 0 END),
                   SUM(CASE WHEN i.Status = @superseded THEN 1 ELSE 0 END),
                   MIN(CASE
                       WHEN i.Status = @itemError
                        AND i.CompletedAt IS NOT NULL
                        AND (
                            i.AcceptedReceiptId IS NOT NULL
                            OR i.AttemptCount < @maxAttempts)
                           THEN i.CompletedAt
                       ELSE NULL
                   END)
            FROM candidate_runs r
            LEFT JOIN authoring_run_items i ON i.RunId = r.Id
            GROUP BY r.RowId, r.Id, r.ProcessorKind, r.AuthoringEpoch, r.Status,
                     r.DatabaseOnly, r.TotalItems, r.CreatedAt, r.StartedAt,
                     r.CompletedAt, r.Error, r.SnapshotId, r.RequestJson,
                     r.Purpose, r.SourceRunId,
                     r.Priority
            ORDER BY r.Priority, r.CreatedAt DESC, r.RowId DESC
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        command.Parameters.AddWithValue("@queued", AuthoringStatusValues.Runs.Queued);
        command.Parameters.AddWithValue("@running", AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue("@finalizing", AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue("@runError", AuthoringStatusValues.Runs.Error);
        command.Parameters.AddWithValue(
            "@authoring",
            AuthoringRunPurposeValues.Authoring);
        command.Parameters.AddWithValue(
            "@initialRevalidation",
            AuthoringRunPurposeValues.InitialRevalidation);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue("@itemError", AuthoringStatusValues.Items.Error);
        command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
        command.Parameters.AddWithValue("@maxAttempts", _retryPolicy.MaxAttempts);
        command.Parameters.AddWithValue("@candidateLimit", limit + 1);

        List<AuthoringOperatorRunSummary> runs = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            DateTimeOffset? errorCompletedAt = ReadNullableDate(reader, 18);
            runs.Add(new AuthoringOperatorRunSummary(
                ReadRun(reader),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                errorCompletedAt is null
                    ? null
                    : _retryPolicy.GetNextAutomaticRetryAt(
                        errorCompletedAt.Value)));
        }

        bool truncated = runs.Count > limit;
        if (truncated)
        {
            runs.RemoveAt(runs.Count - 1);
        }
        return new AuthoringOperatorRunList(runs, truncated);
    }

    public async Task EnsureActiveRunCapacityAvailableAsync(
        string processorKind,
        int maxActiveRuns,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        if (maxActiveRuns < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActiveRuns),
                "Active authoring run capacity must be greater than or equal to 1.");
        }

        await using SqliteConnection connection = _openConnection();
        await EnsureActiveRunCapacityAvailableAsync(
            connection,
            processorKind,
            maxActiveRuns,
            ct);
    }

    public async Task<AuthoringRunRecord?> GetFencedRunAsync(
        string processorKind,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.RowId, r.Id, r.ProcessorKind, r.AuthoringEpoch, r.Status,
                   r.DatabaseOnly, r.TotalItems, r.CreatedAt, r.StartedAt,
                   r.CompletedAt, r.Error, r.SnapshotId, r.RequestJson,
                   r.Purpose, r.SourceRunId
            FROM authoring_mutation_fences f
            INNER JOIN authoring_runs r ON r.Id = f.RunId
            WHERE f.ProcessorKind = @processorKind
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<AuthoringRunRecord?> GetOldestQueuedRunAsync(
        string processorKind,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly,
                   TotalItems, CreatedAt, StartedAt, CompletedAt, Error, SnapshotId,
                   RequestJson, Purpose, SourceRunId
            FROM authoring_runs
            WHERE ProcessorKind = @processorKind AND Status = @status
            ORDER BY CreatedAt, RowId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        command.Parameters.AddWithValue("@status", AuthoringStatusValues.Runs.Queued);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<AuthoringRunRecord>> GetRunsReadyForFinalizationAsync(
        string processorKind,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorKind);
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.RowId, r.Id, r.ProcessorKind, r.AuthoringEpoch, r.Status,
                   r.DatabaseOnly, r.TotalItems, r.CreatedAt, r.StartedAt,
                   r.CompletedAt, r.Error, r.SnapshotId, r.RequestJson,
                   r.Purpose, r.SourceRunId
            FROM authoring_runs r
            WHERE r.ProcessorKind = @processorKind
              AND r.Status IN (@running, @finalizing, @error)
              AND NOT EXISTS (
                  SELECT 1
                  FROM authoring_run_items i
                  WHERE i.RunId = r.Id
                    AND i.Status NOT IN (@complete, @superseded)
              )
            ORDER BY r.CreatedAt, r.RowId
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        command.Parameters.AddWithValue("@running", AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue("@finalizing", AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue("@error", AuthoringStatusValues.Runs.Error);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);

        List<AuthoringRunRecord> runs = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(ReadRun(reader));
        }
        return runs;
    }

    public async Task<DateTimeOffset?> GetEarliestAutomaticRetryAtAsync(
        string runId,
        CancellationToken ct = default)
    {
        IReadOnlyList<AuthoringRunItemRecord> items = await GetRunItemsAsync(runId, ct);
        return items
            .Where(item =>
                string.Equals(
                    item.Status,
                    AuthoringStatusValues.Items.Error,
                    StringComparison.Ordinal) &&
                item.CompletedAt is not null)
            .Select(item => _retryPolicy.GetNextAutomaticRetryAt(item.CompletedAt!.Value))
            .Cast<DateTimeOffset?>()
            .Min();
    }

    public async Task<IReadOnlyList<AuthoringRunItemRecord>> GetRunItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                   CurrentOperationId, AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt,
                   CompletedAt, Error, PostPersistenceLeaseId, PostPersistenceLeaseAcquiredAt
            FROM authoring_run_items
            WHERE RunId = @runId
            ORDER BY RowId
            """;
        command.Parameters.AddWithValue("@runId", runId);

        List<AuthoringRunItemRecord> items = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadRunItem(reader));
        }
        return items;
    }

    public async Task<IReadOnlyList<AuthoringRunItemRecord>>
        GetRevalidationCorpusItemsAsync(
            string runId,
            CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        return await ReadRevalidationCorpusItemsAsync(connection, runId, ct);
    }

    public static async Task<IReadOnlyList<AuthoringRunItemRecord>>
        ReadRevalidationCorpusItemsAsync(
            SqliteConnection connection,
            string runId,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE run_chain(Id) AS (
                SELECT @runId
                UNION
                SELECT lineage.PreviousRunId
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            )
            SELECT i.RowId, i.Id, i.RunId, i.BusinessKey, i.ItemKind,
                   i.ExpectedSourceRevision, i.Status, i.CurrentOperationId,
                   i.AcceptedReceiptId, i.AttemptCount, i.CreatedAt, i.StartedAt,
                   i.CompletedAt, i.Error, i.PostPersistenceLeaseId,
                   i.PostPersistenceLeaseAcquiredAt
            FROM authoring_run_items i
            INNER JOIN run_chain chain ON chain.Id = i.RunId
            WHERE i.Status <> @superseded
            ORDER BY i.RowId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@superseded",
            AuthoringStatusValues.Items.Superseded);

        List<AuthoringRunItemRecord> items = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadRunItem(reader));
        }
        return items;
    }

    public async Task<IReadOnlyList<AuthoringRunItemRecord>>
        GetRevalidationSupersededItemsAsync(
            string runId,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE run_chain(Id, Depth) AS (
                SELECT @runId, 0
                UNION ALL
                SELECT lineage.PreviousRunId, chain.Depth + 1
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            )
            SELECT i.RowId, i.Id, i.RunId, i.BusinessKey, i.ItemKind,
                   i.ExpectedSourceRevision, i.Status, i.CurrentOperationId,
                   i.AcceptedReceiptId, i.AttemptCount, i.CreatedAt, i.StartedAt,
                   i.CompletedAt, i.Error, i.PostPersistenceLeaseId,
                   i.PostPersistenceLeaseAcquiredAt
            FROM authoring_run_items i
            INNER JOIN run_chain chain ON chain.Id = i.RunId
            WHERE i.Status = @superseded
              AND i.AcceptedReceiptId IS NULL
            ORDER BY chain.Depth, i.RowId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@superseded",
            AuthoringStatusValues.Items.Superseded);

        List<AuthoringRunItemRecord> items = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(ReadRunItem(reader));
        }
        return items;
    }

    public async Task<AuthoringResultReceipt?> GetReceiptByOperationAsync(
        string operationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using SqliteConnection connection = _openConnection();
        AuthoringResultReceiptRecord? receipt = await ReadReceiptByOperationAsync(
            connection,
            operationId,
            ct);
        return receipt is null ? null : ToContract(receipt);
    }

    public async Task<bool> TryAcquireMutationFenceAsync(
        string processorKind,
        string runId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringProcessorModeRecord mode = await ReadProcessorModeAsync(connection, processorKind, ct)
                ?? throw new AuthoringConflictException(
                    AuthoringConflictCode.AuthoringNotActivated,
                    $"Authoring is not activated for processor '{processorKind}'.");
            EnsureRunBacked(mode);

            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            if (!string.Equals(run.ProcessorKind, processorKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Run '{runId}' belongs to '{run.ProcessorKind}', not '{processorKind}'.");
            }
            if (run.AuthoringEpoch != mode.Epoch)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' belongs to authoring epoch {run.AuthoringEpoch}, not active epoch {mode.Epoch}.");
            }

            await using SqliteCommand existingCommand = connection.CreateCommand();
            existingCommand.CommandText =
                "SELECT RunId FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind";
            existingCommand.Parameters.AddWithValue("@processorKind", processorKind);
            string? existingRunId = (string?)await existingCommand.ExecuteScalarAsync(ct);
            if (existingRunId is not null &&
                !string.Equals(existingRunId, runId, StringComparison.Ordinal))
            {
                await CommitAsync(connection, ct);
                return false;
            }

            if (existingRunId is null)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                    VALUES (@processorKind, @runId, @leaseId, @acquiredAt)
                    """,
                    ct,
                    ("@processorKind", processorKind),
                    ("@runId", runId),
                    ("@leaseId", Guid.NewGuid().ToString("N")),
                    ("@acquiredAt", Format(timestamp)));
            }

            if (run.Status is not (AuthoringStatusValues.Runs.Queued or AuthoringStatusValues.Runs.Running or AuthoringStatusValues.Runs.Error))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' cannot acquire the mutation fence from status '{run.Status}'.");
            }

            if (!string.Equals(run.Status, AuthoringStatusValues.Runs.Running, StringComparison.Ordinal))
            {
                AuthoringStatusValues.EnsureRunTransition(run.Status, AuthoringStatusValues.Runs.Running);
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_runs
                    SET Status = @status,
                        StartedAt = COALESCE(StartedAt, @startedAt),
                        Error = NULL
                    WHERE Id = @runId
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Runs.Running),
                    ("@startedAt", Format(timestamp)),
                    ("@runId", runId));
            }

            await CommitAsync(connection, ct);
            return true;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task ReleaseMutationFenceAsync(
        string processorKind,
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        await ExecuteAsync(
            connection,
            "DELETE FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind AND RunId = @runId",
            ct,
            ("@processorKind", processorKind),
            ("@runId", runId));
    }

    public Task<AuthoringOperationClaim?> ClaimNextItemAsync(
        string runId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
        => ClaimItemInternalAsync(runId, itemId: null, now, ct);

    public Task<AuthoringOperationClaim?> ClaimItemAsync(
        string runId,
        string itemId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
        => ClaimItemInternalAsync(runId, itemId, now, ct);

    public async Task<AuthoringReceiptAcceptance> AcceptResultAsync(
        AuthoringResultSubmission submission,
        string operationToken,
        AuthoringDomainPersistence? persistDomainResult = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.ContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(submission.ObservedSourceRevision);
        submission = submission with
        {
            ObservedSourceRevision =
                AuthoringSourceRevision.CanonicalizeTimestamp(
                    submission.ObservedSourceRevision),
        };

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunAttemptRecord attempt = await ReadAttemptAsync(connection, submission.OperationId, ct)
                ?? throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{submission.OperationId}' is unknown.");

            if (!AuthoringOperationTokenVerifier.Verify(operationToken, attempt.TokenVerifier))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.InvalidOperationToken,
                    "The authoring operation token is invalid.");
            }

            AuthoringResultReceiptRecord? accepted = await ReadReceiptByOperationAsync(
                connection,
                submission.OperationId,
                ct);
            if (accepted is not null)
            {
                EnsureReplayMatches(accepted, submission);
                await CommitAsync(connection, ct);
                return new AuthoringReceiptAcceptance(ToContract(accepted), IsReplay: true);
            }

            AuthoringRunItemRecord item = await ReadRunItemAsync(connection, submission.ItemId, ct)
                ?? throw new KeyNotFoundException($"Authoring item '{submission.ItemId}' was not found.");
            if (!string.Equals(item.RunId, submission.RunId, StringComparison.Ordinal) ||
                !string.Equals(attempt.RunId, submission.RunId, StringComparison.Ordinal) ||
                !string.Equals(attempt.RunItemId, submission.ItemId, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    "The submitted run, item, and operation coordinates do not match.");
            }

            if (!string.Equals(item.CurrentOperationId, submission.OperationId, StringComparison.Ordinal) ||
                !string.Equals(attempt.Status, AuthoringStatusValues.Attempts.Active, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{submission.OperationId}' is no longer active.");
            }

            if (!string.Equals(
                    item.ExpectedSourceRevision,
                    submission.ObservedSourceRevision,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"Observed source revision '{submission.ObservedSourceRevision}' does not match frozen revision '{item.ExpectedSourceRevision}'.");
            }

            AuthoringRunRecord run = await ReadRunAsync(connection, submission.RunId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{submission.RunId}' was not found.");

            if (persistDomainResult is not null)
            {
                await persistDomainResult(connection, ct);
            }

            string receiptId = Guid.NewGuid().ToString("N");
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_result_receipts
                    (Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                     ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
                VALUES
                    (@id, @operationId, @runId, @runItemId, @businessKey, @contentHash,
                     @expectedSourceRevision, @observedSourceRevision, @authoringEpoch, @persistedAt)
                """,
                ct,
                ("@id", receiptId),
                ("@operationId", submission.OperationId),
                ("@runId", submission.RunId),
                ("@runItemId", submission.ItemId),
                ("@businessKey", item.BusinessKey),
                ("@contentHash", submission.ContentHash),
                ("@expectedSourceRevision", item.ExpectedSourceRevision),
                ("@observedSourceRevision", submission.ObservedSourceRevision),
                ("@authoringEpoch", run.AuthoringEpoch),
                ("@persistedAt", Format(timestamp)));

            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_attempts
                SET Status = @status,
                    ContentHash = @contentHash,
                    ObservedSourceRevision = @observedSourceRevision,
                    CompletedAt = @completedAt,
                    Error = NULL
                WHERE OperationId = @operationId AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Attempts.Accepted),
                ("@contentHash", submission.ContentHash),
                ("@observedSourceRevision", submission.ObservedSourceRevision),
                ("@completedAt", Format(timestamp)),
                ("@operationId", submission.OperationId),
                ("@expectedStatus", AuthoringStatusValues.Attempts.Active));

            int itemUpdates = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    AcceptedReceiptId = @receiptId,
                    Error = NULL
                WHERE Id = @itemId
                  AND RunId = @runId
                  AND CurrentOperationId = @operationId
                  AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Items.Persisted),
                ("@receiptId", receiptId),
                ("@itemId", submission.ItemId),
                ("@runId", submission.RunId),
                ("@operationId", submission.OperationId),
                ("@expectedStatus", AuthoringStatusValues.Items.InProgress));
            if (itemUpdates != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{submission.OperationId}' lost its item claim before persistence.");
            }

            await CommitAsync(connection, ct);
            return new AuthoringReceiptAcceptance(
                new AuthoringResultReceipt(
                    receiptId,
                    submission.RunId,
                    submission.ItemId,
                    submission.OperationId,
                    item.BusinessKey,
                    submission.ContentHash,
                    item.ExpectedSourceRevision,
                    submission.ObservedSourceRevision,
                    run.AuthoringEpoch,
                    timestamp),
                IsReplay: false);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRetryResult> RetryItemAsync(
        string itemId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunItemRecord item = await ReadRunItemAsync(connection, itemId, ct)
                ?? throw new KeyNotFoundException($"Authoring item '{itemId}' was not found.");
            AuthoringRunRecord run = await ReadRunAsync(connection, item.RunId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{item.RunId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, run.Id, ct);
            if (!string.Equals(item.Status, AuthoringStatusValues.Items.Error, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.ItemNotClaimable,
                    $"Item '{itemId}' cannot be retried from status '{item.Status}'.");
            }

            bool requiresAuthoring = string.IsNullOrWhiteSpace(item.AcceptedReceiptId);
            if (requiresAuthoring &&
                !_retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.AttemptLimitReached,
                    $"Item '{itemId}' has exhausted its {_retryPolicy.MaxAttempts} authoring attempts.");
            }

            string nextStatus = requiresAuthoring
                ? AuthoringStatusValues.Items.Pending
                : AuthoringStatusValues.Items.Persisted;
            AuthoringStatusValues.EnsureItemTransition(item.Status, nextStatus);

            if (requiresAuthoring && item.CurrentOperationId is not null)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_attempts
                    SET Status = @status,
                        CompletedAt = COALESCE(CompletedAt, @completedAt),
                        Error = COALESCE(Error, @error)
                    WHERE OperationId = @operationId AND Status = @activeStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Attempts.Error),
                    ("@completedAt", Format(timestamp)),
                    ("@error", item.Error),
                    ("@operationId", item.CurrentOperationId),
                    ("@activeStatus", AuthoringStatusValues.Attempts.Active));
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    CurrentOperationId = CASE WHEN @requiresAuthoring = 1 THEN NULL ELSE CurrentOperationId END,
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    StartedAt = CASE WHEN @requiresAuthoring = 1 THEN NULL ELSE StartedAt END,
                    CompletedAt = NULL,
                    Error = NULL
                WHERE Id = @itemId AND Status = @expectedStatus
                """,
                ct,
                ("@status", nextStatus),
                ("@requiresAuthoring", requiresAuthoring),
                ("@itemId", itemId),
                ("@expectedStatus", AuthoringStatusValues.Items.Error));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.ItemNotClaimable,
                    $"Item '{itemId}' changed before its retry transition completed.");
            }

            await CommitAsync(connection, ct);
            return new AuthoringRetryResult(itemId, requiresAuthoring);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task MarkItemCompleteAsync(
        string itemId,
        string receiptId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_items
            SET Status = @status,
                CompletedAt = @completedAt,
                PostPersistenceLeaseId = NULL,
                PostPersistenceLeaseAcquiredAt = NULL,
                Error = NULL
            WHERE Id = @itemId
              AND AcceptedReceiptId = @receiptId
              AND Status = @expectedStatus
            """,
            ct,
            ("@status", AuthoringStatusValues.Items.Complete),
            ("@completedAt", Format(timestamp)),
            ("@itemId", itemId),
            ("@receiptId", receiptId),
            ("@expectedStatus", AuthoringStatusValues.Items.Persisted));
        if (updated != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.ItemNotClaimable,
                $"Item '{itemId}' is not persisted with receipt '{receiptId}'.");
        }
    }

    public async Task<string?> ClaimPersistedItemAsync(
        string itemId,
        string receiptId,
        string operationId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        string leaseId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_items
            SET Status = @status,
                StartedAt = @startedAt,
                PostPersistenceLeaseId = @leaseId,
                PostPersistenceLeaseAcquiredAt = @leaseAcquiredAt,
                CompletedAt = NULL,
                Error = NULL
            WHERE Id = @itemId
              AND AcceptedReceiptId = @receiptId
              AND CurrentOperationId = @operationId
              AND Status = @expectedStatus
            """,
            ct,
            ("@status", AuthoringStatusValues.Items.InProgress),
            ("@startedAt", Format(timestamp)),
            ("@leaseId", leaseId),
            ("@leaseAcquiredAt", Format(timestamp)),
            ("@itemId", itemId),
            ("@receiptId", receiptId),
            ("@operationId", operationId),
            ("@expectedStatus", AuthoringStatusValues.Items.Persisted));
        return updated == 1 ? leaseId : null;
    }

    public async Task MarkClaimCompleteAsync(
        string itemId,
        string receiptId,
        string operationId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_items
            SET Status = @status,
                CompletedAt = @completedAt,
                PostPersistenceLeaseId = NULL,
                PostPersistenceLeaseAcquiredAt = NULL,
                Error = NULL
            WHERE Id = @itemId
              AND AcceptedReceiptId = @receiptId
              AND (
                  (PostPersistenceLeaseId IS NULL AND CurrentOperationId = @claimId)
                  OR PostPersistenceLeaseId = @claimId
              )
              AND Status IN (@persistedStatus, @inProgressStatus)
            """,
            ct,
            ("@status", AuthoringStatusValues.Items.Complete),
            ("@completedAt", Format(timestamp)),
            ("@itemId", itemId),
            ("@receiptId", receiptId),
            ("@claimId", operationId),
            ("@persistedStatus", AuthoringStatusValues.Items.Persisted),
            ("@inProgressStatus", AuthoringStatusValues.Items.InProgress));
        if (updated != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StaleOperation,
                $"Operation '{operationId}' no longer owns item '{itemId}'.");
        }
    }

    public async Task MarkItemErrorAsync(
        string itemId,
        string error,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_items
            SET Status = @status, CompletedAt = @completedAt, Error = @error
            WHERE Id = @itemId
              AND Status IN (@inProgress, @persisted)
            """,
            ct,
            ("@status", AuthoringStatusValues.Items.Error),
            ("@completedAt", Format(timestamp)),
            ("@error", TruncateError(error)),
            ("@itemId", itemId),
            ("@inProgress", AuthoringStatusValues.Items.InProgress),
            ("@persisted", AuthoringStatusValues.Items.Persisted));
        if (updated != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.ItemNotClaimable,
                $"Item '{itemId}' is not active or persisted.");
        }
    }

    public async Task MarkClaimErrorAsync(
        string itemId,
        string operationId,
        string error,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunItemRecord item = await ReadRunItemAsync(connection, itemId, ct)
                ?? throw new KeyNotFoundException($"Authoring item '{itemId}' was not found.");
            AuthoringRunRecord run = await ReadRunAsync(connection, item.RunId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{item.RunId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, run.Id, ct);
            bool ownsItem =
                string.Equals(
                    item.Status,
                    AuthoringStatusValues.Items.InProgress,
                    StringComparison.Ordinal) &&
                (string.Equals(item.PostPersistenceLeaseId, operationId, StringComparison.Ordinal) ||
                 (item.PostPersistenceLeaseId is null &&
                  string.Equals(item.CurrentOperationId, operationId, StringComparison.Ordinal)));
            if (!ownsItem)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{operationId}' no longer owns item '{itemId}'.");
            }

            bool requiresAuthoring = string.IsNullOrWhiteSpace(item.AcceptedReceiptId);
            string nextStatus = requiresAuthoring &&
                !_retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount)
                    ? AuthoringStatusValues.Items.Superseded
                    : AuthoringStatusValues.Items.Error;
            AuthoringStatusValues.EnsureItemTransition(item.Status, nextStatus);

            if (requiresAuthoring)
            {
                int attemptUpdates = await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_attempts
                    SET Status = @status,
                        CompletedAt = @completedAt,
                        Error = @error
                    WHERE OperationId = @operationId
                      AND RunItemId = @itemId
                      AND Status = @activeStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Attempts.Error),
                    ("@completedAt", Format(timestamp)),
                    ("@error", TruncateError(error)),
                    ("@operationId", operationId),
                    ("@itemId", itemId),
                    ("@activeStatus", AuthoringStatusValues.Attempts.Active));
                if (attemptUpdates != 1)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StaleOperation,
                        $"Operation '{operationId}' no longer owns an active attempt for item '{itemId}'.");
                }
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    CompletedAt = @completedAt,
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    Error = @error
                WHERE Id = @itemId
                  AND (
                      (PostPersistenceLeaseId IS NULL AND CurrentOperationId = @claimId)
                      OR PostPersistenceLeaseId = @claimId
                  )
                  AND Status = @expectedStatus
                """,
                ct,
                ("@status", nextStatus),
                ("@completedAt", Format(timestamp)),
                ("@error", TruncateError(error)),
                ("@itemId", itemId),
                ("@claimId", operationId),
                ("@expectedStatus", AuthoringStatusValues.Items.InProgress));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{operationId}' lost item '{itemId}' during failure recording.");
            }

            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringErrorReconciliationResult> ReconcileErroredItemsAsync(
        string runId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, run.Id, ct);

            List<AuthoringRunItemRecord> items = [];
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT RowId, Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                           Status, CurrentOperationId, AcceptedReceiptId, AttemptCount,
                           CreatedAt, StartedAt, CompletedAt, Error,
                           PostPersistenceLeaseId, PostPersistenceLeaseAcquiredAt
                    FROM authoring_run_items
                    WHERE RunId = @runId AND Status = @status
                    ORDER BY RowId
                    """;
                command.Parameters.AddWithValue("@runId", runId);
                command.Parameters.AddWithValue("@status", AuthoringStatusValues.Items.Error);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    items.Add(ReadRunItem(reader));
                }
            }

            int retriedItems = 0;
            int resumedReceiptItems = 0;
            int supersededItems = 0;
            DateTimeOffset? nextRetryAt = null;
            foreach (AuthoringRunItemRecord item in items)
            {
                DateTimeOffset completedAt = item.CompletedAt ?? timestamp;
                if (item.CompletedAt is null)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_items
                        SET CompletedAt = @completedAt
                        WHERE Id = @itemId AND Status = @status AND CompletedAt IS NULL
                        """,
                        ct,
                        ("@completedAt", Format(completedAt)),
                        ("@itemId", item.Id),
                        ("@status", AuthoringStatusValues.Items.Error));
                }

                if (item.CurrentOperationId is not null)
                {
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_attempts
                        SET Status = @status,
                            CompletedAt = COALESCE(CompletedAt, @completedAt),
                            Error = COALESCE(Error, @error)
                        WHERE OperationId = @operationId AND Status = @activeStatus
                        """,
                        ct,
                        ("@status", AuthoringStatusValues.Attempts.Error),
                        ("@completedAt", Format(completedAt)),
                        ("@error", item.Error),
                        ("@operationId", item.CurrentOperationId),
                        ("@activeStatus", AuthoringStatusValues.Attempts.Active));
                }

                bool requiresAuthoring = string.IsNullOrWhiteSpace(item.AcceptedReceiptId);
                if (requiresAuthoring &&
                    !_retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount))
                {
                    int updated = await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_items
                        SET Status = @status,
                            CompletedAt = @completedAt,
                            PostPersistenceLeaseId = NULL,
                            PostPersistenceLeaseAcquiredAt = NULL
                        WHERE Id = @itemId AND Status = @expectedStatus
                        """,
                        ct,
                        ("@status", AuthoringStatusValues.Items.Superseded),
                        ("@completedAt", Format(completedAt)),
                        ("@itemId", item.Id),
                        ("@expectedStatus", AuthoringStatusValues.Items.Error));
                    supersededItems += updated;
                    continue;
                }

                DateTimeOffset dueAt = _retryPolicy.GetNextAutomaticRetryAt(completedAt);
                if (dueAt > timestamp)
                {
                    nextRetryAt = nextRetryAt is null || dueAt < nextRetryAt
                        ? dueAt
                        : nextRetryAt;
                    continue;
                }

                string nextStatus = requiresAuthoring
                    ? AuthoringStatusValues.Items.Pending
                    : AuthoringStatusValues.Items.Persisted;
                int transitioned = await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_items
                    SET Status = @status,
                        CurrentOperationId =
                            CASE WHEN @requiresAuthoring = 1 THEN NULL ELSE CurrentOperationId END,
                        PostPersistenceLeaseId = NULL,
                        PostPersistenceLeaseAcquiredAt = NULL,
                        StartedAt = CASE WHEN @requiresAuthoring = 1 THEN NULL ELSE StartedAt END,
                        CompletedAt = NULL,
                        Error = NULL
                    WHERE Id = @itemId AND Status = @expectedStatus
                    """,
                    ct,
                    ("@status", nextStatus),
                    ("@requiresAuthoring", requiresAuthoring),
                    ("@itemId", item.Id),
                    ("@expectedStatus", AuthoringStatusValues.Items.Error));
                if (requiresAuthoring)
                {
                    retriedItems += transitioned;
                }
                else
                {
                    resumedReceiptItems += transitioned;
                }
            }

            await CommitAsync(connection, ct);
            return new AuthoringErrorReconciliationResult(
                retriedItems,
                resumedReceiptItems,
                supersededItems,
                nextRetryAt);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRetryResult> RecoverOrphanedClaimAsync(
        string itemId,
        string operationId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunItemRecord item = await ReadRunItemAsync(connection, itemId, ct)
                ?? throw new KeyNotFoundException($"Authoring item '{itemId}' was not found.");
            AuthoringRunRecord run = await ReadRunAsync(connection, item.RunId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{item.RunId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, run.Id, ct);
            if (!string.Equals(item.Status, AuthoringStatusValues.Items.InProgress, StringComparison.Ordinal) ||
                !(string.Equals(item.PostPersistenceLeaseId, operationId, StringComparison.Ordinal) ||
                  (item.PostPersistenceLeaseId is null &&
                   string.Equals(item.CurrentOperationId, operationId, StringComparison.Ordinal))))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{operationId}' no longer owns item '{itemId}'.");
            }

            bool requiresAuthoring = string.IsNullOrWhiteSpace(item.AcceptedReceiptId);
            if (requiresAuthoring)
            {
                int attemptUpdates = await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_attempts
                    SET Status = @status,
                        CompletedAt = COALESCE(CompletedAt, @completedAt),
                        Error = COALESCE(Error, @error)
                    WHERE OperationId = @operationId AND Status = @activeStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Attempts.Error),
                    ("@completedAt", Format(timestamp)),
                    ("@error", "Authoring claim was abandoned before a receipt was accepted."),
                    ("@operationId", operationId),
                    ("@activeStatus", AuthoringStatusValues.Attempts.Active));
                if (attemptUpdates != 1)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StaleOperation,
                        $"Operation '{operationId}' no longer owns an active attempt for item '{itemId}'.");
                }
            }

            string nextStatus = !requiresAuthoring
                ? AuthoringStatusValues.Items.Persisted
                : _retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount)
                    ? AuthoringStatusValues.Items.Error
                    : AuthoringStatusValues.Items.Superseded;
            string? error = requiresAuthoring
                ? "Authoring claim was abandoned before a receipt was accepted."
                : null;
            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    StartedAt = CASE WHEN @requiresAuthoring = 1 THEN StartedAt ELSE NULL END,
                    CompletedAt = CASE WHEN @requiresAuthoring = 1 THEN @completedAt ELSE NULL END,
                    Error = @error
                WHERE Id = @itemId
                  AND (
                      PostPersistenceLeaseId = @claimId
                      OR (PostPersistenceLeaseId IS NULL AND CurrentOperationId = @claimId)
                  )
                  AND Status = @expectedStatus
                """,
                ct,
                ("@status", nextStatus),
                ("@requiresAuthoring", requiresAuthoring),
                ("@completedAt", Format(timestamp)),
                ("@error", error),
                ("@itemId", itemId),
                ("@claimId", operationId),
                ("@expectedStatus", AuthoringStatusValues.Items.InProgress));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{operationId}' lost item '{itemId}' during orphan recovery.");
            }

            await CommitAsync(connection, ct);
            return new AuthoringRetryResult(itemId, requiresAuthoring);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRunStageRecord> EnsureRunStageAsync(
        string runId,
        string stageName,
        string partitionKey,
        string inputFingerprint,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunStageRecord? existing = await ReadStageAsync(
                connection,
                runId,
                stageName,
                partitionKey,
                ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.InputFingerprint, inputFingerprint, StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageFingerprintMismatch,
                        $"Stage '{stageName}' partition '{partitionKey}' has fingerprint '{existing.InputFingerprint}', not '{inputFingerprint}'.");
                }

                await CommitAsync(connection, ct);
                return existing;
            }

            string id = Guid.NewGuid().ToString("N");
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_run_stages
                    (Id, RunId, StageName, PartitionKey, InputFingerprint, Status, AttemptCount, CreatedAt)
                VALUES
                    (@id, @runId, @stageName, @partitionKey, @inputFingerprint, @status, 0, @createdAt)
                """,
                ct,
                ("@id", id),
                ("@runId", runId),
                ("@stageName", stageName),
                ("@partitionKey", partitionKey),
                ("@inputFingerprint", inputFingerprint),
                ("@status", AuthoringStatusValues.Stages.Pending),
                ("@createdAt", Format(timestamp)));

            AuthoringRunStageRecord stage = await ReadStageAsync(connection, runId, stageName, partitionKey, ct)
                ?? throw new InvalidOperationException($"Failed to create stage '{stageName}'.");
            await CommitAsync(connection, ct);
            return stage;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringRunStageLease?> TryStartRunStageAsync(
        string stageId,
        TimeSpan? orphanedAfter = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        TimeSpan threshold = orphanedAfter ?? TimeSpan.FromMinutes(10);
        if (threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(orphanedAfter), "The orphaned stage threshold must be positive.");
        }

        string leaseId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE authoring_run_stages
            SET Status = @status,
                AttemptCount = AttemptCount + 1,
                StartedAt = @startedAt,
                LeaseId = @leaseId,
                LeaseAcquiredAt = @leaseAcquiredAt,
                CompletedAt = NULL,
                Error = NULL
            WHERE Id = @stageId
              AND (
                  Status IN (@pending, @error)
                  OR (
                      Status = @inProgress
                      AND (LeaseAcquiredAt IS NULL OR LeaseAcquiredAt <= @orphanedBefore)
                  )
              )
            RETURNING AttemptCount
            """;
        command.Parameters.AddWithValue("@status", AuthoringStatusValues.Stages.InProgress);
        command.Parameters.AddWithValue("@startedAt", Format(timestamp));
        command.Parameters.AddWithValue("@leaseId", leaseId);
        command.Parameters.AddWithValue("@leaseAcquiredAt", Format(timestamp));
        command.Parameters.AddWithValue("@stageId", stageId);
        command.Parameters.AddWithValue("@pending", AuthoringStatusValues.Stages.Pending);
        command.Parameters.AddWithValue("@error", AuthoringStatusValues.Stages.Error);
        command.Parameters.AddWithValue("@inProgress", AuthoringStatusValues.Stages.InProgress);
        command.Parameters.AddWithValue("@orphanedBefore", Format(timestamp - threshold));
        object? result = await command.ExecuteScalarAsync(ct);
        return result is null
            ? null
            : new AuthoringRunStageLease(
                stageId,
                leaseId,
                Convert.ToInt32(result, CultureInfo.InvariantCulture));
    }

    public async Task CompleteRunStageAsync(
        string stageId,
        string leaseId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_stages
            SET Status = @status,
                CompletedAt = @completedAt,
                LeaseId = NULL,
                LeaseAcquiredAt = NULL,
                Error = NULL
            WHERE Id = @stageId AND Status = @expectedStatus AND LeaseId = @leaseId
            """,
            ct,
            ("@status", AuthoringStatusValues.Stages.Complete),
            ("@completedAt", Format(timestamp)),
            ("@stageId", stageId),
            ("@leaseId", leaseId),
            ("@expectedStatus", AuthoringStatusValues.Stages.InProgress));
        if (updated != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageLeaseLost,
                $"Stage '{stageId}' is no longer owned by lease '{leaseId}'.");
        }
    }

    public async Task FailRunStageAsync(
        string stageId,
        string leaseId,
        string error,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_run_stages
            SET Status = @status,
                CompletedAt = @completedAt,
                LeaseId = NULL,
                LeaseAcquiredAt = NULL,
                Error = @error
            WHERE Id = @stageId AND Status = @expectedStatus AND LeaseId = @leaseId
            """,
            ct,
            ("@status", AuthoringStatusValues.Stages.Error),
            ("@completedAt", Format(timestamp)),
            ("@error", TruncateError(error)),
            ("@stageId", stageId),
            ("@leaseId", leaseId),
            ("@expectedStatus", AuthoringStatusValues.Stages.InProgress));
        if (updated != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageLeaseLost,
                $"Stage '{stageId}' is no longer owned by lease '{leaseId}'.");
        }
    }

    public async Task<IReadOnlyList<AuthoringRunStageRecord>> GetRunStagesAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunId, StageName, PartitionKey, InputFingerprint, Status,
                   AttemptCount, LeaseId, LeaseAcquiredAt, CreatedAt, StartedAt, CompletedAt, Error
            FROM authoring_run_stages
            WHERE RunId = @runId
            ORDER BY RowId
            """;
        command.Parameters.AddWithValue("@runId", runId);

        List<AuthoringRunStageRecord> stages = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stages.Add(ReadStage(reader));
        }
        return stages;
    }

    public async Task<bool> AllItemsCompleteAsync(string runId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_run_items
            WHERE RunId = @runId AND Status NOT IN (@complete, @superseded)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0;
    }

    public async Task MarkRunFinalizingAsync(
        string runId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, runId, ct);
            if (string.Equals(run.Status, AuthoringStatusValues.Runs.Finalizing, StringComparison.Ordinal))
            {
                await CommitAsync(connection, ct);
                return;
            }

            AuthoringStatusValues.EnsureRunTransition(run.Status, AuthoringStatusValues.Runs.Finalizing);
            if (await CountIncompleteItemsAsync(connection, runId, ct) != 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' still has incomplete items.");
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_runs
                SET Status = @status,
                    StartedAt = COALESCE(StartedAt, @startedAt),
                    CompletedAt = NULL,
                    Error = NULL
                WHERE Id = @runId AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Runs.Finalizing),
                ("@startedAt", Format(timestamp)),
                ("@runId", runId),
                ("@expectedStatus", run.Status));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' changed before finalization could start.");
            }

            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task MarkRunErrorAsync(
        string runId,
        string error,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, runId, ct);
            if (!string.Equals(run.Status, AuthoringStatusValues.Runs.Error, StringComparison.Ordinal))
            {
                AuthoringStatusValues.EnsureRunTransition(run.Status, AuthoringStatusValues.Runs.Error);
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_runs
                SET Status = @status, CompletedAt = @completedAt, Error = @error
                WHERE Id = @runId AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Runs.Error),
                ("@completedAt", Format(timestamp)),
                ("@error", TruncateError(error)),
                ("@runId", runId),
                ("@expectedStatus", run.Status));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' changed before its error could be recorded.");
            }

            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task SupersedeRunAsync(
        string runId,
        string reason,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            if (string.Equals(run.Status, AuthoringStatusValues.Runs.Superseded, StringComparison.Ordinal))
            {
                await CommitAsync(connection, ct);
                return;
            }

            AuthoringStatusValues.EnsureRunTransition(run.Status, AuthoringStatusValues.Runs.Superseded);
            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_attempts
                SET Status = @superseded,
                    CompletedAt = COALESCE(CompletedAt, @completedAt),
                    Error = COALESCE(Error, @error)
                WHERE RunId = @runId AND Status = @active
                """,
                ct,
                ("@superseded", AuthoringStatusValues.Attempts.Superseded),
                ("@completedAt", Format(timestamp)),
                ("@error", TruncateError(reason)),
                ("@runId", runId),
                ("@active", AuthoringStatusValues.Attempts.Active));
            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @errorStatus,
                    CompletedAt = @completedAt,
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    Error = @error
                WHERE RunId = @runId AND Status <> @completeStatus
                """,
                ct,
                ("@errorStatus", AuthoringStatusValues.Items.Error),
                ("@completedAt", Format(timestamp)),
                ("@error", TruncateError(reason)),
                ("@runId", runId),
                ("@completeStatus", AuthoringStatusValues.Items.Complete));

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_runs
                SET Status = @status, CompletedAt = @completedAt, Error = @error
                WHERE Id = @runId AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Runs.Superseded),
                ("@completedAt", Format(timestamp)),
                ("@error", TruncateError(reason)),
                ("@runId", runId),
                ("@expectedStatus", run.Status));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' changed before it could be superseded.");
            }

            await ExecuteAsync(
                connection,
                "DELETE FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind AND RunId = @runId",
                ct,
                ("@processorKind", run.ProcessorKind),
                ("@runId", runId));
            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringItemSupersedeResult> SupersedeErroredItemAsync(
        string runId,
        string itemId,
        string reason,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string normalizedReason = reason.Trim();
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, run.Id, ct);
            AuthoringRunItemRecord item = await ReadRunItemAsync(connection, itemId, ct)
                ?? throw new KeyNotFoundException($"Authoring item '{itemId}' was not found.");
            if (!string.Equals(item.RunId, runId, StringComparison.Ordinal))
            {
                throw new KeyNotFoundException(
                    $"Authoring item '{itemId}' was not found in run '{runId}'.");
            }
            if (!string.Equals(
                    item.Status,
                    AuthoringStatusValues.Items.Error,
                    StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.ItemNotClaimable,
                    $"Item '{itemId}' cannot be superseded from status '{item.Status}'.");
            }
            if (!string.IsNullOrWhiteSpace(item.AcceptedReceiptId))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.ItemNotClaimable,
                    $"Item '{itemId}' has an accepted receipt and cannot be superseded.");
            }

            if (item.CurrentOperationId is not null)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_attempts
                    SET Status = @status,
                        CompletedAt = COALESCE(CompletedAt, @completedAt),
                        Error = COALESCE(Error, @error)
                    WHERE OperationId = @operationId AND Status = @activeStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Attempts.Error),
                    ("@completedAt", Format(item.CompletedAt ?? timestamp)),
                    ("@error", item.Error),
                    ("@operationId", item.CurrentOperationId),
                    ("@activeStatus", AuthoringStatusValues.Attempts.Active));
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    CompletedAt = @completedAt,
                    PostPersistenceLeaseId = NULL,
                    PostPersistenceLeaseAcquiredAt = NULL,
                    Error = @reason
                WHERE Id = @itemId
                  AND RunId = @runId
                  AND Status = @expectedStatus
                  AND AcceptedReceiptId IS NULL
                """,
                ct,
                ("@status", AuthoringStatusValues.Items.Superseded),
                ("@completedAt", Format(timestamp)),
                ("@reason", TruncateError(normalizedReason)),
                ("@itemId", itemId),
                ("@runId", runId),
                ("@expectedStatus", AuthoringStatusValues.Items.Error));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.ItemNotClaimable,
                    $"Item '{itemId}' changed before it could be superseded.");
            }

            await CommitAsync(connection, ct);
            return new AuthoringItemSupersedeResult(
                runId,
                itemId,
                AuthoringStatusValues.Items.Superseded,
                normalizedReason);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<bool> SupersedeRunItemsAsync(
        string runId,
        IReadOnlyCollection<string> itemIds,
        string reason,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (itemIds.Count == 0)
        {
            return false;
        }

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            string[] ids = itemIds.Distinct(StringComparer.Ordinal).ToArray();
            string placeholders = string.Join(", ", ids.Select((_, index) => $"@item{index}"));

            await using (SqliteCommand attempts = connection.CreateCommand())
            {
                attempts.CommandText =
                    $"""
                    UPDATE authoring_run_attempts
                    SET Status = @superseded,
                        CompletedAt = COALESCE(CompletedAt, @completedAt),
                        Error = COALESCE(Error, @error)
                    WHERE RunItemId IN ({placeholders}) AND Status = @active
                    """;
                attempts.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Attempts.Superseded);
                attempts.Parameters.AddWithValue("@completedAt", Format(timestamp));
                attempts.Parameters.AddWithValue("@error", TruncateError(reason));
                attempts.Parameters.AddWithValue("@active", AuthoringStatusValues.Attempts.Active);
                AddItemIdParameters(attempts, ids);
                await attempts.ExecuteNonQueryAsync(ct);
            }

            await using (SqliteCommand items = connection.CreateCommand())
            {
                items.CommandText =
                    $"""
                    UPDATE authoring_run_items
                    SET Status = @superseded,
                        CompletedAt = @completedAt,
                        PostPersistenceLeaseId = NULL,
                        PostPersistenceLeaseAcquiredAt = NULL,
                        Error = @error
                    WHERE RunId = @runId
                      AND Id IN ({placeholders})
                      AND AcceptedReceiptId IS NULL
                      AND Status <> @complete
                    """;
                items.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
                items.Parameters.AddWithValue("@completedAt", Format(timestamp));
                items.Parameters.AddWithValue("@error", TruncateError(reason));
                items.Parameters.AddWithValue("@runId", runId);
                items.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
                AddItemIdParameters(items, ids);
                await items.ExecuteNonQueryAsync(ct);
            }

            await using SqliteCommand remaining = connection.CreateCommand();
            remaining.CommandText =
                """
                SELECT COUNT(*)
                FROM authoring_run_items
                WHERE RunId = @runId AND Status <> @superseded
                """;
            remaining.Parameters.AddWithValue("@runId", runId);
            remaining.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
            bool wholeRunSuperseded =
                Convert.ToInt32(await remaining.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0;
            if (wholeRunSuperseded)
            {
                AuthoringStatusValues.EnsureRunTransition(
                    run.Status,
                    AuthoringStatusValues.Runs.Superseded);
                int updated = await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_runs
                    SET Status = @status, CompletedAt = @completedAt, Error = @error
                    WHERE Id = @runId AND Status = @expectedStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Runs.Superseded),
                    ("@completedAt", Format(timestamp)),
                    ("@error", TruncateError(reason)),
                    ("@runId", runId),
                    ("@expectedStatus", run.Status));
                if (updated != 1)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.RunNotActive,
                        $"Run '{runId}' changed before it could be superseded.");
                }

                await ExecuteAsync(
                    connection,
                    "DELETE FROM authoring_mutation_fences WHERE ProcessorKind = @processorKind AND RunId = @runId",
                    ct,
                    ("@processorKind", run.ProcessorKind),
                    ("@runId", runId));
            }

            await CommitAsync(connection, ct);
            return wholeRunSuperseded;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task CompleteRunAsync(
        string runId,
        string? snapshotId,
        DateTimeOffset? now = null,
        Func<SqliteConnection, CancellationToken, Task>? completionGuard = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            string status = run.DatabaseOnly
                ? AuthoringStatusValues.Runs.CompletedDatabaseOnly
                : AuthoringStatusValues.Runs.Completed;

            if (run.Status is AuthoringStatusValues.Runs.Completed or AuthoringStatusValues.Runs.CompletedDatabaseOnly)
            {
                if (!string.Equals(run.Status, status, StringComparison.Ordinal) ||
                    !string.Equals(run.SnapshotId, snapshotId, StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.RunNotActive,
                        $"Run '{runId}' is already complete with a different outcome.");
                }
                await CommitAsync(connection, ct);
                return;
            }

            AuthoringStatusValues.EnsureRunTransition(run.Status, status);
            if (!string.Equals(run.Status, AuthoringStatusValues.Runs.Finalizing, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' cannot complete from status '{run.Status}'.");
            }

            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, runId, ct);
            if (await CountIncompleteItemsAsync(connection, runId, ct) != 0 ||
                await CountIncompleteStagesAsync(connection, runId, ct) != 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' still has incomplete items or stages.");
            }

            if (run.DatabaseOnly)
            {
                if (snapshotId is not null)
                {
                    throw new InvalidOperationException(
                        $"Database-only run '{runId}' cannot complete with a snapshot.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(snapshotId))
                {
                    throw new InvalidOperationException($"Run '{runId}' requires a ready snapshot.");
                }

                AuthoringReviewSnapshotRecord snapshot = await ReadSnapshotAsync(connection, snapshotId, ct)
                    ?? throw new InvalidOperationException($"Snapshot '{snapshotId}' was not found.");
                bool snapshotMatches = string.Equals(
                        snapshot.Status,
                        AuthoringStatusValues.Snapshots.Ready,
                        StringComparison.Ordinal) &&
                    string.Equals(snapshot.RunId, runId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.ProcessorKind, run.ProcessorKind, StringComparison.Ordinal) &&
                    snapshot.AuthoringEpoch == run.AuthoringEpoch;
                if (!snapshotMatches)
                {
                    throw new InvalidOperationException(
                        $"Snapshot '{snapshotId}' is not a ready snapshot for run '{runId}'.");
                }
            }

            if (completionGuard is not null)
            {
                await completionGuard(connection, ct);
            }

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_runs
                SET Status = @status, SnapshotId = @snapshotId, CompletedAt = @completedAt, Error = NULL
                WHERE Id = @runId AND Status = @expectedStatus
                """,
                ct,
                ("@status", status),
                ("@snapshotId", snapshotId),
                ("@completedAt", Format(timestamp)),
                ("@runId", runId),
                ("@expectedStatus", AuthoringStatusValues.Runs.Finalizing));
            if (updated != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' changed before completion.");
            }

            int released = await ExecuteAsync(
                connection,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind AND RunId = @runId
                """,
                ct,
                ("@processorKind", run.ProcessorKind),
                ("@runId", runId));
            if (released != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.MutationFenceUnavailable,
                    $"Run '{runId}' lost its mutation fence before completion.");
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE authoring_processor_modes
                SET RevalidationRequired = 0,
                    RevalidationRunId = NULL,
                    UpdatedAt = @updatedAt
                WHERE ProcessorKind = @processorKind
                  AND RevalidationRequired = 1
                  AND RevalidationRunId = @runId
                """,
                ct,
                ("@updatedAt", Format(timestamp)),
                ("@processorKind", run.ProcessorKind),
                ("@runId", runId));

            await CommitAsync(connection, ct);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task<AuthoringReviewSnapshotRecord> BeginSnapshotAsync(
        string processorKind,
        string runId,
        int schemaVersion,
        string tempPath,
        string finalPath,
        int itemCount,
        int receiptCount,
        IReadOnlyDictionary<string, long> tableCounts,
        DateTimeOffset? now = null,
        CancellationToken ct = default,
        AuthoringSnapshotPublicationProof? publicationProof = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(tableCounts);
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;

        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            if (!string.Equals(run.ProcessorKind, processorKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Run '{runId}' does not belong to '{processorKind}'.");
            }

            await EnsureFenceOwnedAsync(connection, processorKind, runId, ct);
            if (!string.Equals(run.Status, AuthoringStatusValues.Runs.Finalizing, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' cannot create a snapshot from status '{run.Status}'.");
            }
            if (await CountIncompleteItemsAsync(connection, runId, ct) != 0 ||
                await CountIncompleteStagesAsync(connection, runId, ct) != 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' still has incomplete items or stages.");
            }

            long sequence = await GetNextSnapshotSequenceAsync(connection, processorKind, ct);
            string id = Guid.NewGuid().ToString("N");
            string countsJson = JsonSerializer.Serialize(tableCounts);
            string? publicationProofJson = publicationProof is null
                ? null
                : JsonSerializer.Serialize(
                    publicationProof,
                    JsonSerializerOptions.Web);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_review_snapshots
                    (Id, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion, Status,
                     TempPath, Path, SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                     PublicationProofJson, CreatedAt)
                VALUES
                    (@id, @processorKind, @runId, @authoringEpoch, @sequence, @schemaVersion, @status,
                     @tempPath, @path, 0, @itemCount, @receiptCount, @tableCountsJson,
                     @publicationProofJson, @createdAt)
                """,
                ct,
                ("@id", id),
                ("@processorKind", processorKind),
                ("@runId", runId),
                ("@authoringEpoch", run.AuthoringEpoch),
                ("@sequence", sequence),
                ("@schemaVersion", schemaVersion),
                ("@status", AuthoringStatusValues.Snapshots.Creating),
                ("@tempPath", tempPath),
                ("@path", finalPath),
                ("@itemCount", itemCount),
                ("@receiptCount", receiptCount),
                ("@tableCountsJson", countsJson),
                ("@publicationProofJson", publicationProofJson),
                ("@createdAt", Format(timestamp)));

            AuthoringReviewSnapshotRecord record = await ReadSnapshotAsync(connection, id, ct)
                ?? throw new InvalidOperationException($"Failed to create snapshot record '{id}'.");
            await CommitAsync(connection, ct);
            return record;
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task MarkSnapshotPromotedAsync(
        string snapshotId,
        string checksumSha256,
        long sizeBytes,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_review_snapshots
            SET Status = @status,
                ChecksumSha256 = @checksum,
                SizeBytes = @sizeBytes,
                FinalizedAt = @finalizedAt,
                Error = NULL
            WHERE Id = @snapshotId AND Status IN (@creating, @promoted)
            """,
            ct,
            ("@status", AuthoringStatusValues.Snapshots.Promoted),
            ("@checksum", checksumSha256),
            ("@sizeBytes", sizeBytes),
            ("@finalizedAt", Format(timestamp)),
            ("@snapshotId", snapshotId),
            ("@creating", AuthoringStatusValues.Snapshots.Creating),
            ("@promoted", AuthoringStatusValues.Snapshots.Promoted));
        if (updated != 1)
        {
            throw new InvalidOperationException($"Snapshot '{snapshotId}' is not awaiting promotion.");
        }
    }

    public async Task UpdateSnapshotTableCountsAsync(
        string snapshotId,
        IReadOnlyDictionary<string, long> tableCounts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableCounts);
        await using SqliteConnection connection = _openConnection();
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE authoring_review_snapshots
            SET TableCountsJson = @tableCountsJson
            WHERE Id = @snapshotId AND Status = @status
            """,
            ct,
            ("@tableCountsJson", JsonSerializer.Serialize(tableCounts)),
            ("@snapshotId", snapshotId),
            ("@status", AuthoringStatusValues.Snapshots.Creating));
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Snapshot '{snapshotId}' is not in the creating state.");
        }
    }

    public async Task<AuthoringSnapshotDescriptor> MarkSnapshotReadyAsync(
        string snapshotId,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringReviewSnapshotRecord record = await ReadSnapshotAsync(connection, snapshotId, ct)
                ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' was not found.");
            if (string.IsNullOrWhiteSpace(record.ChecksumSha256))
            {
                throw new InvalidOperationException($"Snapshot '{snapshotId}' has no checksum.");
            }

            if (!string.Equals(record.Status, AuthoringStatusValues.Snapshots.Ready, StringComparison.Ordinal))
            {
                int updated = await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_review_snapshots
                    SET Status = @status, FinalizedAt = @finalizedAt, Error = NULL
                    WHERE Id = @snapshotId AND Status = @expectedStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Snapshots.Ready),
                    ("@finalizedAt", Format(timestamp)),
                    ("@snapshotId", snapshotId),
                    ("@expectedStatus", AuthoringStatusValues.Snapshots.Promoted));
                if (updated != 1)
                {
                    throw new InvalidOperationException($"Snapshot '{snapshotId}' is not promoted.");
                }
                record.Status = AuthoringStatusValues.Snapshots.Ready;
                record.FinalizedAt = timestamp;
            }

            await CommitAsync(connection, ct);
            return ToDescriptor(record);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    public async Task MarkSnapshotErrorAsync(
        string snapshotId,
        string error,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await ExecuteAsync(
            connection,
            """
            UPDATE authoring_review_snapshots
            SET Status = @status, FinalizedAt = @finalizedAt, Error = @error
            WHERE Id = @snapshotId
            """,
            ct,
            ("@status", AuthoringStatusValues.Snapshots.Error),
            ("@finalizedAt", Format(timestamp)),
            ("@error", TruncateError(error)),
            ("@snapshotId", snapshotId));
    }

    public async Task<IReadOnlyList<AuthoringReviewSnapshotRecord>> GetSnapshotRecordsAsync(
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion, Status,
                   TempPath, Path, ChecksumSha256, SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                   CreatedAt, FinalizedAt, Error, PublicationProofJson
            FROM authoring_review_snapshots
            ORDER BY ProcessorKind, Sequence
            """;

        List<AuthoringReviewSnapshotRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(ReadSnapshot(reader));
        }
        return records;
    }

    public async Task<AuthoringSnapshotDescriptor?> GetSnapshotDescriptorAsync(
        string snapshotId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = _openConnection();
        AuthoringReviewSnapshotRecord? record = await ReadSnapshotAsync(connection, snapshotId, ct);
        return record is not null &&
            string.Equals(record.Status, AuthoringStatusValues.Snapshots.Ready, StringComparison.Ordinal)
            ? ToDescriptor(record)
            : null;
    }

    public async Task<AuthoringReviewSnapshotRecord?> GetReadySnapshotRecordAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await using SqliteConnection connection = _openConnection();
        AuthoringRunRecord? run = await ReadRunAsync(connection, runId, ct);
        if (run?.SnapshotId is null)
        {
            return null;
        }

        AuthoringReviewSnapshotRecord? snapshot =
            await ReadSnapshotAsync(connection, run.SnapshotId, ct);
        return snapshot is not null &&
            string.Equals(snapshot.RunId, runId, StringComparison.Ordinal) &&
            string.Equals(
                snapshot.Status,
                AuthoringStatusValues.Snapshots.Ready,
                StringComparison.Ordinal)
            ? snapshot
            : null;
    }

    private async Task<AuthoringOperationClaim?> ClaimItemInternalAsync(
        string runId,
        string? itemId,
        DateTimeOffset? now,
        CancellationToken ct)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        await using SqliteConnection connection = _openConnection();
        await BeginImmediateAsync(connection, ct);
        try
        {
            AuthoringRunRecord run = await ReadRunAsync(connection, runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            if (!string.Equals(run.Status, AuthoringStatusValues.Runs.Running, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RunNotActive,
                    $"Run '{runId}' is not running.");
            }
            await EnsureFenceOwnedAsync(connection, run.ProcessorKind, runId, ct);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT RowId, Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                       CurrentOperationId, AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt,
                       CompletedAt, Error, PostPersistenceLeaseId, PostPersistenceLeaseAcquiredAt
                FROM authoring_run_items
                WHERE RunId = @runId
                  AND Status = @status
                  {(itemId is null ? "" : "AND Id = @itemId")}
                ORDER BY RowId
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue("@status", AuthoringStatusValues.Items.Pending);
            if (itemId is not null)
            {
                command.Parameters.AddWithValue("@itemId", itemId);
            }

            AuthoringRunItemRecord? item;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct))
            {
                item = await reader.ReadAsync(ct) ? ReadRunItem(reader) : null;
            }

            if (item is null)
            {
                await CommitAsync(connection, ct);
                return null;
            }

            if (!_retryPolicy.CanStartAnotherAuthoringAttempt(item.AttemptCount))
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE authoring_run_items
                    SET Status = @status,
                        CompletedAt = @completedAt,
                        Error = COALESCE(Error, @error)
                    WHERE Id = @itemId AND Status = @expectedStatus
                    """,
                    ct,
                    ("@status", AuthoringStatusValues.Items.Superseded),
                    ("@completedAt", Format(timestamp)),
                    ("@error", $"Authoring attempt limit of {_retryPolicy.MaxAttempts} was reached."),
                    ("@itemId", item.Id),
                    ("@expectedStatus", AuthoringStatusValues.Items.Pending));
                await CommitAsync(connection, ct);
                return null;
            }

            IssuedAuthoringOperationToken issued = AuthoringOperationTokenVerifier.Issue();
            string operationId = Guid.NewGuid().ToString("N");
            int attemptNumber = checked(item.AttemptCount + 1);

            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE authoring_run_items
                SET Status = @status,
                    CurrentOperationId = @operationId,
                    AttemptCount = @attemptCount,
                    StartedAt = @startedAt,
                    CompletedAt = NULL,
                    Error = NULL
                WHERE Id = @itemId AND Status = @expectedStatus
                """,
                ct,
                ("@status", AuthoringStatusValues.Items.InProgress),
                ("@operationId", operationId),
                ("@attemptCount", attemptNumber),
                ("@startedAt", Format(timestamp)),
                ("@itemId", item.Id),
                ("@expectedStatus", AuthoringStatusValues.Items.Pending));
            if (updated != 1)
            {
                await RollbackAsync(connection);
                return null;
            }

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_run_attempts
                    (OperationId, RunId, RunItemId, AttemptNumber, TokenVerifier, Status, CreatedAt)
                VALUES
                    (@operationId, @runId, @runItemId, @attemptNumber, @tokenVerifier, @status, @createdAt)
                """,
                ct,
                ("@operationId", operationId),
                ("@runId", runId),
                ("@runItemId", item.Id),
                ("@attemptNumber", attemptNumber),
                ("@tokenVerifier", issued.Verifier),
                ("@status", AuthoringStatusValues.Attempts.Active),
                ("@createdAt", Format(timestamp)));

            await CommitAsync(connection, ct);
            return new AuthoringOperationClaim(
                runId,
                item.Id,
                item.BusinessKey,
                item.ItemKind,
                item.ExpectedSourceRevision,
                operationId,
                issued.Token,
                attemptNumber);
        }
        catch
        {
            await RollbackAsync(connection);
            throw;
        }
    }

    private static async Task ValidateMaintenanceSourceAsync(
        SqliteConnection connection,
        string processorKind,
        string purpose,
        string? sourceRunId,
        CancellationToken ct)
    {
        if (sourceRunId is null)
        {
            return;
        }

        AuthoringRunRecord sourceRun =
            await ReadRunAsync(connection, sourceRunId, ct)
            ?? throw new KeyNotFoundException(
                $"Source authoring run '{sourceRunId}' was not found.");
        if (!string.Equals(
                sourceRun.ProcessorKind,
                processorKind,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Source authoring run '{sourceRunId}' belongs to processor '{sourceRun.ProcessorKind}', not '{processorKind}'.",
                nameof(sourceRunId));
        }
        if (!string.Equals(
                purpose,
                AuthoringRunPurposeValues.PublicationRefresh,
                StringComparison.Ordinal))
        {
            return;
        }
        if (!string.Equals(
                sourceRun.Status,
                AuthoringStatusValues.Runs.Completed,
                StringComparison.Ordinal) ||
            sourceRun.DatabaseOnly ||
            string.IsNullOrWhiteSpace(sourceRun.SnapshotId))
        {
            throw new ArgumentException(
                $"Source authoring run '{sourceRunId}' is not a completed snapshot-producing run.",
                nameof(sourceRunId));
        }

        AuthoringReviewSnapshotRecord? snapshot =
            await ReadSnapshotAsync(connection, sourceRun.SnapshotId, ct);
        if (snapshot is null ||
            !string.Equals(
                snapshot.ProcessorKind,
                processorKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.RunId,
                sourceRunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                snapshot.Status,
                AuthoringStatusValues.Snapshots.Ready,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.ChecksumSha256))
        {
            throw new ArgumentException(
                $"Source authoring run '{sourceRunId}' does not have a ready snapshot descriptor.",
                nameof(sourceRunId));
        }
    }

    private static void EnsureRunBacked(AuthoringProcessorModeRecord mode)
    {
        if (string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.Legacy, StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.AuthoringNotActivated,
                $"Authoring is not activated for processor '{mode.ProcessorKind}'.");
        }
        if (string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.CutoverInProgress,
                $"Authoring cutover is in progress for processor '{mode.ProcessorKind}'.");
        }
        if (!string.Equals(mode.Mode, AuthoringStatusValues.ProcessorModes.RunBacked, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown authoring mode '{mode.Mode}'.");
        }
    }

    private static async Task EnsureFenceOwnedAsync(
        SqliteConnection connection,
        string processorKind,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM authoring_mutation_fences
            WHERE ProcessorKind = @processorKind AND RunId = @runId
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        command.Parameters.AddWithValue("@runId", runId);
        if (await command.ExecuteScalarAsync(ct) is null)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.MutationFenceUnavailable,
                $"Run '{runId}' does not own the '{processorKind}' mutation fence.");
        }
    }

    private static void EnsureReplayMatches(
        AuthoringResultReceiptRecord accepted,
        AuthoringResultSubmission submission)
    {
        bool same = string.Equals(accepted.RunId, submission.RunId, StringComparison.Ordinal) &&
            string.Equals(accepted.RunItemId, submission.ItemId, StringComparison.Ordinal) &&
            string.Equals(accepted.OperationId, submission.OperationId, StringComparison.Ordinal) &&
            string.Equals(accepted.ContentHash, submission.ContentHash, StringComparison.Ordinal) &&
            string.Equals(accepted.ObservedSourceRevision, submission.ObservedSourceRevision, StringComparison.Ordinal);
        if (!same)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.ContentChanged,
                $"Operation '{submission.OperationId}' was already accepted with different coordinates, content, or source revision.");
        }
    }

    private static async Task<long> GetNextSnapshotSequenceAsync(
        SqliteConnection connection,
        string processorKind,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(Sequence), 0) + 1 FROM authoring_review_snapshots WHERE ProcessorKind = @processorKind";
        command.Parameters.AddWithValue("@processorKind", processorKind);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountIncompleteItemsAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_run_items
            WHERE RunId = @runId AND Status NOT IN (@complete, @superseded)
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRevalidationAuthoringAttemptsAsync(
        SqliteConnection connection,
        string runId,
        AuthoringRunItemDefinition item,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE run_chain(Id) AS (
                SELECT @runId
                UNION ALL
                SELECT lineage.PreviousRunId
                FROM authoring_revalidation_lineage lineage
                INNER JOIN run_chain chain ON lineage.RunId = chain.Id
            )
            SELECT COUNT(*)
            FROM authoring_run_attempts attempt
            INNER JOIN authoring_run_items item
                ON item.Id = attempt.RunItemId
            INNER JOIN run_chain chain
                ON chain.Id = item.RunId
            WHERE item.BusinessKey = @businessKey COLLATE NOCASE
              AND item.ItemKind = @itemKind COLLATE NOCASE
              AND item.ExpectedSourceRevision = @expectedSourceRevision
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue(
            "@businessKey",
            item.BusinessKey);
        command.Parameters.AddWithValue("@itemKind", item.ItemKind);
        command.Parameters.AddWithValue(
            "@expectedSourceRevision",
            item.ExpectedSourceRevision);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountIncompleteStagesAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_run_stages
            WHERE RunId = @runId AND Status <> @complete
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Stages.Complete);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<AuthoringProcessorModeRecord?> ReadProcessorModeAsync(
        SqliteConnection connection,
        string processorKind,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, ProcessorKind, Mode, Epoch, RevalidationRequired, RevalidationRunId, UpdatedAt
            FROM authoring_processor_modes
            WHERE ProcessorKind = @processorKind
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new AuthoringProcessorModeRecord
        {
            RowId = reader.GetInt32(0),
            ProcessorKind = reader.GetString(1),
            Mode = reader.GetString(2),
            Epoch = reader.GetInt64(3),
            RevalidationRequired = reader.GetBoolean(4),
            RevalidationRunId = ReadNullableString(reader, 5),
            UpdatedAt = ParseDate(reader.GetString(6)),
        };
    }

    private static async Task<AuthoringRunRecord?> ReadRunAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                   CreatedAt, StartedAt, CompletedAt, Error, SnapshotId, RequestJson,
                   Purpose, SourceRunId
            FROM authoring_runs
            WHERE Id = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return ReadRun(reader);
    }

    private static async Task EnsureActiveRunCapacityAvailableAsync(
        SqliteConnection connection,
        string processorKind,
        int maxActiveRuns,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id
            FROM authoring_runs
            WHERE ProcessorKind = @processorKind
              AND Status IN (@queued, @running, @finalizing, @error)
            ORDER BY CreatedAt, RowId
            LIMIT @maxActiveRuns
            """;
        command.Parameters.AddWithValue("@processorKind", processorKind);
        command.Parameters.AddWithValue("@queued", AuthoringStatusValues.Runs.Queued);
        command.Parameters.AddWithValue("@running", AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue("@finalizing", AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue("@error", AuthoringStatusValues.Runs.Error);
        command.Parameters.AddWithValue("@maxActiveRuns", maxActiveRuns);

        List<string> activeRunIds = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            activeRunIds.Add(reader.GetString(0));
        }

        if (activeRunIds.Count >= maxActiveRuns)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.ActiveRunCapacityReached,
                $"Processor '{processorKind}' has reached its active authoring run capacity of {maxActiveRuns}. Active run IDs: {string.Join(", ", activeRunIds)}.",
                activeRunIds);
        }
    }

    private static AuthoringRunRecord ReadRun(SqliteDataReader reader)
        => new()
        {
            RowId = reader.GetInt32(0),
            Id = reader.GetString(1),
            ProcessorKind = reader.GetString(2),
            AuthoringEpoch = reader.GetInt64(3),
            Status = reader.GetString(4),
            DatabaseOnly = reader.GetBoolean(5),
            TotalItems = reader.GetInt32(6),
            CreatedAt = ParseDate(reader.GetString(7)),
            StartedAt = ReadNullableDate(reader, 8),
            CompletedAt = ReadNullableDate(reader, 9),
            Error = ReadNullableString(reader, 10),
            SnapshotId = ReadNullableString(reader, 11),
            RequestJson = ReadNullableString(reader, 12),
            Purpose = reader.GetString(13),
            SourceRunId = ReadNullableString(reader, 14),
        };

    private static async Task<AuthoringRunItemRecord?> ReadRunItemAsync(
        SqliteConnection connection,
        string itemId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                   CurrentOperationId, AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt,
                   CompletedAt, Error, PostPersistenceLeaseId, PostPersistenceLeaseAcquiredAt
            FROM authoring_run_items
            WHERE Id = @itemId
            """;
        command.Parameters.AddWithValue("@itemId", itemId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRunItem(reader) : null;
    }

    private static AuthoringRunItemRecord ReadRunItem(SqliteDataReader reader)
        => new()
        {
            RowId = reader.GetInt32(0),
            Id = reader.GetString(1),
            RunId = reader.GetString(2),
            BusinessKey = reader.GetString(3),
            ItemKind = reader.GetString(4),
            ExpectedSourceRevision = reader.GetString(5),
            Status = reader.GetString(6),
            CurrentOperationId = ReadNullableString(reader, 7),
            AcceptedReceiptId = ReadNullableString(reader, 8),
            AttemptCount = reader.GetInt32(9),
            CreatedAt = ParseDate(reader.GetString(10)),
            StartedAt = ReadNullableDate(reader, 11),
            CompletedAt = ReadNullableDate(reader, 12),
            Error = ReadNullableString(reader, 13),
            PostPersistenceLeaseId = ReadNullableString(reader, 14),
            PostPersistenceLeaseAcquiredAt = ReadNullableDate(reader, 15),
        };

    private static async Task<AuthoringRunAttemptRecord?> ReadAttemptAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, OperationId, RunId, RunItemId, AttemptNumber, TokenVerifier, Status,
                   ContentHash, ObservedSourceRevision, CreatedAt, CompletedAt, Error
            FROM authoring_run_attempts
            WHERE OperationId = @operationId
            """;
        command.Parameters.AddWithValue("@operationId", operationId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new AuthoringRunAttemptRecord
        {
            RowId = reader.GetInt32(0),
            OperationId = reader.GetString(1),
            RunId = reader.GetString(2),
            RunItemId = reader.GetString(3),
            AttemptNumber = reader.GetInt32(4),
            TokenVerifier = reader.GetString(5),
            Status = reader.GetString(6),
            ContentHash = ReadNullableString(reader, 7),
            ObservedSourceRevision = ReadNullableString(reader, 8),
            CreatedAt = ParseDate(reader.GetString(9)),
            CompletedAt = ReadNullableDate(reader, 10),
            Error = ReadNullableString(reader, 11),
        };
    }

    private static async Task<AuthoringResultReceiptRecord?> ReadReceiptByOperationAsync(
        SqliteConnection connection,
        string operationId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                   ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt
            FROM authoring_result_receipts
            WHERE OperationId = @operationId
            """;
        command.Parameters.AddWithValue("@operationId", operationId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new AuthoringResultReceiptRecord
        {
            RowId = reader.GetInt32(0),
            Id = reader.GetString(1),
            OperationId = reader.GetString(2),
            RunId = reader.GetString(3),
            RunItemId = reader.GetString(4),
            BusinessKey = reader.GetString(5),
            ContentHash = reader.GetString(6),
            ExpectedSourceRevision = reader.GetString(7),
            ObservedSourceRevision = reader.GetString(8),
            AuthoringEpoch = reader.GetInt64(9),
            PersistedAt = ParseDate(reader.GetString(10)),
        };
    }

    private static async Task<AuthoringRunStageRecord?> ReadStageAsync(
        SqliteConnection connection,
        string runId,
        string stageName,
        string partitionKey,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunId, StageName, PartitionKey, InputFingerprint, Status,
                   AttemptCount, LeaseId, LeaseAcquiredAt, CreatedAt, StartedAt, CompletedAt, Error
            FROM authoring_run_stages
            WHERE RunId = @runId AND StageName = @stageName AND PartitionKey = @partitionKey
            """;
        command.Parameters.AddWithValue("@runId", runId);
        command.Parameters.AddWithValue("@stageName", stageName);
        command.Parameters.AddWithValue("@partitionKey", partitionKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStage(reader) : null;
    }

    private static AuthoringRunStageRecord ReadStage(SqliteDataReader reader)
        => new()
        {
            RowId = reader.GetInt32(0),
            Id = reader.GetString(1),
            RunId = reader.GetString(2),
            StageName = reader.GetString(3),
            PartitionKey = reader.GetString(4),
            InputFingerprint = reader.GetString(5),
            Status = reader.GetString(6),
            AttemptCount = reader.GetInt32(7),
            LeaseId = ReadNullableString(reader, 8),
            LeaseAcquiredAt = ReadNullableDate(reader, 9),
            CreatedAt = ParseDate(reader.GetString(10)),
            StartedAt = ReadNullableDate(reader, 11),
            CompletedAt = ReadNullableDate(reader, 12),
            Error = ReadNullableString(reader, 13),
        };

    private static async Task<AuthoringReviewSnapshotRecord?> ReadSnapshotAsync(
        SqliteConnection connection,
        string snapshotId,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion, Status,
                   TempPath, Path, ChecksumSha256, SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                   CreatedAt, FinalizedAt, Error, PublicationProofJson
            FROM authoring_review_snapshots
            WHERE Id = @snapshotId
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSnapshot(reader) : null;
    }

    private static AuthoringReviewSnapshotRecord ReadSnapshot(SqliteDataReader reader)
        => new()
        {
            RowId = reader.GetInt32(0),
            Id = reader.GetString(1),
            ProcessorKind = reader.GetString(2),
            RunId = reader.GetString(3),
            AuthoringEpoch = reader.GetInt64(4),
            Sequence = reader.GetInt64(5),
            SchemaVersion = reader.GetInt32(6),
            Status = reader.GetString(7),
            TempPath = reader.GetString(8),
            Path = reader.GetString(9),
            ChecksumSha256 = ReadNullableString(reader, 10),
            SizeBytes = reader.GetInt64(11),
            ItemCount = reader.GetInt32(12),
            ReceiptCount = reader.GetInt32(13),
            TableCountsJson = reader.GetString(14),
            CreatedAt = ParseDate(reader.GetString(15)),
            FinalizedAt = ReadNullableDate(reader, 16),
            Error = ReadNullableString(reader, 17),
            PublicationProofJson = ReadNullableString(reader, 18),
        };

    private static AuthoringResultReceipt ToContract(AuthoringResultReceiptRecord receipt)
        => new(
            receipt.Id,
            receipt.RunId,
            receipt.RunItemId,
            receipt.OperationId,
            receipt.BusinessKey,
            receipt.ContentHash,
            receipt.ExpectedSourceRevision,
            receipt.ObservedSourceRevision,
            receipt.AuthoringEpoch,
            receipt.PersistedAt);

    private static AuthoringSnapshotDescriptor ToDescriptor(AuthoringReviewSnapshotRecord record)
        => new(
            record.ProcessorKind,
            record.RunId,
            record.Id,
            record.AuthoringEpoch,
            record.Sequence,
            record.SchemaVersion,
            record.ChecksumSha256
                ?? throw new InvalidOperationException($"Snapshot '{record.Id}' has no checksum."),
            record.SizeBytes,
            record.ItemCount,
            record.ReceiptCount,
            JsonSerializer.Deserialize<Dictionary<string, long>>(record.TableCountsJson)
                ?? new Dictionary<string, long>(StringComparer.Ordinal),
            System.IO.Path.GetFileName(record.Path),
            record.CreatedAt,
            record.PublicationProofJson is null
                ? null
                : JsonSerializer.Deserialize<AuthoringSnapshotPublicationProof>(
                    record.PublicationProofJson,
                    JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException(
                        $"Snapshot '{record.Id}' has an empty publication proof."));

    internal static async Task InsertRunInputProvenanceAsync(
        SqliteConnection connection,
        string runId,
        IReadOnlyCollection<AuthoringRunInputProvenanceDefinition>? inputProvenance,
        DateTimeOffset capturedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (inputProvenance is null || inputProvenance.Count == 0)
        {
            return;
        }

        HashSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (AuthoringRunInputProvenanceDefinition definition in inputProvenance)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Source);
            string source = definition.Source.Trim();
            if (!sources.Add(source))
            {
                throw new ArgumentException(
                    $"Input provenance contains more than one definition for source '{source}'.",
                    nameof(inputProvenance));
            }

            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_run_input_provenance(
                    RunId, Source, LatestSuccessfulRefreshAt,
                    ContentRevision, CapturedAt)
                VALUES(
                    @runId, @source, @latestSuccessfulRefreshAt,
                    @contentRevision, @capturedAt)
                """,
                ct,
                ("@runId", runId),
                ("@source", source),
                ("@latestSuccessfulRefreshAt",
                    definition.LatestSuccessfulRefreshAt is null
                        ? null
                        : Format(definition.LatestSuccessfulRefreshAt.Value)),
                ("@contentRevision", definition.ContentRevision),
                ("@capturedAt", Format(capturedAt)));
        }
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

    private static void AddItemIdParameters(SqliteCommand command, IReadOnlyList<string> itemIds)
    {
        for (int index = 0; index < itemIds.Count; index++)
        {
            command.Parameters.AddWithValue($"@item{index}", itemIds[index]);
        }
    }

    private static Task BeginImmediateAsync(SqliteConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, "BEGIN IMMEDIATE", ct);

    private static Task CommitAsync(SqliteConnection connection, CancellationToken ct)
        => ExecuteAsync(connection, "COMMIT", ct);

    private static async Task RollbackAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
        }
        catch (SqliteException)
        {
        }
    }

    private static string Format(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string TruncateError(string error)
        => error.Length <= 4096 ? error : error[..4096];
}
