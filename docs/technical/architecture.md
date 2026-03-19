# Architecture

This document describes the system architecture of FHIR Augury, the
relationships between components, and the key design decisions.

## Overview

FHIR Augury is a unified knowledge platform that downloads, indexes, and
cross-references content from four HL7 FHIR community platforms into a single
SQLite database with full-text search.

```
┌─────────────────────────────────────────────────────────┐
│                     Data Sources                        │
│  ┌──────┐  ┌───────┐  ┌────────────┐  ┌────────┐      │
│  │ Jira │  │ Zulip │  │ Confluence │  │ GitHub │      │
│  └──┬───┘  └───┬───┘  └─────┬──────┘  └───┬────┘      │
│     └──────────┼────────────┼──────────────┘           │
│                ▼            ▼                           │
│  ┌──────────────────────────────────────┐              │
│  │         SQLite + FTS5 Database       │              │
│  │  ┌─────────┐ ┌──────┐ ┌──────────┐  │              │
│  │  │ Content │ │ FTS5 │ │ BM25 Idx │  │              │
│  │  │ Tables  │ │ Index│ │ + X-Refs │  │              │
│  │  └─────────┘ └──────┘ └──────────┘  │              │
│  └──────────┬───────────────┬───────────┘              │
│             │               │                          │
│  ┌──────────┴──┐  ┌────────┴────────┐  ┌───────────┐  │
│  │  CLI Tool   │  │  HTTP Service   │  │ MCP Server│  │
│  │ fhir-augury │  │  (background +  │  │ (LLM agent│  │
│  │             │  │   REST API)     │  │  access)  │  │
│  └─────────────┘  └─────────────────┘  └───────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Component Overview

| Component | Project | Role |
|-----------|---------|------|
| **Models** | `FhirAugury.Models` | Shared interfaces (`IDataSource`, `IResponseCache`), enums, configuration types |
| **Database** | `FhirAugury.Database` | SQLite schema, FTS5 setup, source-generated CRUD, `DatabaseService` singleton |
| **Sources** | `FhirAugury.Sources.*` | Four data source connectors (Jira, Zulip, Confluence, GitHub) |
| **Indexing** | `FhirAugury.Indexing` | FTS5 search, BM25 scoring, cross-reference linking, FHIR-aware tokenization |
| **CLI** | `FhirAugury.Cli` | Command-line interface (11 commands, direct DB or remote service) |
| **Service** | `FhirAugury.Service` | ASP.NET Core background service with HTTP API and scheduled sync |
| **MCP** | `FhirAugury.Mcp` | Model Context Protocol server for LLM agents (20 tools) |

## Project Dependencies

```
FhirAugury.Models          ← No dependencies (interfaces, enums, config)
    ↑
FhirAugury.Database        ← Models
    ↑
FhirAugury.Sources.*       ← Models, Database
FhirAugury.Indexing        ← Models, Database
    ↑
FhirAugury.Cli             ← Models, Database, Indexing, all Sources
FhirAugury.Service         ← Models, Database, Indexing, all Sources
FhirAugury.Mcp             ← Models, Database, Indexing
```

## Data Flow

### Ingestion Pipeline

1. **Fetch** — A source connector (`IDataSource`) fetches data from the remote
   API, handling authentication, pagination, and rate limiting
2. **Cache** (optional) — Raw API responses are stored in the file-system cache
   for offline replay
3. **Map** — Source-specific mappers convert JSON/XML responses into strongly-typed
   record objects
4. **Store** — Records are upserted into SQLite content tables via
   source-generated CRUD
5. **FTS5 sync** — Triggers on content tables automatically update FTS5 virtual
   tables (no application code needed)
6. **Cross-reference linking** — `CrossRefLinker` scans text fields for
   cross-source identifiers using regex patterns
7. **BM25 indexing** — `Bm25Calculator` tokenizes text, classifies keywords,
   and computes BM25 scores

### Search Pipeline

1. **Query** — User provides a search query via CLI, HTTP API, or MCP tool
2. **Sanitize** — `FtsSearchService` escapes the query for FTS5 MATCH syntax
3. **Execute** — Parallel FTS5 queries against each source's virtual table
4. **Normalize** — Per-source min-max score normalization to `[0, 1]`
5. **Merge** — Results from all sources are interleaved by normalized score
6. **Format** — Output as table, JSON, or Markdown

### Similarity Search Pipeline

1. **Seed** — Extract top 10 BM25 keywords for the seed item
2. **Expand** — Find all items sharing those keywords; sum their BM25 scores
3. **Boost** — Cross-referenced items get a 2× score boost
4. **Rank** — Sort by combined score, return top results

## Key Design Decisions

### SQLite over PostgreSQL/Elasticsearch

The entire knowledge base is a single portable SQLite file. FTS5 provides
excellent full-text search. WAL mode enables concurrent readers (MCP server)
alongside a single writer (background service). No external infrastructure
required.

### Source-Generated CRUD over ORM

The project uses `cslightdbgen.sqlitegen` (a Roslyn source generator) instead
of Entity Framework Core. Each table is a `partial record class` decorated with
attributes; the generator emits all CRUD code at compile time. Benefits:

- Zero reflection, AOT-compatible
- Compile-time schema validation
- No migration files
- Strongly-typed queries throughout

### Content-Synced FTS5 with Triggers

FTS5 virtual tables use `content='<table_name>'` with INSERT/UPDATE/DELETE
triggers to stay automatically in sync. No explicit rebuild needed for
incremental operations — this is critical for the live service that receives
continuous updates.

### Queue-Based Ingestion

Both scheduled and on-demand ingestion requests flow through a
`System.Threading.Channels` bounded channel (capacity 100). This ensures:

- Serialized writes to SQLite
- Natural backpressure when the system is busy
- Uniform code path for all ingestion types
- No external message broker dependency

### MCP Server as Read-Only Process

The MCP server opens the database in read-only mode and runs as a separate
process. SQLite WAL mode supports concurrent readers, so the MCP server can
run alongside the write-capable background service without contention.

### IDataSource Abstraction

Every source connector implements three methods:

- `DownloadAllAsync` — full download
- `DownloadIncrementalAsync` — fetch only new/updated items since a timestamp
- `IngestItemAsync` — fetch a single item by identifier

This consistent interface lets the ingestion worker, scheduler, CLI, and API
treat all sources uniformly.

### Minimal External Dependencies

Direct `HttpClient` for Jira, Confluence, and GitHub (no vendor SDKs) keeps
the dependency tree shallow. `zulip-cs-lib` is used because it's authored by
the project maintainer. The only significant dependency is `Microsoft.Data.Sqlite`
for database access.

## Concurrency Model

- **Single writer:** The background service (`IngestionWorker`) is the only
  process that writes to the database
- **Multiple readers:** The MCP server and CLI can read concurrently via
  SQLite WAL mode
- **Queue serialization:** All write operations go through the `IngestionQueue`
  channel, ensuring no concurrent writes
- **HTTP API:** Read endpoints serve directly; write endpoints (ingest triggers)
  enqueue requests and return `202 Accepted`

## Error Handling

- **Transient failures:** `HttpRetryHelper` retries on HTTP 429/500/502/503/504
  with exponential backoff + jitter (max 3 retries). Respects `Retry-After`
  headers.
- **Auth failures:** Immediate failure on HTTP 401/403 with a clear error
  message
- **Partial failure isolation:** One source failing doesn't block others
- **GitHub rate limiting:** Dedicated `GitHubRateLimiter` monitors
  `X-RateLimit-Remaining` headers and pauses automatically
- **Graceful shutdown:** The ingestion worker drains the queue on shutdown
