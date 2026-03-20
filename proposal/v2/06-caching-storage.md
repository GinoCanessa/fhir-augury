# FHIR Augury v2 — Caching & Storage

## Design Philosophy

In v2, each source service manages two layers of persistence:

1. **Cache layer** — raw API responses on the file system. This is the ground
   truth. Survives database rebuilds, schema changes, and container restarts.
2. **Database layer** — normalized, indexed SQLite database. This is a derived,
   queryable projection of the cached data. Can be rebuilt from cache at any
   time without network access.

```
Remote API ──► Cache (filesystem) ──► SQLite Database (normalized + FTS5)
                  │                          │
                  │  (raw JSON/XML)          │  (structured records + indexes)
                  │                          │
                  └── survives rebuilds      └── can be regenerated from cache
```

---

## Local Cache

### Purpose

Every HTTP response fetched from a remote source is automatically written to a
cache directory. This ensures:

1. **Fast rebuilds** — if the database needs to be recreated (schema change,
   corruption, migration), the service reads from cache instead of
   re-downloading from the remote API
2. **Offline development** — work on indexing, search, or MCP features without
   network access
3. **Pre-seeding** — drop files into the cache directory and ingest without
   credentials
4. **API quota preservation** — avoid redundant downloads

### Cache Architecture

Each source service has its own cache, managed by a `ResponseCache` class:

```csharp
public class ResponseCache
{
    private readonly string _cacheRoot;

    /// Write a response to the cache.
    public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// Read a cached response. Returns null if not cached.
    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken ct);

    /// Check if a cache entry exists and is fresh enough.
    public bool Exists(string relativePath, TimeSpan? maxAge = null);

    /// Enumerate all cached files matching a pattern.
    public IEnumerable<string> EnumerateFiles(string pattern);

    /// Get total cache size in bytes.
    public long GetTotalSizeBytes();

    /// Clear the entire cache.
    public void Clear();
}
```

### Cache Modes

Each source service supports configurable cache modes:

| Mode | Behavior |
|------|----------|
| `ReadWrite` | Check cache before API calls; write responses to cache (default) |
| `WriteOnly` | Always call API; write responses to cache (for forced refresh) |
| `ReadOnly` | Only read from cache; never call API (offline mode) |
| `Disabled` | No caching; always call API directly |

```json
{
  "Jira": {
    "CacheMode": "ReadWrite",
    "CachePath": "./cache/jira",
    "CacheMaxAge": "7.00:00:00"
  }
}
```

### Cache Directory Layouts

Each source uses a layout optimized for its data patterns:

#### Jira Cache

```
cache/jira/
├── _meta.json                           # Sync cursor, last download timestamp
├── DayOf_2026-03-18-000.xml             # Date-based batch files
├── DayOf_2026-03-18-001.xml             # Sequence number for multiple batches/day
├── DayOf_2026-03-17-000.xml
└── ...
```

Jira data is downloaded in date-range batches (all issues updated in a given
day/week). Each batch file contains multiple issues. The date-based naming
enables incremental cache population — only fetch batches for days not already
cached.

#### Zulip Cache

```
cache/zulip/
├── _meta_s{streamId}.json               # Per-stream sync cursor
├── s{streamId}/                         # One directory per stream
│   ├── _WeekOf_2024-08-05-000.json      # Weekly batches (initial bulk download)
│   ├── _WeekOf_2024-08-05-001.json      # Sequence for multi-page weeks
│   ├── DayOf_2026-03-18-000.json        # Daily batches (incremental)
│   └── ...
```

Zulip messages are append-only, so the cache grows monotonically. Each batch
file contains messages for a time window within a stream.

#### Confluence Cache

```
cache/confluence/
├── _meta.json                           # Global sync cursor
├── spaces/
│   ├── FHIR.json                        # Space metadata
│   └── FHIRI.json
├── pages/
│   ├── {pageId}.json                    # One file per page (complete content)
│   └── ...
```

Confluence uses per-page caching because pages are large, individually
addressable, and updated independently.

#### GitHub Cache

```
cache/github/
├── _meta.json                           # Global sync cursor
├── repos/
│   ├── HL7_fhir/                        # Underscore-separated owner/repo
│   │   ├── issues/
│   │   │   ├── {number}.json            # One file per issue/PR
│   │   │   └── ...
│   │   ├── comments/
│   │   │   ├── {issueNumber}.json       # All comments for an issue
│   │   │   └── ...
│   │   └── commits/
│   │       ├── page-001.json            # Paginated commit history
│   │       └── ...
│   └── HL7_fhir-ig-publisher/
│       └── ...
```

---

## Database Layer

### One Database Per Service

Each source service has its own SQLite database containing:

1. **Source-specific tables** — normalized records for the source's content type
2. **FTS5 virtual tables** — full-text search indexes with content-synced triggers
3. **BM25 keyword tables** — pre-computed keyword scores for similarity queries
4. **Internal reference tables** — links within the source's own data
5. **Sync state table** — ingestion tracking

The orchestrator has a separate database for cross-reference links and scan
state.

| Service | Database | Estimated Size |
|---------|----------|----------------|
| Jira | `data/jira.db` | 500 MB – 1 GB |
| Zulip | `data/zulip.db` | 1.5 – 2 GB |
| Confluence | `data/confluence.db` | 200 – 500 MB |
| GitHub | `data/github.db` | 100 – 300 MB |
| Orchestrator | `data/orchestrator.db` | 50 – 200 MB |

### Database Rebuild From Cache

Each source service implements a `RebuildFromCache` operation:

```csharp
public class DatabaseRebuilder
{
    public async Task<RebuildResult> RebuildAsync(
        ResponseCache cache,
        SourceDatabase database,
        CancellationToken ct)
    {
        // 1. Drop and recreate all tables
        database.DropAllTables();
        database.CreateAllTables();

        // 2. Read all cached files in chronological order
        var cacheFiles = cache.EnumerateFiles("*")
            .OrderBy(CacheFileOrdering);

        // 3. Parse and load each cached response
        int itemsLoaded = 0;
        using var transaction = database.BeginTransaction();

        foreach (var file in cacheFiles)
        {
            var data = await cache.ReadAsync(file, ct);
            var items = _parser.Parse(data);

            foreach (var item in items)
            {
                database.Upsert(item);
                itemsLoaded++;
            }
        }

        transaction.Commit();

        // 4. Rebuild FTS5 indexes
        database.RebuildFtsIndexes();

        // 5. Rebuild BM25 keyword scores
        await _indexer.RebuildBm25Async(database, ct);

        // 6. Rebuild internal references
        await _indexer.RebuildInternalRefsAsync(database, ct);

        return new RebuildResult(itemsLoaded, stopwatch.Elapsed);
    }
}
```

### WAL Mode

All SQLite databases use WAL (Write-Ahead Logging) mode for concurrent read
access while ingestion is running:

```csharp
public class SourceDatabase
{
    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
```

---

## Volume Management (Docker)

For Docker deployments, cache and data directories are mounted as named volumes
so they persist across container rebuilds:

```yaml
services:
  source-jira:
    volumes:
      - jira-cache:/app/cache     # Raw API responses (persist across rebuilds)
      - jira-data:/app/data       # SQLite database (can be rebuilt from cache)

volumes:
  jira-cache:     # Precious — never auto-delete
  jira-data:      # Rebuildable — can be deleted and regenerated from cache
  zulip-cache:
  zulip-data:
  confluence-cache:
  confluence-data:
  github-cache:
  github-data:
  orchestrator-data:
```

**Key insight:** Cache volumes are the critical data. Database volumes are
derived and can be regenerated. This means:

- `docker compose down -v` (which removes volumes) loses the cache and requires
  re-download. Use `docker compose down` (without `-v`) to preserve volumes.
- To force a database rebuild: delete only the data volume, keep the cache
  volume, and restart the service.
- To share data: copy the cache directory from one machine to another.

---

## Ingestion Pipeline

Each source service's ingestion pipeline integrates caching:

```
                                     ┌──────────────┐
                                     │ Cache Exists? │
                                     └──────┬───────┘
                                            │
                              ┌─────────────┼─────────────┐
                              ▼             │             ▼
                         Yes (fresh)        │         No / Stale
                              │             │             │
                              ▼             │             ▼
                     Read from cache        │     Fetch from remote API
                              │             │             │
                              │             │             ▼
                              │             │     Write to cache
                              │             │             │
                              └─────────────┼─────────────┘
                                            │
                                            ▼
                                    Parse & Normalize
                                            │
                                            ▼
                                  Upsert into SQLite DB
                                            │
                                            ▼
                               Update FTS5 (via triggers)
                                            │
                                            ▼
                              Update BM25 keyword scores
                                            │
                                            ▼
                            Update internal references
```
