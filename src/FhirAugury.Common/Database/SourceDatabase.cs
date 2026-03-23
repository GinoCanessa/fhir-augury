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

        var mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate;
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
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

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

        return connection;
    }

    /// <summary>
    /// Initializes the database schema. Called on service startup.
    /// Subclasses override to create their specific tables and FTS5 indexes.
    /// </summary>
    public void Initialize()
    {
        using var connection = OpenConnection();
        InitializeSchema(connection);
        Logger.LogInformation("Database initialized");
    }

    /// <summary>Override to create source-specific tables, indexes, and FTS5 virtual tables.</summary>
    protected abstract void InitializeSchema(SqliteConnection connection);

    /// <summary>
    /// Executes a batch of items, committing every <paramref name="batchSize"/> items.
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

    /// <summary>
    /// Executes an action inside a transaction.
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
    /// Creates an FTS5 virtual table with content-sync triggers.
    /// </summary>
    protected static void CreateFts5Table(
        SqliteConnection connection,
        string ftsTableName,
        string contentTable,
        string contentRowId,
        string[] indexedColumns)
    {
        var columnList = string.Join(", ", indexedColumns);
        var sourceColumns = string.Join(", ", indexedColumns.Select(c => $"new.{c}"));
        var oldColumns = string.Join(", ", indexedColumns.Select(c => $"old.{c}"));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName}
            USING fts5({columnList}, content={contentTable}, content_rowid={contentRowId});

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
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {ftsTableName}({ftsTableName}) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the database file size in bytes, or 0 for in-memory databases.</summary>
    public long GetDatabaseSizeBytes()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA page_count;";
        var pageCount = Convert.ToInt64(cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA page_size;";
        var pageSize = Convert.ToInt64(cmd.ExecuteScalar());
        return pageCount * pageSize;
    }

    /// <summary>Runs PRAGMA integrity_check.</summary>
    public string CheckIntegrity()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return cmd.ExecuteScalar()?.ToString() ?? "unknown";
    }

    private static void FlushBatch<T>(
        SqliteConnection connection,
        List<T> batch,
        Action<SqliteConnection, T> processItem)
    {
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
