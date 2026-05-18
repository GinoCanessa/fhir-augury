using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

internal static class PreparerDbTrimmer
{
    private static readonly string[] ChildTablesByTicketKey =
    [
        "prepared_ticket_related_jira",
        "prepared_ticket_related_github",
        "prepared_ticket_related_zulip",
        "prepared_ticket_jira_xref",
        "prepared_ticket_hydration",
        "prepared_jira_hydration",
        "prepared_zulip_hydration",
        "prepared_github_hydration",
        "prepared_repo_hydration",
        "prepared_ticket_repos",
    ];

    public sealed record BuildResult(byte[] DbBytes, long SurvivingTicketCount);

    /// <summary>
    /// Copies the source preparer DB to a temp file, runs the filter-aware
    /// trim (which is a no-op when all <paramref name="filters"/> are
    /// inactive — the WHERE predicates collapse to TRUE), vacuums, and
    /// returns the resulting bytes along with the surviving ticket count.
    /// The pipeline always runs so downstream backfill steps can hang off
    /// of it; with no filters the surviving count equals the source count.
    /// </summary>
    public static async Task<BuildResult> BuildAsync(
        string sourceDbPath,
        ResolvedFilters filters,
        CancellationToken ct)
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            File.Copy(sourceDbPath, tempPath, overwrite: true);

            long surviving;
            {
                SqliteConnectionStringBuilder builder = new()
                {
                    DataSource = tempPath,
                    Mode = SqliteOpenMode.ReadWrite,
                };
                await using SqliteConnection connection = new(builder.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);

                await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Trim prepared_tickets to the intersection of all active filters.
                // Bind each filter as NULL when inactive so its predicate collapses to TRUE.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        DELETE FROM prepared_tickets WHERE Key NOT IN (
                          SELECT pt.Key FROM prepared_tickets pt
                          LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key
                          LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt.Key
                          WHERE (@project IS NULL OR LOWER(jst.Project) = LOWER(@project))
                            AND (@wg      IS NULL OR LOWER(jst.WorkGroup) = LOWER(@wg))
                            AND (@spec    IS NULL OR LOWER(pth.Specification) = LOWER(@spec))
                        )
                        """;
                    cmd.Parameters.AddWithValue("@project", (object?)filters.Project ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@wg", (object?)filters.WorkGroup ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@spec", (object?)filters.Specification ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (string table in ChildTablesByTicketKey)
                {
                    await using SqliteCommand cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        $"DELETE FROM {table} WHERE TicketKey NOT IN (SELECT Key FROM prepared_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM jira_processing_source_tickets " +
                        "WHERE Key NOT IN (SELECT Key FROM prepared_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT COUNT(*) FROM prepared_tickets";
                    object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    surviving = value is long l ? l : Convert.ToInt64(value);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            // VACUUM cannot run inside a transaction and there is no prior repo
            // precedent for it; reopen a fresh connection to run it standalone.
            {
                SqliteConnectionStringBuilder builder = new()
                {
                    DataSource = tempPath,
                    Mode = SqliteOpenMode.ReadWrite,
                };
                await using SqliteConnection connection = new(builder.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = "VACUUM";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Close all pooled connections to the temp file before reading bytes,
            // so no .db-wal/.db-shm sidecar files linger past the read.
            SqliteConnection.ClearAllPools();

            byte[] bytes = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
            return new BuildResult(bytes, surviving);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }
}
