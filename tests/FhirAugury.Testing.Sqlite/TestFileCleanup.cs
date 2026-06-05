using Microsoft.Data.Sqlite;

namespace FhirAugury.Testing.Sqlite;

/// <summary>
/// Helpers for cleaning up temp SQLite databases in test teardown.
/// Microsoft.Data.Sqlite's connection pooling and shared cache can delay release of
/// native file handles even after connections are disposed, causing transient
/// IOExceptions on Windows when parallel tests race to delete their temp directories.
/// </summary>
public static class TestFileCleanup
{
    /// <summary>
    /// Clears all SQLite connection pools and forces a full GC pass so finalizers
    /// release any lingering native file handles before a delete is attempted.
    /// </summary>
    public static void ClearSqlitePools()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Clears SQLite pools, forces finalization, and retries recursive directory delete
    /// a few times to tolerate file handles still being released.
    /// No-ops when the path is null/empty or the directory does not exist.
    /// </summary>
    public static void SafeDeleteDirectory(string path, int maxAttempts = 5)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        ClearSqlitePools();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Clears SQLite pools, forces finalization, and retries deletion of a single
    /// SQLite database file a few times to tolerate file handles still being released.
    /// Also best-effort deletes the matching <c>-wal</c> and <c>-shm</c> sidecar
    /// files written by SQLite's WAL journal mode.
    /// No-ops when the path is null/empty or the file does not exist.
    /// </summary>
    public static void SafeDeleteFile(string path, int maxAttempts = 5)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        ClearSqlitePools();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                break;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }

        TryBestEffortDelete(path + "-wal");
        TryBestEffortDelete(path + "-shm");
    }

    private static void TryBestEffortDelete(string sidecar)
    {
        try
        {
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }
        catch
        {
            // Best-effort sidecar cleanup; swallowed by design.
        }
    }
}
