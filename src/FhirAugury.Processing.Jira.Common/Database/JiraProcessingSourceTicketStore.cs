using System.Globalization;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Database;

public sealed class JiraProcessingSourceTicketStore : IProcessingWorkItemStore<JiraProcessingSourceTicketRecord>
{
    private readonly string _dbPath;
    private readonly Func<ResolvedJiraProcessingFilters> _filtersFactory;

    public JiraProcessingSourceTicketStore(string dbPath, ResolvedJiraProcessingFilters? filters = null)
    {
        _dbPath = dbPath;
        _filtersFactory = () => filters ?? new ResolvedJiraProcessingFilters();
        EnsureSchema();
    }

    public JiraProcessingSourceTicketStore(
        IOptions<ProcessingServiceOptions> processingOptions,
        IOptions<JiraProcessingOptions> jiraOptions,
        JiraProcessingFilterResolver filterResolver)
    {
        _dbPath = processingOptions.Value.DatabasePath;
        _filtersFactory = () => filterResolver.Resolve(jiraOptions.Value);
        EnsureSchema();
    }

    public async Task<JiraProcessingSourceTicketRecord> UpsertAsync(
        JiraIssueSummaryEntry ticket,
        string sourceTicketShape,
        bool resetProcessingStatus,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        JiraProcessingSourceTicketRecord? existing = await SelectByKeyAsync(connection, transaction, ticket.Key, sourceTicketShape, ct);
        if (existing is null)
        {
            JiraProcessingSourceTicketRecord inserted = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = ticket.Key,
                Title = ticket.Title,
                Description = null,
                Project = ticket.ProjectKey,
                Status = ticket.Status,
                WorkGroup = ticket.WorkGroup,
                Type = ticket.Type,
                SourceTicketShape = sourceTicketShape,
                LastSyncedAt = now,
                LastUpdated = ticket.UpdatedAt,
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
        existing.LastSyncedAt = now;
        existing.LastUpdated = ticket.UpdatedAt;
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
        Func<IJiraProcessingTicketFilterCandidate, bool> predicate = JiraSourceTicketPredicateBuilder.Build(filters);
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM jira_processing_source_tickets
            WHERE (ProcessingStatus IS NULL OR ProcessingStatus = @stale) AND SourceTicketShape = @shape
            ORDER BY LastUpdated ASC, Key ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@stale", ProcessingStatusValues.Stale);
        command.Parameters.AddWithValue("@shape", filters.SourceTicketShape);
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
        return await SelectByKeyAsync(connection, null, key, sourceTicketShape, ct);
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
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath)) ?? ".");
        using SqliteConnection connection = OpenConnection();
        JiraProcessingSourceTicketRecord.CreateTable(connection);
        EnsureCompositeUniqueIndex(connection);
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
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_jira_processing_source_tickets_key_shape ON jira_processing_source_tickets(Key, SourceTicketShape);";
        command.ExecuteNonQuery();
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, JiraProcessingSourceTicketRecord record, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jira_processing_source_tickets
            (Id, Key, Title, Description, Project, Status, WorkGroup, Type, SourceTicketShape, LastSyncedAt, LastUpdated,
             StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt, ProcessingStatus, ProcessingError, ProcessingAttemptCount,
             CompletionId, ErrorMessage, AgentExitCode, ErrorOccurredAt)
            VALUES
            (@Id, @Key, @Title, @Description, @Project, @Status, @WorkGroup, @Type, @SourceTicketShape, @LastSyncedAt, @LastUpdated,
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
                LastSyncedAt = @LastSyncedAt,
                LastUpdated = @LastUpdated,
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
        command.Parameters.AddWithValue("@SourceTicketShape", record.SourceTicketShape);
        command.Parameters.AddWithValue("@LastSyncedAt", Format(record.LastSyncedAt));
        command.Parameters.AddWithValue("@LastUpdated", FormatNullable(record.LastUpdated));
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
        command.CommandText = "SELECT * FROM jira_processing_source_tickets WHERE Key = @key AND SourceTicketShape = @shape";
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
        SourceTicketShape = reader.GetString(reader.GetOrdinal("SourceTicketShape")),
        LastSyncedAt = ParseDate(reader, "LastSyncedAt") ?? DateTimeOffset.MinValue,
        LastUpdated = ParseDate(reader, "LastUpdated"),
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
