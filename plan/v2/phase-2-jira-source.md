# Phase 2: Jira Source Service

**Goal:** Build the first source service to validate the v2 architecture.
Jira is built first because it has the richest data model (custom fields,
issue links, JIRA-Spec-Artifacts integration) and serves as the reference
implementation for the other source services.

**Proposal references:** [03-source-services](../../proposal/v2/03-source-services.md) (Jira section),
[05-api-contracts](../../proposal/v2/05-api-contracts.md) (`jira.proto`),
[06-caching-storage](../../proposal/v2/06-caching-storage.md) (Jira cache)

**Depends on:** Phase 1

---

## 2.1 — Project Setup

### 2.1.1 — Create `FhirAugury.Source.Jira` project

Create `src/FhirAugury.Source.Jira/` as a standalone ASP.NET web application
(`Microsoft.NET.Sdk.Web`).

**Project structure:**
```
FhirAugury.Source.Jira/
├── Api/                      # gRPC + HTTP endpoints
│   ├── JiraGrpcService.cs    # gRPC SourceService + JiraService impl
│   └── JiraHttpApi.cs        # HTTP Minimal API endpoints
├── Ingestion/                # Download, parse, normalize
│   ├── JiraIngestionPipeline.cs
│   ├── JiraSource.cs         # API client (adapted from v1)
│   ├── JiraFieldMapper.cs    # JSON→record mapping (from v1)
│   ├── JiraCommentParser.cs  # Comment extraction (from v1)
│   ├── JiraXmlParser.cs      # XML bulk parsing (from v1)
│   ├── JiraAuthHandler.cs    # Cookie/API-token auth (from v1)
│   └── SpecArtifacts/        # JIRA-Spec-Artifacts integration
│       ├── SpecArtifactIngester.cs  # git clone/pull, XML parsing
│       └── SpecArtifactXmlParser.cs # Parse families, specs, workgroups
├── Cache/                    # File-system response cache config
│   └── JiraCacheLayout.cs    # Date-based batch file naming
├── Database/                 # SQLite schema, generated CRUD, FTS5
│   ├── JiraDatabase.cs       # Schema creation, FTS5 setup
│   └── Records/              # cslightdbgen record types
│       ├── JiraIssueRecord.cs
│       ├── JiraCommentRecord.cs
│       ├── JiraIssueLinkRecord.cs
│       ├── JiraSpecArtifactRecord.cs
│       └── JiraSyncStateRecord.cs
├── Indexing/                 # FTS5, BM25, internal refs
│   ├── JiraIndexer.cs        # FTS5 + BM25 + internal link indexing
│   └── JiraQueryBuilder.cs   # SQL builder for QueryIssues
├── Workers/                  # Background services
│   └── ScheduledIngestionWorker.cs
├── Configuration/
│   └── JiraServiceOptions.cs
├── Program.cs                # Service host
├── appsettings.json
└── FhirAugury.Source.Jira.csproj
```

**Dependencies:**
- `FhirAugury.Common` (project reference)
- `Microsoft.Data.Sqlite`
- `cslightdbgen.sqlitegen` (source generator)
- `Grpc.AspNetCore`
- `Microsoft.Extensions.Http`

### 2.1.2 — Configuration schema

Implement strongly-typed options from the proposal:

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "cookie",
    "CachePath": "./cache/jira",
    "DatabasePath": "./data/jira.db",
    "SyncSchedule": "01:00:00",
    "DefaultProject": "FHIR",
    "Ports": { "Http": 5160, "Grpc": 5161 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

Environment variable prefix: `FHIR_AUGURY_JIRA_`.

---

## 2.2 — Database Schema

### 2.2.1 — Define Jira record types

Create source-generated record types for the Jira service's own SQLite
database (`jira.db`). These are adapted from v1's `JiraIssueRecord` and
`JiraCommentRecord` in `FhirAugury.Database`, but now live within the
Jira service project.

**Tables:**

| Table | Record Type | Purpose |
|-------|------------|---------|
| `jira_issues` | `JiraIssueRecord` | Full issue records (30+ fields including HL7 custom fields) |
| `jira_comments` | `JiraCommentRecord` | Comments on issues |
| `jira_issue_links` | `JiraIssueLinkRecord` | Internal links between issues (duplicates, blocks, relates to) |
| `jira_spec_artifacts` | `JiraSpecArtifactRecord` | Parsed JIRA-Spec-Artifacts data (family, spec key, git URL, etc.) |
| `jira_issues_fts` | (FTS5 virtual table) | Full-text search on issues |
| `jira_comments_fts` | (FTS5 virtual table) | Full-text search on comments |
| `index_keywords` | `KeywordRecord` | BM25 keyword scores |
| `sync_state` | `SyncStateRecord` | Ingestion tracking |

The `JiraIssueRecord` carries forward all HL7 custom fields from v1:
`WorkGroup`, `Specification`, `RaisedInVersion`, `SelectedBallot`,
`RelatedArtifacts`, `ChangeType`, etc.

### 2.2.2 — Create `JiraDatabase` class

Extends `SourceDatabase` (from Common) with Jira-specific schema:

- Creates all tables on first run
- Sets up FTS5 virtual tables with content-sync triggers
  (INSERT/UPDATE/DELETE triggers that keep FTS5 in sync with base tables)
- Provides methods for batch upsert operations

### 2.2.3 — Implement FTS5 setup

Create FTS5 virtual tables and content-sync triggers, adapted from v1's
`FtsSetup.cs` but scoped to Jira tables only:

- `jira_issues_fts` — indexes `title`, `description`, `labels`,
  `work_group`, `specification`
- `jira_comments_fts` — indexes `body`

---

## 2.3 — Ingestion Pipeline

### 2.3.1 — Adapt `JiraSource` from v1

The v1 `JiraSource` class in `FhirAugury.Sources.Jira/` already implements
the core API client. Adapt it for v2:

- Remove `IDataSource` interface dependency (v1 pattern)
- Integrate with the service's own `ResponseCache` (cache every API response)
- Integrate with the service's own `JiraDatabase` (upsert directly)
- Support both full and incremental downloads
- Date-based batch cache file naming: `DayOf_{date}-{seq}.xml`

The v1 code for `JiraFieldMapper`, `JiraCommentParser`, `JiraXmlParser`,
and `JiraAuthHandler` can be largely reused with minimal changes.

### 2.3.2 — Implement `JiraIngestionPipeline`

Orchestrates the full ingestion flow:

1. Fetch data from API (or read from cache if available)
2. Write raw responses to cache
3. Parse and normalize (using field mapper)
4. Upsert into SQLite database
5. Update FTS5 indexes (via triggers — automatic)
6. Update BM25 keyword scores (incremental)
7. Update internal issue links
8. Update sync state

### 2.3.3 — Implement JIRA-Spec-Artifacts integration

New capability for v2 (not in v1). The Jira service maintains a local clone
of `HL7/JIRA-Spec-Artifacts` under `cache/jira/jira-spec-artifacts/`.

**Process:**
1. `git clone` on first run, `git pull` on subsequent runs
2. After pull, check if HEAD SHA changed since last parse
3. If changed, parse XML files:
   - `_families.xml` → Jira project prefixes
   - `_workgroups.xml` → HL7 work group definitions
   - `SPECS-{FAMILY}.xml` → specification listings per family
   - `{FAMILY}-{key}.xml` → per-spec detail (gitUrl, artifacts, pages)
4. Load parsed data into `jira_spec_artifacts` table
5. Store last-processed SHA in `sync_state`

**Key output:** The `gitUrl` attribute from each specification XML links a
Jira spec to its GitHub repository. This data is served to the GitHub
service via the `ListSpecArtifacts` gRPC method.

### 2.3.4 — Implement issue link parsing

Parse internal issue links from Jira data:

- Jira link data (duplicates, blocks, is-blocked-by, relates-to)
- Custom fields: `relatedIssues`, `duplicateOf`, `consideredRelatedIssues`
- Store in `jira_issue_links` table

---

## 2.4 — Internal Indexing

### 2.4.1 — BM25 keyword scoring

Adapt v1's `Bm25Calculator` from `FhirAugury.Indexing/Bm25/` for
Jira-specific use:

- Tokenize issue titles and descriptions
- Classify tokens (stop words, FHIR vocabulary, identifiers)
- Compute IDF across the Jira corpus
- Bulk SQL update for BM25 keyword scores in `index_keywords`
- Support both full rebuild and incremental updates

### 2.4.2 — Internal cross-reference indexing

Within the Jira corpus:

- Issues sharing the same `specification` or `workGroup`
- Issue links from `jira_issue_links`
- BM25 similarity within Jira (for "find similar issues")

---

## 2.5 — gRPC Service Implementation

### 2.5.1 — Implement `SourceService` RPCs

Implement the common `SourceService` contract defined in
`source_service.proto`:

| RPC | Implementation |
|-----|---------------|
| `Search` | Query `jira_issues_fts`, return ranked results with snippets |
| `GetItem` | SELECT from `jira_issues` by key, optionally include comments |
| `ListItems` | Paginated listing with filters (status, date, labels, etc.) |
| `GetRelated` | BM25 similarity + internal issue links |
| `GetSnapshot` | Render issue as rich markdown (title, fields, comments, links) |
| `GetContent` | Return issue description/body in requested format |
| `StreamSearchableText` | Stream all issues' searchable text (title + description + comments) for xref scanning. Filter by `since` timestamp. |
| `TriggerIngestion` | Enqueue full or incremental ingestion run |
| `GetIngestionStatus` | Return current ingestion state from `sync_state` |
| `RebuildFromCache` | Drop DB, recreate from cached responses |
| `GetStats` | Total issues, comments, DB size, cache size, last sync |
| `HealthCheck` | Service status, version, uptime |

### 2.5.2 — Implement `JiraService` RPCs

Implement Jira-specific gRPC extensions defined in `jira.proto`:

| RPC | Implementation |
|-----|---------------|
| `GetIssueComments` | Stream comments for a specific issue |
| `GetIssueLinks` | Return internal issue links |
| `ListByWorkGroup` | Filter issues by `work_group` field |
| `ListBySpecification` | Filter issues by `specification` field |
| `QueryIssues` | Structured query — build SQL WHERE from `JiraQueryRequest` fields (see 2.5.3) |
| `ListSpecArtifacts` | Stream parsed spec-artifact entries |
| `GetIssueNumbers` | Return all known issue numbers (for GitHub xref validation) |
| `GetIssueSnapshot` | Jira-specific snapshot with custom parameters |

### 2.5.3 — Implement `QueryIssues` SQL builder

The `QueryIssues` RPC supports composable structured queries. Build a
parameterized SQL query from `JiraQueryRequest` fields:

- All fields optional, combined with AND
- Repeated values within a field use OR
- `exclude_projects` applied after `projects`
- `query` field triggers FTS5 subquery within filtered results
- `sort_by` and `sort_order` with safe column name validation
- `limit` and `offset` for pagination

Example generated SQL:
```sql
SELECT * FROM jira_issues
WHERE status IN (@s0, @s1)
  AND work_group IN (@wg0)
  AND project_key NOT IN (@ep0)
  AND key IN (SELECT key FROM jira_issues_fts WHERE jira_issues_fts MATCH @q)
ORDER BY updated_at DESC
LIMIT @limit OFFSET @offset
```

---

## 2.6 — HTTP API

### 2.6.1 — Implement source service HTTP endpoints

Lightweight HTTP API for standalone use and debugging, as defined in
[05-api-contracts](../../proposal/v2/05-api-contracts.md):

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/v1/search?q={query}&limit={n}` | Search Jira issues |
| `GET` | `/api/v1/items/{key}` | Get full issue details |
| `GET` | `/api/v1/items?limit={n}&offset={n}` | List issues |
| `GET` | `/api/v1/items/{key}/related` | Related issues |
| `GET` | `/api/v1/items/{key}/snapshot` | Markdown snapshot |
| `GET` | `/api/v1/items/{key}/content?format={fmt}` | Issue content/body |
| `POST` | `/api/v1/ingest` | Trigger ingestion |
| `GET` | `/api/v1/status` | Ingestion status |
| `POST` | `/api/v1/rebuild` | Rebuild from cache |
| `GET` | `/api/v1/stats` | Source statistics |

---

## 2.7 — Rebuild From Cache

### 2.7.1 — Implement database rebuild

The `RebuildFromCache` operation recreates the Jira database entirely from
cached API responses:

1. Drop and recreate all tables (including FTS5)
2. Enumerate all cached files in chronological order
3. Parse each cached response (XML/JSON)
4. Upsert into database (using transactions for performance)
5. Rebuild FTS5 indexes
6. Rebuild BM25 keyword scores
7. Rebuild internal issue links
8. Re-parse JIRA-Spec-Artifacts (from cached local clone)

This must work without any network access.

---

## 2.8 — Background Workers

### 2.8.1 — Implement `ScheduledIngestionWorker`

A `BackgroundService` that triggers incremental ingestion at the configured
`SyncSchedule` interval. Adapted from v1's `ScheduledIngestionService` but
scoped to Jira only.

---

## 2.9 — Service Host

### 2.9.1 — Implement `Program.cs`

Wire everything together following the service host pattern from Phase 1:

- Kestrel configuration with HTTP (5160) and gRPC (5161) ports
- Configuration: `appsettings.json` → env vars (`FHIR_AUGURY_JIRA_`) →
  user secrets
- DI registration for database, cache, ingestion pipeline, indexer, workers
- gRPC service mapping
- HTTP API mapping
- Health check endpoint

---

## 2.10 — Tests

### 2.10.1 — Unit tests

- **Ingestion:** Field mapping, comment parsing, XML parsing (reuse v1 test
  data and patterns from `FhirAugury.Sources.Tests`)
- **Database:** Record CRUD, FTS5 search, sync state tracking
- **Indexing:** BM25 calculation, internal link resolution, QueryIssues SQL
  builder
- **Cache:** Cache read/write, enumeration, rebuild-from-cache
- **JIRA-Spec-Artifacts:** XML parsing, SHA change detection

### 2.10.2 — gRPC endpoint tests

Test each gRPC RPC using in-memory gRPC hosting:
- `Search` returns ranked results
- `GetItem` returns complete issue with comments
- `QueryIssues` handles all filter combinations
- `ListSpecArtifacts` streams spec-artifact entries
- `StreamSearchableText` respects `since` filter

---

## Phase 2 Verification

- [ ] Jira service starts independently on ports 5160 (HTTP) and 5161 (gRPC)
- [ ] Full download populates both cache and database
- [ ] Incremental sync fetches only updated issues
- [ ] FTS5 search returns ranked results with snippets
- [ ] `GetItem` returns complete issue with all fields and comments
- [ ] `QueryIssues` handles composable structured queries
- [ ] `ListSpecArtifacts` serves parsed JIRA-Spec-Artifacts data
- [ ] `GetIssueNumbers` returns the set of all Jira issue numbers
- [ ] `RebuildFromCache` recreates database without network calls
- [ ] HTTP API works for standalone use
- [ ] All tests pass
