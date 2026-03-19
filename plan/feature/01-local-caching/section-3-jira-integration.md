# Section 3: Jira Source Integration

**Goal:** Modify `JiraSource` to write API responses to the cache during
download, and to support cache-only mode using `JiraXmlParser` for ingestion
from pre-populated directories.

**Dependencies:** Section 2 (cache implementation)

---

## 3.1 — Add Cache Support to JiraSourceOptions

### Objective

Extend `JiraSourceOptions` with cache-related properties so the source knows
its cache mode and can receive an `IResponseCache` instance.

### File to Modify: `src/FhirAugury.Sources.Jira/JiraSourceOptions.cs`

Add the following properties:

```csharp
using FhirAugury.Models.Caching;

// Add to existing JiraSourceOptions record:
public CacheMode CacheMode { get; init; } = CacheMode.Disabled;
public IResponseCache? Cache { get; init; }
```

### Design Notes

- `CacheMode.Disabled` as default preserves existing behaviour — no cache
  interaction unless explicitly configured.
- `Cache` is nullable; when null and mode is not `Disabled`, the source
  should throw a descriptive `InvalidOperationException` at construction time.

### Acceptance Criteria

- [ ] New properties added with backwards-compatible defaults
- [ ] Existing code that creates `JiraSourceOptions` without cache properties
      continues to compile and work

---

## 3.2 — Modify JiraSource Download Loop (WriteThrough / WriteOnly)

### Objective

Modify `JiraSource.DownloadAllAsync` and `DownloadIncrementalAsync` so that
each API response page is written to the cache as a dated batch file.

### File to Modify: `src/FhirAugury.Sources.Jira/JiraSource.cs`

### Current Flow

```
for each page:
    response = await HttpGet(searchUrl + startAt)
    jsonDoc = JsonDocument.Parse(response)
    foreach issue in jsonDoc:
        MapAndUpsert(issue)
```

### New Flow (WriteThrough / WriteOnly)

```
for each page:
    response = await HttpGet(searchUrl + startAt)

    if cache mode is WriteThrough or WriteOnly:
        key = CacheFileNaming.GenerateDailyFileName(today, "xml", existingFiles)
        await cache.PutAsync("jira", key, responseStream)

    if cache mode is not WriteOnly:
        jsonDoc = JsonDocument.Parse(response)
        foreach issue in jsonDoc:
            MapAndUpsert(issue)
```

### Implementation Details

1. **Cache key generation:** At the start of each download run, call
   `cache.EnumerateKeys("jira")` to get the list of existing files. Pass this
   to `CacheFileNaming.GenerateDailyFileName` for each page to get the next
   available sequence number.

2. **Stream handling:** The HTTP response is a `Stream`. To both cache it and
   parse it, read the response into a `MemoryStream`, write the memory stream
   to cache via `PutAsync`, then reset the position and parse. This avoids
   making two HTTP requests.

   ```csharp
   var responseStream = await httpResponse.Content.ReadAsStreamAsync(ct);
   var memStream = new MemoryStream();
   await responseStream.CopyToAsync(memStream, ct);

   if (shouldCache)
   {
       memStream.Position = 0;
       await cache.PutAsync("jira", cacheKey, memStream, ct);
   }

   memStream.Position = 0;
   var jsonDoc = await JsonDocument.ParseAsync(memStream, cancellationToken: ct);
   ```

3. **WriteThrough cache hit:** In `WriteThrough` mode, before making the HTTP
   request, check the cache. However, for Jira's date-based batch approach,
   the cache is not keyed by query — it stores raw batch files. Cache hits
   are meaningful only in `CacheOnly` mode. In `WriteThrough`, the source
   always fetches from the API and appends new batch files. Already-fetched
   date ranges are skipped by the incremental sync cursor, not by cache lookups.

4. **Metadata update:** After the download loop completes, update
   `_meta_jira.json` with the current sync date and file count.

### Acceptance Criteria

- [ ] Each API response page produces one `DayOf_yyyy-MM-dd-###.xml` file
- [ ] Sequence numbers increment correctly across pages within a day
- [ ] Response content is identical whether parsed from cache or network
- [ ] `WriteOnly` mode writes to cache but does not process/upsert responses
- [ ] `_meta_jira.json` is updated after each download run
- [ ] `Disabled` mode has zero cache interaction (no performance impact)

---

## 3.3 — Implement Jira Cache-Only Ingestion

### Objective

Add a new code path that loads and processes all cached Jira XML files in
chronological order, using `JiraXmlParser.ParseExport` for deserialization.
This is the primary use case described in the proposal.

### File to Modify: `src/FhirAugury.Sources.Jira/JiraSource.cs`

### New Method or Code Path

When `CacheMode == CacheOnly`, `DownloadAllAsync` should:

```csharp
// In DownloadAllAsync, early branch on CacheOnly:
if (_options.CacheMode == CacheMode.CacheOnly)
    return await LoadFromCacheAsync(options, ct);
```

### `LoadFromCacheAsync` Implementation

```
1. keys = cache.EnumerateKeys("jira")
      → returns all *.xml files sorted by date (oldest → newest)

2. for each key in keys:
      cache.TryGet("jira", key, out stream)

      records = JiraXmlParser.ParseExport(stream)

      for each (issue, comments) in records:
          Upsert issue into database
          Insert/update comments
          Track in IngestionResult

3. return IngestionResult with totals
```

### Design Notes

- **All three file name patterns are supported** because `EnumerateKeys`
  delegates to `CacheFileNaming` which recognizes `_WeekOf_`, `DayOf_`, and
  `DayOf_-###` patterns.
- **Upsert semantics:** because files are loaded oldest-first, if a ticket
  appears in multiple batch files, the final database state reflects the newest
  version. This relies on the existing `ProcessIssue` pipeline using INSERT OR
  REPLACE / upsert.
- **No credentials required:** `LoadFromCacheAsync` makes zero HTTP calls.
- **No incremental sync cursor needed:** in `CacheOnly` mode, all files are
  always processed. The upsert ensures correctness.
- **`DownloadIncrementalAsync` in CacheOnly mode:** should also delegate to
  `LoadFromCacheAsync` (same behaviour — process all cached files). The `since`
  parameter is ignored because we can't filter batch files by internal content
  dates without parsing them.

### Acceptance Criteria

- [ ] `CacheOnly` mode processes all `*.xml` files in the jira cache directory
- [ ] Files are processed in chronological order (oldest → newest)
- [ ] `JiraXmlParser.ParseExport` is used for deserialization
- [ ] Duplicate tickets across files resolve to their newest version via upsert
- [ ] Zero HTTP calls are made
- [ ] No credentials are required (no auth handler invoked)
- [ ] `IngestionResult` correctly reports counts
- [ ] Works with all three file naming patterns (legacy weekly, legacy daily,
      current sequenced daily)

---

## 3.4 — Jira Cache Format Consideration

### Objective

Address the proposal's decision point about Jira file format consistency.

### Decision

The current Jira source fetches JSON from the REST API but the XML parser
(`JiraXmlParser`) is used for bulk export files. For the cache:

- **API responses are JSON** — when caching API responses in `WriteThrough`
  mode, the cached files should be stored as JSON (`.json` extension) since
  that's what the API returns.
- **Pre-seeded files may be XML** — `CacheOnly` mode must support both XML
  (`.xml`) and JSON (`.json`) files in the cache directory.
- **`EnumerateKeys("jira")`** should return both `*.xml` and `*.json` files,
  sorted together by date.
- **The source detects format by extension** and dispatches to
  `JiraXmlParser.ParseExport` (for `.xml`) or `JiraJsonParser` (for `.json`,
  using the existing `JsonDocument`-based mapping).

### Implementation

Add a helper method to `JiraSource`:

```csharp
private IEnumerable<(JiraIssueRecord Issue, List<JiraCommentRecord> Comments)>
    ParseCachedFile(Stream stream, string key)
{
    if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        return JiraXmlParser.ParseExport(stream);

    // JSON: parse using existing JsonDocument-based field mapper
    return ParseJsonExport(stream);
}
```

### Acceptance Criteria

- [ ] Cache-only mode handles both `.xml` and `.json` files
- [ ] Files of different formats are sorted together by embedded date
- [ ] API-cached files use `.json` extension
- [ ] Pre-seeded XML export files work without conversion
