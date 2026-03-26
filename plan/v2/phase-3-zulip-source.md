# Phase 3: Zulip Source Service

**Goal:** Build the second source service, confirming the patterns from
Phase 2 generalize. Zulip is the highest-volume source (1M+ messages) and
validates that the architecture handles large datasets.

**Proposal references:** [03-source-services](../../proposal/v2/03-source-services.md) (Zulip section),
[05-api-contracts](../../proposal/v2/05-api-contracts.md) (`zulip.proto`),
[06-caching-storage](../../proposal/v2/06-caching-storage.md) (Zulip cache)

**Depends on:** Phase 2 (patterns established)

---

## 3.1 — Project Setup

### 3.1.1 — Create `FhirAugury.Source.Zulip` project

Create `src/FhirAugury.Source.Zulip/` following the same structure as the
Jira source service:

```
FhirAugury.Source.Zulip/
├── Api/
│   ├── ZulipGrpcService.cs
│   └── ZulipHttpApi.cs
├── Ingestion/
│   ├── ZulipIngestionPipeline.cs
│   ├── ZulipSource.cs          # API client (adapted from v1)
│   ├── ZulipMessageMapper.cs   # JSON→record mapping (from v1)
│   ├── ZulipAuthHandler.cs     # HTTP Basic auth (from v1)
│   └── ZulipContentProcessor.cs # HTML→plain text conversion
├── Cache/
│   └── ZulipCacheLayout.cs     # Per-stream date-based batches
├── Database/
│   ├── ZulipDatabase.cs
│   └── Records/
│       ├── ZulipStreamRecord.cs
│       ├── ZulipMessageRecord.cs
│       └── ZulipSyncStateRecord.cs
├── Indexing/
│   ├── ZulipIndexer.cs
│   └── ZulipQueryBuilder.cs    # SQL builder for QueryMessages
├── Workers/
│   └── ScheduledIngestionWorker.cs
├── Configuration/
│   └── ZulipServiceOptions.cs
├── Program.cs
├── appsettings.json
└── FhirAugury.Source.Zulip.csproj
```

### 3.1.2 — Configuration schema

```json
{
  "Zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "CredentialFile": "~/.zuliprc",
    "CachePath": "./cache/zulip",
    "DatabasePath": "./data/zulip.db",
    "SyncSchedule": "04:00:00",
    "Ports": { "Http": 5170, "Grpc": 5171 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

Environment variable prefix: `FHIR_AUGURY_ZULIP_`.

---

## 3.2 — Database Schema

### 3.2.1 — Define Zulip record types

**Tables:**

| Table | Record Type | Purpose |
|-------|------------|---------|
| `zulip_streams` | `ZulipStreamRecord` | Stream metadata (name, description, message count) |
| `zulip_messages` | `ZulipMessageRecord` | Messages with content, sender, topic, HTML |
| `zulip_messages_fts` | (FTS5 virtual table) | Full-text search on messages |
| `index_keywords` | `KeywordRecord` | BM25 keyword scores |
| `sync_state` | `SyncStateRecord` | Per-stream ingestion state |

The `ZulipMessageRecord` stores both `content` (plain text, used for FTS5
indexing) and `content_html` (original HTML from the Zulip API, preserved
for rendering).

### 3.2.2 — Create `ZulipDatabase` class

Extends `SourceDatabase` with Zulip-specific schema:

- `zulip_messages_fts` — indexes `content`, `topic`, `sender_name`
- Per-stream sync state tracking
- Efficient batch inserts for high-volume message ingestion

---

## 3.3 — Ingestion Pipeline

### 3.3.1 — Adapt `ZulipSource` from v1

The v1 `ZulipSource` in `FhirAugury.Sources.Zulip/` handles API pagination
and stream/message fetching. Adapt for v2:

- Integrate with per-service `ResponseCache`
- Per-stream date-based cache files: `s{streamId}/DayOf_{date}-{seq}.json`
- Weekly batches for initial bulk download: `_WeekOf_{date}-{seq}.json`
- Per-stream sync cursors stored in `_meta_s{streamId}.json`

### 3.3.2 — Implement HTML content processing

New in v2. The Zulip API provides message content in HTML format. During
ingestion:

1. Preserve original HTML in `content_html` field and in cache
2. Convert HTML to plain text for `content` field
3. Use plain text for FTS5 indexing
4. Similar processing approach as Jira's HTML/markup handling

### 3.3.3 — Implement `ZulipIngestionPipeline`

Same flow as Jira but with Zulip-specific considerations:

1. Fetch streams list
2. For each stream, fetch messages (paginated by anchor)
3. Cache raw responses as per-stream batch files
4. Parse, process HTML, normalize
5. Upsert into database
6. Update FTS5 (via triggers)
7. Update BM25 keyword scores
8. Update per-stream sync state

---

## 3.4 — Internal Indexing

### 3.4.1 — Topic threading

Messages within the same stream + topic form a thread. Index:

- Topic → messages mapping for thread retrieval
- Stream → topics mapping ordered by most recent activity
- Topic message count tracking

### 3.4.2 — BM25 keyword scoring

Adapted from Jira (Phase 2), scoped to the Zulip message corpus.

### 3.4.3 — Sender indexing

Index messages by sender for `GetMessagesByUser` queries.

---

## 3.5 — gRPC Service Implementation

### 3.5.1 — Implement `SourceService` RPCs

Same common contract as Jira, adapted for Zulip data model.

### 3.5.2 — Implement `ZulipService` RPCs

| RPC | Implementation |
|-----|---------------|
| `GetThread` | Return all messages in a stream+topic, ordered by timestamp |
| `ListStreams` | Stream all `zulip_streams` records |
| `ListTopics` | List topics within a stream, ordered by last activity |
| `GetMessagesByUser` | Filter messages by sender name/ID |
| `QueryMessages` | Structured query (see 3.5.3) |
| `GetThreadSnapshot` | Render a topic thread as rich markdown |

### 3.5.3 — Implement `QueryMessages` SQL builder

Build parameterized SQL from `ZulipQueryRequest` fields:

- `stream_names` / `stream_ids` — filter by stream (OR within, AND across)
- `topic` — exact match
- `topic_keyword` — LIKE-based substring match
- `sender_names` / `sender_ids` — filter by sender
- `after` / `before` — timestamp range
- `query` — FTS5 subquery within filtered results
- `sort_by`, `sort_order`, `limit`, `offset`

---

## 3.6 — HTTP API

### 3.6.1 — Implement source service HTTP endpoints

Same pattern as Jira (Phase 2), on port 5170.

---

## 3.7 — Rebuild From Cache

### 3.7.1 — Implement database rebuild

Same approach as Jira but handles per-stream cache structure:

1. Enumerate all stream directories under `cache/zulip/`
2. Process batch files in chronological order within each stream
3. Handle both weekly (initial) and daily (incremental) batch formats

Must handle 1M+ messages efficiently — use large transaction batches.

---

## 3.8 — Tests

### 3.8.1 — Unit tests

- Message mapping (JSON → record)
- HTML content processing (HTML → plain text)
- Topic threading logic
- Per-stream cache layout
- QueryMessages SQL builder
- BM25 scoring

### 3.8.2 — gRPC endpoint tests

- `Search` with ranked results
- `GetThread` returns complete topic threads
- `ListStreams` and `ListTopics` navigation
- `QueryMessages` filter combinations
- `StreamSearchableText` incremental streaming

### 3.8.3 — Performance tests

Given the volume (1M+ messages), verify:
- Rebuild from cache completes in reasonable time
- FTS5 search performance with large corpus
- Batch insert throughput

---

## Phase 3 Verification

- [ ] Zulip service starts independently on ports 5170/5171
- [ ] Full download handles all streams and messages
- [ ] Per-stream incremental sync works correctly
- [ ] HTML content is properly processed to plain text
- [ ] Thread retrieval returns complete topic threads
- [ ] Stream and topic navigation works
- [ ] `QueryMessages` handles composable structured queries
- [ ] `RebuildFromCache` handles 1M+ messages from per-stream batch files
- [ ] The service host pattern from Phase 2 generalizes cleanly
- [ ] All tests pass
