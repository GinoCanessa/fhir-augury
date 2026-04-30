using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database;

/// <summary>
/// Persists the per-(ticket) result of an apply attempt: the aggregated
/// <c>applied_tickets</c> row, per-repo <c>applied_ticket_repos</c> rows,
/// per-(ticket, repo, change) <c>applied_ticket_repo_changes</c> rows, and per-output
/// file <c>applied_ticket_output_files</c> rows. Re-applying a ticket replaces all of
/// the above in a single transaction so a partial overlap with prior state is impossible.
/// </summary>
public sealed class AppliedTicketWriteStore
{
    private readonly string _dbPath;

    public AppliedTicketWriteStore(ApplierDatabase database)
    {
        _dbPath = database.DatabasePath;
    }

    public AppliedTicketWriteStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task DeletePriorAppliedAsync(string ticketKey, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await DeleteWhere(connection, transaction, "applied_ticket_output_files", "IssueKey", ticketKey, ct);
        await DeleteWhere(connection, transaction, "applied_ticket_repo_changes", "IssueKey", ticketKey, ct);
        await DeleteWhere(connection, transaction, "applied_ticket_repos", "IssueKey", ticketKey, ct);
        await DeleteWhere(connection, transaction, "applied_tickets", "Key", ticketKey, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task UpsertAppliedTicketAsync(AppliedTicketRecord row, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO applied_tickets (Id, Key, PlannerCompletionId, ApplyCompletionId, AppliedAt, Outcome, ErrorSummary)
            VALUES (@id, @key, @plannerCid, @applyCid, @at, @outcome, @err)
            ON CONFLICT(Key) DO UPDATE SET
                PlannerCompletionId = excluded.PlannerCompletionId,
                ApplyCompletionId = excluded.ApplyCompletionId,
                AppliedAt = excluded.AppliedAt,
                Outcome = excluded.Outcome,
                ErrorSummary = excluded.ErrorSummary
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@key", row.Key);
        command.Parameters.AddWithValue("@plannerCid", row.PlannerCompletionId);
        command.Parameters.AddWithValue("@applyCid", row.ApplyCompletionId);
        command.Parameters.AddWithValue("@at", row.AppliedAt.ToString("O"));
        command.Parameters.AddWithValue("@outcome", row.Outcome);
        command.Parameters.AddWithValue("@err", (object?)row.ErrorSummary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAppliedTicketRepoAsync(AppliedTicketRepoRecord row, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO applied_ticket_repos
                (Id, IssueKey, RepoKey, BaselineCommitSha, BranchName, CommitSha, Outcome, ErrorSummary, PushState, PushedAt, PushedCommitSha, AppliedAt)
            VALUES (@id, @issue, @repo, @baseline, @branch, @sha, @outcome, @err, @push, @pushAt, @pushSha, @at)
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@issue", row.IssueKey);
        command.Parameters.AddWithValue("@repo", row.RepoKey);
        command.Parameters.AddWithValue("@baseline", row.BaselineCommitSha);
        command.Parameters.AddWithValue("@branch", (object?)row.BranchName ?? DBNull.Value);
        command.Parameters.AddWithValue("@sha", (object?)row.CommitSha ?? DBNull.Value);
        command.Parameters.AddWithValue("@outcome", row.Outcome);
        command.Parameters.AddWithValue("@err", (object?)row.ErrorSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@push", row.PushState);
        command.Parameters.AddWithValue("@pushAt", (object?)row.PushedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("@pushSha", (object?)row.PushedCommitSha ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", row.AppliedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAppliedTicketRepoChangeAsync(AppliedTicketRepoChangeRecord row, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO applied_ticket_repo_changes
                (Id, IssueKey, TicketRepoId, RepoKey, PlannedChangeId, ChangeSequence, FilePath, ChangeTitle, ApplyOutcome, ApplyErrorSummary, AppliedAt)
            VALUES (@id, @issue, @repoId, @repoKey, @plannedId, @seq, @file, @title, @outcome, @err, @at)
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@issue", row.IssueKey);
        command.Parameters.AddWithValue("@repoId", row.TicketRepoId);
        command.Parameters.AddWithValue("@repoKey", row.RepoKey);
        command.Parameters.AddWithValue("@plannedId", row.PlannedChangeId);
        command.Parameters.AddWithValue("@seq", row.ChangeSequence);
        command.Parameters.AddWithValue("@file", row.FilePath);
        command.Parameters.AddWithValue("@title", row.ChangeTitle);
        command.Parameters.AddWithValue("@outcome", row.ApplyOutcome);
        command.Parameters.AddWithValue("@err", (object?)row.ApplyErrorSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@at", row.AppliedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAppliedTicketOutputFileAsync(AppliedTicketOutputFileRecord row, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO applied_ticket_output_files
                (Id, IssueKey, RepoKey, RelativePath, ByteSize, Sha256, DiffSummary, CapturedAt)
            VALUES (@id, @issue, @repo, @path, @size, @sha, @diff, @at)
            """;
        command.Parameters.AddWithValue("@id", row.Id);
        command.Parameters.AddWithValue("@issue", row.IssueKey);
        command.Parameters.AddWithValue("@repo", row.RepoKey);
        command.Parameters.AddWithValue("@path", row.RelativePath);
        command.Parameters.AddWithValue("@size", row.ByteSize);
        command.Parameters.AddWithValue("@sha", row.Sha256);
        command.Parameters.AddWithValue("@diff", row.DiffSummary);
        command.Parameters.AddWithValue("@at", row.CapturedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppliedTicketRecord?> GetAppliedTicketAsync(string key, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM applied_tickets WHERE Key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new AppliedTicketRecord
        {
            RowId = reader.GetInt32(reader.GetOrdinal("RowId")),
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Key = reader.GetString(reader.GetOrdinal("Key")),
            PlannerCompletionId = reader.GetString(reader.GetOrdinal("PlannerCompletionId")),
            ApplyCompletionId = reader.GetString(reader.GetOrdinal("ApplyCompletionId")),
            AppliedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("AppliedAt"))),
            Outcome = reader.GetString(reader.GetOrdinal("Outcome")),
            ErrorSummary = reader.IsDBNull(reader.GetOrdinal("ErrorSummary")) ? null : reader.GetString(reader.GetOrdinal("ErrorSummary")),
        };
    }

    public async Task<int> CountAppliedTicketReposAsync(string ticketKey, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM applied_ticket_repos WHERE IssueKey = @key";
        command.Parameters.AddWithValue("@key", ticketKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> CountAppliedOutputFilesAsync(string ticketKey, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM applied_ticket_output_files WHERE IssueKey = @key";
        command.Parameters.AddWithValue("@key", ticketKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task DeleteWhere(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column} = @v";
        command.Parameters.AddWithValue("@v", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
