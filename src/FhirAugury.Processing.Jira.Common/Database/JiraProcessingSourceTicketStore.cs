using System.Globalization;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Database;

public sealed class JiraProcessingSourceTicketStore : IProcessingWorkItemStore<JiraProcessingSourceTicketRecord>
{
    private const string SourceTicketTable = "jira_processing_source_tickets";
    private const string CompositeIndexName = "idx_jira_processing_source_tickets_key_shape";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly Func<ResolvedJiraProcessingFilters> _filtersFactory;

    public string DatabasePath => _dbPath;

    public JiraProcessingSourceTicketStore(string dbPath, ResolvedJiraProcessingFilters? filters = null)
    {
        _dbPath = dbPath;
        _connectionString = CreateConnectionString(dbPath);
        _filtersFactory = () => filters ?? new ResolvedJiraProcessingFilters();
        EnsureSchema();
    }

    public JiraProcessingSourceTicketStore(
        IOptions<ProcessingServiceOptions> processingOptions,
        IOptions<JiraProcessingOptions> jiraOptions,
        JiraProcessingFilterResolver filterResolver)
    {
        _dbPath = processingOptions.Value.DatabasePath;
        _connectionString = CreateConnectionString(_dbPath);
        _filtersFactory = () => filterResolver.Resolve(jiraOptions.Value);
        EnsureSchema();
    }

    public Task<JiraProcessingSourceTicketRecord> UpsertAsync(
        JiraIssueSummaryEntry ticket,
        string sourceTicketShape,
        bool resetProcessingStatus,
        CancellationToken ct)
        => UpsertAsync(
            ticket,
            sourceTicketShape,
            resetProcessingStatus,
            sourceProjectLastSuccessfulRefreshAt: null,
            sourceContentRevision: null,
            ct: ct);

    public Task<JiraProcessingSourceTicketRecord> UpsertAsync(
        JiraIssueSummaryEntry ticket,
        string sourceTicketShape,
        bool resetProcessingStatus,
        SourceReadProvenance? provenance,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        DateTimeOffset? sourceProjectLastSuccessfulRefreshAt = null;
        long? sourceContentRevision = null;
        if (provenance is { IsStable: true } &&
            string.Equals(
                provenance.Source,
                "jira",
                StringComparison.OrdinalIgnoreCase) &&
            provenance.ProjectLastSuccessfulRefreshAt is not null)
        {
            sourceContentRevision = provenance.ContentRevision;
            foreach ((string project, DateTimeOffset? watermark) in
                     provenance.ProjectLastSuccessfulRefreshAt)
            {
                if (string.Equals(
                        project,
                        ticket.ProjectKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    sourceProjectLastSuccessfulRefreshAt = watermark;
                    break;
                }
            }
        }

        return UpsertAsync(
            ticket,
            sourceTicketShape,
            resetProcessingStatus,
            sourceProjectLastSuccessfulRefreshAt,
            sourceContentRevision,
            ct);
    }

    public async Task<JiraProcessingSourceTicketRecord> UpsertAsync(
        JiraIssueSummaryEntry ticket,
        string sourceTicketShape,
        bool resetProcessingStatus,
        DateTimeOffset? sourceProjectLastSuccessfulRefreshAt,
        long? sourceContentRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        sourceTicketShape = NormalizeSourceTicketShape(sourceTicketShape);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        JiraProcessingSourceTicketRecord? existing = await SelectByKeyAsync(connection, transaction, ticket.Key, sourceTicketShape, ct);
        if (existing is null)
        {
            JiraProcessingSourceTicketRecord inserted = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = sourceTicketShape == "fhir" ? UppercaseAscii(ticket.Key) : ticket.Key,
                Title = ticket.Title,
                Description = null,
                Project = ticket.ProjectKey,
                Status = ticket.Status,
                WorkGroup = ticket.WorkGroup,
                Type = ticket.Type,
                Specification = ticket.Specification,
                SourceTicketShape = sourceTicketShape,
                LastSyncedAt = now,
                LastUpdated = ticket.UpdatedAt,
                SourceProjectLastSuccessfulRefreshAt =
                    sourceProjectLastSuccessfulRefreshAt,
                SourceContentRevision = sourceContentRevision,
            };
            await InsertAsync(connection, transaction, inserted, ct);
            await transaction.CommitAsync(ct);
            return inserted;
        }

        existing.Title = ticket.Title;
        existing.Project = ticket.ProjectKey;
        existing.Status = ticket.Status;
        existing.WorkGroup = ticket.WorkGroup;
        existing.Type = ticket.Type;
        existing.Specification = ticket.Specification;
        existing.LastSyncedAt = now;
        existing.LastUpdated = ticket.UpdatedAt;
        existing.SourceProjectLastSuccessfulRefreshAt =
            sourceProjectLastSuccessfulRefreshAt;
        existing.SourceContentRevision = sourceContentRevision;
        if (resetProcessingStatus)
        {
            ClearProcessing(existing);
        }

        await UpdateAsync(connection, transaction, existing, ct);
        await transaction.CommitAsync(ct);
        return existing;
    }

    public async Task<JiraProcessingSourceTicketRecord?> ResetForReprocessingAsync(string key, string sourceTicketShape, CancellationToken ct)
    {
        sourceTicketShape = NormalizeSourceTicketShape(sourceTicketShape);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        JiraProcessingSourceTicketRecord? existing = await SelectByKeyAsync(connection, transaction, key, sourceTicketShape, ct);
        if (existing is null)
        {
            return null;
        }

        ClearProcessing(existing);
        await UpdateAsync(connection, transaction, existing, ct);
        await transaction.CommitAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<JiraProcessingSourceTicketRecord>> GetPendingAsync(int maxItems, CancellationToken ct)
    {
        ResolvedJiraProcessingFilters filters = _filtersFactory();
        Func<IJiraProcessingTicketFilterCandidate, bool> predicate = JiraStoredTicketFacetPredicateBuilder.Build(filters);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM jira_processing_source_tickets
            WHERE (ProcessingStatus IS NULL OR ProcessingStatus = @stale) AND SourceTicketShape = @shape
            ORDER BY LastUpdated ASC, Key ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@stale", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@shape", NormalizeSourceTicketShape(filters.SourceTicketShape));
        command.Parameters.AddWithValue("@limit", Math.Max(maxItems * 5, maxItems));
        List<JiraProcessingSourceTicketRecord> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            JiraProcessingSourceTicketRecord row = ReadRecord(reader);
            if (predicate(row))
            {
                rows.Add(row);
                if (rows.Count >= maxItems)
                {
                    break;
                }
            }
        }

        return rows;
    }

    public async Task<bool> ClaimItemAsync(JiraProcessingSourceTicketRecord item, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jira_processing_source_tickets
            SET ProcessingStatus = @status,
                StartedProcessingAt = @started,
                LastProcessingAttemptAt = @started,
                ProcessingAttemptCount = ProcessingAttemptCount + 1
            WHERE Id = @id AND (ProcessingStatus IS NULL OR ProcessingStatus = @stale)
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.InProgress);
        command.Parameters.AddWithValue("@stale", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@started", Format(startedAt));
        command.Parameters.AddWithValue("@id", item.Id);
        int affected = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        if (affected == 1)
        {
            item.ProcessingStatus = ProcessingStatusValues.InProgress;
            item.StartedProcessingAt = startedAt;
            item.LastProcessingAttemptAt = startedAt;
            item.ProcessingAttemptCount++;
            return true;
        }

        return false;
    }

    public async Task MarkCompleteAsync(JiraProcessingSourceTicketRecord item, DateTimeOffset completedAt, CancellationToken ct)
    {
        // CompletionId is stamped idempotently: the queue runner calls MarkCompleteAsync
        // a second time after the handler has already done so (ProcessingQueueRunner.cs:108),
        // so the UPDATE preserves the GUID stamped by the first call via COALESCE and the
        // SELECT re-binds whatever value actually landed on disk back onto `item`.
        string newCompletionId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jira_processing_source_tickets
            SET ProcessingStatus = @status,
                CompletedProcessingAt = @completed,
                CompletionId = COALESCE(CompletionId, @completionId),
                ProcessingError = NULL,
                ErrorMessage = NULL,
                AgentExitCode = NULL,
                ErrorOccurredAt = NULL
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Complete);
        command.Parameters.AddWithValue("@completed", Format(completedAt));
        command.Parameters.AddWithValue("@completionId", newCompletionId);
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT CompletionId FROM jira_processing_source_tickets WHERE Id = @id";
        select.Parameters.AddWithValue("@id", item.Id);
        object? stored = await select.ExecuteScalarAsync(ct);
        item.CompletionId = stored is string s ? s : newCompletionId;
        item.ProcessingStatus = ProcessingStatusValues.Complete;
        item.CompletedProcessingAt = completedAt;
        item.ProcessingError = null;
        item.ErrorMessage = null;
        item.AgentExitCode = null;
        item.ErrorOccurredAt = null;
    }

    public Task MarkErrorAsync(JiraProcessingSourceTicketRecord item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct) =>
        MarkErrorAsync(item, errorMessage, item.AgentExitCode, completedAt, ct);

    public async Task MarkErrorAsync(JiraProcessingSourceTicketRecord item, string errorMessage, int? agentExitCode, DateTimeOffset completedAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jira_processing_source_tickets
            SET ProcessingStatus = @status,
                CompletedProcessingAt = @completed,
                CompletionId = NULL,
                ProcessingError = @error,
                ErrorMessage = @error,
                AgentExitCode = @exitCode,
                ErrorOccurredAt = @completed
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Error);
        command.Parameters.AddWithValue("@completed", Format(completedAt));
        command.Parameters.AddWithValue("@error", errorMessage);
        command.Parameters.AddWithValue("@exitCode", (object?)agentExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);
        item.ProcessingStatus = ProcessingStatusValues.Error;
        item.CompletedProcessingAt = completedAt;
        item.CompletionId = null;
        item.ProcessingError = errorMessage;
        item.ErrorMessage = errorMessage;
        item.AgentExitCode = agentExitCode;
        item.ErrorOccurredAt = completedAt;
    }

    public async Task MarkStaleAsync(JiraProcessingSourceTicketRecord item, DateTimeOffset markedAt, CancellationToken ct)
    {
        // Stale rows are previously-completed entries whose upstream input has changed.
        // Clears CompletionId and the error fields so the next claim begins from a clean
        // slate, but preserves CompletedProcessingAt / LastProcessingAttemptAt for audit.
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jira_processing_source_tickets
            SET ProcessingStatus = @status,
                CompletionId = NULL,
                ProcessingError = NULL,
                ErrorMessage = NULL,
                AgentExitCode = NULL,
                ErrorOccurredAt = NULL
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);
        item.ProcessingStatus = ProcessingStatusValues.Stale;
        item.CompletionId = null;
        item.ProcessingError = null;
        item.ErrorMessage = null;
        item.AgentExitCode = null;
        item.ErrorOccurredAt = null;
        _ = markedAt;
    }

    public async Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset cutoff = now - olderThan;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jira_processing_source_tickets
            SET ProcessingStatus = NULL,
                StartedProcessingAt = NULL,
                CompletionId = NULL,
                ProcessingError = NULL
            WHERE ProcessingStatus = @status AND LastProcessingAttemptAt < @cutoff
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.InProgress);
        command.Parameters.AddWithValue("@cutoff", Format(cutoff));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProcessingQueueStats> GetQueueStatsAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        int complete = await CountAsync(connection, "ProcessingStatus = @status", ProcessingStatusValues.Complete, ct);
        int pending = await CountAsync(connection, "ProcessingStatus IS NULL OR ProcessingStatus = @status", ProcessingStatusValues.Stale, ct);
        int inProgress = await CountAsync(connection, "ProcessingStatus = @status", ProcessingStatusValues.InProgress, ct);
        int error = await CountAsync(connection, "ProcessingStatus = @status", ProcessingStatusValues.Error, ct);
        DateTimeOffset? lastCompleted = await LastCompletedAsync(connection, ct);
        return new ProcessingQueueStats(complete, pending, inProgress, error, null, lastCompleted);
    }

    public async Task<JiraProcessingSourceTicketRecord?> GetByKeyAsync(string key, string sourceTicketShape, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        return await SelectByKeyAsync(
            connection,
            null,
            key,
            NormalizeSourceTicketShape(sourceTicketShape),
            ct);
    }

    public async Task<JiraProcessingSourceTicketRecord?> GetByIdAsync(
        string id,
        CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jira_processing_source_tickets WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<JiraProcessingSourceTicketRecord>> GetLocalAuthoringCandidatesAsync(
        ResolvedJiraProcessingFilters filters,
        int? maxItems,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        Func<IJiraProcessingTicketFilterCandidate, bool> predicate =
            JiraStoredTicketFacetPredicateBuilder.Build(filters);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT *
            FROM jira_processing_source_tickets
            WHERE SourceTicketShape = @shape
            ORDER BY LastUpdated ASC, Key ASC
            """;
        command.Parameters.AddWithValue("@shape", NormalizeSourceTicketShape(filters.SourceTicketShape));

        List<JiraProcessingSourceTicketRecord> candidates = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            JiraProcessingSourceTicketRecord row = ReadRecord(reader);
            if (predicate(row))
            {
                candidates.Add(row);
            }
        }
        await reader.DisposeAsync();

        List<JiraProcessingSourceTicketRecord> pending = [];
        foreach (JiraProcessingSourceTicketRecord candidate in candidates)
        {
            if (!await HasFrozenRevisionAsync(connection, candidate, ct))
            {
                pending.Add(candidate);
                if (maxItems is not null && pending.Count >= maxItems.Value)
                {
                    break;
                }
            }
        }
        return pending;
    }

    public static string GetSourceRevision(JiraProcessingSourceTicketRecord ticket)
        => JiraSourceRevision.Compute(ticket);

    public static async Task EnsureCurrentSourceRevisionAsync(
        SqliteConnection connection,
        string key,
        string sourceTicketShape,
        string observedSourceRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTicketShape);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedSourceRevision);
        JiraProcessingSourceTicketRecord? current = await SelectByKeyAsync(
            connection,
            transaction: null,
            key: key,
            sourceTicketShape: NormalizeSourceTicketShape(sourceTicketShape),
            ct: ct);
        if (current is null)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Jira source ticket '{key}' is no longer available.");
        }

        string currentRevision = GetSourceRevision(current);
        observedSourceRevision =
            AuthoringSourceRevision.CanonicalizeTimestamp(
                observedSourceRevision);
        if (!string.Equals(
                currentRevision,
                observedSourceRevision,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Observed source revision '{observedSourceRevision}' is no longer current; processor source revision is '{currentRevision}'.");
        }
    }

    public static async Task EnsureRunSourceRevisionsCurrentAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        IReadOnlyList<AuthoringRunItemRecord> items =
            await AuthoringRunStore.ReadRevalidationCorpusItemsAsync(
                connection,
                runId,
                ct);
        foreach (AuthoringRunItemRecord item in items)
        {
            await EnsureCurrentSourceRevisionAsync(
                connection,
                item.BusinessKey,
                item.ItemKind,
                item.ExpectedSourceRevision,
                ct);
        }
    }

    private static void ClearProcessing(JiraProcessingSourceTicketRecord record)
    {
        record.StartedProcessingAt = null;
        record.CompletedProcessingAt = null;
        record.LastProcessingAttemptAt = null;
        record.ProcessingStatus = null;
        record.ProcessingError = null;
        record.ProcessingAttemptCount = 0;
        record.CompletionId = null;
        record.ErrorMessage = null;
        record.AgentExitCode = null;
        record.ErrorOccurredAt = null;
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }

    private static async Task<bool> HasFrozenRevisionAsync(
        SqliteConnection connection,
        JiraProcessingSourceTicketRecord ticket,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM authoring_run_items
            WHERE BusinessKey = @businessKey
              AND ItemKind = @itemKind
              AND ExpectedSourceRevision = @sourceRevision
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@businessKey", ticket.Key);
        command.Parameters.AddWithValue("@itemKind", ticket.SourceTicketShape);
        command.Parameters.AddWithValue("@sourceRevision", GetSourceRevision(ticket));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static string CreateConnectionString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Pooling = false,
    }.ToString();

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".");
        using SqliteConnection connection = OpenConnection();
        EnsureSchema(connection);
    }

    /// <summary>
    /// Admits the supported source-ticket schema and retained identities before
    /// making schema-only changes in one immediate transaction. Additive columns
    /// must precede generated creation, which also creates their indexes.
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ExecuteSchemaTransaction(connection, () =>
        {
            bool exists = ValidateExistingSourceTicketSchema(connection);
            CompositeIndexState indexState = ValidateCompositeIndex(connection);
            if (exists)
            {
                ValidateRetainedIdentities(connection);
                SqliteSchemaHelpers.AddColumnIfMissing(
                    connection, SourceTicketTable, "Specification", "TEXT NOT NULL DEFAULT ''");
                SqliteSchemaHelpers.AddColumnIfMissing(
                    connection, SourceTicketTable, "SourceProjectLastSuccessfulRefreshAt", "TEXT NULL");
                SqliteSchemaHelpers.AddColumnIfMissing(
                    connection, SourceTicketTable, "SourceContentRevision", "INTEGER NULL");
                SqliteSchemaHelpers.AddColumnIfMissing(
                    connection, SourceTicketTable, "CompletionId", "TEXT NULL");
            }

            JiraProcessingSourceTicketRecord.CreateTable(connection);
            ValidateCompletionIndex(connection);
            EnsureCompositeUniqueIndexCore(connection, indexState);
        });
    }

    /// <summary>
    /// CsLightDbGen does not currently expose a way to declare a composite UNIQUE index, so
    /// the (Key, SourceTicketShape) uniqueness contract from the prior hand-written DDL is
    /// preserved here as a follow-on CREATE UNIQUE INDEX. Required by the upsert path:
    /// concurrent UpsertAsync callers rely on this constraint to surface duplicate inserts
    /// rather than silently double-write.
    /// </summary>
    public static void EnsureCompositeUniqueIndex(SqliteConnection connection)
    {
        ExecuteSchemaTransaction(connection, () =>
        {
            if (!ValidateExistingSourceTicketSchema(connection))
            {
                throw SchemaRefusal(connection, "unsupported schema", "the source table is absent");
            }
            CompositeIndexState indexState = ValidateCompositeIndex(connection);
            ValidateRetainedIdentities(connection);
            EnsureCompositeUniqueIndexCore(connection, indexState);
        });
    }

    private static void ExecuteSchemaTransaction(SqliteConnection connection, Action action)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        // A failed begin, including a caller-owned transaction, is not ours to roll back.
        command.ExecuteNonQuery();
        try
        {
            action();
            command.CommandText = "COMMIT";
            command.ExecuteNonQuery();
        }
        catch
        {
            try
            {
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            }
            catch
            {
                // A rollback failure must not replace the original admission/DDL failure.
            }
            throw;
        }
    }

    private static bool ValidateExistingSourceTicketSchema(SqliteConnection connection)
    {
        SchemaObject? sourceObject = ReadSchemaObject(connection, SourceTicketTable);
        if (sourceObject is null)
        {
            return false;
        }
        if (sourceObject.Type != "table" || sourceObject.Temporary)
        {
            throw SchemaRefusal(connection, "unsupported schema", "the source name is occupied by another object");
        }

        using (SqliteCommand table = connection.CreateCommand())
        {
            table.CommandText = """
                SELECT type, wr FROM pragma_table_list
                WHERE schema = 'main' AND name = @name COLLATE NOCASE
                """;
            table.Parameters.AddWithValue("@name", SourceTicketTable);
            using SqliteDataReader reader = table.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "table" || reader.GetInt32(1) != 0)
            {
                throw SchemaRefusal(connection, "RowId contract", "an ordinary rowid table is required");
            }
        }

        Dictionary<string, ColumnDefinition> columns = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand table = connection.CreateCommand())
        {
            table.CommandText = $"PRAGMA main.table_xinfo({QuoteIdentifier(SourceTicketTable)})";
            using SqliteDataReader reader = table.ExecuteReader();
            while (reader.Read())
            {
                ColumnDefinition column = new(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6));
                columns.Add(column.Name, column);
            }
        }

        (string Name, string Affinity, bool Nullable, bool Additive)[] contracts =
        [
            ("RowId", "INTEGER", false, false),
            ("Id", "TEXT", false, false),
            ("Key", "TEXT", false, false),
            ("Title", "TEXT", false, false),
            ("Description", "TEXT", true, false),
            ("Project", "TEXT", false, false),
            ("Status", "TEXT", false, false),
            ("WorkGroup", "TEXT", false, false),
            ("Type", "TEXT", false, false),
            ("SourceTicketShape", "TEXT", false, false),
            ("LastSyncedAt", "TEXT", false, false),
            ("LastUpdated", "TEXT", true, false),
            ("StartedProcessingAt", "TEXT", true, false),
            ("CompletedProcessingAt", "TEXT", true, false),
            ("LastProcessingAttemptAt", "TEXT", true, false),
            ("ProcessingStatus", "TEXT", true, false),
            ("ProcessingError", "TEXT", true, false),
            ("ProcessingAttemptCount", "INTEGER", false, false),
            ("ErrorMessage", "TEXT", true, false),
            ("AgentExitCode", "INTEGER", true, false),
            ("ErrorOccurredAt", "TEXT", true, false),
            ("Specification", "TEXT", false, true),
            ("SourceProjectLastSuccessfulRefreshAt", "TEXT", true, true),
            ("SourceContentRevision", "INTEGER", true, true),
            ("CompletionId", "TEXT", true, true),
        ];
        string[] missing = contracts
            .Where(contract => !contract.Additive && !columns.ContainsKey(contract.Name))
            .Select(contract => contract.Name).ToArray();
        if (missing.Length != 0)
        {
            throw SchemaRefusal(connection, "missing required columns", string.Join(", ", missing));
        }

        IReadOnlyList<IndexDefinition> indexes = ReadIndexDefinitions(connection);
        ColumnDefinition rowId = columns["RowId"];
        // INTEGER affinity alone is insufficient. DESC and WITHOUT ROWID primary
        // keys have a primary-key index rather than aliasing SQLite's rowid.
        if (!string.Equals(rowId.Type.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase) ||
            rowId.PrimaryKey != 1 || columns.Values.Count(column => column.PrimaryKey != 0) != 1 ||
            indexes.Any(index => index.Origin == "pk"))
        {
            throw SchemaRefusal(connection, "RowId contract", "RowId must be an automatically generated rowid alias");
        }

        foreach (var (name, affinity, nullable, _) in contracts)
        {
            if (columns.TryGetValue(name, out ColumnDefinition? column) &&
                (GetAffinity(column.Type) != affinity || column.Hidden != 0 ||
                 (name != "RowId" && column.NotNull == nullable)))
            {
                throw SchemaRefusal(connection, "column contract", $"incompatible affinity, nullability or generated column: {name}");
            }
        }

        int result = SQLitePCL.raw.sqlite3_table_column_metadata(
            connection.Handle, "main", SourceTicketTable, columns["Id"].Name,
            out string _, out string idCollation, out int _, out int _, out int _);
        if (result != SQLitePCL.raw.SQLITE_OK ||
            !indexes.Any(index => index.Unique && !index.Partial && index.Terms.Count == 1 &&
                IsColumn(index.Terms[0], "Id") &&
                CollationEnsuresIdUniqueness(idCollation, index.Terms[0].Collation)))
        {
            throw SchemaRefusal(connection, "Id uniqueness", "a non-partial single-column unique index compatible with Id equality is required");
        }

        HashSet<string> known = contracts.Select(contract => contract.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Description is inserted as NULL and retained thereafter; completion
        // identities are NULL or independently stamped GUIDs. Other processing
        // fields can legitimately share values when claimed or completed.
        HashSet<string> compatibleNullableUniqueColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "Description", "CompletionId",
        };
        foreach (ColumnDefinition column in columns.Values.Where(column => !known.Contains(column.Name)))
        {
            if (column.NotNull && (column.Hidden != 0 || !HasNonNullDefault(connection, column.Default)))
            {
                throw SchemaRefusal(connection, "extra column contract", $"an omitted column requires a non-null default: {column.Name}");
            }
            if (column.Hidden == 0 && (column.Default is null ||
                string.Equals(column.Default, "NULL", StringComparison.OrdinalIgnoreCase)))
            {
                compatibleNullableUniqueColumns.Add(column.Name);
            }
        }
        foreach (IndexDefinition index in indexes.Where(index => index.Unique &&
                     !string.Equals(index.Name, CompositeIndexName, StringComparison.OrdinalIgnoreCase)))
        {
            bool redundantIdentity = index.Terms.Any(term => IsColumn(term, "RowId") ||
                (IsColumn(term, "Id") &&
                 (CollationEnsuresIdUniqueness(idCollation, term.Collation) ||
                  CollationEnsuresIdUniqueness(term.Collation, idCollation))));
            bool redundantBusinessKey = new[] { "Key", "SourceTicketShape" }.All(name =>
                index.Terms.Any(term => IsColumn(term, name) &&
                    (string.Equals(term.Collation, "BINARY", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(term.Collation, "NOCASE", StringComparison.OrdinalIgnoreCase))));
            bool compatibleNullableValue = index.Terms.Any(term => term.ColumnId >= 0 &&
                term.Name is not null && compatibleNullableUniqueColumns.Contains(term.Name));
            if (!redundantIdentity && !redundantBusinessKey && !compatibleNullableValue)
            {
                throw SchemaRefusal(connection, "extra index contract", "an additional unique index restricts source-ticket writes");
            }
        }
        return true;
    }

    private static bool HasNonNullDefault(SqliteConnection connection, string? defaultSql)
    {
        if (defaultSql is null)
        {
            return false;
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT ({defaultSql}) IS NOT NULL";
        try
        {
            return command.ExecuteScalar() is long value && value == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static bool CollationEnsuresIdUniqueness(string columnCollation, string indexCollation)
        => string.Equals(columnCollation, indexCollation, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(columnCollation, "BINARY", StringComparison.OrdinalIgnoreCase) &&
             (string.Equals(indexCollation, "NOCASE", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(indexCollation, "RTRIM", StringComparison.OrdinalIgnoreCase)));

    private static string GetAffinity(string type)
    {
        if (type.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return "INTEGER";
        }
        if (type.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("CLOB", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return "TEXT";
        }
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
        {
            return "BLOB";
        }
        if (type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("FLOA", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("DOUB", StringComparison.OrdinalIgnoreCase))
        {
            return "REAL";
        }
        return "NUMERIC";
    }

    private static void ValidateRetainedIdentities(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM jira_processing_source_tickets
            GROUP BY Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE
            HAVING COUNT(*) > 1
            LIMIT 1
            """;
        if (command.ExecuteScalar() is not null)
        {
            throw SchemaRefusal(connection, "identity collision", "retained Key/SourceTicketShape pairs conflict under NOCASE");
        }
        command.CommandText = """
            SELECT 1 FROM jira_processing_source_tickets
            WHERE LOWER(TRIM(SourceTicketShape)) = 'fhir'
              AND (SourceTicketShape COLLATE BINARY <> 'fhir' COLLATE BINARY
                   OR Key COLLATE BINARY <> UPPER(Key) COLLATE BINARY)
            LIMIT 1
            """;
        if (command.ExecuteScalar() is not null)
        {
            throw SchemaRefusal(connection, "noncanonical FHIR identity", "retained FHIR keys and shapes require explicit reconciliation");
        }
    }

    private static CompositeIndexState ValidateCompositeIndex(SqliteConnection connection)
    {
        SchemaObject? indexObject = ReadSchemaObject(connection, CompositeIndexName);
        if (indexObject is null)
        {
            return CompositeIndexState.Absent;
        }
        if (indexObject.Temporary || indexObject.Type != "index" ||
            !string.Equals(indexObject.Table, SourceTicketTable, StringComparison.OrdinalIgnoreCase))
        {
            throw SchemaRefusal(connection, "composite index ownership", "the managed index name is occupied by another object");
        }

        IndexDefinition? index = ReadIndexDefinitions(connection).SingleOrDefault(
            index => string.Equals(index.Name, CompositeIndexName, StringComparison.OrdinalIgnoreCase));
        if (index is not null && index.Unique && !index.Partial && index.Terms.Count == 2 &&
            IsColumn(index.Terms[0], "Key") && IsColumn(index.Terms[1], "SourceTicketShape"))
        {
            if (index.Terms.All(term => string.Equals(term.Collation, "NOCASE", StringComparison.OrdinalIgnoreCase)))
            {
                return CompositeIndexState.Current;
            }
            if (index.Terms.All(term => !term.Descending &&
                string.Equals(term.Collation, "BINARY", StringComparison.OrdinalIgnoreCase)))
            {
                return CompositeIndexState.LegacyBinary;
            }
        }
        throw SchemaRefusal(connection, "composite index definition", "only the current NOCASE or legacy BINARY/BINARY unique index is supported");
    }

    private static void EnsureCompositeUniqueIndexCore(
        SqliteConnection connection,
        CompositeIndexState state)
    {
        if (state == CompositeIndexState.Current)
        {
            return;
        }
        using SqliteCommand command = connection.CreateCommand();
        if (state == CompositeIndexState.LegacyBinary)
        {
            command.CommandText = $"DROP INDEX {QuoteIdentifier(CompositeIndexName)}";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE UNIQUE INDEX idx_jira_processing_source_tickets_key_shape
            ON jira_processing_source_tickets(Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE)
            """;
        command.ExecuteNonQuery();
    }

    private static void ValidateCompletionIndex(SqliteConnection connection)
    {
        if (!ReadIndexDefinitions(connection).Any(index => !index.Partial &&
            index.Terms.Count == 1 && IsColumn(index.Terms[0], "CompletionId")))
        {
            throw SchemaRefusal(connection, "completion index definition", "a non-partial index over the CompletionId column is required");
        }
    }

    private static SchemaObject? ReadSchemaObject(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, tbl_name, 0 FROM main.sqlite_schema WHERE name = @name COLLATE NOCASE
            UNION ALL
            SELECT type, tbl_name, 1 FROM temp.sqlite_schema WHERE name = @name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("@name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        SchemaObject result = new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0);
        if (reader.Read())
        {
            throw SchemaRefusal(connection, "schema object ownership", "a managed name is occupied by multiple objects");
        }
        return result;
    }

    private static IReadOnlyList<IndexDefinition> ReadIndexDefinitions(SqliteConnection connection)
    {
        List<IndexDefinition> indexes = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA main.index_list({QuoteIdentifier(SourceTicketTable)})";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                indexes.Add(new IndexDefinition(
                    reader.GetString(1), reader.GetInt32(2) != 0,
                    reader.GetString(3), reader.GetInt32(4) != 0, []));
            }
        }
        foreach (IndexDefinition index in indexes)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA main.index_xinfo({QuoteIdentifier(index.Name)})";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(5) == 1)
                {
                    index.Terms.Add(new IndexTerm(
                        reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt32(3) != 0, reader.GetString(4)));
                }
            }
        }
        return indexes;
    }

    private static bool IsColumn(IndexTerm term, string name)
        => term.ColumnId >= 0 && string.Equals(term.Name, name, StringComparison.OrdinalIgnoreCase);

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static InvalidOperationException SchemaRefusal(
        SqliteConnection connection,
        string category,
        string detail)
        => new($"Cannot initialize table '{SourceTicketTable}' in database '{connection.DataSource}': {category}: {detail}.");

    private sealed record SchemaObject(string Type, string Table, bool Temporary);
    private sealed record ColumnDefinition(string Name, string Type, bool NotNull, string? Default, int PrimaryKey, int Hidden);
    private sealed record IndexDefinition(string Name, bool Unique, string Origin, bool Partial, List<IndexTerm> Terms);
    private sealed record IndexTerm(int ColumnId, string? Name, bool Descending, string Collation);
    private enum CompositeIndexState { Absent, Current, LegacyBinary }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, JiraProcessingSourceTicketRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jira_processing_source_tickets
            (Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification, SourceTicketShape, LastSyncedAt, LastUpdated,
             SourceProjectLastSuccessfulRefreshAt, SourceContentRevision,
             StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt, ProcessingStatus, ProcessingError, ProcessingAttemptCount,
             CompletionId, ErrorMessage, AgentExitCode, ErrorOccurredAt)
            VALUES
            (@Id, @Key, @Title, @Description, @Project, @Status, @WorkGroup, @Type, @Specification, @SourceTicketShape, @LastSyncedAt, @LastUpdated,
             @SourceProjectLastSuccessfulRefreshAt, @SourceContentRevision,
             @StartedProcessingAt, @CompletedProcessingAt, @LastProcessingAttemptAt, @ProcessingStatus, @ProcessingError, @ProcessingAttemptCount,
             @CompletionId, @ErrorMessage, @AgentExitCode, @ErrorOccurredAt)
            """;
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAsync(SqliteConnection connection, SqliteTransaction transaction, JiraProcessingSourceTicketRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jira_processing_source_tickets SET
                Title = @Title,
                Description = @Description,
                Project = @Project,
                Status = @Status,
                WorkGroup = @WorkGroup,
                Type = @Type,
                Specification = @Specification,
                LastSyncedAt = @LastSyncedAt,
                LastUpdated = @LastUpdated,
                SourceProjectLastSuccessfulRefreshAt = @SourceProjectLastSuccessfulRefreshAt,
                SourceContentRevision = @SourceContentRevision,
                StartedProcessingAt = @StartedProcessingAt,
                CompletedProcessingAt = @CompletedProcessingAt,
                LastProcessingAttemptAt = @LastProcessingAttemptAt,
                ProcessingStatus = @ProcessingStatus,
                ProcessingError = @ProcessingError,
                ProcessingAttemptCount = @ProcessingAttemptCount,
                CompletionId = @CompletionId,
                ErrorMessage = @ErrorMessage,
                AgentExitCode = @AgentExitCode,
                ErrorOccurredAt = @ErrorOccurredAt
            WHERE Id = @Id
            """;
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(SqliteCommand command, JiraProcessingSourceTicketRecord record)
    {
        command.Parameters.AddWithValue("@Id", record.Id);
        command.Parameters.AddWithValue("@Key", record.Key);
        command.Parameters.AddWithValue("@Title", record.Title);
        command.Parameters.AddWithValue("@Description", (object?)record.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("@Project", record.Project);
        command.Parameters.AddWithValue("@Status", record.Status);
        command.Parameters.AddWithValue("@WorkGroup", record.WorkGroup);
        command.Parameters.AddWithValue("@Type", record.Type);
        command.Parameters.AddWithValue("@Specification", record.Specification);
        command.Parameters.AddWithValue("@SourceTicketShape", record.SourceTicketShape);
        command.Parameters.AddWithValue("@LastSyncedAt", Format(record.LastSyncedAt));
        command.Parameters.AddWithValue("@LastUpdated", FormatNullable(record.LastUpdated));
        command.Parameters.AddWithValue(
            "@SourceProjectLastSuccessfulRefreshAt",
            FormatNullable(record.SourceProjectLastSuccessfulRefreshAt));
        command.Parameters.AddWithValue(
            "@SourceContentRevision",
            (object?)record.SourceContentRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("@StartedProcessingAt", FormatNullable(record.StartedProcessingAt));
        command.Parameters.AddWithValue("@CompletedProcessingAt", FormatNullable(record.CompletedProcessingAt));
        command.Parameters.AddWithValue("@LastProcessingAttemptAt", FormatNullable(record.LastProcessingAttemptAt));
        command.Parameters.AddWithValue("@ProcessingStatus", (object?)record.ProcessingStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProcessingError", (object?)record.ProcessingError ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProcessingAttemptCount", record.ProcessingAttemptCount);
        command.Parameters.AddWithValue("@CompletionId", (object?)record.CompletionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ErrorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@AgentExitCode", (object?)record.AgentExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@ErrorOccurredAt", FormatNullable(record.ErrorOccurredAt));
    }

    private static async Task<JiraProcessingSourceTicketRecord?> SelectByKeyAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string sourceTicketShape, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT * FROM jira_processing_source_tickets WHERE Key = @key COLLATE NOCASE AND SourceTicketShape = @shape COLLATE NOCASE";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@shape", sourceTicketShape);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRecord(reader);
        }

        return null;
    }

    private static JiraProcessingSourceTicketRecord ReadRecord(SqliteDataReader reader) => new()
    {
        RowId = reader.GetInt32(reader.GetOrdinal("RowId")),
        Id = reader.GetString(reader.GetOrdinal("Id")),
        Key = reader.GetString(reader.GetOrdinal("Key")),
        Title = reader.GetString(reader.GetOrdinal("Title")),
        Description = GetNullableString(reader, "Description"),
        Project = reader.GetString(reader.GetOrdinal("Project")),
        Status = reader.GetString(reader.GetOrdinal("Status")),
        WorkGroup = reader.GetString(reader.GetOrdinal("WorkGroup")),
        Type = reader.GetString(reader.GetOrdinal("Type")),
        Specification = reader.GetString(reader.GetOrdinal("Specification")),
        SourceTicketShape = reader.GetString(reader.GetOrdinal("SourceTicketShape")),
        LastSyncedAt = ParseDate(reader, "LastSyncedAt") ?? DateTimeOffset.MinValue,
        LastUpdated = ParseDate(reader, "LastUpdated"),
        SourceProjectLastSuccessfulRefreshAt = ParseDate(
            reader,
            "SourceProjectLastSuccessfulRefreshAt"),
        SourceContentRevision = GetNullableLong(
            reader,
            "SourceContentRevision"),
        StartedProcessingAt = ParseDate(reader, "StartedProcessingAt"),
        CompletedProcessingAt = ParseDate(reader, "CompletedProcessingAt"),
        LastProcessingAttemptAt = ParseDate(reader, "LastProcessingAttemptAt"),
        ProcessingStatus = GetNullableString(reader, "ProcessingStatus"),
        ProcessingError = GetNullableString(reader, "ProcessingError"),
        ProcessingAttemptCount = reader.GetInt32(reader.GetOrdinal("ProcessingAttemptCount")),
        CompletionId = GetNullableString(reader, "CompletionId"),
        ErrorMessage = GetNullableString(reader, "ErrorMessage"),
        AgentExitCode = GetNullableInt(reader, "AgentExitCode"),
        ErrorOccurredAt = ParseDate(reader, "ErrorOccurredAt"),
    };

    private static async Task<int> CountAsync(SqliteConnection connection, string whereClause, string? status, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM jira_processing_source_tickets WHERE {whereClause}";
        if (status is not null)
        {
            command.Parameters.AddWithValue("@status", status);
        }

        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> LastCompletedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(CompletedProcessingAt) FROM jira_processing_source_tickets WHERE ProcessingStatus = @status";
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Complete);
        object? value = await command.ExecuteScalarAsync(ct);
        if (value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static string UppercaseAscii(string value) => string.Create(
        value.Length,
        value,
        static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                destination[index] = character is >= 'a' and <= 'z'
                    ? (char)(character - ('a' - 'A'))
                    : character;
            }
        });

    private static string NormalizeSourceTicketShape(string value)
        => string.Equals(value, "fhir", StringComparison.OrdinalIgnoreCase)
            ? "fhir"
            : value.Trim().ToLowerInvariant();
    private static object FormatNullable(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, string name)
    {
        string? value = GetNullableString(reader, name);
        if (value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }
}
