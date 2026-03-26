using FhirAugury.Common.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Confluence.Database;

/// <summary>Confluence-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class ConfluenceDatabase : SourceDatabase
{
    private readonly string? _ftsTokenizer;

    public ConfluenceDatabase(string dbPath, ILogger<ConfluenceDatabase> logger, bool readOnly = false, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        ConfluenceSpaceRecord.CreateTable(connection);
        ConfluencePageRecord.CreateTable(connection);
        ConfluenceCommentRecord.CreateTable(connection);
        ConfluencePageLinkRecord.CreateTable(connection);
        ConfluenceSyncStateRecord.CreateTable(connection);
        ConfluenceKeywordRecord.CreateTable(connection);
        ConfluenceCorpusKeywordRecord.CreateTable(connection);
        ConfluenceDocStatsRecord.CreateTable(connection);
        ConfluenceJiraRefRecord.CreateTable(connection);

        CreateConfluencePagesFts(connection);
    }

    private void CreateConfluencePagesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "confluence_pages_fts",
            contentTable: "confluence_pages",
            contentRowId: "Id",
            indexedColumns: ["BodyPlain", "Title", "Labels"],
            tokenizer: _ftsTokenizer);
    }

    /// <summary>Rebuilds the FTS5 index from the content table.</summary>
    public void RebuildFtsIndexes()
    {
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "confluence_pages_fts");
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase()
    {
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS confluence_pages_fts;
            DROP TABLE IF EXISTS confluence_pages;
            DROP TABLE IF EXISTS confluence_spaces;
            DROP TABLE IF EXISTS confluence_comments;
            DROP TABLE IF EXISTS confluence_page_links;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            DROP TABLE IF EXISTS confluence_jira_refs;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
