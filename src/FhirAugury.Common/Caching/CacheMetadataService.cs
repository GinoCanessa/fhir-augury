using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Caching;

/// <summary>Helper for reading and writing per-source metadata JSON files.</summary>
public static class CacheMetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Read a metadata file asynchronously, returning null if it doesn't exist.</summary>
    public static async Task<T?> ReadMetadataAsync<T>(
        string rootPath,
        string metaFileName,
        CancellationToken ct = default) where T : class
    {
        var path = Path.Combine(rootPath, metaFileName);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    /// <summary>Write a metadata file atomically (temp + move).</summary>
    public static async Task WriteMetadataAsync<T>(
        string rootPath,
        string metaFileName,
        T metadata,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var finalPath = Path.Combine(rootPath, metaFileName);
        await AtomicFileWriter.WriteAsync(
            finalPath,
            async fs => await JsonSerializer.SerializeAsync(fs, metadata, JsonOptions, ct),
            logger,
            ct);
    }
}
