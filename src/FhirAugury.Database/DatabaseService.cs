using Microsoft.Data.Sqlite;
using FhirAugury.Database.Records;

namespace FhirAugury.Database;

/// <summary>Manages the SQLite database connection and initialization.</summary>
public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    /// <summary>Default batch size for transaction-batched inserts.</summary>
    public const int DefaultBatchSize = 1000;

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
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA cache_size = -64000;
                PRAGMA temp_store = MEMORY;
                """;
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

        ZulipStreamRecord.CreateTable(connection);
        ZulipStreamRecord.LoadMaxKey(connection);

        ZulipMessageRecord.CreateTable(connection);
        ZulipMessageRecord.LoadMaxKey(connection);

        // Phase 5: Confluence tables
        ConfluenceSpaceRecord.CreateTable(connection);
        ConfluenceSpaceRecord.LoadMaxKey(connection);

        ConfluencePageRecord.CreateTable(connection);
        ConfluencePageRecord.LoadMaxKey(connection);

        ConfluenceCommentRecord.CreateTable(connection);
        ConfluenceCommentRecord.LoadMaxKey(connection);

        // Phase 5: GitHub tables
        GitHubRepoRecord.CreateTable(connection);
        GitHubRepoRecord.LoadMaxKey(connection);

        GitHubIssueRecord.CreateTable(connection);
        GitHubIssueRecord.LoadMaxKey(connection);

        GitHubCommentRecord.CreateTable(connection);
        GitHubCommentRecord.LoadMaxKey(connection);

        // Phase 3: Cross-reference and BM25 tables
        CrossRefLinkRecord.CreateTable(connection);
        CrossRefLinkRecord.LoadMaxKey(connection);

        KeywordRecord.CreateTable(connection);
        KeywordRecord.LoadMaxKey(connection);

        CorpusKeywordRecord.CreateTable(connection);
        CorpusKeywordRecord.LoadMaxKey(connection);

        DocStatsRecord.CreateTable(connection);
        DocStatsRecord.LoadMaxKey(connection);

        // Create FTS5 virtual tables and triggers
        FtsSetup.CreateJiraFts(connection);
        FtsSetup.CreateZulipFts(connection);
        FtsSetup.CreateConfluenceFts(connection);
        FtsSetup.CreateGitHubFts(connection);
    }

    /// <summary>
    /// Executes an action inside a BEGIN IMMEDIATE transaction.
    /// Automatically commits on success or rolls back on failure.
    /// Note: The action should use raw SQL commands rather than source-generated
    /// Insert/Update methods, as those create their own transactions.
    /// </summary>
    public static void ExecuteInTransaction(SqliteConnection connection, Action<SqliteConnection> action)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            action(connection);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Executes a batch of items, committing every <paramref name="batchSize"/> items.
    /// Uses savepoints to avoid nested transaction conflicts with source-generated code.
    /// </summary>
    public static void ExecuteInBatches<T>(
        SqliteConnection connection,
        IEnumerable<T> items,
        Action<SqliteConnection, T> processItem,
        int batchSize = DefaultBatchSize)
    {
        var batch = new List<T>(batchSize);

        foreach (var item in items)
        {
            batch.Add(item);
            if (batch.Count >= batchSize)
            {
                FlushBatch(connection, batch, processItem);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            FlushBatch(connection, batch, processItem);
        }
    }

    private static void FlushBatch<T>(
        SqliteConnection connection,
        List<T> batch,
        Action<SqliteConnection, T> processItem)
    {
        // Use raw BEGIN/COMMIT to wrap the batch — the source-generated Insert
        // methods call BeginTransaction() internally, but SQLite ignores nested
        // BEGIN when one is already active (it becomes a no-op in WAL mode).
        // Instead, we wrap with SAVEPOINT which is safe for nesting.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SAVEPOINT batch_insert;";
        cmd.ExecuteNonQuery();

        try
        {
            foreach (var item in batch)
            {
                processItem(connection, item);
            }

            cmd.CommandText = "RELEASE SAVEPOINT batch_insert;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            cmd.CommandText = "ROLLBACK TO SAVEPOINT batch_insert;";
            cmd.ExecuteNonQuery();
            throw;
        }
    }

    /// <summary>
    /// Runs PRAGMA integrity_check and returns the result.
    /// Returns "ok" if the database is healthy.
    /// </summary>
    public string CheckIntegrity()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar()?.ToString() ?? "unknown";
        return result;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
