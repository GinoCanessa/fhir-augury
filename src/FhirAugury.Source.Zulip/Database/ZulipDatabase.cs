using FhirAugury.Common.Database;
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
        ZulipMessageTicketRecord.CreateTable(connection);
        ZulipThreadTicketRecord.CreateTable(connection);
        ZulipSyncStateRecord.CreateTable(connection);
        ZulipKeywordRecord.CreateTable(connection);
        ZulipCorpusKeywordRecord.CreateTable(connection);
        ZulipDocStatsRecord.CreateTable(connection);

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

    /// <summary>Rebuilds the FTS5 index from the content table.</summary>
    public void RebuildFtsIndexes()
    {
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "zulip_messages_fts");
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
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
