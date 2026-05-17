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

        DbShape shape = await DetectShapeAsync(sourceDbPath, ct).ConfigureAwait(false);
        if (shape != DbShape.LegacyMissingHydration)
        {
            // Modern DB (hydration present) or not-a-preparer-DB
            // (no `prepared_tickets` table): pass the source path
            // through untouched. Modern DBs need no work; truly broken
            // DBs are left to surface a clean schema error from the
            // first downstream stage that reads `prepared_tickets`.
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

    private enum DbShape
    {
        Modern,
        LegacyMissingHydration,
        NotAPreparerDb,
    }

    private static async Task<DbShape> DetectShapeAsync(string sourceDbPath, CancellationToken ct)
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
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name IN ('prepared_tickets', 'prepared_ticket_hydration')";

        bool hasPreparedTickets = false;
        bool hasHydration = false;
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            if (name == "prepared_tickets") hasPreparedTickets = true;
            else if (name == "prepared_ticket_hydration") hasHydration = true;
        }

        if (hasHydration) return DbShape.Modern;
        if (hasPreparedTickets) return DbShape.LegacyMissingHydration;
        return DbShape.NotAPreparerDb;
    }
}
