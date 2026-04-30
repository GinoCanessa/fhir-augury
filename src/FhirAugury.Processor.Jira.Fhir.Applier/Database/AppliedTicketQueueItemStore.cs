using System.Globalization;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database;

/// <summary>
/// Per-applier work item store. Mirrors the shape of
/// <c>JiraProcessingSourceTicketStore</c> for the
/// <see cref="AppliedTicketQueueItemRecord"/> table.
/// <list type="bullet">
/// <item><c>ProcessingStatus IS NULL</c> or <c>'stale'</c> = pending-eligible</item>
/// <item><c>'in-progress'</c> = currently being applied</item>
/// <item><c>'complete'</c> = successfully applied</item>
/// <item><c>'error'</c> = failed (a future plan revision can re-enqueue)</item>
/// </list>
/// </summary>
public sealed class AppliedTicketQueueItemStore : IProcessingWorkItemStore<AppliedTicketQueueItemRecord>
{
    private readonly string _dbPath;

    public AppliedTicketQueueItemStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchemaPresent();
    }

    public AppliedTicketQueueItemStore(IOptions<ProcessingServiceOptions> processingOptions)
        : this(processingOptions.Value.DatabasePath)
    {
    }

    public async Task<IReadOnlyList<AppliedTicketQueueItemRecord>> GetPendingAsync(int maxItems, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM applied_ticket_queue_items
            WHERE ProcessingStatus IS NULL OR ProcessingStatus = @stale
            ORDER BY DiscoveredAt ASC, Key ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@stale", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@limit", Math.Max(1, maxItems));
        List<AppliedTicketQueueItemRecord> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadRecord(reader));
        }
        return rows;
    }

    public async Task<bool> ClaimItemAsync(AppliedTicketQueueItemRecord item, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
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

    public async Task MarkCompleteAsync(AppliedTicketQueueItemRecord item, DateTimeOffset completedAt, CancellationToken ct)
    {
        // CompletionId is stamped idempotently (handler stamps first, runner stamps again).
        // COALESCE preserves the first-write GUID; the SELECT-back binds the actual stored
        // value onto the in-memory item even when no DB row exists yet (test fixtures may
        // hand the handler a raw record). See JiraProcessingSourceTicketStore for full
        // background.
        string newCompletionId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
            SET ProcessingStatus = @status,
                CompletedProcessingAt = @completed,
                CompletionId = COALESCE(CompletionId, @completionId),
                ProcessingError = NULL,
                Outcome = COALESCE(Outcome, @successOutcome),
                ErrorSummary = NULL
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Complete);
        command.Parameters.AddWithValue("@completed", Format(completedAt));
        command.Parameters.AddWithValue("@completionId", newCompletionId);
        command.Parameters.AddWithValue("@successOutcome", "Success");
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT CompletionId FROM applied_ticket_queue_items WHERE Id = @id";
        select.Parameters.AddWithValue("@id", item.Id);
        object? stored = await select.ExecuteScalarAsync(ct);
        item.CompletionId = stored is string s ? s : newCompletionId;
        item.ProcessingStatus = ProcessingStatusValues.Complete;
        item.CompletedProcessingAt = completedAt;
        item.Outcome ??= "Success";
        item.ErrorSummary = null;
    }

    public async Task MarkErrorAsync(AppliedTicketQueueItemRecord item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
            SET ProcessingStatus = @status,
                CompletedProcessingAt = @completed,
                CompletionId = NULL,
                ProcessingError = @error,
                Outcome = @failedOutcome,
                ErrorSummary = @error
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Error);
        command.Parameters.AddWithValue("@completed", Format(completedAt));
        command.Parameters.AddWithValue("@error", errorMessage);
        command.Parameters.AddWithValue("@failedOutcome", "Failed");
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);
        item.ProcessingStatus = ProcessingStatusValues.Error;
        item.CompletedProcessingAt = completedAt;
        item.CompletionId = null;
        item.ProcessingError = errorMessage;
        item.Outcome = "Failed";
        item.ErrorSummary = errorMessage;
    }

    public async Task UpdateOutcomeAsync(AppliedTicketQueueItemRecord item, string outcome, string? errorSummary, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
            SET Outcome = @outcome,
                ErrorSummary = @err
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@err", (object?)errorSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);
        item.Outcome = outcome;
        item.ErrorSummary = errorSummary;
    }

    public async Task MarkStaleAsync(AppliedTicketQueueItemRecord item, DateTimeOffset markedAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
            SET ProcessingStatus = @status,
                CompletionId = NULL,
                ProcessingError = NULL,
                ErrorSummary = NULL
            WHERE Id = @id
            """;
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@id", item.Id);
        await command.ExecuteNonQueryAsync(ct);
        item.ProcessingStatus = ProcessingStatusValues.Stale;
        item.CompletionId = null;
        item.ProcessingError = null;
        item.ErrorSummary = null;
        _ = markedAt;
    }

    public async Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset cutoff = now - olderThan;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE applied_ticket_queue_items
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

    public async Task<AppliedTicketQueueItemRecord?> GetByKeyAsync(string key, string sourceTicketShape, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM applied_ticket_queue_items WHERE Key = @key AND SourceTicketShape = @shape";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@shape", sourceTicketShape);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRecord(reader);
        }
        return null;
    }

    /// <summary>
    /// Discovery-side upsert from <c>PlannerWorkQueue</c>.
    /// <list type="bullet">
    /// <item>If no local row exists, inserts one with status null and no completion id.</item>
    /// <item>If local <c>PlannerCompletionId</c> matches input, just touches <c>LastSyncedAt</c>.</item>
    /// <item>If local row is <c>complete</c> and input GUID differs, marks the row <c>stale</c>
    /// (preserving the prior <c>CompletedProcessingAt</c> for audit, clearing
    /// <c>CompletionId</c> / <c>Outcome</c> / <c>ErrorSummary</c>) and updates the planner
    /// reference fields.</item>
    /// <item>If local row is <c>null</c> / <c>error</c> / <c>stale</c> and input GUID differs,
    /// updates the planner reference fields and resets processing state for next claim.</item>
    /// <item>If local row is <c>in-progress</c>, the planner reference fields are
    /// <strong>not</strong> updated; orphan recovery handles that case after the apply
    /// completes / fails.</item>
    /// </list>
    /// </summary>
    public async Task<AppliedTicketQueueItemUpsertResult> UpsertFromPlannerAsync(
        string key,
        string sourceTicketShape,
        string? plannerCompletionId,
        DateTimeOffset? plannerCompletedAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        AppliedTicketQueueItemRecord? existing = await SelectByKeyAsync(connection, transaction, key, sourceTicketShape, ct);
        if (existing is null)
        {
            AppliedTicketQueueItemRecord inserted = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = key,
                SourceTicketShape = sourceTicketShape,
                PlannerCompletionId = plannerCompletionId,
                PlannerCompletedAt = plannerCompletedAt,
                DiscoveredAt = now,
                LastSyncedAt = now,
            };
            await InsertAsync(connection, transaction, inserted, ct);
            await transaction.CommitAsync(ct);
            return AppliedTicketQueueItemUpsertResult.Inserted;
        }

        if (string.Equals(existing.PlannerCompletionId, plannerCompletionId, StringComparison.Ordinal))
        {
            existing.LastSyncedAt = now;
            await UpdateLastSyncedOnlyAsync(connection, transaction, existing, ct);
            await transaction.CommitAsync(ct);
            return AppliedTicketQueueItemUpsertResult.Unchanged;
        }

        if (string.Equals(existing.ProcessingStatus, ProcessingStatusValues.InProgress, StringComparison.Ordinal))
        {
            existing.LastSyncedAt = now;
            await UpdateLastSyncedOnlyAsync(connection, transaction, existing, ct);
            await transaction.CommitAsync(ct);
            return AppliedTicketQueueItemUpsertResult.SkippedInProgress;
        }

        bool wasComplete = string.Equals(existing.ProcessingStatus, ProcessingStatusValues.Complete, StringComparison.Ordinal);
        existing.PlannerCompletionId = plannerCompletionId;
        existing.PlannerCompletedAt = plannerCompletedAt;
        existing.LastSyncedAt = now;
        existing.CompletionId = null;
        existing.ErrorSummary = null;
        existing.ProcessingError = null;
        if (wasComplete)
        {
            existing.ProcessingStatus = ProcessingStatusValues.Stale;
            existing.Outcome = null;
        }
        else
        {
            existing.ProcessingStatus = null;
            existing.StartedProcessingAt = null;
            existing.CompletedProcessingAt = null;
            existing.LastProcessingAttemptAt = null;
            existing.ProcessingAttemptCount = 0;
            existing.Outcome = null;
        }

        await UpdateAsync(connection, transaction, existing, ct);
        await transaction.CommitAsync(ct);
        return wasComplete
            ? AppliedTicketQueueItemUpsertResult.MarkedStale
            : AppliedTicketQueueItemUpsertResult.UpdatedPlanReference;
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private void EnsureSchemaPresent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".");
        using SqliteConnection connection = OpenConnection();
        AppliedTicketQueueItemRecord.CreateTable(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_applied_ticket_queue_items_key_shape ON applied_ticket_queue_items(Key, SourceTicketShape);";
        command.ExecuteNonQuery();
    }

    private static async Task<AppliedTicketQueueItemRecord?> SelectByKeyAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string sourceTicketShape, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM applied_ticket_queue_items WHERE Key = @key AND SourceTicketShape = @shape";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@shape", sourceTicketShape);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRecord(reader);
        }
        return null;
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, AppliedTicketQueueItemRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO applied_ticket_queue_items
            (Id, Key, SourceTicketShape, PlannerCompletionId, PlannerCompletedAt, DiscoveredAt, LastSyncedAt,
             StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt, ProcessingStatus, ProcessingError,
             ProcessingAttemptCount, CompletionId, Outcome, ErrorSummary)
            VALUES
            (@Id, @Key, @SourceTicketShape, @PlannerCompletionId, @PlannerCompletedAt, @DiscoveredAt, @LastSyncedAt,
             @StartedProcessingAt, @CompletedProcessingAt, @LastProcessingAttemptAt, @ProcessingStatus, @ProcessingError,
             @ProcessingAttemptCount, @CompletionId, @Outcome, @ErrorSummary)
            """;
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAsync(SqliteConnection connection, SqliteTransaction transaction, AppliedTicketQueueItemRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE applied_ticket_queue_items SET
                PlannerCompletionId = @PlannerCompletionId,
                PlannerCompletedAt = @PlannerCompletedAt,
                LastSyncedAt = @LastSyncedAt,
                StartedProcessingAt = @StartedProcessingAt,
                CompletedProcessingAt = @CompletedProcessingAt,
                LastProcessingAttemptAt = @LastProcessingAttemptAt,
                ProcessingStatus = @ProcessingStatus,
                ProcessingError = @ProcessingError,
                ProcessingAttemptCount = @ProcessingAttemptCount,
                CompletionId = @CompletionId,
                Outcome = @Outcome,
                ErrorSummary = @ErrorSummary
            WHERE Id = @Id
            """;
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateLastSyncedOnlyAsync(SqliteConnection connection, SqliteTransaction transaction, AppliedTicketQueueItemRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE applied_ticket_queue_items SET LastSyncedAt = @LastSyncedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@LastSyncedAt", Format(record.LastSyncedAt));
        command.Parameters.AddWithValue("@Id", record.Id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(SqliteCommand command, AppliedTicketQueueItemRecord record)
    {
        command.Parameters.AddWithValue("@Id", record.Id);
        command.Parameters.AddWithValue("@Key", record.Key);
        command.Parameters.AddWithValue("@SourceTicketShape", record.SourceTicketShape);
        command.Parameters.AddWithValue("@PlannerCompletionId", (object?)record.PlannerCompletionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@PlannerCompletedAt", FormatNullable(record.PlannerCompletedAt));
        command.Parameters.AddWithValue("@DiscoveredAt", Format(record.DiscoveredAt));
        command.Parameters.AddWithValue("@LastSyncedAt", Format(record.LastSyncedAt));
        command.Parameters.AddWithValue("@StartedProcessingAt", FormatNullable(record.StartedProcessingAt));
        command.Parameters.AddWithValue("@CompletedProcessingAt", FormatNullable(record.CompletedProcessingAt));
        command.Parameters.AddWithValue("@LastProcessingAttemptAt", FormatNullable(record.LastProcessingAttemptAt));
        command.Parameters.AddWithValue("@ProcessingStatus", (object?)record.ProcessingStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProcessingError", (object?)record.ProcessingError ?? DBNull.Value);
        command.Parameters.AddWithValue("@ProcessingAttemptCount", record.ProcessingAttemptCount);
        command.Parameters.AddWithValue("@CompletionId", (object?)record.CompletionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Outcome", (object?)record.Outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("@ErrorSummary", (object?)record.ErrorSummary ?? DBNull.Value);
    }

    private static AppliedTicketQueueItemRecord ReadRecord(SqliteDataReader reader) => new()
    {
        RowId = reader.GetInt32(reader.GetOrdinal("RowId")),
        Id = reader.GetString(reader.GetOrdinal("Id")),
        Key = reader.GetString(reader.GetOrdinal("Key")),
        SourceTicketShape = reader.GetString(reader.GetOrdinal("SourceTicketShape")),
        PlannerCompletionId = GetNullableString(reader, "PlannerCompletionId"),
        PlannerCompletedAt = ParseDate(reader, "PlannerCompletedAt"),
        DiscoveredAt = ParseDate(reader, "DiscoveredAt") ?? DateTimeOffset.MinValue,
        LastSyncedAt = ParseDate(reader, "LastSyncedAt") ?? DateTimeOffset.MinValue,
        StartedProcessingAt = ParseDate(reader, "StartedProcessingAt"),
        CompletedProcessingAt = ParseDate(reader, "CompletedProcessingAt"),
        LastProcessingAttemptAt = ParseDate(reader, "LastProcessingAttemptAt"),
        ProcessingStatus = GetNullableString(reader, "ProcessingStatus"),
        ProcessingError = GetNullableString(reader, "ProcessingError"),
        ProcessingAttemptCount = reader.GetInt32(reader.GetOrdinal("ProcessingAttemptCount")),
        CompletionId = GetNullableString(reader, "CompletionId"),
        Outcome = GetNullableString(reader, "Outcome"),
        ErrorSummary = GetNullableString(reader, "ErrorSummary"),
    };

    private static async Task<int> CountAsync(SqliteConnection connection, string whereClause, string? status, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM applied_ticket_queue_items WHERE {whereClause}";
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
        command.CommandText = "SELECT MAX(CompletedProcessingAt) FROM applied_ticket_queue_items WHERE ProcessingStatus = @status";
        command.Parameters.AddWithValue("@status", ProcessingStatusValues.Complete);
        object? value = await command.ExecuteScalarAsync(ct);
        if (value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static object FormatNullable(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
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

public enum AppliedTicketQueueItemUpsertResult
{
    Inserted,
    Unchanged,
    UpdatedPlanReference,
    MarkedStale,
    SkippedInProgress,
}
