using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Zulip.Database;

/// <summary>Zulip-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class ZulipDatabase : SourceDatabase
{
    private readonly string? _ftsTokenizer;

    public ZulipDatabase(string dbPath, ILogger<ZulipDatabase> logger, bool readOnly = false, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        ZulipStreamRecord.CreateTable(connection);
        ZulipMessageRecord.CreateTable(connection);
        ZulipThreadTicketRecord.CreateTable(connection);
        ZulipSyncStateRecord.CreateTable(connection);
        ZulipKeywordRecord.CreateTable(connection);
        ZulipCorpusKeywordRecord.CreateTable(connection);
        ZulipDocStatsRecord.CreateTable(connection);

        // Shared cross-reference tables
        JiraXRefRecord.CreateTable(connection);
        GitHubXRefRecord.CreateTable(connection);
        ConfluenceXRefRecord.CreateTable(connection);
        FhirElementXRefRecord.CreateTable(connection);

        MigrateSchema(connection);
        CreateZulipMessagesFts(connection);
    }

    private void CreateZulipMessagesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "zulip_messages_fts",
            contentTable: "zulip_messages",
            contentRowId: "Id",
            indexedColumns: ["ContentPlain", "Topic"],
            tokenizer: _ftsTokenizer);
    }

    /// <summary>
    /// Applies schema migrations for columns added after initial release.
    /// Safe to call repeatedly; each migration checks before altering.
    /// </summary>
    private static void MigrateSchema(SqliteConnection connection)
    {
        // Migration: add BaselineValue column to zulip_streams (default 5)
        if (!ColumnExists(connection, "zulip_streams", "BaselineValue"))
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE zulip_streams ADD COLUMN BaselineValue INTEGER NOT NULL DEFAULT 5;";
            cmd.ExecuteNonQuery();
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column) return true;
        }
        return false;
    }

    /// <summary>Rebuilds the FTS5 index from the content table.</summary>
    public void RebuildFtsIndexes()
    {
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "zulip_messages_fts");
    }

    /// <summary>
    /// Check if the primary content table of this database is empty
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    public bool PrimaryContentTableIsEmpty(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM zulip_messages";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase()
    {
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS zulip_messages_fts;
            DROP TABLE IF EXISTS zulip_thread_tickets;
            DROP TABLE IF EXISTS zulip_message_tickets;
            DROP TABLE IF EXISTS zulip_messages;
            DROP TABLE IF EXISTS zulip_streams;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            DROP TABLE IF EXISTS xref_jira;
            DROP TABLE IF EXISTS xref_github;
            DROP TABLE IF EXISTS xref_confluence;
            DROP TABLE IF EXISTS xref_fhir_element;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
