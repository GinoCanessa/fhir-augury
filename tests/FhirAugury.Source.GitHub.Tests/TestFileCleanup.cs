using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Helpers for cleaning up temp SQLite databases in test teardown.
/// Microsoft.Data.Sqlite's connection pooling and shared cache can delay release of
/// native file handles even after connections are disposed, causing transient
/// IOExceptions on Windows when parallel tests race to delete their temp directories.
/// </summary>
internal static class TestFileCleanup
{
    /// <summary>
    /// Clears SQLite pools, forces finalization, and retries recursive directory delete
    /// a few times to tolerate file handles still being released.
    /// </summary>
    public static void SafeDeleteDirectory(string path, int maxAttempts = 5)
    {
        if (!Directory.Exists(path))
            return;

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

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
}
