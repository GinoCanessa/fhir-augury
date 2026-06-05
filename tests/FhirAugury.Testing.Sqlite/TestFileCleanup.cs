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
    /// Clears all SQLite connection pools. Note that <see cref="SqliteConnection.ClearAllPools"/>
    /// is a process-global operation that can race with active connections in parallel xUnit
    /// classes. The retry-based <see cref="SafeDeleteFile"/> / <see cref="SafeDeleteDirectory"/>
    /// helpers call this only on retry (not eagerly) to minimize the race window. We deliberately
    /// do NOT force a GC pass here — forcing finalization while another thread holds an open
    /// <see cref="SqliteConnection"/> can surface as <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c>.
    /// </summary>
    public static void ClearSqlitePools()
    {
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Clears SQLite pools, forces finalization, and retries recursive directory delete
    /// a few times to tolerate file handles still being released.
    /// The first attempt does NOT clear pools, to avoid disrupting connections held
    /// by parallel tests; pools are cleared lazily only when a retry is needed.
    /// No-ops when the path is null/empty or the directory does not exist.
    /// </summary>
    public static void SafeDeleteDirectory(string path, int maxAttempts = 5)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                ClearSqlitePools();
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                ClearSqlitePools();
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Clears SQLite pools, forces finalization, and retries deletion of a single
    /// SQLite database file a few times to tolerate file handles still being released.
    /// Also best-effort deletes the matching <c>-wal</c> and <c>-shm</c> sidecar
    /// files written by SQLite's WAL journal mode.
    /// The first attempt does NOT clear pools, to avoid disrupting connections held
    /// by parallel tests; pools are cleared lazily only when a retry is needed.
    /// No-ops when the path is null/empty or the file does not exist.
    /// </summary>
    public static void SafeDeleteFile(string path, int maxAttempts = 5)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                break;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                ClearSqlitePools();
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                ClearSqlitePools();
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
