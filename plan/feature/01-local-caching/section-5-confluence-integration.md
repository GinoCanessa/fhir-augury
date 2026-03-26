# Section 5: Confluence Source Integration

**Goal:** Modify `ConfluenceSource` to cache individual page responses as
one-file-per-entity and to support cache-only ingestion from pre-populated
page directories.

**Dependencies:** Section 2 (cache implementation)

---

## 5.1 — Add Cache Support to ConfluenceSourceOptions

### Objective

Extend `ConfluenceSourceOptions` with cache-related properties.

### File to Modify: `src/FhirAugury.Sources.Confluence/ConfluenceSourceOptions.cs`

```csharp
using FhirAugury.Models.Caching;

// Add to existing ConfluenceSourceOptions record:
public CacheMode CacheMode { get; init; } = CacheMode.Disabled;
public IResponseCache? Cache { get; init; }
```

### Acceptance Criteria

- [ ] New properties added with backwards-compatible defaults

---

## 5.2 — Modify Confluence Download Loop (WriteThrough / WriteOnly)

### Objective

Modify `ConfluenceSource` so that each page's API response is cached as an
individual JSON file under `{CacheRoot}/confluence/pages/{page-id}.json`.

### File to Modify: `src/FhirAugury.Sources.Confluence/ConfluenceSource.cs`

### Cache Directory Layout

```
{CacheRoot}/confluence/
├── pages/
│   ├── 12345.json
│   ├── 67890.json
│   └── ...
```

### New Flow

```
for each space:
    for each page (paginated):
        response = await HttpGet(pageUrl)

        if caching:
            key = $"pages/{pageId}.json"
            await cache.PutAsync("confluence", key, responseStream)

        if mode is not WriteOnly:
            process(response)

update _meta_confluence.json
```

### Implementation Details

1. **Cache key:** `pages/{pageId}.json` — the page ID is the Confluence content
   ID (a numeric string). This produces a flat directory of JSON files, one per
   page.

2. **WriteThrough cache check:** Before fetching a page from the API, check
   `cache.TryGet("confluence", $"pages/{pageId}.json", out stream)`. If a
   cache hit occurs, parse from cache and skip the HTTP call. Unlike Jira/Zulip
   (which are batch-oriented), Confluence's per-entity layout makes cache hits
   meaningful in `WriteThrough` mode.

3. **Freshness:** In `WriteThrough` mode, the source can compare the cached
   page's `version.number` (stored in the JSON) against the API's page list
   to determine if the cache is stale. However, for simplicity in the initial
   implementation, `WriteThrough` always fetches from API and overwrites the
   cache. Freshness checking is a future optimisation.

4. **Metadata update:** After the download loop, update `_meta_confluence.json`
   with the sync timestamp and file count.

### Acceptance Criteria

- [ ] Each page produces one `{pageId}.json` file under `pages/`
- [ ] Cache is overwritten when the same page is re-downloaded
- [ ] `_meta_confluence.json` is updated after each run
- [ ] `WriteOnly` writes to cache but does not process
- [ ] `Disabled` mode has zero cache interaction

---

## 5.3 — Implement Confluence Cache-Only Ingestion

### Objective

Add a code path that loads all cached Confluence page JSON files and ingests
them without network access.

### File to Modify: `src/FhirAugury.Sources.Confluence/ConfluenceSource.cs`

### `LoadFromCacheAsync` Implementation

```
1. keys = cache.EnumerateKeys("confluence")
      → returns "pages/{pageId}.json" entries (alphabetical by page ID)

2. for each key in keys:
      cache.TryGet("confluence", key, out stream)
      jsonDoc = JsonDocument.Parse(stream)

      Map via ConfluenceContentParser → upsert ConfluencePageRecord
      Track in IngestionResult

3. return IngestionResult with totals
```

### Design Notes

- **Space information:** In cache-only mode, the space key is extracted from
  each page's JSON content (the `space.key` field in the Confluence API
  response). A `ConfluenceSpaceRecord` should be upserted for each unique
  space encountered.

- **No ordering requirement:** Unlike Jira/Zulip, Confluence pages are
  independent entities. Processing order doesn't matter — each page is a
  complete snapshot. Alphabetical by page ID is deterministic and sufficient.

- **Comments:** If the cached page JSON includes expanded comments (via
  the `?expand=` query parameter used during download), they should be
  extracted and inserted as `ConfluenceCommentRecord`s. If comments are not
  in the cached JSON, they are simply not available in cache-only mode.

### Acceptance Criteria

- [ ] All `*.json` files under `pages/` are discovered and processed
- [ ] Space records are created from page content
- [ ] Page records are upserted correctly
- [ ] Zero HTTP calls, no credentials required
- [ ] `IngestionResult` reports correct counts
