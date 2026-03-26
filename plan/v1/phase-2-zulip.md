# Phase 2: Zulip Integration

**Goal:** Add Zulip as the second data source and implement unified
cross-source search.

**Depends on:** Phase 1 (Foundation)

**Status:** ✅ Complete (2026-03-18)

---

## 2.1 — Zulip Database Tables

### Objective

Define the Zulip SQLite records and FTS5 tables.

### Files to Create in `FhirAugury.Database/`

#### 2.1.1 `Records/ZulipStreamRecord.cs`

Fields: Id, Name, Description, IsWebPublic, MessageCount, LastFetchedAt.

#### 2.1.2 `Records/ZulipMessageRecord.cs`

Fields: Id, StreamId (FK → ZulipStreamRecord.Id), StreamName, Topic,
SenderId, SenderName, SenderEmail, ContentHtml, ContentPlain, Timestamp,
CreatedAt, Reactions (JSON).

Indexes: StreamId, (StreamId, Topic), SenderId, Timestamp.

#### 2.1.3 Update `FtsSetup.cs`

Add FTS5 table and triggers for Zulip:
- `zulip_messages_fts` — indexes: StreamName, Topic, SenderName, ContentPlain
- Content-synced with INSERT/UPDATE/DELETE triggers on `zulip_messages`

#### 2.1.4 Update `DatabaseService.InitializeDatabase()`

Add Zulip table creation to the initialization sequence.

### Acceptance Criteria

- [x] Source generator compiles Zulip records
- [x] CRUD operations work on both Zulip tables
- [x] FTS5 triggers fire on insert/update/delete
- [x] Existing Jira tables/tests still work

---

## 2.2 — Zulip Source Implementation

### Objective

Implement the Zulip data source using `zulip-cs-lib`.

### Files to Create in `FhirAugury.Sources.Zulip/`

#### 2.2.1 NuGet references

```xml
<PackageReference Include="zulip-cs-lib" Version="0.0.1-*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`

#### 2.2.2 `ZulipSource.cs`

Implements `IDataSource`:

- `SourceName` → `"zulip"`
- `DownloadAllAsync`:
  1. Fetch all streams via `GET /api/v1/streams`
  2. Filter to `is_web_public` streams
  3. For each stream, paginate messages in ascending order
     (anchor = 0, batch size 1000, `num_after = 1000`)
  4. Strip HTML from content → `ContentPlain`
  5. Upsert streams and messages into database
  6. Track highest message ID per stream in `sync_state`
- `DownloadIncrementalAsync`:
  1. For each stream, read last cursor (highest msg ID) from `sync_state`
  2. Fetch messages with anchor = last ID + 1
  3. Also check for edited messages since `since` timestamp
  4. Upsert new/updated messages
- `IngestItemAsync`:
  1. Parse identifier as `stream:topic`
  2. Fetch messages for that stream+topic narrow
  3. Upsert all messages in the topic thread

#### 2.2.3 `ZulipSourceOptions.cs`

Configuration:
- `string BaseUrl` (default: `https://chat.fhir.org`)
- `string? CredentialFile` (path to `.zuliprc` file)
- `string? Email`
- `string? ApiKey`
- `int BatchSize` (default: 1000)
- `bool OnlyWebPublic` (default: true)

#### 2.2.4 `ZulipMessageMapper.cs`

Maps `zulip-cs-lib` message objects to `ZulipMessageRecord`:
- Extracts all fields from the Zulip API response
- Strips HTML content to plain text for indexing
- Converts Unix timestamp to `DateTimeOffset`
- Serializes reactions to JSON string

### Acceptance Criteria

- [x] Can authenticate with Zulip API using `.zuliprc` file
- [x] Full download iterates all public streams and paginates messages
- [x] Incremental download uses last-seen message ID as anchor
- [x] On-demand download fetches a single topic thread
- [x] HTML content is stripped for `ContentPlain`
- [x] Messages are correctly upserted (no duplicates on re-download)

---

## 2.3 — CLI Extensions (Zulip)

### Objective

Extend the CLI to support Zulip download, search, and display.

### Files to Update/Create

#### 2.3.1 Update `Commands/DownloadCommand.cs`

Add `--source zulip` support with Zulip auth options:
- `--zulip-email`
- `--zulip-api-key`
- `--zulip-rc` (path to `.zuliprc`)

#### 2.3.2 Update `Commands/SyncCommand.cs`

Add `--source zulip` and `--source all` support.
When `all`, iterates through each registered source.

#### 2.3.3 Update `Commands/SearchCommand.cs`

Add `--source zulip` filter. When no source filter, search both Jira and Zulip.

#### 2.3.4 Update `Commands/SnapshotCommand.cs`

Add Zulip thread snapshot:
- Input: `--source zulip --id "stream:topic"`
- Renders the full topic thread with sender, timestamp, content
- Messages displayed in chronological order

#### 2.3.5 Update `Commands/StatsCommand.cs`

Add Zulip statistics: stream count, message count, last sync time.

### Acceptance Criteria

- [x] `fhir-augury download --source zulip` downloads messages
- [x] `fhir-augury search -q "test" -s zulip` searches Zulip only
- [x] `fhir-augury snapshot --source zulip --id "implementers:FHIRPath"` shows thread
- [x] `fhir-augury stats` shows both Jira and Zulip counts

---

## 2.4 — Unified Search

### Objective

Implement cross-source full-text search combining Jira and Zulip results
with score normalization.

### Files to Create/Update in `FhirAugury.Indexing/`

#### 2.4.1 Update `FtsSearchService.cs`

Add methods:
- `SearchZulipMessages(connection, query, stream?, limit)` → `List<SearchResult>`
- `UnifiedSearch(connection, query, sources?, limit)` → `List<SearchResult>`

The unified search:
1. Queries each enabled source's FTS5 table independently
2. Normalizes scores across sources using min-max scaling within each source
3. Merges and sorts by normalized score
4. Returns the top N results

#### 2.4.2 `ScoreNormalizer.cs`

Implements min-max normalization for cross-source score comparability:
- Within each source's result set, normalize to [0, 1] range
- Apply optional source-weight multiplier (configurable)
- Handle edge cases (single result, all same score)

### Acceptance Criteria

- [x] Unified search returns results from both Jira and Zulip
- [x] Results are interleaved by relevance, not grouped by source
- [x] Score normalization makes cross-source ranking reasonable
- [x] Source filter (`-s jira`) correctly limits search scope

---

## 2.5 — Tests

### New Test Files

#### `tests/FhirAugury.Database.Tests/`

- `ZulipStreamRecordTests.cs` — CRUD operations
- `ZulipMessageRecordTests.cs` — CRUD operations
- `Fts5ZulipTests.cs` — FTS5 search on Zulip messages

#### `tests/FhirAugury.Sources.Tests/`

- `ZulipMessageMapperTests.cs` — message field mapping, HTML stripping

#### `tests/FhirAugury.Indexing.Tests/`

- Create project with xUnit dependencies
- `UnifiedSearchTests.cs` — cross-source search with mock data
- `ScoreNormalizerTests.cs` — normalization edge cases

### Test Data

- `tests/TestData/sample-zulip-messages.json` — representative Zulip API response

### Acceptance Criteria

- [x] All new tests pass
- [x] All Phase 1 tests still pass
- [x] Unified search tested with data from both sources simultaneously
