using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests;

internal static class TestDbCleanup
{
    // Number of attempts and per-attempt delay chosen to cover the
    // ~tens-of-ms window between SqliteConnection.ClearAllPools()
    // returning and the OS releasing the underlying file handle on
    // Windows. Total worst case ≈ 250 ms per fixture Dispose.
    private const int MaxAttempts = 5;
    private const int DelayMillisecondsBetweenAttempts = 50;

    public static void DeleteDatabaseFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        SqliteConnection.ClearAllPools();
        TryDeleteWithRetries(() =>
        {
            if (File.Exists(path)) File.Delete(path);
        });
    }

    public static void DeleteDirectoryTree(string root)
    {
        if (string.IsNullOrEmpty(root)) return;
        SqliteConnection.ClearAllPools();
        TryDeleteWithRetries(() =>
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        });
    }

    private static void TryDeleteWithRetries(Action delete)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(DelayMillisecondsBetweenAttempts);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(DelayMillisecondsBetweenAttempts);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
