# Section 2: Cache Implementation

**Goal:** Implement `FileSystemResponseCache` — the concrete cache that maps
`(source, key)` pairs to files on disk, manages metadata files, and delegates
to `CacheFileNaming` for batch file ordering.

**Dependencies:** Section 1 (core abstractions)

---

## 2.1 — FileSystemResponseCache

### Objective

Implement `IResponseCache` against the local file system. The class must
support the directory layout defined in the proposal and handle all three
source types (Jira batch, Zulip batch, Confluence per-entity).

### File to Create: `src/FhirAugury.Models/Caching/FileSystemResponseCache.cs`

### Constructor

```csharp
public class FileSystemResponseCache : IResponseCache
{
    private readonly string _rootPath;

    public FileSystemResponseCache(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }
}
```

### Method Implementations

#### `TryGet(string source, string key, out Stream content)`

1. Compute path: `Path.Combine(_rootPath, source, key)`.
2. If file exists, open a `FileStream` in read mode and return `true`.
3. Otherwise set `content = Stream.Null` and return `false`.

**Edge cases:**

- `key` may contain subdirectory separators (e.g., `s270/DayOf_2026-03-18-000.json`
  for Zulip). `Path.Combine` handles this naturally.
- Path traversal protection: validate that the resolved path is under `_rootPath`.

#### `PutAsync(string source, string key, Stream content, CancellationToken ct)`

1. Compute path: `Path.Combine(_rootPath, source, key)`.
2. Create parent directories if needed.
3. Write `content` to a temporary file in the same directory, then atomically
   move it to the final path. This prevents partial writes from being visible.
4. Log the write at `Debug` level.

**Atomic write pattern:**

```csharp
var tempPath = finalPath + ".tmp";
await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
{
    await content.CopyToAsync(fs, ct);
}
File.Move(tempPath, finalPath, overwrite: true);
```

#### `Remove(string source, string key)`

1. Compute path → `File.Delete` if exists.
2. Remove empty parent directories up to the source root.

#### `EnumerateKeys(string source)` and `EnumerateKeys(string source, string subPath)`

1. Compute directory: `Path.Combine(_rootPath, source)` or
   `Path.Combine(_rootPath, source, subPath)`.
2. If the directory doesn't exist, return empty.
3. Enumerate all files recursively (excluding `_meta_*.json` files).
4. For each file, compute its key relative to the source root.
5. Parse batch files via `CacheFileNaming.TryParse`.
6. Separate parseable batch files from non-batch files.
7. Sort batch files via `CacheFileNaming.SortForIngestion`.
8. Return sorted batch files followed by non-batch files (alphabetical).

**Important:** The `_meta_*.json` files must be excluded from enumeration.
Filter them by checking for the `_meta_` prefix.

#### `Clear(string source)` and `ClearAll()`

- `Clear`: delete the source subdirectory and its `_meta_{source}.json` file.
- `ClearAll`: delete all contents under `_rootPath` (but keep the root itself).

#### `GetStats(string source)`

1. Enumerate all files under `Path.Combine(_rootPath, source)`.
2. Sum file sizes, count files, list subdirectories.
3. Return `CacheStats`.

### Thread Safety

- File reads (`TryGet`, `EnumerateKeys`) are inherently thread-safe on the
  file system — multiple readers can coexist.
- File writes (`PutAsync`) use the atomic temp-file-then-move pattern, so
  concurrent writes to different keys are safe. Concurrent writes to the same
  key produce last-writer-wins semantics (acceptable).
- `Clear` and `ClearAll` are not designed for concurrent use with reads/writes.
  Document this — they are admin operations.

### Acceptance Criteria

- [ ] `PutAsync` creates intermediate directories automatically
- [ ] `PutAsync` uses atomic write (temp + move)
- [ ] `TryGet` returns `false` for missing entries
- [ ] `TryGet` returns a readable stream for existing entries
- [ ] `EnumerateKeys` excludes `_meta_*.json` files
- [ ] `EnumerateKeys` returns batch files in correct chronological order
- [ ] `Clear` removes source directory and metadata file
- [ ] `GetStats` returns correct file count and byte total
- [ ] Path traversal is prevented (keys containing `..` are rejected)

---

## 2.2 — Metadata File Operations

### Objective

Add helper methods to `FileSystemResponseCache` (or a companion
`CacheMetadataService` static class) for reading and writing the per-source
metadata JSON files.

### Methods to Add

```csharp
public static class CacheMetadataService
{
    /// <summary>Read a metadata file, returning null if it doesn't exist.</summary>
    public static T? ReadMetadata<T>(string rootPath, string metaFileName)
        where T : class;

    /// <summary>Write a metadata file atomically (temp + move).</summary>
    public static Task WriteMetadataAsync<T>(
        string rootPath, string metaFileName, T metadata, CancellationToken ct);
}
```

### Metadata File Paths (from Proposal)

| Source | File Path | Record Type |
|--------|-----------|-------------|
| Jira | `{CacheRoot}/_meta_jira.json` | `JiraCacheMetadata` |
| Confluence | `{CacheRoot}/_meta_confluence.json` | `ConfluenceCacheMetadata` |
| Zulip (per-stream) | `{CacheRoot}/zulip/_meta_s{id}.json` | `ZulipStreamCacheMetadata` |

### Implementation Details

- Use `System.Text.Json.JsonSerializer` with `JsonSerializerOptions` configured
  for camelCase property naming (matching the `[JsonPropertyName]` attributes on
  the metadata records).
- `WriteMetadataAsync` uses the same atomic temp-file-then-move pattern as
  `PutAsync`.
- `ReadMetadata<T>` returns `null` if the file does not exist, rather than
  throwing.

### Acceptance Criteria

- [ ] `ReadMetadata` returns `null` for missing files
- [ ] `ReadMetadata` deserializes valid JSON into the correct record type
- [ ] `WriteMetadataAsync` creates parent directories if needed
- [ ] `WriteMetadataAsync` uses atomic write pattern
- [ ] Round-trip: write then read returns identical data

---

## 2.3 — NullResponseCache

### Objective

Provide a no-op implementation of `IResponseCache` for use when caching is
`Disabled`. This avoids null checks throughout the source code.

### File to Create: `src/FhirAugury.Models/Caching/NullResponseCache.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>
/// No-op cache implementation used when caching is disabled.
/// All reads miss, all writes are discarded.
/// </summary>
public sealed class NullResponseCache : IResponseCache
{
    public static readonly NullResponseCache Instance = new();

    public bool TryGet(string source, string key, out Stream content)
    {
        content = Stream.Null;
        return false;
    }

    public Task PutAsync(string source, string key, Stream content, CancellationToken ct)
        => Task.CompletedTask;

    public void Remove(string source, string key) { }
    public IEnumerable<string> EnumerateKeys(string source) => [];
    public IEnumerable<string> EnumerateKeys(string source, string subPath) => [];
    public void Clear(string source) { }
    public void ClearAll() { }
    public CacheStats GetStats(string source) => new(source, 0, 0, []);
}
```

### Acceptance Criteria

- [ ] Implements all `IResponseCache` methods as no-ops
- [ ] Singleton instance pattern
- [ ] `TryGet` always returns `false`
- [ ] `EnumerateKeys` always returns empty
