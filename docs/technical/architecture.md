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
│ │[SQLite]  │ │ :5170    │ │ :5180       │ │ :5190       │       │
│ │[FTS5]    │ │[SQLite]  │ │[SQLite]     │ │[SQLite]     │       │
│ │[Cache]   │ │[FTS5]    │ │[FTS5]       │ │[FTS5]       │       │
│ └────┬─────┘ │[Cache]   │ │[Cache]      │ │[Cache]      │       │
│      │       └─────┬────┘ └───────┬──────┘ └───────┬──────┘       │
│      │  HTTP       │     HTTP     │       HTTP     │              │
│      └─────────────┼──────────────┼────────────────┘              │
│                    ▼              ▼                                │
│           ┌────────────────────────────────┐                      │
│           │      Orchestrator :5150       │                      │
│           │  [UnifiedSearch] [XRefFanout] │                      │
│           │  [RelatedItems]  [SQLite]     │                      │
│           └────────┬──────────┬────────────┘                      │
│                    │          │                                    │
│           ┌────────┴──┐  ┌───┴─────────┐  ┌────────────────┐     │
│           │  CLI Tool │  │  MCP Server │  │ Direct HTTP    │     │
│           │  (HTTP)   │  │  (HTTP)     │  │ Clients        │     │
│           └───────────┘  └─────────────┘  └────────────────┘     │
└──────────────────────────────────────────────────────────────────────┘

Ports (HTTP only):
  Orchestrator     :5150
  Source.Jira      :5160
  Source.Zulip     :5170
  Source.Confluence :5180
  Source.GitHub     :5190
```

## Component Overview

| Component | Project | Role |
|-----------|---------|------|
| **Common** | `FhirAugury.Common` | Shared library: API contracts, caching, database helpers, text utilities, HTTP client helpers, auxiliary database loader, BM25 configuration |
| **Source.Jira** | `FhirAugury.Source.Jira` | Jira source service — downloads, indexes, and serves Jira issues and comments |
| **Source.Zulip** | `FhirAugury.Source.Zulip` | Zulip source service — downloads, indexes, and serves Zulip streams and messages |
| **Source.Confluence** | `FhirAugury.Source.Confluence` | Confluence source service — downloads, indexes, and serves Confluence pages and comments |
| **Source.GitHub** | `FhirAugury.Source.GitHub` | GitHub source service — downloads, indexes, and serves GitHub issues, PRs, and comments |
| **Orchestrator** | `FhirAugury.Orchestrator` | Central coordinator — unified search, cross-references, related items, health monitoring |
| **MCP Shared** | `FhirAugury.McpShared` | Shared MCP library: 15 tool implementations (UnifiedTools, ContentTools, JiraTools, ZulipTools) and McpHttpRegistration |
| **MCP Stdio** | `FhirAugury.McpStdio` | Stdio-based MCP server for LLM agents (packaged as `fhir-augury-mcp` dotnet tool, generic .NET Host) |
| **MCP HTTP** | `FhirAugury.McpHttp` | HTTP/SSE-based MCP server (ASP.NET Core, port 5200, `/mcp` endpoint, Aspire ServiceDefaults) |
| **CLI** | `FhirAugury.Cli` | Command-line interface (13 commands, HTTP to orchestrator) |
| **Dev UI** | `FhirAugury.DevUi` | Blazor Server operational dashboard (port 5210, HTTP to orchestrator) |
| **ServiceDefaults** | `FhirAugury.ServiceDefaults` | Shared Aspire defaults: OpenTelemetry, health checks, service discovery, HTTP resilience |
| **AppHost** | `FhirAugury.AppHost` | .NET Aspire distributed application host — orchestrates all services for local development |

## Project Dependencies

```
FhirAugury.Common              ← Shared API contracts, caching, database, text, HTTP client helpers
    ↑
FhirAugury.ServiceDefaults     ← Aspire shared project: OpenTelemetry, health checks, resilience
    ↑
FhirAugury.Source.Jira         ← Common + ServiceDefaults (HTTP API controllers)
FhirAugury.Source.Zulip        ← Common + ServiceDefaults (HTTP API controllers)
FhirAugury.Source.Confluence   ← Common + ServiceDefaults (HTTP API controllers)
FhirAugury.Source.GitHub       ← Common + ServiceDefaults (HTTP API controllers)
    ↑
FhirAugury.Orchestrator        ← Common + ServiceDefaults (HTTP API, consumes source HTTP APIs)
    ↑
FhirAugury.McpShared            ← Common (shared MCP tool implementations, HTTP clients)
FhirAugury.McpStdio             ← McpShared (stdio transport, generic .NET Host)
FhirAugury.McpHttp              ← McpShared + ServiceDefaults (HTTP/SSE transport, ASP.NET Core)
FhirAugury.Cli                  ← Common (HTTP client to Orchestrator)
FhirAugury.DevUi                ← Common + ServiceDefaults (Blazor Server, HTTP to Orchestrator)

FhirAugury.AppHost             ← Aspire AppHost (references all service projects for orchestration)
```

## Source Service Architecture

Each source service (`Source.Jira`, `Source.Zulip`, `Source.Confluence`,
`Source.GitHub`) follows the same internal structure:

| Directory | Purpose |
|-----------|---------|
| `Api/` | HTTP API controller implementations |
| `Cache/` | File-system response cache for raw API responses |
| `Configuration/` | Source-specific options (including `Bm25`, `AuxiliaryDatabase`, and `DictionaryDatabase` sub-options) |
| `Database/` | SQLite schema, record types, source-generated CRUD |
| `Indexing/` | FTS5 search and BM25 indexing logic (uses shared `TokenCounter` and `Lemmatizer`) |
| `Ingestion/` | Download pipeline: fetch → cache → parse → store |
| `Workers/` | Background workers (e.g., `ScheduledIngestionWorker`) |
| `Program.cs` | Entry point: Kestrel HTTP server, DI registration |

Each service has its own SQLite database (WAL mode, FTS5 virtual tables) and
file-system response cache. Services expose HTTP API controllers for both
common operations (search, get item, ingestion) and source-specific endpoints.

At startup, each service registers an `AuxiliaryDatabase` singleton that loads
optional external stop words, lemmatization data, and FHIR vocabulary from
read-only SQLite databases. A `DictionaryDatabase` is also built from source
text files in the configured dictionary path. The `Lemmatizer` and merged
stop-word/vocabulary sets are injected into the service's indexer alongside
configurable `Bm25Options` (K1/B/UseLemmatization parameters).

## Data Flow

### Ingestion Pipeline (per source service)

1. **Trigger** — `ScheduledIngestionWorker` runs on a timer, or an on-demand
   trigger arrives via the `TriggerIngestion` HTTP API call
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
7. **Notify** — The source service notifies peers via
   `NotifyPeerIngestionComplete` so they can re-scan for new cross-references

### Search Pipeline

1. **Query** — User provides a search query via CLI, MCP tool, or direct HTTP API
2. **Route** — The orchestrator's `ContentController` receives the request
3. **Fan-out** — `SourceHttpClient` sends parallel content search HTTP calls to all
   healthy source services
4. **Per-source search** — Each source executes an FTS5 MATCH query against its
   own database and returns scored results
5. **Normalize** — Per-source min-max score normalization to `[0, 1]`
6. **Freshness decay** — Scores are adjusted based on item age
7. **Sort & limit** — Results are sorted by final score and truncated to the
   requested limit
8. **Return** — Merged results are returned to the client

### Cross-Reference System

Cross-references are **source-owned**: each source service maintains its own
set of xref tables that track references TO other sources found within its
content.

1. **Extract** — During ingestion, each source service runs shared extractors
   from `FhirAugury.Common.Indexing` against its content to find references
   to items in other sources
2. **Store** — Extracted references are stored in the source's own database
   in typed xref tables (`xref_jira`, `xref_zulip`, `xref_confluence`,
   `xref_github`, `xref_fhir_element`)
3. **Query** — The orchestrator fans out `GetItemCrossReferences` HTTP calls
   to all sources and merges the results
4. **Peer notification** — When a source completes ingestion, it notifies
   peers via `NotifyPeerIngestionComplete` so they can re-scan for new
   references

### Related Items

The `RelatedItemFinder` combines four signals to rank related items:

| Signal | Weight | Description |
|--------|--------|-------------|
| Cross-source references | 10 | Items linked via cross-references (outgoing + incoming) |
| BM25 text similarity | 3 | Keyword overlap via BM25 scoring |
| Shared metadata | 2 | Common labels, components, specifications, etc. |

## Key Design Decisions

### SQLite per Service (Not Shared)

Each source service owns its own SQLite database. This provides process
isolation, independent scaling, and eliminates cross-service write contention.
The orchestrator has a separate SQLite database for scan state coordination. WAL mode
enables concurrent reads within each service.

### HTTP/REST for Inter-Service Communication

All service-to-service communication uses HTTP/REST with JSON. Shared API
contract classes in `FhirAugury.Common/Api/` define the request/response types:

- `SearchContracts` — Common search request/response types
- `ItemContracts` — Item retrieval contracts
- `CrossReferenceContracts` — Cross-reference query/response types
- `IngestionContracts` — Ingestion trigger and status contracts
- `ServiceContracts` — Service status and health contracts
- `ContentFormats` — Content format definitions

Services use `IHttpClientFactory` with named clients for HTTP communication.
The orchestrator communicates with source services via HTTP, and MCP/CLI
clients connect to the orchestrator via HTTP.

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

### MCP and CLI as HTTP Clients

Both MCP servers and the CLI are thin HTTP clients to the orchestrator. They
contain no database access or business logic — all intelligence lives in the
orchestrator and source services. The MCP servers use `IHttpClientFactory` with
named clients ("orchestrator", "jira", "zulip", "confluence", "github"). The
CLI uses `HttpServiceClient` for HTTP communication. McpHttp is also an
ASP.NET Core web application (port 5200, `/mcp` endpoint) that participates
in Aspire orchestration via ServiceDefaults.

## Concurrency Model

- **Service independence:** Each source service runs as an independent process
  with its own database and ingestion pipeline
- **WAL mode:** SQLite WAL mode enables concurrent reads within each service
  (HTTP request handlers alongside the ingestion writer)
- **Parallel fan-out:** The orchestrator sends HTTP requests to all source
  services in parallel using `Task.WhenAll`
- **Health monitoring:** `ServiceHealthMonitor` polls source services every 60
  seconds; unhealthy sources are excluded from fan-out

## Error Handling

- **HTTP error handling:** Standard HTTP status codes (404 Not Found, 503
  Service Unavailable, 500 Internal Server Error, etc.) for error responses
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
- **Aspire WaitFor:** When using the Aspire AppHost, `WaitFor()` ensures the
  orchestrator doesn't start until all source services are healthy

## .NET Aspire Integration

FHIR Augury supports [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/)
as an optional orchestration layer for development.

### ServiceDefaults

The `FhirAugury.ServiceDefaults` project is a shared Aspire project
(`IsAspireSharedProject`) referenced by all web services. It provides:

- **OpenTelemetry** — Logging, metrics (ASP.NET Core, HTTP, runtime), and
  distributed tracing (ASP.NET Core, HTTP) with OTLP export when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is configured
- **Health checks** — Readiness (`/health`) and liveness (`/alive`) endpoints
- **Service discovery** — Aspire service discovery for HTTP clients
- **HTTP resilience** — Standard resilience handler with retry/circuit-breaker

Each web service calls `builder.AddServiceDefaults()` in its `Program.cs` to
opt in to these defaults. The ServiceDefaults are active both when running
under Aspire and when running standalone.

### AppHost

The `FhirAugury.AppHost` project uses `Aspire.AppHost.Sdk` to orchestrate all
eight projects with fixed ports matching the existing convention:

| Service | HTTP |
|---------|------|
| source-jira | 5160 |
| source-zulip | 5170 |
| source-confluence | 5180 |
| source-github | 5190 |
| orchestrator | 5150 |
| devui | 5210 |
| mcp | 5200 |
| cli | — |

The orchestrator uses `WaitFor()` to depend on Jira, Zulip, and GitHub
source services. Confluence, Dev UI, the MCP HTTP server, and the CLI use
`WithExplicitStart()` to allow manual triggering. All endpoints use
`isProxied: false` so services listen on their own ports directly (no
Aspire reverse proxy).
