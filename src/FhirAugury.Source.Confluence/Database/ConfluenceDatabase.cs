using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
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
        // The generated CreateTable is create-if-not-exists, so a column added
        // to an existing table would never appear — and the generated
        // CREATE INDEX on it would then fail. This runs inside db.Initialize()
        // in the DI factory, i.e. before Kestrel binds, so the service would not
        // start and /api/v1/rebuild would not be reachable to fix it.
        MigrateConfluencePagesStatus(connection);

        ConfluenceSpaceRecord.CreateTable(connection);
        ConfluencePageRecord.CreateTable(connection);
        ConfluenceCommentRecord.CreateTable(connection);
        ConfluenceAttachmentRecord.CreateTable(connection);
        ConfluencePageLinkRecord.CreateTable(connection);
        ConfluenceSyncStateRecord.CreateTable(connection);
        ConfluenceKeywordRecord.CreateTable(connection);
        ConfluenceCorpusKeywordRecord.CreateTable(connection);
        ConfluenceDocStatsRecord.CreateTable(connection);

        // Shared cross-reference tables
        JiraXRefRecord.CreateTable(connection);
        ZulipXRefRecord.CreateTable(connection);
        GitHubXRefRecord.CreateTable(connection);
        FhirElementXRefRecord.CreateTable(connection);

        CreateConfluencePagesFts(connection);
    }

    /// <summary>
    /// Adds <c>confluence_pages.Status</c> to a database created before it
    /// existed. The subsequent replay overwrites the default with the real
    /// value.
    /// </summary>
    /// <remarks>
    /// Adding a non-indexed column does not require rebuilding
    /// <c>confluence_pages_fts</c>: that FTS5 table indexes only
    /// <c>BodyPlain</c>, <c>Title</c> and <c>Labels</c>.
    /// </remarks>
    private static void MigrateConfluencePagesStatus(SqliteConnection connection)
    {
        if (!TableExists(connection, "confluence_pages") || ColumnExists(connection, "confluence_pages", "Status"))
        {
            return;
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "ALTER TABLE confluence_pages ADD COLUMN Status TEXT NOT NULL DEFAULT 'current'";
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        cmd.CommandText = "SELECT COUNT(*) FROM confluence_pages";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
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
            DROP TABLE IF EXISTS confluence_attachments;
            DROP TABLE IF EXISTS confluence_page_links;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            DROP TABLE IF EXISTS xref_jira;
            DROP TABLE IF EXISTS xref_zulip;
            DROP TABLE IF EXISTS xref_github;
            DROP TABLE IF EXISTS xref_fhir_element;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
