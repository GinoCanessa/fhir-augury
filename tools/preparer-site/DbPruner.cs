using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

internal static class DbPruner
{
    // Allowlist: columns of jira_processing_source_tickets that the SPA reads.
    // Any future column added to the source table is dropped (safe-for-size)
    // rather than wrongly kept.
    private static readonly string[] KeptSourceTicketColumns =
    {
        "Key",
        "Title",
        "WorkGroup",
        "Status",
        "Type",
    };

    internal static void Prune(string sourceDbPath, string targetDbPath)
    {
        if (File.Exists(targetDbPath))
        {
            File.Delete(targetDbPath);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = targetDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        using SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();

        string escapedSource = sourceDbPath.Replace("'", "''");
        Exec(connection, $"ATTACH DATABASE '{escapedSource}' AS src");
        try
        {
            // prepared_tickets — full table.
            Exec(connection, "CREATE TABLE prepared_tickets AS SELECT * FROM src.prepared_tickets");

            // Child tables — full column set, filtered to in-run keys.
            string[] childTables =
            {
                "prepared_ticket_repos",
                "prepared_ticket_related_jira",
                "prepared_ticket_related_zulip",
                "prepared_ticket_related_github",
            };
            foreach (string child in childTables)
            {
                Exec(connection,
                    $"CREATE TABLE {child} AS SELECT c.* FROM src.{child} c " +
                    "WHERE c.TicketKey IN (SELECT Key FROM prepared_tickets)");
            }

            // jira_processing_source_tickets — allowlisted columns only,
            // filtered to in-run keys.
            string columnList = string.Join(", ", KeptSourceTicketColumns);
            Exec(connection,
                $"CREATE TABLE jira_processing_source_tickets AS SELECT {columnList} " +
                "FROM src.jira_processing_source_tickets " +
                "WHERE Key IN (SELECT Key FROM prepared_tickets)");
        }
        finally
        {
            Exec(connection, "DETACH DATABASE src");
        }

        Exec(connection, "VACUUM");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
