using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Caching;

/// <summary>
/// Writes files atomically using a temp-file-then-move pattern.
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Writes content to <paramref name="path"/> atomically by writing to a temporary
    /// file first, then moving it into place.
    /// </summary>
    public static async Task WriteAsync(
        string path,
        Func<Stream, Task> writeAction,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        try
        {
            await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await writeAction(fs);
            }
            MoveWithRetry(tempPath, path, logger);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to clean up temp file '{TempPath}'", tempPath);
            }
            throw;
        }
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/>,
    /// retrying briefly on a transient sharing failure.
    /// </summary>
    /// <remarks>
    /// Readers open cached files with <c>FileShare.Delete</c>, which is what
    /// normally lets this move land while a reader holds the file. It is not
    /// quite sufficient on Windows: replacing a file whose previous incarnation
    /// is still delete-pending (a reader has not yet closed it) fails with
    /// <see cref="UnauthorizedAccessException"/> because the name is still in
    /// use. That window is sub-millisecond, so a short bounded retry closes it
    /// rather than surfacing a spurious failure to the caller — which matters
    /// most when a completeness report is being read during an ingestion run.
    /// </remarks>
    private static void MoveWithRetry(string source, string destination, ILogger? logger)
    {
        const int MaxAttempts = 10;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is UnauthorizedAccessException or IOException)
            {
                logger?.LogDebug(
                    "Atomic replace of '{Destination}' contended (attempt {Attempt}); retrying",
                    destination, attempt);
                Thread.Sleep(Random.Shared.Next(5, 25));
            }
        }
    }
}
