# Feature: Local Response Caching for Data Sources

## Summary

Add a file-system cache layer between data sources and their remote APIs so
that raw API responses are persisted to a local directory. Downloads check the
cache before making HTTP requests, and pre-populated directories (e.g., a folder
of previously exported Jira XML files or Zulip JSON archives) are ingested
directly without any network calls. This ensures that re-launching the service,
rebuilding the project, or recreating a container never triggers a full
re-download of content that has already been fetched.

**Scope:** Caching applies to Jira, Zulip, and Confluence. GitHub is excluded —
GitHub data is self-contained in cloned repositories and does not require
API-level caching.

## Problem

Today every `IDataSource.DownloadAllAsync` call goes straight to the remote API.
This creates several pain points:

1. **Slow cold starts** — a fresh service launch or a `docker build` that resets
   state requires re-downloading tens of thousands of issues, a million+ Zulip
   messages, and thousands of Confluence pages. This can take hours and hammers
   rate-limited APIs.
2. **No offline development** — working on indexing, search, or MCP features
   requires a live network connection even if the data has been fetched before.
3. **No pre-seeding** — users who already have bulk exports (e.g., Jira XML
   dumps, Zulip JSON archives) cannot feed them into the pipeline without
   manually converting them or re-downloading from the API.
4. **Wasted API quota** — Jira's session-based auth makes repeated full
   downloads expensive. Zulip is more lenient, but 1 M+ messages still take
   considerable wall-clock time.

## Goals

1. Every HTTP response fetched by a source is automatically written to a
   cache directory, organized in a deterministic, human-browsable layout.
2. On subsequent runs the source checks the cache first; if a valid cache entry
   exists and is fresh enough, the network call is skipped entirely.
3. Users can point a source at a directory of pre-existing files (e.g., a
   colleague's export, a CI artifact, or a manual download) and ingest them
   without credentials or network access.
4. Cache directories survive container rebuilds when mounted as a volume, and
   survive service restarts by default.
5. Cache behaviour is fully configurable: per-source enable/disable, cache-only
   mode, and manual cache clearing.

## Non-Goals

- Replacing the SQLite database — the cache stores raw API payloads; the
  database stores normalized, indexed records. Both are needed.
- Implementing a distributed or shared cache — this is a local file-system
  cache only.
- Caching at the HTTP client level (e.g., `ETag`/`If-Modified-Since`) — that
  is a separate, complementary optimisation.
- Caching GitHub data — GitHub source data lives in cloned git repositories,
  which are inherently local and versioned. No API-level cache is needed.

---

## Design

### Cache Directory Layout

Each cached source gets a subdirectory under a shared cache root. **Jira and
Zulip both use date-based batch files** because their data is downloaded in
bulk — Jira via JQL date-range queries, Zulip by fetching message history per
stream. A single batch file contains all items that were created or updated
within that date window. Confluence uses one file per page. GitHub is excluded
from caching entirely.

```
{CacheRoot}/
├── _meta_jira.json                     # Jira sync cursor & metadata
├── _meta_confluence.json               # Confluence sync cursor & metadata
├── jira/
│   ├── DayOf_2026-03-18-000.xml        # new-format batch: date + sequence
│   ├── DayOf_2026-03-18-001.xml        # second batch for the same day
│   ├── DayOf_2025-11-05.xml            # legacy daily batch (no sequence)
│   └── _WeekOf_2024-08-05.xml          # legacy weekly batch
├── zulip/
│   ├── _meta_s270.json                 # stream s270 cursor (includes stream name)
│   ├── _meta_s412.json                 # stream s412 cursor
│   ├── s270/                           # stream ID with 's' prefix
│   │   ├── _WeekOf_2024-08-05-000.json # weekly batch (initial download)
│   │   ├── _WeekOf_2024-08-05-001.json # second page for that week
│   │   ├── DayOf_2026-03-18-000.json   # daily batch (incremental)
│   │   └── ...
│   ├── s412/
│   │   └── ...
│   └── ...
├── confluence/
│   ├── pages/
│   │   ├── {page-id}.json
│   │   └── ...
```

**Design rationale:**

- **Date-based batch files for Jira and Zulip.** Both sources fetch data in
  bulk. Caching per-entity (per-ticket or per-message) would require splitting
  every API response and reassembling on read, creating millions of tiny files
  for Zulip. Caching by date keeps raw API payloads intact, aligns with the
  date-range queries used for incremental sync, and matches the format of
  existing pre-downloaded exports.
- **Weekly batches for initial Zulip download.** When downloading a stream's
  full history for the first time, messages are grouped into weekly batches
  (`_WeekOf_yyyy-MM-dd-###.json`). This keeps file counts manageable — a stream
  with 5 years of history produces ~260 weekly files instead of millions of
  individual message files. Subsequent incremental syncs use daily batches
  (`DayOf_yyyy-MM-dd-###.json`).
- **Oldest-to-newest loading order.** When loading cached files (Jira or Zulip),
  files are sorted by their embedded date (and sequence number) and processed
  from oldest to newest. A Jira ticket can appear in multiple batch files (e.g.,
  downloaded when created and again when updated); a Zulip message can appear in
  overlapping weekly/daily batches. Loading chronologically ensures the most
  recent version of each record wins via upsert.
- **One file per entity** for Confluence pages keeps the cache human-browsable
  and makes partial updates trivial — just drop new files in.
- **GitHub excluded.** GitHub source data (issues, discussions, etc.) lives in
  cloned git repositories. The repository itself is the cache — `git pull` is
  the incremental sync. No API-level caching is needed.
- **`_meta_*.json` files** store sync cursors so the cache layer knows where
  a previous download left off without querying the database. Jira and
  Confluence each have a single metadata file (`_meta_jira.json`,
  `_meta_confluence.json`) in the cache root. Zulip has one metadata file per
  stream (`_meta_s{id}.json` in `{CacheRoot}/zulip/`), which also records the
  current human-readable stream name for reference.

### Jira Cache File Naming

Three file name patterns are recognized, listed in the order they were
introduced:

| Pattern | Example | Description |
|---|---|---|
| `_WeekOf_yyyy-MM-dd.xml` | `_WeekOf_2024-08-05.xml` | Legacy weekly batch. The date is the Monday of the week. Contains all issues created or updated during that week. |
| `DayOf_yyyy-MM-dd.xml` | `DayOf_2025-11-05.xml` | Legacy daily batch (no sequence number). Contains all issues created or updated on that date. |
| `DayOf_yyyy-MM-dd-###.xml` | `DayOf_2026-03-18-000.xml` | **Current format.** Daily batch with a zero-filled three-digit sequence number. Each API response page is saved as a separate file; the sequence increments from `000`. |

All three patterns are supported for reading. Only the current format
(`DayOf_yyyy-MM-dd-###.xml`) is used when writing new downloads.

**Sorting rules for loading:**

1. Extract the date from the file name (`yyyy-MM-dd` portion).
2. For weekly files (`_WeekOf_`), the date is the start of the week.
3. Files are sorted ascending by date, then by sequence number (legacy files
   without a sequence number sort before sequenced files for the same date),
   then alphabetically by prefix (`_WeekOf_` before `DayOf_` for the same
   date) to ensure weekly batches are loaded before daily ones when dates
   overlap.
4. Files are processed in this order so that newer data overwrites older data
   for the same ticket via upsert.

### Zulip Cache File Naming

Zulip cache files live under `{CacheRoot}/zulip/s{stream-id}/` (stream ID
prefixed with `s` so directory names begin with a letter) and follow the same
date-based batch pattern as Jira, using JSON instead of XML.

| Pattern | Example | When used |
|---|---|---|
| `_WeekOf_yyyy-MM-dd-###.json` | `_WeekOf_2024-08-05-000.json` | **Initial full download.** When downloading a stream's complete history for the first time, messages are grouped into weekly batches. The date is the Monday of the week. Each API response page is a separate file; the sequence increments from `000`. |
| `DayOf_yyyy-MM-dd-###.json` | `DayOf_2026-03-18-000.json` | **Incremental updates.** After the initial download, subsequent syncs fetch only messages posted since the last sync and store them in daily batches with sequence numbers. |

**Sorting rules for loading** are the same as Jira:

1. Extract the date from the file name.
2. Sort ascending by date, then sequence number, then prefix (`_WeekOf_` before
   `DayOf_` for the same date).
3. Process oldest-first so that if a message appears in overlapping
   weekly/daily batches, the newest version wins via upsert.

**Why weekly for initial download?** A busy Zulip stream can have millions of
messages spanning years. Using daily files would produce thousands of files with
many empty days. Weekly batches keep the file count manageable (~52 per year)
while still being granular enough for human browsing and partial re-ingestion.

### Core Abstraction

A new `IResponseCache` interface is added to `FhirAugury.Models`:

```csharp
/// <summary>
/// File-system cache for raw API responses.
/// </summary>
public interface IResponseCache
{
    /// <summary>Check whether a cached entry exists and is fresh.</summary>
    bool TryGet(string source, string key, out Stream content);

    /// <summary>Write a response to the cache.</summary>
    Task PutAsync(string source, string key, Stream content, CancellationToken ct);

    /// <summary>Delete a single cached entry.</summary>
    void Remove(string source, string key);

    /// <summary>Enumerate all cached keys for a source (for cache-only ingestion).</summary>
    IEnumerable<string> EnumerateKeys(string source);
}
```

A concrete `FileSystemResponseCache` lives in a new
`FhirAugury.Caching` project (or in `FhirAugury.Models` if keeping
the project count down is preferred).

For Jira specifically, the cache key for writes is derived from the current date
and an auto-incrementing sequence number: `DayOf_2026-03-18-000`. The `PutAsync`
method determines the next available sequence number by scanning existing files
for the same date prefix. When enumerating keys, `EnumerateKeys("jira")` returns
all `*.xml` files matching any of the three recognized patterns (see
[Jira Cache File Naming](#jira-cache-file-naming)), sorted in chronological
order (oldest first).

For Zulip, the same pattern applies per stream subdirectory (`s{stream-id}/`).
`EnumerateKeys` returns all `*.json` files across all stream directories, sorted
by stream then chronologically within each stream (see
[Zulip Cache File Naming](#zulip-cache-file-naming)). During initial full
download, `PutAsync` generates `_WeekOf_` prefixed files; during incremental
sync, it generates `DayOf_` prefixed files.

### Source Integration

Each source receives the cache through its constructor. The download loop
changes from:

```
for each page:
    response = await HttpGet(url)
    process(response)
```

to:

```
for each page:
    response = await HttpGet(url)
    await cache.PutAsync(source, key, response)
    process(response)
```

When running in **cache-only mode**, files are loaded from the cache instead:

```
for each key in cache.EnumerateKeys(source):   // sorted oldest → newest
    cache.TryGet(source, key, out cached)
    process(cached)                             // upsert — latest version wins
```

**Batch source loading behaviour (Jira and Zulip):** Because batch files are
date-ordered and a single record may appear in multiple files (e.g., a Jira
ticket created on day 1 and updated on day 5, or a Zulip message appearing in
both a weekly and daily batch), loading oldest-to-newest ensures the final state
of each record in the database reflects its most recent update. The `process`
step uses upsert semantics — if a record already exists in the database, it is
overwritten with the newer data.

### Cache-Only / Local Directory Mode

This is the primary use case from the feature request. A user who has a folder
of pre-downloaded Jira XML export files sets:

```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "/data/fhir-cache",
      "Sources": {
        "jira": {
          "Mode": "CacheOnly"
        }
      }
    }
  }
}
```

Or via CLI:

```
fhir-augury download jira --cache-only --cache-path /data/jira-exports
```

In this mode the Jira source:

1. Calls `cache.EnumerateKeys("jira")` to discover all `*.xml` files under
   `{CacheRoot}/jira/`, matching any of the three recognized file name patterns
   (`_WeekOf_yyyy-MM-dd.xml`, `DayOf_yyyy-MM-dd.xml`, `DayOf_yyyy-MM-dd-###.xml`).
2. Sorts the files chronologically — oldest date first, weekly files before
   daily files for the same date, lower sequence numbers before higher ones.
3. Deserializes each XML file using `JiraXmlParser.ParseExport`, extracting all
   issues and comments from the batch.
4. Runs the normal `ProcessIssue` pipeline (field mapping → upsert → indexing)
   for every issue in every file. Because files are processed oldest-first and
   a ticket may appear in multiple files, the final database state reflects the
   most recent version of each ticket.
5. Makes zero network calls and requires no credentials.

### Configuration

New properties on the shared `SourceConfiguration` base and a top-level
`Cache` section:

```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "./cache",
      "DefaultMode": "WriteThrough"
    },
    "Sources": {
      "jira": {
        "Cache": {
          "Mode": "WriteThrough",
          "Path": null
        }
      }
    }
  }
}
```

| Property | Type | Default | Description |
|---|---|---|---|
| `Cache.RootPath` | `string` | `"./cache"` | Root directory for all source caches |
| `Cache.DefaultMode` | `CacheMode` | `WriteThrough` | Default mode for all sources |
| `Sources.{name}.Cache.Mode` | `CacheMode` | (inherits default) | Per-source override |
| `Sources.{name}.Cache.Path` | `string?` | `null` | Override the cache subdirectory for this source |

**`CacheMode` enum:**

| Value | Behaviour |
|---|---|
| `Disabled` | No caching — current behaviour; always fetch from API |
| `WriteThrough` | Read from cache if fresh → otherwise fetch from API → write to cache |
| `CacheOnly` | Read from cache only — no network calls, no credentials required |
| `WriteOnly` | Always fetch from API → write to cache (useful for building a cache without using it yet) |

### CLI Surface

New global option and per-source overrides:

```
fhir-augury download jira --cache-path ./my-jira-dump --cache-mode CacheOnly
fhir-augury download zulip --cache-mode WriteThrough
fhir-augury cache stats                    # show cache size per source
fhir-augury cache clear --source jira      # clear one source's cache
fhir-augury cache clear                    # clear all caches
```

### Freshness & Staleness

Sync cursors and freshness metadata are stored in per-source metadata files
rather than per-file sidecars:

```json
// _meta_jira.json
{
  "lastSyncDate": "2026-03-18",
  "lastSyncTimestamp": "2026-03-18T10:00:00Z",
  "totalFiles": 542,
  "format": "xml"
}
```

```json
// zulip/_meta_s270.json
{
  "streamId": 270,
  "streamName": "implementers",
  "lastSyncDate": "2026-03-18",
  "lastSyncTimestamp": "2026-03-18T10:00:00Z",
  "initialDownloadComplete": true
}
```

In `CacheOnly` mode, freshness metadata is ignored — all cached entries are
processed unconditionally. Already-cached data is never re-downloaded; to force
a fresh download, clear the cache first via `cache clear`.

### Docker / Container Support

The cache directory is designed to be a bind-mount or Docker volume:

```yaml
# docker-compose.yml
services:
  fhir-augury:
    volumes:
      - ./local-cache:/data/cache   # survives rebuilds
      - fhir-augury-db:/data/db
    environment:
      - FHIR_AUGURY_Cache__RootPath=/data/cache
```

```dockerfile
# dockerfile addition
VOLUME ["/data/cache"]
```

This means `docker compose down && docker compose build && docker compose up`
reuses the cache — no re-download.

---

## Interaction with Existing Features

### Incremental Sync

The cache and incremental sync are complementary:

- **Incremental sync** reduces *what* is fetched (only items updated since last
  sync).
- **The cache** reduces *whether* each item needs to be fetched at all (skip if
  already in cache and fresh).

During an incremental run the source still queries the API for the list of
updated items, but individual item fetches hit the cache first.

### Cross-Reference Indexing

No change. The cross-reference linker operates on `IngestedItem` records
returned by sources. Whether those items came from cache or network is
invisible to the indexing pipeline.

### MCP Server

No change. The MCP server reads from the SQLite database, not from raw API
responses. The cache is upstream of the database.

---

## Migration & Backwards Compatibility

- **No breaking changes.** The default `CacheMode` is `WriteThrough`, which
  adds caching transparently. Existing configurations continue to work.
- **Existing databases are unaffected.** The cache is a new, parallel artefact.
- **No new required configuration.** All cache settings have sensible defaults.

## Implementation Sketch

1. Add `IResponseCache` and `CacheMode` to `FhirAugury.Models`.
2. Implement `FileSystemResponseCache` with per-source metadata files
   (`_meta_jira.json`, `_meta_confluence.json`, `zulip/_meta_s{id}.json`),
   date-based batch naming for Jira and Zulip, and one-file-per-entity for
   Confluence. Cache size is unlimited; manual clearing via `cache clear`.
3. Implement shared date-based cache file discovery: scan for `_WeekOf_` and
   `DayOf_` patterns (with and without sequence numbers), extract embedded
   dates, and return them sorted oldest-first. This logic is shared between
   Jira (`.xml`) and Zulip (`.json`).
4. Implement cache write for batch sources: generate
   `DayOf_yyyy-MM-dd-###.{ext}` (or `_WeekOf_` for Zulip initial download)
   file names, auto-incrementing the sequence number per date prefix. Existing
   legacy files (`_WeekOf_` without sequence, `DayOf_` without sequence) are
   never modified.
5. Add `CacheConfiguration` record and wire it into `appsettings.json` binding.
6. Modify the Jira source's download loop to write each API response page as
   a new sequenced batch file via `cache.PutAsync`. All cached Jira files must
   use a single format; if JSON is preferred going forward, implement a
   lossless XML-to-JSON conversion utility for existing exports.
7. Modify the Zulip source's download loop: use `_WeekOf_` batches for full
   history download, `DayOf_` batches for incremental sync. Each API response
   page is saved as a sequenced file within `s{stream-id}/`. Stream metadata
   files record the current stream name for human reference.
8. Add cache-only ingestion paths for Jira (XML via `JiraXmlParser`) and Zulip
   (JSON) that enumerate cached files in date order and upsert all records.
   Already-cached data is never re-downloaded; to start fresh, clear the cache.
9. Modify the Confluence source's download loop to check/write through the
   cache (one file per page).
10. Add `cache-path` and `cache-mode` CLI options.
11. Add `cache stats` and `cache clear` CLI commands.
12. Add unit tests for cache hit/miss/enumeration, file name pattern parsing,
    and chronological sort order (shared logic for both Jira and Zulip).
13. Add integration test: pre-populate directories with `_WeekOf_`, `DayOf_`,
    and `DayOf_…-###` files for both Jira and Zulip → run `CacheOnly`
    download → verify records appearing in multiple files resolve to their
    newest version.
14. Update `docs/configuration.md` with cache settings.
15. Update `dockerfile` to expose a cache volume.

## Decisions

The following questions were raised during design and have been resolved:

1. **Metadata storage:** Per-source metadata files instead of per-file sidecar
   `.meta` files. Jira uses `_meta_jira.json` in the cache root. Zulip uses
   `_meta_s{id}.json` per stream (in `{CacheRoot}/zulip/`, as a peer to the
   stream directories), which also records the current human-readable stream
   name. Confluence uses `_meta_confluence.json` in the cache root.

2. **CacheOnly mode and incremental sync:** In `CacheOnly` mode, the source
   enumerates all cached entries and does not make any API calls — including the
   "what changed?" query that `DownloadIncrementalAsync` normally issues.

3. **Cache size limits:** No automatic eviction. The cache is unlimited. Users
   who want to reclaim space use `cache clear` (per-source or global).

4. **Legacy weekly files:** Existing `_WeekOf_` files are loaded as-is and never
   modified or re-split into daily files.

5. **Jira file format consistency:** All files in the Jira cache must use the
   same format. If JSON is the preferred format going forward, a lossless
   XML-to-JSON conversion utility must be provided to migrate existing XML
   exports. The conversion must be lossless — no data may be dropped.

6. **Zulip stream directories:** Named by stream ID with an `s` prefix (e.g.,
   `s270`, `s412`) so that directory names begin with a letter. The per-stream
   metadata file (`_meta_s{id}.json`) includes the current stream name for
   human reference.

7. **Re-downloading cached data:** Already-cached data is never re-downloaded.
   If a user wants to download everything from scratch, they clear the cache
   first via `cache clear`.