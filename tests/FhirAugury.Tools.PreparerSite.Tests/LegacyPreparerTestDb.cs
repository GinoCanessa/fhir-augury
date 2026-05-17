using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite.Tests;

// Intentionally omits prepared_*_hydration and prepared_ticket_jira_xref to
// mirror the legacy on-disk shape from the bug report (pre-9f068f5).
// Uses only raw SqliteConnection/SqliteCommand so the test exercises the
// "PreparerDatabase has never been instantiated against this file" path that
// preparer-site actually hits in production.
internal static class LegacyPreparerTestDb
{
    public static async Task SeedAsync(
        string dbPath,
        IReadOnlyList<PreparerTestDb.SourceTicketSeed> tickets)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();

        string[] createStatements =
        [
            // jira_processing_source_tickets has long included SourceTicketShape; the
            // legacy DB shape from the bug report omits only the hydration / xref
            // tables (added in 9f068f5), not this column. Including it keeps
            // EnsureSchema's EnsureCompositeUniqueIndex pass valid against the seed.
            "CREATE TABLE jira_processing_source_tickets (Key TEXT PRIMARY KEY, Project TEXT, WorkGroup TEXT, SourceTicketShape TEXT)",
            "CREATE TABLE prepared_tickets (Key TEXT PRIMARY KEY)",
            "CREATE TABLE prepared_ticket_repos (TicketKey TEXT)",
            "CREATE TABLE prepared_ticket_related_jira (TicketKey TEXT)",
            "CREATE TABLE prepared_ticket_related_zulip (TicketKey TEXT)",
            "CREATE TABLE prepared_ticket_related_github (TicketKey TEXT)",
        ];
        foreach (string sql in createStatements)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (PreparerTestDb.SourceTicketSeed ticket in tickets)
        {
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO prepared_tickets (Key) VALUES (@key)";
                cmd.Parameters.AddWithValue("@key", ticket.Key);
                await cmd.ExecuteNonQueryAsync();
            }

            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO jira_processing_source_tickets (Key, Project, WorkGroup, SourceTicketShape) " +
                    "VALUES (@key, @project, @wg, 'default')";
                cmd.Parameters.AddWithValue("@key", ticket.Key);
                cmd.Parameters.AddWithValue("@project", ticket.Project);
                cmd.Parameters.AddWithValue("@wg", ticket.WorkGroup);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await using SqliteConnection cp = new($"Data Source={dbPath}");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }
}
