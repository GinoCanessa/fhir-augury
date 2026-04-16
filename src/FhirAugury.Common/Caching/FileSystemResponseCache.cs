using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Caching;

/// <summary>
/// File-system cache implementation that maps (source, key) pairs to files on disk.
/// </summary>
public class FileSystemResponseCache : IResponseCache
{
    private readonly string _rootPath;
    private readonly string _rootPathWithSep;
    private readonly ILogger? _logger;

    public FileSystemResponseCache(string rootPath, ILogger? logger = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _rootPathWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        _logger = logger;
        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public bool TryGet(string source, string key, [NotNullWhen(true)] out Stream? content)
    {
        string path = ResolvePath(source, key);

        // Retry to handle races with concurrent PutAsync (temp+move)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!File.Exists(path))
            {
                content = null;
                return false;
            }

            try
            {
                content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (FileNotFoundException)
            {
                // File was moved/deleted between Exists check and Open
            }
            catch (IOException) when (attempt < 2)
            {
                // File may be locked by a concurrent write
            }

            Thread.Sleep(Random.Shared.Next(10, 50));
        }

        content = null;
        return false;
    }

    public async Task PutAsync(string source, string key, Stream content, CancellationToken ct)
    {
        string finalPath = ResolvePath(source, key);
        await AtomicFileWriter.WriteAsync(
            finalPath,
            async fs => await content.CopyToAsync(fs, ct),
            _logger,
            ct);
    }

    public void Remove(string source, string key)
    {
        string path = ResolvePath(source, key);
        if (File.Exists(path))
            File.Delete(path);
    }

    public IEnumerable<string> EnumerateKeys(string source)
    {
        string sourceDir = ResolveSourcePath(source);
        return EnumerateKeysInternal(sourceDir, source);
    }

    public IEnumerable<string> EnumerateKeys(string source, string subPath)
    {
        string sourceDir = ResolveSourcePath(source);
        return EnumerateKeysInternal(Path.Combine(sourceDir, subPath), source);
    }

    public void Clear(string source)
    {
        string sourceDir = ResolveSourcePath(source);
        if (Directory.Exists(sourceDir))
            Directory.Delete(sourceDir, recursive: true);

        string metaFile = Path.Combine(_rootPath, $"_meta_{source}.json");
        if (File.Exists(metaFile))
            File.Delete(metaFile);
    }

    public void ClearAll()
    {
        if (!Directory.Exists(_rootPath))
            return;

        foreach (string entry in Directory.EnumerateFileSystemEntries(_rootPath))
        {
            if (File.Exists(entry))
                File.Delete(entry);
            else if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
        }
    }

    public CacheStats GetStats(string source)
    {
        string sourceDir = Path.Combine(_rootPath, source);
        if (!Directory.Exists(sourceDir))
            return new CacheStats(source, 0, 0, []);

        int fileCount = 0;
        long totalBytes = 0;
        HashSet<string> subPaths = new HashSet<string>();

        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            FileInfo info = new FileInfo(file);
            fileCount++;
            totalBytes += info.Length;

            string relative = Path.GetRelativePath(sourceDir, file);
            string? dirPart = Path.GetDirectoryName(relative);
            if (!string.IsNullOrEmpty(dirPart))
            {
                string topLevel = dirPart.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                subPaths.Add(topLevel);
            }
        }

        return new CacheStats(source, fileCount, totalBytes, subPaths.Order().ToList());
    }

    public Task<Stream?> TryGetAsync(string source, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryGet(source, key, out Stream? content) ? content : (Stream?)null);
    }

    public Task RemoveAsync(string source, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Remove(source, key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Clear(source);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearAll();
        return Task.CompletedTask;
    }

    private IEnumerable<string> EnumerateKeysInternal(string directory, string source)
    {
        if (!Directory.Exists(directory))
            return [];

        string sourceRoot = Path.Combine(_rootPath, source);
        List<string> allFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !IsMetadataFile(Path.GetFileName(f)))
            .ToList();

        List<CacheFileNaming.ParsedBatchFile> batchFiles = new List<CacheFileNaming.ParsedBatchFile>();
        List<string> nonBatchKeys = new List<string>();

        foreach (string? file in allFiles)
        {
            string key = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            string fileName = Path.GetFileName(file);

            if (CacheFileNaming.TryParse(fileName, out CacheFileNaming.ParsedBatchFile? parsed))
            {
                batchFiles.Add(parsed with { FileName = key });
            }
            else
            {
                nonBatchKeys.Add(key);
            }
        }

        IEnumerable<string> sortedBatch = CacheFileNaming.SortForIngestion(batchFiles).Select(f => f.FileName);
        nonBatchKeys.Sort(StringComparer.OrdinalIgnoreCase);

        return sortedBatch.Concat(nonBatchKeys);
    }

    private void EnsureWithinRoot(string resolved, string paramName)
    {
        if (!resolved.StartsWith(_rootPathWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, _rootPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{paramName}' resolves outside the cache root.", paramName);
    }

    private string ResolveSourcePath(string source)
    {
        string combined = Path.Combine(_rootPath, source);
        string resolved = Path.GetFullPath(combined);
        EnsureWithinRoot(resolved, nameof(source));
        return resolved;
    }

    private string ResolvePath(string source, string key)
    {
        string combined = Path.Combine(_rootPath, source, key.Replace('/', Path.DirectorySeparatorChar));
        string resolved = Path.GetFullPath(combined);
        EnsureWithinRoot(resolved, nameof(key));
        return resolved;
    }

    private static bool IsMetadataFile(string fileName) =>
        fileName.StartsWith("_meta_", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
