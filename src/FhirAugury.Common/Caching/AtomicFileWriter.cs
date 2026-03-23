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
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await writeAction(fs);
            }
            File.Move(tempPath, path, overwrite: true);
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
}
