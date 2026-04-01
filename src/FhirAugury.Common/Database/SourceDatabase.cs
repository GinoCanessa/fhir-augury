using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Database;

/// <summary>
/// Base class for per-service SQLite database management.
/// Each source service extends this with its own schema.
/// </summary>
public abstract class SourceDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    /// <summary>Indicates whether this instance has been disposed.</summary>
    protected bool Disposed { get; private set; }

    protected ILogger Logger { get; }

    /// <summary>Default batch size for transaction-batched operations.</summary>
    public const int DefaultBatchSize = 1000;

    protected SourceDatabase(string dbPath, ILogger logger, bool readOnly = false)
    {
        _readOnly = readOnly;
        Logger = logger;

        SqliteOpenMode mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Opens a new SQLite connection with WAL mode and performance pragmas.</summary>
    /// <remarks>
    /// Ideally protected, but kept public because composed services (gRPC, HTTP, indexers)
    /// need direct connection access. A future refactor could introduce a connection factory.
    /// </remarks>
    public SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new SqliteConnection(_connectionString);
        connection.Open();

        if (!_readOnly)
        {
            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA cache_size = -64000;
                PRAGMA temp_store = MEMORY;
                """;
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>
    /// Initializes the database schema. Called on service startup.
    /// Subclasses override to create their specific tables and FTS5 indexes.
    /// </summary>
    public void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        InitializeSchema(connection);
        Logger.LogInformation("Database initialized");
    }

    /// <summary>Override to create source-specific tables, indexes, and FTS5 virtual tables.</summary>
    protected abstract void InitializeSchema(SqliteConnection connection);

    /// <summary>
    /// Returns true if the specified table contains zero rows.
    /// Useful for detecting missing index data on startup.
    /// </summary>
    public bool TableIsEmpty(string tableName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
    }

    /// <summary>
    /// Executes a batch of items, committing every <paramref name="batchSize"/> items.
    /// </summary>
    public static void ExecuteInBatches<T>(
        SqliteConnection connection,
        IEnumerable<T> items,
        Action<SqliteConnection, T> processItem,
        int batchSize = DefaultBatchSize)
    {
        List<T> batch = new List<T>(batchSize);

        foreach (T? item in items)
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

    /// <summary>
    /// Executes an action inside a transaction.
    /// </summary>
    public static void ExecuteInTransaction(SqliteConnection connection, Action<SqliteConnection> action)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
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
    /// Creates an FTS5 virtual table with content-sync triggers.
    /// </summary>
    /// <param name="tokenizer">
    /// Optional FTS5 tokenizer specification (e.g., "porter", "unicode61 remove_diacritics 1").
    /// When null, the SQLite default tokenizer is used.
    /// </param>
    protected static void CreateFts5Table(
        SqliteConnection connection,
        string ftsTableName,
        string contentTable,
        string contentRowId,
        string[] indexedColumns,
        string? tokenizer = null)
    {
        string columnList = string.Join(", ", indexedColumns);
        string sourceColumns = string.Join(", ", indexedColumns.Select(c => $"new.{c}"));
        string oldColumns = string.Join(", ", indexedColumns.Select(c => $"old.{c}"));
        string tokenizeClause = tokenizer is not null ? $", tokenize='{tokenizer}'" : "";

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName}
            USING fts5({columnList}, content={contentTable}, content_rowid={contentRowId}{tokenizeClause});

            CREATE TRIGGER IF NOT EXISTS {ftsTableName}_ai AFTER INSERT ON {contentTable} BEGIN
                INSERT INTO {ftsTableName}(rowid, {columnList}) VALUES (new.{contentRowId}, {sourceColumns});
            END;

            CREATE TRIGGER IF NOT EXISTS {ftsTableName}_ad AFTER DELETE ON {contentTable} BEGIN
                INSERT INTO {ftsTableName}({ftsTableName}, rowid, {columnList}) VALUES('delete', old.{contentRowId}, {oldColumns});
            END;

            CREATE TRIGGER IF NOT EXISTS {ftsTableName}_au AFTER UPDATE ON {contentTable} BEGIN
                INSERT INTO {ftsTableName}({ftsTableName}, rowid, {columnList}) VALUES('delete', old.{contentRowId}, {oldColumns});
                INSERT INTO {ftsTableName}(rowid, {columnList}) VALUES (new.{contentRowId}, {sourceColumns});
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rebuilds an FTS5 index from its content table.</summary>
    protected static void RebuildFts5(SqliteConnection connection, string ftsTableName)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {ftsTableName}({ftsTableName}) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the database file size in bytes, or 0 for in-memory databases.</summary>
    public long GetDatabaseSizeBytes()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA page_count;";
        long pageCount = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA page_size;";
        long pageSize = Convert.ToInt64(cmd.ExecuteScalar());
        return pageCount * pageSize;
    }

    /// <summary>
    /// Lightweight connectivity/liveness check suitable for frequent health probes.
    /// Uses a trivial query instead of a full integrity scan.
    /// </summary>
    public string QuickCheck()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1;";
        object? result = cmd.ExecuteScalar();
        return result is not null ? "ok" : "unknown";
    }

    /// <summary>
    /// Runs a full PRAGMA integrity_check. This scans every page of the database
    /// and can be very slow on large databases. Use only for diagnostics, not health probes.
    /// </summary>
    public string CheckIntegrity()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return cmd.ExecuteScalar()?.ToString() ?? "unknown";
    }

    private static void FlushBatch<T>(
        SqliteConnection connection,
        List<T> batch,
        Action<SqliteConnection, T> processItem)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SAVEPOINT batch_insert;";
        cmd.ExecuteNonQuery();

        try
        {
            foreach (T? item in batch)
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

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Override in subclasses to release resources.</summary>
    protected virtual void Dispose(bool disposing)
    {
        Disposed = true;
    }
}
