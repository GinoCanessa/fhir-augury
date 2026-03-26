using FhirAugury.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Database;

/// <summary>
/// Builds and manages the shared dictionary SQLite database from word list and typo source files.
/// Thread-safe and cross-process safe via file-based locking.
/// </summary>
public static class DictionaryDatabase
{
    private const string WordFilePattern = "*.words.txt";
    private const string TypoFilePattern = "*.typo.txt";
    private const int LockRetryDelayMs = 500;
    private const int LockTimeoutMs = 120_000;
    private const int BatchSize = 5000;

    /// <summary>
    /// Ensures the dictionary database exists, building it from source files if necessary.
    /// Safe to call concurrently from multiple processes — uses file-based locking.
    /// </summary>
    public static async Task EnsureCreatedAsync(DictionaryDatabaseOptions options, ILogger logger, CancellationToken ct = default)
    {
        string sourcePath = Path.GetFullPath(options.SourcePath);
        string dbPath = Path.GetFullPath(options.DatabasePath);
        string lockPath = dbPath + ".lock";

        if (!options.ForceRebuild && File.Exists(dbPath))
        {
            logger.LogInformation("Dictionary database already exists at {Path}, skipping build", dbPath);
            return;
        }

        if (!Directory.Exists(sourcePath))
        {
            logger.LogWarning("Dictionary source directory not found: {Path}. Skipping dictionary build", sourcePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        logger.LogInformation("Acquiring dictionary build lock...");
        await using FileLock fileLock = await AcquireFileLockAsync(lockPath, logger, ct);

        // Double-check after acquiring lock — another process may have built it
        if (!options.ForceRebuild && File.Exists(dbPath))
        {
            logger.LogInformation("Dictionary database was created by another process, skipping build");
            return;
        }

        logger.LogInformation("Building dictionary database from {Source} → {Db}", sourcePath, dbPath);
        BuildDatabase(sourcePath, dbPath, logger);
        logger.LogInformation("Dictionary database build complete");
    }

    /// <summary>
    /// Builds (or rebuilds) the dictionary database from source files.
    /// </summary>
    private static void BuildDatabase(string sourcePath, string dbPath, ILogger logger)
    {
        // Build to a temp file, then move into place atomically
        string tempPath = dbPath + ".tmp";

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

            using (SqliteConnection connection = new SqliteConnection(connectionString))
            {
                connection.Open();

                using SqliteCommand pragma = connection.CreateCommand();
                pragma.CommandText = """
                    PRAGMA journal_mode = DELETE;
                    PRAGMA synchronous = OFF;
                    PRAGMA temp_store = MEMORY;
                    """;
                pragma.ExecuteNonQuery();

                CreateSchema(connection);

                int wordCount = LoadWordFiles(connection, sourcePath, logger);
                int typoCount = LoadTypoFiles(connection, sourcePath, logger);

                logger.LogInformation("Dictionary loaded: {Words} words, {Typos} typos", wordCount, typoCount);
            }

            // Atomic move into final location
            File.Move(tempPath, dbPath, overwrite: true);
        }
        catch
        {
            // Clean up temp file on failure
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup
            }

            throw;
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS words (
                Id INTEGER PRIMARY KEY,
                Word TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS typos (
                Id INTEGER PRIMARY KEY,
                Typo TEXT NOT NULL UNIQUE,
                Correction TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_words_word ON words (Word);
            CREATE INDEX IF NOT EXISTS idx_typos_typo ON typos (Typo);
            CREATE INDEX IF NOT EXISTS idx_typos_correction ON typos (Correction);
            """;
        cmd.ExecuteNonQuery();
    }

    private static int LoadWordFiles(SqliteConnection connection, string sourcePath, ILogger logger)
    {
        string[] wordFiles = Directory.GetFiles(sourcePath, WordFilePattern);
        int totalCount = 0;

        foreach (string filePath in wordFiles)
        {
            logger.LogDebug("Loading words from {File}", Path.GetFileName(filePath));
            int count = 0;
            int batchCount = 0;
            SqliteTransaction transaction = connection.BeginTransaction();

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO words (Word) VALUES (@word);";
            cmd.Transaction = transaction;
            SqliteParameter wordParam = cmd.Parameters.Add("@word", SqliteType.Text);

            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    if (IsSkippableLine(line))
                    {
                        continue;
                    }

                    wordParam.Value = line;
                    cmd.ExecuteNonQuery();
                    count++;
                    batchCount++;

                    if (batchCount >= BatchSize)
                    {
                        transaction.Commit();
                        transaction.Dispose();
                        transaction = connection.BeginTransaction();
                        cmd.Transaction = transaction;
                        batchCount = 0;
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                transaction.Dispose();
            }

            totalCount += count;
            logger.LogDebug("Loaded {Count} words from {File}", count, Path.GetFileName(filePath));
        }

        return totalCount;
    }

    private static int LoadTypoFiles(SqliteConnection connection, string sourcePath, ILogger logger)
    {
        string[] typoFiles = Directory.GetFiles(sourcePath, TypoFilePattern);
        int totalCount = 0;

        foreach (string filePath in typoFiles)
        {
            logger.LogDebug("Loading typos from {File}", Path.GetFileName(filePath));
            int count = 0;
            int batchCount = 0;
            SqliteTransaction transaction = connection.BeginTransaction();

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO typos (Typo, Correction) VALUES (@typo, @correction);";
            cmd.Transaction = transaction;
            SqliteParameter typoParam = cmd.Parameters.Add("@typo", SqliteType.Text);
            SqliteParameter correctionParam = cmd.Parameters.Add("@correction", SqliteType.Text);

            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    if (IsSkippableLine(line))
                    {
                        continue;
                    }

                    string[] parts = line.Split("->", 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2)
                    {
                        logger.LogDebug("Skipping invalid typo line: {Line}", line);
                        continue;
                    }

                    typoParam.Value = parts[0];
                    correctionParam.Value = parts[1];
                    cmd.ExecuteNonQuery();
                    count++;
                    batchCount++;

                    if (batchCount >= BatchSize)
                    {
                        transaction.Commit();
                        transaction.Dispose();
                        transaction = connection.BeginTransaction();
                        cmd.Transaction = transaction;
                        batchCount = 0;
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                transaction.Dispose();
            }

            totalCount += count;
            logger.LogDebug("Loaded {Count} typos from {File}", count, Path.GetFileName(filePath));
        }

        return totalCount;
    }

    private static bool IsSkippableLine(string line)
    {
        return string.IsNullOrWhiteSpace(line)
            || line.StartsWith('#')
            || line.StartsWith('!');
    }

    /// <summary>
    /// Acquires an exclusive file lock, retrying until timeout.
    /// </summary>
    private static async Task<FileLock> AcquireFileLockAsync(string lockPath, ILogger logger, CancellationToken ct)
    {
        long startTicks = Environment.TickCount64;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                FileStream fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new FileLock(fs, lockPath);
            }
            catch (IOException) when (Environment.TickCount64 - startTicks < LockTimeoutMs)
            {
                logger.LogDebug("Dictionary lock held by another process, waiting...");
                await Task.Delay(LockRetryDelayMs, ct);
            }
            catch (IOException)
            {
                throw new TimeoutException(
                    $"Timed out waiting for dictionary build lock after {LockTimeoutMs / 1000}s. " +
                    $"If no other process is building, delete {lockPath} manually.");
            }
        }
    }

    /// <summary>
    /// Disposable wrapper around a file-based lock.
    /// </summary>
    private sealed class FileLock : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly string _path;

        public FileLock(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // Best-effort cleanup of lock file
            }
        }
    }
}
