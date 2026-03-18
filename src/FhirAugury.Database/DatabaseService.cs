using Microsoft.Data.Sqlite;
using FhirAugury.Database.Records;

namespace FhirAugury.Database;

/// <summary>Manages the SQLite database connection and initialization.</summary>
public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    public DatabaseService(string dbPath, bool readOnly = false)
    {
        _readOnly = readOnly;
        var mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Creates a new connection string for in-memory databases (testing).</summary>
    public static DatabaseService CreateInMemory()
    {
        var service = new DatabaseService(":memory:");
        return service;
    }

    /// <summary>Opens a new SQLite connection.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Initializes the database schema: tables, indexes, FTS5, and triggers.</summary>
    public void InitializeDatabase()
    {
        using var connection = OpenConnection();

        if (!_readOnly)
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        // Create core tables via source-generated methods
        SyncStateRecord.CreateTable(connection);
        SyncStateRecord.LoadMaxKey(connection);

        IngestionLogRecord.CreateTable(connection);
        IngestionLogRecord.LoadMaxKey(connection);

        JiraIssueRecord.CreateTable(connection);
        JiraIssueRecord.LoadMaxKey(connection);

        JiraCommentRecord.CreateTable(connection);
        JiraCommentRecord.LoadMaxKey(connection);

        // Create FTS5 virtual tables and triggers
        FtsSetup.CreateJiraFts(connection);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
