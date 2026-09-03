using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public sealed class StagingDatabase : IDisposable
{
    private bool _closed;

    private StagingDatabase(string path, PreparerDatabase database)
    {
        Path = path;
        Database = database;
    }

    public string Path { get; }
    public PreparerDatabase Database { get; }

    public static StagingDatabase Create(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Staging path has no parent: {fullPath}");
        Directory.CreateDirectory(directory);
        foreach (string candidate in new[] { fullPath, $"{fullPath}-wal", $"{fullPath}-shm" })
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                throw new IOException($"Staging residue will not be reused: {candidate}");
            }
        }

        using (new FileStream(
                   fullPath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None,
                   1,
                   FileOptions.WriteThrough))
        {
        }

        PreparerDatabase database = new(
            fullPath,
            NullLogger<PreparerDatabase>.Instance);
        try
        {
            database.Initialize();
            return new StagingDatabase(fullPath, database);
        }
        catch
        {
            database.Dispose();
            DeleteIfPresent(fullPath);
            DeleteIfPresent($"{fullPath}-wal");
            DeleteIfPresent($"{fullPath}-shm");
            throw;
        }
    }

    public string CloseCheckpointAndHash()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Staging database is already closed.");
        }

        Database.Dispose();
        _closed = true;
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using (SqliteConnection connection = new(connectionString))
        {
            connection.Open();
            using SqliteCommand checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using SqliteDataReader reader = checkpoint.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 0)
            {
                throw new InvalidOperationException(
                    "Could not checkpoint the staging database because it is still busy.");
            }

            reader.Close();
            using SqliteCommand journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            string mode = journal.ExecuteScalar()?.ToString() ?? string.Empty;
            if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Could not leave WAL mode before promotion; SQLite returned '{mode}'.");
            }
        }

        RequireNoSidecarDependency($"{Path}-wal", isWal: true);
        RequireNoSidecarDependency($"{Path}-shm", isWal: false);
        using (new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        return ImportFileHash.ComputeSha256(Path);
    }

    public void Dispose()
    {
        if (!_closed)
        {
            Database.Dispose();
            _closed = true;
        }
    }

    public static IReadOnlyList<string> DeleteClosedFiles(string mainPath)
    {
        List<string> errors = [];
        foreach (string path in new[] { mainPath, $"{mainPath}-wal", $"{mainPath}-shm" })
        {
            try
            {
                DeleteIfPresent(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return errors;
    }

    private static void RequireNoSidecarDependency(string path, bool isWal)
    {
        if (!File.Exists(path))
        {
            return;
        }

        long length = new FileInfo(path).Length;
        if (isWal && length > 0)
        {
            throw new InvalidOperationException(
                $"Checkpoint left a non-empty WAL sidecar: {path}");
        }

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
        File.Delete(path);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
