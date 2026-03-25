# Architecture

This document describes the system architecture of FHIR Augury, the
relationships between components, and the key design decisions.

## Overview

FHIR Augury is a microservices-based knowledge platform that downloads,
indexes, and cross-references content from four HL7 FHIR community platforms.
Each data source runs as an independent service with its own SQLite database
and FTS5 index. A central orchestrator aggregates search across all sources,
manages cross-references, and provides a unified API to clients.

```
┌──────────────────────────────────────────────────────────────────────┐
│                        External Data Sources                        │
│   ┌──────┐     ┌───────┐     ┌────────────┐     ┌────────┐         │
│   │ Jira │     │ Zulip │     │ Confluence │     │ GitHub │         │
│   └──┬───┘     └───┬───┘     └─────┬──────┘     └───┬────┘         │
│      │             │               │                 │              │
│ ┌────┴─────┐ ┌─────┴────┐ ┌───────┴──────┐ ┌───────┴──────┐       │
│ │Source.Jira│ │Source.    │ │Source.       │ │Source.       │       │
│ │ :5160    │ │Zulip     │ │Confluence   │ │GitHub       │       │
│ │ :5161    │ │ :5170    │ │ :5180       │ │ :5190       │       │
│ │[SQLite]  │ │ :5171    │ │ :5181       │ │ :5191       │       │
│ │[FTS5]    │ │[SQLite]  │ │[SQLite]     │ │[SQLite]     │       │
│ │[Cache]   │ │[FTS5]    │ │[FTS5]       │ │[FTS5]       │       │
│ └────┬─────┘ │[Cache]   │ │[Cache]      │ │[Cache]      │       │
│      │       └─────┬────┘ └───────┬──────┘ └───────┬──────┘       │
│      │  gRPC       │     gRPC     │       gRPC     │              │
│      └─────────────┼──────────────┼────────────────┘              │
│                    ▼              ▼                                │
│           ┌────────────────────────────────┐                      │
│           │      Orchestrator :5150/:5151  │                      │
│           │  [UnifiedSearch] [CrossRefs]   │                      │
│           │  [RelatedItems]  [SQLite]      │                      │
│           └────────┬──────────┬────────────┘                      │
│                    │          │                                    │
│           ┌────────┴──┐  ┌───┴─────────┐  ┌────────────────┐     │
│           │  CLI Tool │  │  MCP Server │  │ Direct gRPC    │     │
│           │  (gRPC)   │  │  (gRPC)     │  │ Clients        │     │
│           └───────────┘  └─────────────┘  └────────────────┘     │
└──────────────────────────────────────────────────────────────────────┘

Ports: HTTP (even) / gRPC (odd)
  Orchestrator  :5150 / :5151
  Source.Jira   :5160 / :5161
  Source.Zulip  :5170 / :5171
  Source.Confluence :5180 / :5181
  Source.GitHub  :5190 / :5191
```

## Component Overview

| Component | Project | Role |
|-----------|---------|------|
| **Common** | `FhirAugury.Common` | Shared library: compiled protos, caching, database helpers, text utilities, gRPC client helpers, auxiliary database loader, BM25 configuration |
| **Source.Jira** | `FhirAugury.Source.Jira` | Jira source service — downloads, indexes, and serves Jira issues and comments |
| **Source.Zulip** | `FhirAugury.Source.Zulip` | Zulip source service — downloads, indexes, and serves Zulip streams and messages |
| **Source.Confluence** | `FhirAugury.Source.Confluence` | Confluence source service — downloads, indexes, and serves Confluence pages and comments |
| **Source.GitHub** | `FhirAugury.Source.GitHub` | GitHub source service — downloads, indexes, and serves GitHub issues, PRs, and comments |
| **Orchestrator** | `FhirAugury.Orchestrator` | Central coordinator — unified search, cross-references, related items, health monitoring |
| **MCP** | `FhirAugury.Mcp` | Model Context Protocol server for LLM agents (17 tools, stdio transport, gRPC to orchestrator) |
| **CLI** | `FhirAugury.Cli` | Command-line interface (10+ commands, gRPC to orchestrator) |

## Project Dependencies

```
protos/                        ← 6 proto files (source, orchestrator, jira, zulip, confluence, github)
    ↑
FhirAugury.Common              ← Compiles protos; shared caching, database, text, gRPC helpers
    ↑
FhirAugury.Source.Jira         ← Common (implements SourceService + JiraService gRPC)
FhirAugury.Source.Zulip        ← Common (implements SourceService + ZulipService gRPC)
FhirAugury.Source.Confluence   ← Common (implements SourceService + ConfluenceService gRPC)
FhirAugury.Source.GitHub       ← Common (implements SourceService + GitHubService gRPC)
    ↑
FhirAugury.Orchestrator        ← Common (consumes SourceService gRPC from all sources)
    ↑
FhirAugury.Mcp                 ← Common (gRPC client to Orchestrator)
FhirAugury.Cli                 ← Common (gRPC client to Orchestrator)
```

## Source Service Architecture

Each source service (`Source.Jira`, `Source.Zulip`, `Source.Confluence`,
`Source.GitHub`) follows the same internal structure:

| Directory | Purpose |
|-----------|---------|
| `Api/` | gRPC service implementations (SourceService + source-specific service) |
| `Cache/` | File-system response cache for raw API responses |
| `Configuration/` | Source-specific options (including `Bm25` and `AuxiliaryDatabase` sub-options) |
| `Database/` | SQLite schema, record types, source-generated CRUD |
| `Indexing/` | FTS5 search and BM25 indexing logic (uses shared `TokenCounter` and `Lemmatizer`) |
| `Ingestion/` | Download pipeline: fetch → cache → parse → store |
| `Workers/` | Background workers (e.g., `ScheduledIngestionWorker`) |
| `Program.cs` | Entry point: dual-port Kestrel (HTTP + gRPC), DI registration |

Each service has its own SQLite database (WAL mode, FTS5 virtual tables) and
file-system response cache. Services implement both the common `SourceService`
gRPC contract and a source-specific gRPC service (e.g., `JiraService`).

At startup, each service registers an `AuxiliaryDatabase` singleton that loads
optional external stop words, lemmatization data, and FHIR vocabulary from
read-only SQLite databases. The `Lemmatizer` and merged stop-word/vocabulary
sets are injected into the service's indexer alongside configurable `Bm25Options`
(K1/B parameters).

## Data Flow

### Ingestion Pipeline (per source service)

1. **Trigger** — `ScheduledIngestionWorker` runs on a timer, or an on-demand
   trigger arrives via the `TriggerIngestion` gRPC call
2. **Fetch** — The ingestion pipeline fetches data from the remote API, handling
   authentication, pagination, and rate limiting
3. **Cache** — Raw API responses are stored in the file-system cache
   (`FileSystemResponseCache`) for offline replay and rebuild
4. **Parse** — Source-specific mappers convert JSON/XML responses into
   strongly-typed record objects
5. **Store** — Records are upserted into the service's SQLite database via
   source-generated CRUD
6. **FTS5 sync** — Triggers on content tables automatically update FTS5 virtual
   tables (no application code needed)
7. **Notify** — The source service calls `NotifyIngestionComplete` on the
   orchestrator, which triggers a cross-reference scan of new items

### Search Pipeline

1. **Query** — User provides a search query via CLI, MCP tool, or direct gRPC
2. **Route** — The orchestrator's `UnifiedSearchService` receives the request
3. **Fan-out** — `SourceRouter` sends parallel `Search` gRPC calls to all
   healthy source services
4. **Per-source search** — Each source executes an FTS5 MATCH query against its
   own database and returns scored results
5. **Normalize** — Per-source min-max score normalization to `[0, 1]`
6. **Cross-ref boost** — Items with cross-references to other results get a
   score boost from the orchestrator's cross-reference database
7. **Freshness decay** — Scores are adjusted based on item age
8. **Sort & limit** — Results are sorted by final score and truncated to the
   requested limit
9. **Return** — Merged results are returned to the client

### Cross-Reference System

The orchestrator's `CrossRefLinker` builds and maintains a cross-reference
graph across all sources:

1. **Stream** — `StreamSearchableText` gRPC calls stream searchable text from
   each source service
2. **Extract** — Regex patterns in `CrossRefPatterns` identify cross-source
   identifiers (Jira issue keys like `FHIR-12345`, URLs, GitHub references)
3. **Store** — Extracted cross-references are persisted in the orchestrator's
   own SQLite database
4. **Scan schedule** — `XRefScanWorker` runs every 30 minutes to process new
   content

### Related Items

The `RelatedItemFinder` combines four signals to rank related items:

| Signal | Weight | Description |
|--------|--------|-------------|
| Explicit cross-references | 10 | Direct xrefs from source item to target |
| Reverse cross-references | 8 | Other items that reference the source item |
| BM25 text similarity | 3 | Keyword overlap via BM25 scoring |
| Shared metadata | 2 | Common labels, components, specifications, etc. |

## Key Design Decisions

### SQLite per Service (Not Shared)

Each source service owns its own SQLite database. This provides process
isolation, independent scaling, and eliminates cross-service write contention.
The orchestrator has a separate SQLite database for cross-references. WAL mode
enables concurrent reads within each service.

### gRPC for Inter-Service Communication

All service-to-service communication uses gRPC with Protocol Buffers. Six proto
files define the service contracts:

- `source_service.proto` — Common contract all sources implement
- `orchestrator.proto` — Orchestrator's unified API
- `jira.proto`, `zulip.proto`, `confluence.proto`, `github.proto` —
  Source-specific operations

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
triggers to stay automatically in sync. The `SourceDatabase` abstract class
in `FhirAugury.Common` provides `CreateFts5Table` helpers that set up these
triggers. No explicit rebuild needed for incremental operations.

### Per-Service File-System Caching

Each source service maintains its own file-system response cache with four
modes via `CacheMode`:

- `Disabled` — No caching
- `WriteThrough` — Cache responses and serve from API
- `CacheOnly` — Serve only from cache (offline mode)
- `WriteOnly` — Cache responses but don't read from cache

This enables `RebuildFromCache` — rebuilding the database entirely from cached
API responses without hitting the remote API.

### MCP and CLI as gRPC Clients

Both the MCP server and CLI are thin gRPC clients to the orchestrator. They
contain no database access or business logic — all intelligence lives in the
orchestrator and source services.

## Concurrency Model

- **Service independence:** Each source service runs as an independent process
  with its own database and ingestion pipeline
- **WAL mode:** SQLite WAL mode enables concurrent reads within each service
  (gRPC readers alongside the ingestion writer)
- **Parallel fan-out:** The orchestrator sends gRPC requests to all source
  services in parallel using `Task.WhenAll`
- **Health monitoring:** `ServiceHealthMonitor` polls source services every 60
  seconds; unhealthy sources are excluded from fan-out

## Error Handling

- **gRPC status codes:** `GrpcErrorMapper` in `FhirAugury.Common` maps
  exceptions to appropriate gRPC status codes (NotFound, Unavailable,
  Internal, etc.)
- **Transient HTTP failures:** `HttpRetryHelper` retries on HTTP
  429/500/502/503/504 with exponential backoff + jitter (max 3 retries).
  Respects `Retry-After` headers.
- **Auth failures:** Immediate failure on HTTP 401/403 with a clear error
  message
- **Partial failure isolation:** One source service failing doesn't block
  others — the orchestrator returns results from healthy sources
- **GitHub rate limiting:** Dedicated `GitHubRateLimiter` monitors
  `X-RateLimit-Remaining` headers and pauses automatically
- **Health checks:** All services expose `/health` endpoints; Docker Compose
  uses these for readiness checks
