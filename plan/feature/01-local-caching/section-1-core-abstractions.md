# Section 1: Core Abstractions

**Goal:** Define the cache interface, mode enum, configuration records, and
shared file-naming utilities in `FhirAugury.Models` so that the cache layer
has zero dependency on concrete source or infrastructure projects.

**Dependencies:** None

---

## 1.1 — Cache Mode Enum

### Objective

Define the four caching behaviours as a strongly-typed enum.

### File to Create: `src/FhirAugury.Models/Caching/CacheMode.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>
/// Controls how a data source interacts with the file-system cache.
/// </summary>
public enum CacheMode
{
    /// <summary>No caching — always fetch from API (current behaviour).</summary>
    Disabled,

    /// <summary>Read from cache if fresh → otherwise fetch from API → write to cache.</summary>
    WriteThrough,

    /// <summary>Read from cache only — no network calls, no credentials required.</summary>
    CacheOnly,

    /// <summary>Always fetch from API → write to cache (build cache without using it).</summary>
    WriteOnly,
}
```

### Acceptance Criteria

- [ ] Enum compiles with XML doc comments on every member
- [ ] No dependency on any other project

---

## 1.2 — IResponseCache Interface

### Objective

Define the core cache abstraction that all sources depend on. This must be
source-agnostic — sources pass a `source` name and a `key` string; the
implementation maps those to file-system paths.

### File to Create: `src/FhirAugury.Models/Caching/IResponseCache.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>
/// File-system cache for raw API responses.
/// </summary>
public interface IResponseCache
{
    /// <summary>
    /// Check whether a cached entry exists. If so, returns a readable stream
    /// positioned at the start of the cached content.
    /// </summary>
    bool TryGet(string source, string key, out Stream content);

    /// <summary>
    /// Write a response to the cache. Creates intermediate directories as needed.
    /// </summary>
    Task PutAsync(string source, string key, Stream content, CancellationToken ct);

    /// <summary>Delete a single cached entry.</summary>
    void Remove(string source, string key);

    /// <summary>
    /// Enumerate all cached keys for a source, returned in the correct
    /// ingestion order (oldest → newest for date-based batch sources).
    /// </summary>
    IEnumerable<string> EnumerateKeys(string source);

    /// <summary>
    /// Enumerate all cached keys for a source and sub-path (e.g., a Zulip
    /// stream directory), returned in ingestion order.
    /// </summary>
    IEnumerable<string> EnumerateKeys(string source, string subPath);

    /// <summary>Delete all cached entries for a source.</summary>
    void Clear(string source);

    /// <summary>Delete all cached entries for all sources.</summary>
    void ClearAll();

    /// <summary>
    /// Get cache statistics: total files and total bytes per source.
    /// </summary>
    CacheStats GetStats(string source);
}
```

### Design Notes

- `TryGet` returns `Stream` (not `byte[]`) so callers can stream large files
  without loading everything into memory.
- `EnumerateKeys(source)` returns keys sorted in ingestion order. For Jira and
  Zulip this means date-ascending with proper prefix/sequence tiebreaking. For
  Confluence this means alphabetical by page ID.
- The two-argument `EnumerateKeys(source, subPath)` supports Zulip's per-stream
  directory layout — `EnumerateKeys("zulip", "s270")` enumerates only stream
  270's files.

### Acceptance Criteria

- [ ] Interface compiles with XML doc comments on every member
- [ ] No dependency on concrete implementation or infrastructure

---

## 1.3 — Cache Statistics Record

### Objective

Provide a simple record for cache statistics, returned by `IResponseCache.GetStats`.

### File to Create: `src/FhirAugury.Models/Caching/CacheStats.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>Per-source cache statistics.</summary>
public record CacheStats(
    string Source,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> SubPaths);
```

### Acceptance Criteria

- [ ] Record compiles
- [ ] Used by `IResponseCache.GetStats`

---

## 1.4 — Cache Configuration Records

### Objective

Define configuration records that bind to the new `Cache` section in
`appsettings.json`. These live in `FhirAugury.Models` so both CLI and
Service projects can reference them without depending on each other.

### File to Create: `src/FhirAugury.Models/Caching/CacheConfiguration.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>Top-level cache configuration.</summary>
public class CacheConfiguration
{
    /// <summary>Root directory for all source caches.</summary>
    public string RootPath { get; set; } = "./cache";

    /// <summary>Default cache mode for all sources.</summary>
    public CacheMode DefaultMode { get; set; } = CacheMode.WriteThrough;
}

/// <summary>Per-source cache configuration override.</summary>
public class SourceCacheConfiguration
{
    /// <summary>Cache mode override for this source.</summary>
    public CacheMode? Mode { get; set; }

    /// <summary>
    /// Override the cache subdirectory for this source. When null, uses
    /// the source name as the subdirectory (e.g., "jira", "zulip").
    /// </summary>
    public string? Path { get; set; }
}
```

### Design Notes

- `CacheConfiguration` is the top-level `"Cache": { ... }` section.
- `SourceCacheConfiguration` appears inside each source's config as a nested
  `"Cache": { ... }` object.
- `Mode` is nullable so that omitting it means "inherit from `DefaultMode`".
- These are `class` (not `record`) to support `IOptions<T>` binding via
  `Microsoft.Extensions.Options` (mutable setters required).

### Acceptance Criteria

- [ ] Classes compile
- [ ] Default values match the proposal (`"./cache"`, `WriteThrough`)
- [ ] `SourceCacheConfiguration.Mode` is nullable (inheritance semantics)

---

## 1.5 — Cache Metadata Records

### Objective

Define the JSON-serializable metadata records that are written to
`_meta_*.json` files to track sync cursors.

### File to Create: `src/FhirAugury.Models/Caching/CacheMetadata.cs`

```csharp
using System.Text.Json.Serialization;

namespace FhirAugury.Models.Caching;

/// <summary>
/// Sync metadata for Jira cache. Stored at {CacheRoot}/_meta_jira.json.
/// </summary>
public record JiraCacheMetadata
{
    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "xml";
}

/// <summary>
/// Sync metadata for a single Zulip stream.
/// Stored at {CacheRoot}/zulip/_meta_s{id}.json.
/// </summary>
public record ZulipStreamCacheMetadata
{
    [JsonPropertyName("streamId")]
    public int StreamId { get; init; }

    [JsonPropertyName("streamName")]
    public string? StreamName { get; init; }

    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("initialDownloadComplete")]
    public bool InitialDownloadComplete { get; init; }
}

/// <summary>
/// Sync metadata for Confluence cache. Stored at {CacheRoot}/_meta_confluence.json.
/// </summary>
public record ConfluenceCacheMetadata
{
    [JsonPropertyName("lastSyncDate")]
    public string? LastSyncDate { get; init; }

    [JsonPropertyName("lastSyncTimestamp")]
    public DateTimeOffset? LastSyncTimestamp { get; init; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "json";
}
```

### Acceptance Criteria

- [ ] Records compile with `System.Text.Json` attributes
- [ ] Round-trip serialize/deserialize preserves all fields
- [ ] `FhirAugury.Models` does not require new NuGet dependencies (STJ is in-box)

---

## 1.6 — Cache File Naming Utility

### Objective

Implement the shared file-naming and sorting logic used by both Jira (`.xml`)
and Zulip (`.json`) caches. This is the most complex piece of the core
abstractions — it must correctly parse all three legacy + current file name
patterns and sort them in the order specified by the proposal.

### File to Create: `src/FhirAugury.Models/Caching/CacheFileNaming.cs`

```csharp
namespace FhirAugury.Models.Caching;

/// <summary>
/// Shared file-naming and sorting logic for date-based cache batch files.
/// Used by both Jira (.xml) and Zulip (.json) caches.
/// </summary>
public static class CacheFileNaming
{
    /// <summary>Recognized file name patterns.</summary>
    public enum BatchPrefix { WeekOf, DayOf }

    /// <summary>Parsed representation of a cache batch file name.</summary>
    public record ParsedBatchFile(
        string FileName,
        BatchPrefix Prefix,
        DateOnly Date,
        int? SequenceNumber);

    /// <summary>
    /// Try to parse a file name into its components.
    /// Recognizes:
    ///   _WeekOf_yyyy-MM-dd.ext          (legacy weekly, no sequence)
    ///   DayOf_yyyy-MM-dd.ext            (legacy daily, no sequence)
    ///   _WeekOf_yyyy-MM-dd-###.ext      (weekly with sequence)
    ///   DayOf_yyyy-MM-dd-###.ext        (current daily with sequence)
    /// </summary>
    public static bool TryParse(string fileName, out ParsedBatchFile result);

    /// <summary>
    /// Generate the next available file name for a daily batch.
    /// Scans existing files in the directory and increments the sequence number.
    /// Returns "DayOf_yyyy-MM-dd-###.{extension}" format.
    /// </summary>
    public static string GenerateDailyFileName(
        DateOnly date, string extension, IEnumerable<string> existingFiles);

    /// <summary>
    /// Generate the next available file name for a weekly batch.
    /// Returns "_WeekOf_yyyy-MM-dd-###.{extension}" format.
    /// The date is normalized to the Monday of the given week.
    /// </summary>
    public static string GenerateWeeklyFileName(
        DateOnly date, string extension, IEnumerable<string> existingFiles);

    /// <summary>
    /// Sort parsed batch files in the canonical ingestion order:
    /// 1. Ascending by date
    /// 2. Files without sequence numbers before files with sequence numbers
    ///    (for the same date)
    /// 3. _WeekOf_ before DayOf_ (for the same date)
    /// 4. Ascending by sequence number
    /// </summary>
    public static IEnumerable<ParsedBatchFile> SortForIngestion(
        IEnumerable<ParsedBatchFile> files);
}
```

### Implementation Details

**`TryParse` regex patterns:**

```
^_WeekOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$
^DayOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$
```

**Sorting comparator (for `SortForIngestion`):**

```
primary:    Date ascending
secondary:  SequenceNumber is null → sort before non-null (legacy first)
tertiary:   WeekOf → sort before DayOf (same date)
quaternary: SequenceNumber ascending
```

**`GenerateDailyFileName` algorithm:**

1. Filter `existingFiles` for those matching `DayOf_{date}-###.{ext}`.
2. Find the max sequence number among matches (or -1 if none).
3. Return `DayOf_{date:yyyy-MM-dd}-{(max+1):D3}.{ext}`.

**`GenerateWeeklyFileName` algorithm:**

1. Compute the Monday of the week containing `date`.
2. Filter `existingFiles` for `_WeekOf_{monday}-###.{ext}`.
3. Find max sequence number → return `_WeekOf_{monday:yyyy-MM-dd}-{(max+1):D3}.{ext}`.

### Acceptance Criteria

- [ ] `TryParse` correctly parses all four file name patterns
- [ ] `TryParse` rejects malformed file names (returns false)
- [ ] `SortForIngestion` orders: legacy weekly → legacy daily → sequenced files
- [ ] `GenerateDailyFileName` auto-increments from `000`
- [ ] `GenerateWeeklyFileName` normalizes date to Monday of the week
- [ ] Unit tests cover all patterns listed in the proposal's naming tables
