using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database;

/// <summary>
/// Read/write access to the <c>repo_baselines</c> table. Used by the workspace manager
/// after a successful baseline build and by <c>BaselineSyncService</c> to gate the next
/// rebuild on <c>BaselineMinSyncAge</c>.
/// </summary>
public sealed class RepoBaselineStore
{
    private readonly string _dbPath;

    public RepoBaselineStore(ApplierDatabase database)
    {
        _dbPath = database.DatabasePath;
    }

    public RepoBaselineStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task<RepoBaselineRecord?> GetAsync(string repoKey, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM repo_baselines WHERE RepoKey = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", repoKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new RepoBaselineRecord
        {
            RowId = reader.GetInt32(reader.GetOrdinal("RowId")),
            RepoKey = reader.GetString(reader.GetOrdinal("RepoKey")),
            BaselineCommitSha = reader.GetString(reader.GetOrdinal("BaselineCommitSha")),
            LastBuiltAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastBuiltAt"))),
        };
    }

    public async Task UpsertAsync(string repoKey, string commitSha, DateTimeOffset builtAt, CancellationToken ct)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repo_baselines (RepoKey, BaselineCommitSha, LastBuiltAt)
            VALUES (@key, @sha, @at)
            ON CONFLICT(RepoKey) DO UPDATE SET
                BaselineCommitSha = excluded.BaselineCommitSha,
                LastBuiltAt = excluded.LastBuiltAt
            """;
        command.Parameters.AddWithValue("@key", repoKey);
        command.Parameters.AddWithValue("@sha", commitSha);
        command.Parameters.AddWithValue("@at", builtAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
