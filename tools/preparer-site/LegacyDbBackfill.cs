using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

/// <summary>
/// Pre-flights the preparer-site source DB for the post-9f068f5 hydration
/// schema. Modern DBs (the table is present) pass through untouched and every
/// downstream stage reads the user-supplied path directly. Legacy DBs are
/// copied once into a shared temp file, schema-backfilled via
/// <see cref="PreparerDatabase.EnsureSchema(SqliteConnection)"/>, and that
/// temp path is what downstream stages see. The temp file is best-effort
/// deleted when the returned <see cref="Result"/> is disposed.
/// </summary>
internal static class LegacyDbBackfill
{
    internal sealed record Result(string EffectivePath, string? TempPath) : IDisposable
    {
        public void Dispose()
        {
            if (TempPath is null)
            {
                return;
            }

            try { File.Delete(TempPath); } catch { /* best-effort */ }
        }
    }

    public static async Task<Result> PrepareAsync(string sourceDbPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDbPath);

        if (await HasHydrationTableAsync(sourceDbPath, ct).ConfigureAwait(false))
        {
            return new Result(sourceDbPath, TempPath: null);
        }

        string tempPath = Path.GetTempFileName();
        try
        {
            File.Copy(sourceDbPath, tempPath, overwrite: true);

            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWrite,
            };
            await using (SqliteConnection connection = new(builder.ConnectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                PreparerDatabase.EnsureSchema(connection);
            }

            // Release any pooled handles to the temp file so downstream
            // consumers can open it without sidecar-file contention.
            SqliteConnection.ClearAllPools();

            return new Result(tempPath, TempPath: tempPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    private static async Task<bool> HasHydrationTableAsync(string sourceDbPath, CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'prepared_ticket_hydration'";
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        long count = value is long l ? l : Convert.ToInt64(value);
        return count >= 1;
    }
}
