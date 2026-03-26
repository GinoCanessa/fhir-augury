# Section 4: Zulip Source Integration

**Goal:** Modify `ZulipSource` to write API responses to per-stream cache
directories using weekly batches for initial downloads and daily batches for
incremental syncs, and to support cache-only mode for pre-populated Zulip
JSON archives.

**Dependencies:** Section 2 (cache implementation)

---

## 4.1 — Add Cache Support to ZulipSourceOptions

### Objective

Extend `ZulipSourceOptions` with cache-related properties.

### File to Modify: `src/FhirAugury.Sources.Zulip/ZulipSourceOptions.cs`

Add the following properties:

```csharp
using FhirAugury.Models.Caching;

// Add to existing ZulipSourceOptions record:
public CacheMode CacheMode { get; init; } = CacheMode.Disabled;
public IResponseCache? Cache { get; init; }
```

### Acceptance Criteria

- [ ] New properties added with backwards-compatible defaults
- [ ] Existing construction code continues to work

---

## 4.2 — Modify Zulip Download Loop (WriteThrough / WriteOnly)

### Objective

Modify `ZulipSource` so that each API response page is written to the cache
under the correct stream subdirectory with the appropriate batch prefix.

### File to Modify: `src/FhirAugury.Sources.Zulip/ZulipSource.cs`

### Cache Directory Layout

```
{CacheRoot}/zulip/
├── _meta_s270.json
├── _meta_s412.json
├── s270/
│   ├── _WeekOf_2024-08-05-000.json    (initial download)
│   ├── DayOf_2026-03-18-000.json      (incremental)
│   └── ...
├── s412/
│   └── ...
```

### New Flow: Initial Full Download (`DownloadAllAsync`)

When downloading a stream's complete history for the first time:

```
for each stream:
    streamDir = $"s{stream.Id}"
    existingFiles = cache.EnumerateKeys("zulip", streamDir)

    for each API response page (anchor-based pagination):
        response = await HttpGet(messagesUrl)

        if caching:
            weekDate = GetMondayOfWeek(oldestMessageDate)
            key = $"{streamDir}/{CacheFileNaming.GenerateWeeklyFileName(weekDate, "json", existingFiles)}"
            await cache.PutAsync("zulip", key, responseStream)

        process(response)

    update _meta_s{stream.Id}.json: initialDownloadComplete = true
```

**Weekly batch date determination:** Each API response page contains messages
spanning some time range. The weekly batch file is named after the Monday of
the week containing the **oldest message** in that response page. This means
a single week may produce multiple files (different sequence numbers) if
multiple pages fall within the same week.

### New Flow: Incremental Sync (`DownloadIncrementalAsync`)

```
for each stream:
    streamDir = $"s{stream.Id}"
    existingFiles = cache.EnumerateKeys("zulip", streamDir)

    for each API response page (since last cursor):
        response = await HttpGet(messagesUrl)

        if caching:
            key = $"{streamDir}/{CacheFileNaming.GenerateDailyFileName(today, "json", existingFiles)}"
            await cache.PutAsync("zulip", key, responseStream)

        process(response)

    update _meta_s{stream.Id}.json with lastSyncDate/Timestamp
```

### Stream Metadata Updates

After processing each stream, update its metadata file:

```csharp
await CacheMetadataService.WriteMetadataAsync(
    Path.Combine(rootPath, "zulip"),
    $"_meta_s{streamId}.json",
    new ZulipStreamCacheMetadata
    {
        StreamId = streamId,
        StreamName = streamName,
        LastSyncDate = today.ToString("yyyy-MM-dd"),
        LastSyncTimestamp = DateTimeOffset.UtcNow,
        InitialDownloadComplete = true,
    },
    ct);
```

### Implementation Details

1. **Stream directory naming:** `s{streamId}` — the `s` prefix ensures
   directory names start with a letter (per proposal decision #6).

2. **MemoryStream pattern:** Same as Jira (§3.2) — read HTTP response into
   `MemoryStream`, write to cache, reset, parse.

3. **Existing file tracking:** To avoid scanning the directory on every page,
   maintain an in-memory list of generated file names during the download loop
   and append each new name. Pass this growing list to `GenerateWeeklyFileName`
   / `GenerateDailyFileName`.

### Acceptance Criteria

- [ ] Initial download produces `_WeekOf_` prefixed files per stream
- [ ] Incremental sync produces `DayOf_` prefixed files per stream
- [ ] Files are placed in `s{streamId}/` subdirectories
- [ ] Sequence numbers increment correctly across pages
- [ ] `_meta_s{id}.json` is created/updated per stream with stream name
- [ ] `WriteOnly` mode writes but does not process
- [ ] `Disabled` mode has zero cache interaction

---

## 4.3 — Implement Zulip Cache-Only Ingestion

### Objective

Add a code path that loads and processes all cached Zulip JSON files from
per-stream directories in chronological order.

### File to Modify: `src/FhirAugury.Sources.Zulip/ZulipSource.cs`

### `LoadFromCacheAsync` Implementation

```
1. Discover stream directories:
   directories = list subdirectories of {CacheRoot}/zulip/ matching s{digits}

2. For each stream directory (sorted by stream ID for determinism):
      streamId = parse ID from directory name

      Read _meta_s{id}.json if it exists → get streamName for logging

      keys = cache.EnumerateKeys("zulip", $"s{streamId}")
        → returns *.json files sorted by date (oldest → newest)

      for each key in keys:
          cache.TryGet("zulip", key, out stream)
          messages = ParseZulipMessages(stream)

          for each message in messages:
              Map via ZulipMessageMapper → upsert ZulipMessageRecord
              Track in IngestionResult

3. Return IngestionResult with totals
```

### Design Notes

- **Stream discovery:** In `CacheOnly` mode, the source discovers streams by
  scanning for `s{digits}` directories rather than calling the Zulip API.
  The stream name (for the `ZulipStreamRecord`) is read from the metadata
  file if available, or synthesized as `"stream-{id}"` if not.

- **Upsert semantics:** Same as Jira — messages loaded oldest-first, newest
  version wins via upsert if a message appears in overlapping weekly/daily
  batches.

- **ZulipStreamRecord creation:** When processing a stream in cache-only mode,
  upsert a `ZulipStreamRecord` with the stream ID and name (from metadata)
  before processing messages. This ensures the stream exists in the database
  for foreign key consistency.

- **JSON parsing:** The cached JSON files contain raw Zulip API message arrays.
  Parse with `JsonDocument` and map via the existing `ZulipMessageMapper`.

### Acceptance Criteria

- [ ] Discovers streams from `s{id}` directory naming
- [ ] Processes all `*.json` files per stream in chronological order
- [ ] Weekly and daily batch files are handled identically
- [ ] Duplicate messages across overlapping batches resolve correctly
- [ ] Stream metadata file provides stream name when available
- [ ] Zero HTTP calls, no credentials required
- [ ] Works with pre-populated archive directories
