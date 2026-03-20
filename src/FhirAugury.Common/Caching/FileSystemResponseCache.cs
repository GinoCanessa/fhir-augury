namespace FhirAugury.Common.Caching;

/// <summary>
/// File-system cache implementation that maps (source, key) pairs to files on disk.
/// </summary>
public class FileSystemResponseCache : IResponseCache
{
    private readonly string _rootPath;

    public FileSystemResponseCache(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public bool TryGet(string source, string key, out Stream content)
    {
        var path = ResolvePath(source, key);
        if (File.Exists(path))
        {
            content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }

        content = Stream.Null;
        return false;
    }

    public async Task PutAsync(string source, string key, Stream content, CancellationToken ct)
    {
        var finalPath = ResolvePath(source, key);
        var dir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(dir);

        var tempPath = finalPath + ".tmp";
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await content.CopyToAsync(fs, ct);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public void Remove(string source, string key)
    {
        var path = ResolvePath(source, key);
        if (File.Exists(path))
            File.Delete(path);
    }

    public IEnumerable<string> EnumerateKeys(string source)
    {
        var sourceDir = ResolveSourcePath(source);
        return EnumerateKeysInternal(sourceDir, source);
    }

    public IEnumerable<string> EnumerateKeys(string source, string subPath)
    {
        var sourceDir = ResolveSourcePath(source);
        return EnumerateKeysInternal(Path.Combine(sourceDir, subPath), source);
    }

    public void Clear(string source)
    {
        var sourceDir = ResolveSourcePath(source);
        if (Directory.Exists(sourceDir))
            Directory.Delete(sourceDir, recursive: true);

        var metaFile = Path.Combine(_rootPath, $"_meta_{source}.json");
        if (File.Exists(metaFile))
            File.Delete(metaFile);
    }

    public void ClearAll()
    {
        if (!Directory.Exists(_rootPath))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(_rootPath))
        {
            if (File.Exists(entry))
                File.Delete(entry);
            else if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
        }
    }

    public CacheStats GetStats(string source)
    {
        var sourceDir = Path.Combine(_rootPath, source);
        if (!Directory.Exists(sourceDir))
            return new CacheStats(source, 0, 0, []);

        int fileCount = 0;
        long totalBytes = 0;
        var subPaths = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            fileCount++;
            totalBytes += info.Length;

            var relative = Path.GetRelativePath(sourceDir, file);
            var dirPart = Path.GetDirectoryName(relative);
            if (!string.IsNullOrEmpty(dirPart))
            {
                var topLevel = dirPart.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                subPaths.Add(topLevel);
            }
        }

        return new CacheStats(source, fileCount, totalBytes, subPaths.Order().ToList());
    }

    private IEnumerable<string> EnumerateKeysInternal(string directory, string source)
    {
        if (!Directory.Exists(directory))
            return [];

        var sourceRoot = Path.Combine(_rootPath, source);
        var allFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !IsMetadataFile(Path.GetFileName(f)))
            .ToList();

        var batchFiles = new List<CacheFileNaming.ParsedBatchFile>();
        var nonBatchKeys = new List<string>();

        foreach (var file in allFiles)
        {
            var key = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            var fileName = Path.GetFileName(file);

            if (CacheFileNaming.TryParse(fileName, out var parsed))
            {
                batchFiles.Add(parsed with { FileName = key });
            }
            else
            {
                nonBatchKeys.Add(key);
            }
        }

        var sortedBatch = CacheFileNaming.SortForIngestion(batchFiles).Select(f => f.FileName);
        nonBatchKeys.Sort(StringComparer.OrdinalIgnoreCase);

        return sortedBatch.Concat(nonBatchKeys);
    }

    private string ResolveSourcePath(string source)
    {
        var combined = Path.Combine(_rootPath, source);
        var resolved = Path.GetFullPath(combined);

        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, _rootPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Source '{source}' resolves outside the cache root.", nameof(source));

        return resolved;
    }

    private string ResolvePath(string source, string key)
    {
        var combined = Path.Combine(_rootPath, source, key.Replace('/', Path.DirectorySeparatorChar));
        var resolved = Path.GetFullPath(combined);

        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, _rootPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Key '{key}' resolves outside the cache root.", nameof(key));

        return resolved;
    }

    private static bool IsMetadataFile(string fileName) =>
        fileName.StartsWith("_meta_", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
