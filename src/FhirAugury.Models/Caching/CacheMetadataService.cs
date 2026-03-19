using System.Text.Json;

namespace FhirAugury.Models.Caching;

/// <summary>Helper for reading and writing per-source metadata JSON files.</summary>
public static class CacheMetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Read a metadata file, returning null if it doesn't exist.</summary>
    public static T? ReadMetadata<T>(string rootPath, string metaFileName) where T : class
    {
        var path = Path.Combine(rootPath, metaFileName);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>Write a metadata file atomically (temp + move).</summary>
    public static async Task WriteMetadataAsync<T>(
        string rootPath, string metaFileName, T metadata, CancellationToken ct)
    {
        var finalPath = Path.Combine(rootPath, metaFileName);
        var dir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(dir);

        var tempPath = finalPath + ".tmp";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await JsonSerializer.SerializeAsync(fs, metadata, JsonOptions, ct);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }
}
