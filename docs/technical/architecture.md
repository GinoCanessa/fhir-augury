# Architecture

This document describes the system architecture of FHIR Augury, the
relationships between components, and the key design decisions.

## Overview

FHIR Augury is a microservices-based knowledge platform that downloads,
indexes, and cross-references content from HL7 FHIR community platforms. Five
source services back it: four that ingest upstream content (Jira, Zulip,
Confluence, GitHub) plus `source-fhir`, which serves read-only FHIR
specification reference data from a pre-built `fhir-spec.db`. Each source runs
as an independent service with its own SQLite database and FTS5 index. A central
orchestrator aggregates search across all sources, manages cross-references, and
provides a unified API to clients. A separate **processor** stack (Preparer,
Planner, Applier, BallotNotes) consumes that data to produce derived
artifacts. Each authoring processor owns its database, durable run/receipt
ledger, mutation fence, and review snapshots. The Orchestrator exposes thin
HTTP proxies for run control and snapshot transfer; it never opens a processor
database. The Dev UI operations workspace and the CLI are outer clients of
those proxies; neither is a scheduler or completion authority. See the
[processors runbook](processors.md).

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
  Orchestrator      :5150
  Source.Jira       :5160
  Source.Zulip      :5170
  Source.Confluence :5180
  Source.GitHub     :5190
  Source.Fhir       :5195   (read-only FHIR spec reference data)
  Processors:
    Preparer        :5171
    Planner         :5172
    Applier         :5173
    BallotNotes     :5174
  MCP               :5200
  Dev UI            :5210
```

> The diagram shows the four ingesting upstream sources. `source-fhir` (:5195)
> serves read-only FHIR spec reference data, and the processor stack
> (:5171–:5174) consumes source data to produce derived artifacts — see the
> [processors runbook](processors.md).

## Component Overview

| Component | Project | Role |
|-----------|---------|------|
| **Common** | `FhirAugury.Common` | Shared library: API contracts, caching, database helpers, text utilities, HTTP client helpers, auxiliary database loader, BM25 configuration |
| **Source.Jira** | `FhirAugury.Source.Jira` | Jira source service — downloads, indexes, and serves Jira issues and comments |
| **Source.Zulip** | `FhirAugury.Source.Zulip` | Zulip source service — downloads, indexes, and serves Zulip streams and messages |
| **Source.Confluence** | `FhirAugury.Source.Confluence` | Confluence source service — downloads, indexes, and serves Confluence pages and comments |
| **Source.GitHub** | `FhirAugury.Source.GitHub` | GitHub source service — downloads, indexes, and serves GitHub issues, PRs, commits, and FHIR artifacts (StructureDefinitions, canonical artifacts, FSH definitions) |
| **Source.Fhir** | `FhirAugury.Source.Fhir` | FHIR spec source service (port 5195) — serves read-only FHIR specification reference data from a pre-built `fhir-spec.db`, building an FTS sidecar (`fhir-spec-fts.db`) on startup |
| **Parsing.Fhir** | `FhirAugury.Parsing.Fhir` | FHIR XML/JSON parsing library — StructureDefinitions, canonical artifacts (CodeSystem, ValueSet, etc.), Bundles, artifact classification |
| **Parsing.Fsh** | `FhirAugury.Parsing.Fsh` | FSH (FHIR Shorthand) parsing library — Profile, Extension, Resource, Logical, CodeSystem, ValueSet, Instance definitions; sushi-config.yaml parsing |
| **Orchestrator** | `FhirAugury.Orchestrator` | Central coordinator — unified search, cross-references, related items, health monitoring |
| **Processing.Common** | `FhirAugury.Processing.Common` | Durable runs/items/attempts/receipts, operation tokens, retry, mutation fencing, one-way cutover, finalization stages, and snapshot descriptors |
| **Processing.Jira.Common** | `FhirAugury.Processing.Jira.Common` | Jira candidate discovery and frozen source revisions, processor worker dispatch, callback handling, and final source-revision guards |
| **Processing.Client** | `FhirAugury.Processing.Client` | Reusable Orchestrator-based outer run-control client and workflow-bound verified snapshot-pair boundary; deliberately has no worker submit API or operation-token constant |
| **Publishing.Tickets** | `FhirAugury.Publishing.Tickets` | In-process immutable-snapshot discussion/applying publisher shared by the Dev UI and thin `ticket-site` adapter |
| **Processor.Jira.Fhir.Preparer** | `FhirAugury.Processor.Jira.Fhir.Preparer` | Preparer processor (port 5171) — owns ticket-preparation runs, hydration, grouping, receipts, and discussion snapshots |
| **Processor.Jira.Fhir.Planner** | `FhirAugury.Processor.Jira.Fhir.Planner` | Planner processor (port 5172) — owns planning runs, grouping, receipts, applying snapshots, and the Applier compatibility projection |
| **Processor.Jira.Fhir.Applier** | `FhirAugury.Processor.Jira.Fhir.Applier` | Applier processor (port 5173) — auto-discovers completed plans, applies each in a git worktree, push API on demand |
| **Processor.GitHub.Fhir.BallotNotes** | `FhirAugury.Processor.GitHub.Fhir.BallotNotes` | BallotNotes processor (port 5174) — owns immutable hydration executions, note-authoring runs, receipts, maintenance batches, and snapshots |
| **MCP Shared** | `FhirAugury.McpShared` | Shared MCP library: 22 tool-type classes (126 tool methods — UnifiedTools, ContentTools, JiraTools, FhirTools, …) and McpHttpRegistration |
| **MCP Stdio** | `FhirAugury.McpStdio` | Stdio-based MCP server for LLM agents (packaged as `fhir-augury-mcp` dotnet tool, generic .NET Host) |
| **MCP HTTP** | `FhirAugury.McpHttp` | HTTP/SSE-based MCP server (ASP.NET Core, port 5200, `/mcp` endpoint, Aspire ServiceDefaults) |
| **CLI** | `FhirAugury.Cli` | Command-line interface for source queries plus typed processor run control and snapshot download (HTTP to Orchestrator); worker submission remains CLI-private |
| **Dev UI** | `FhirAugury.DevUi` | Blazor Server operations workspace and diagnostics client (port 5210, HTTP to Orchestrator) |
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
FhirAugury.Source.Fhir         ← Common + ServiceDefaults (serves read-only fhir-spec.db, builds FTS sidecar)
    ↑
FhirAugury.Parsing.Fhir       ← Standalone library (Hl7.Fhir.R5 SDK)
FhirAugury.Parsing.Fsh        ← Standalone library (Hl7.FhirShorthand.Serialization NuGet package)
    ↑                            Both used by Source.GitHub for artifact indexing
FhirAugury.Orchestrator        ← Common + ServiceDefaults (HTTP API, consumes source HTTP APIs)
    ↑
FhirAugury.Processing.Common   ← Common (generic Processing lifecycle/queue substrate)
    ↑
FhirAugury.Processing.Jira.Common ← Common + Processing.Common (Jira queue/discovery/agent layer)
    ↑                            Concrete Processing.Jira.* services plug in processor-specific output/tokens
    ↑
FhirAugury.Processor.Jira.Fhir.Preparer       ← Processing.Common + Processing.Jira.Common (ticket-prep, :5171)
FhirAugury.Processor.Jira.Fhir.Planner        ← Processing.Common + Processing.Jira.Common (ticket-plan, :5172)
FhirAugury.Processor.Jira.Fhir.Applier        ← Processing.Common + Processing.Jira.Common (apply plans in worktrees, :5173)
FhirAugury.Processor.GitHub.Fhir.BallotNotes  ← Common + Processing.Common (hydration + run-backed authoring, :5174)
    ↑
FhirAugury.Processing.Client    ← Common + Processing.Contracts (outer control only; no worker callback)
    ↑
FhirAugury.Publishing.Tickets   ← Common + Processing.Client + Processing.Contracts
                                  + Preparer/Planner public snapshot-schema contracts
    ↑
FhirAugury.McpShared            ← Common (shared MCP tool implementations, HTTP clients)
FhirAugury.McpStdio             ← McpShared (stdio transport, generic .NET Host)
FhirAugury.McpHttp              ← McpShared + ServiceDefaults (HTTP/SSE transport, ASP.NET Core)
FhirAugury.Cli                  ← Common + Processing.Client (HTTP to Orchestrator;
                                  CLI assembly retains authenticated worker submission)
FhirAugury.DevUi                ← Common + ServiceDefaults + Processing.Client
                                  + Publishing.Tickets (HTTP to Orchestrator)
tools/ticket-site               ← Processing.Client + Publishing.Tickets (thin CLI adapter)

FhirAugury.AppHost             ← Aspire AppHost (references all service projects for orchestration)
```

## Source Service Architecture

Each ingesting source service (`Source.Jira`, `Source.Zulip`,
`Source.Confluence`, `Source.GitHub`) follows the same internal structure
(`Source.Fhir` is a read-only reference server and omits the download/cache
ingestion pipeline):

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


### Processing Stack

Processing services are layered separately from source ingestion.
`FhirAugury.Processing.Common` owns the durable authoring state machine:
frozen runs and items, public operation IDs plus secret token verifiers,
immutable persistence receipts, finite retry policy, the shared lifecycle
scheduler, typed run controls, processor mutation fences, resumable
finalization stages, one-way legacy cutover/revalidation, and monotonic
snapshot descriptors.

`FhirAugury.Processing.Jira.Common` builds on that substrate for Jira-backed
processors. It discovers candidates through Source.Jira or the Orchestrator,
freezes exact source revisions into a run, launches workers without shell
expansion, supplies callback capabilities through the worker environment, and
performs a final source-revision check inside the transaction that completes an
initial revalidation run. Concrete Preparer and Planner services are the only
writers of their domain databases and own their hydration and grouping stages.

The run-backed data flow is:

1. A client starts a scheduled or explicit run through the Dev UI, typed CLI,
   or Orchestrator proxy.
2. The processor freezes item membership/revisions and leaves the run queued.
   The shared scheduler reconciles source state and acquires the mutation fence
   for the oldest eligible run.
3. The scheduler dispatches workers, automatically retries unpersisted errors
   after the configured minimum delay, and marks the item superseded when its
   total attempt budget is exhausted.
4. Workers submit typed results through operation-scoped callbacks.
5. The processor atomically persists the domain result and receipt, then
   completes required post-persistence work such as ticket hydration.
6. After every item is complete or superseded, fenced finalization refreshes
   shared catalogs, performs grouping, and creates a sanitized immutable
   snapshot. Completion releases the fence before the oldest queued successor
   can activate.
7. `FhirAugury.Processing.Client` downloads a workflow/service-bound pair
   directory and writes `verified-pair.json` last after validating coordinates,
   filenames, length, and digests.
8. The Dev UI invokes `FhirAugury.Publishing.Tickets` in-process, or the
   `ticket-site` adapter invokes the same publisher headlessly. Publication is
   outside processor state and writes `site-manifest.json`.

There are four concrete processors:

- **Preparer** (`Processor.Jira.Fhir.Preparer`, :5171) — queues triaged Jira
  tickets and runs `ticket-prep` as processor-owned workers, completes
  per-item hydration, then finalizes grouping and a discussion snapshot.
- **Planner** (`Processor.Jira.Fhir.Planner`, :5172) — queues resolved
  change-required tickets and runs `ticket-plan`, then finalizes grouping and
  an applying snapshot while preserving the Applier projection.
- **Applier** (`Processor.Jira.Fhir.Applier`, :5173) — auto-discovers completed
  plans from the planner DB, applies each in a per-(ticket, repo) git worktree,
  and exposes `POST /api/v1/applied-tickets/{key}/push` to push on demand.
- **BallotNotes** (`Processor.GitHub.Fhir.BallotNotes`, :5174) — standalone,
  GitHub-source driven. On-demand hydration freezes one commit-window
  execution; a separate processor-owned authoring run produces receipts and a
  snapshot consumed by `notes-site`.

For operator instructions (kick-off curl, monitoring, output locations), see the
[processors runbook](processors.md).

### Operations workspace and local artifacts

`FhirAugury.DevUi` composes three narrow boundaries:

1. typed readiness and source/processor metadata from the Orchestrator;
2. `IAuthoringControlClient` for Preparer/Planner list, detail, start,
   server-advertised item actions, and snapshot download; and
3. `ITicketSitePublisher` for immutable-pair publication.

The overview reads processor-owned ordinary runs on every load. The bounded
list puts active/recoverable runs first and reports truncation; exact run detail
is always addressable by
`/operations/{prepare|plan}/{runId}`. Run status `error` is recoverable and
non-terminal, so polling continues until processor-provided
`state.isTerminal` becomes true.

Mutating outer calls are single-attempt. A response lost after start, retry, or
supersede is an outcome-unknown condition: the UI serializes further
mutations, performs only list/detail reconciliation reads, and never replays
the request. The processor's `allowedActions` values are display capabilities,
not authorization snapshots; mutation-fence, receipt, state, and attempt checks
run again inside the processor transaction.

The AppHost configures two ignored, non-overlapping children beneath
repository `cache\`:

```text
cache\devui-authoring-snapshots\{workflow}\{runId}\
cache\devui-review-sites\{workflow}\{runId}\{discussion|applying}\
```

Only the second root is web-served at
`/review-sites/{workflow}/{runId}/{discussion|applying}/`. Browser input cannot
choose a filesystem path, and path validation rejects escapes, reserved/ADS
segments, and existing reparse points. Snapshot pairs are never served. The
generated site shares the Dev UI origin under the first-release trusted-local
model; remote/multi-user hardening requires a separate design.

Artifacts are retained indefinitely in the first release. There is no
automatic retention job or delete UI; operators may remove old run directories
manually while the Dev UI is stopped.

## Key Design Decisions

### SQLite per Service (Not Shared)

Each source and processor service owns its own SQLite database. Clients,
skills, site generators, and unrelated processors do not open that live store.
Cross-service access uses the owning HTTP API; review publication uses an
immutable snapshot explicitly released by the owner. The deliberate exception
is the existing Applier compatibility path, which opens the Planner database
read-only through `Processing:Applier:PlannerDatabasePath`. The Orchestrator has
a separate SQLite database for scan state coordination. WAL mode enables
concurrent reads within each service.

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
The Orchestrator communicates with source services via HTTP and exposes typed,
thin authoring proxies for processor run creation, bounded list/detail,
retry, explicit item supersession, and streamed snapshot transfer. It also
returns typed source/processor readiness with required-source metadata and an
explicit health-refresh route. MCP, CLI, and Dev UI clients connect to the
Orchestrator via HTTP. The Orchestrator validates only service availability
and preserves processor responses; it does not interpret retry budgets,
receipts, supersession reasons, or processor database state.

The Dev UI's API tester preserves source-oriented tabs but resolves every
descriptor through an explicit gateway route matrix. The invocation base and
schema source are always the Orchestrator and its merged
`/api/v1/openapi.json`; no Dev UI source-direct client or source-address
configuration remains.

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

### MCP, CLI, and Dev UI as HTTP Clients

Both MCP servers, the CLI, and the Dev UI are HTTP clients to the Orchestrator.
They contain no live service-database access. Typed outer authoring commands
start, inspect, request an immediate bounded retry, explicitly supersede a
current non-receipt-backed error with a reason, and download snapshots through
the Orchestrator proxies. Worker callback submission is separate: it exists
only in the CLI assembly and posts to the
processor-supplied callback URL with an environment-only operation token. The
shared outer client and Dev UI expose no submit method or token concept.

Mutating outer requests are not replayed after ambiguous transport failure.
The shared client verifies snapshot identity, workflow/service binding, size,
and SHA-256 before atomically promoting a durable pair directory. McpHttp is
also an ASP.NET Core web application (port 5200, `/mcp` endpoint) that
participates in Aspire orchestration via ServiceDefaults.

## Concurrency Model

- **Service independence:** Each source service runs as an independent process
  with its own database and ingestion pipeline
- **WAL mode:** SQLite WAL mode enables concurrent reads within each service
  (HTTP request handlers alongside the ingestion writer)
- **Parallel fan-out:** The orchestrator sends HTTP requests to all source
  services in parallel using `Task.WhenAll`
- **Processor mutation fence:** One mutating/finalizing authoring or maintenance
  run owns a processor database at a time; item workers may still execute
  concurrently inside that run
- **Startup ownership:** Preparer, Planner, and BallotNotes acquire an exclusive
  owner lock before schema migration or abandoned-fence recovery
- **Health monitoring:** `ServiceHealthMonitor` polls enabled source and
  processing services every 60 seconds; one probe timeout becomes a typed
  unavailable observation rather than failing the whole sweep

## Error Handling

- **HTTP error handling:** Standard HTTP status codes (404 Not Found, 503
  Service Unavailable, 500 Internal Server Error, etc.) for error responses
- **Transient HTTP failures:** for retry-safe operations, `HttpRetryHelper`
  retries on HTTP
  429/500/502/503/504 with exponential backoff + jitter (max 3 retries).
  Respects `Retry-After` headers.
- **Auth failures:** Immediate failure on HTTP 401/403 with a clear error
  message
- **Partial failure isolation:** One source service failing doesn't block
  others — the orchestrator returns results from healthy sources
- **Ambiguous outer mutation:** Start, immediate retry, and supersede are never
  replayed after transport loss; clients reconcile with idempotent reads
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
14 projects (five sources, the orchestrator, four processors, the terminology
server, the Dev UI, the MCP HTTP server, and the CLI) with fixed ports matching
the existing convention:

| Service | HTTP |
|---------|------|
| source-jira | 5160 |
| source-zulip | 5170 |
| source-confluence | 5180 |
| source-github | 5190 |
| source-fhir | 5195 |
| orchestrator | 5150 |
| processor-jira-fhir-preparer | 5171 |
| processor-jira-fhir-planner | 5172 |
| processor-jira-fhir-applier | 5173 |
| processor-github-fhir-ballotnotes | 5174 |
| server-terminology | 5300 |
| devui | 5210 |
| mcp | 5200 |
| cli | — |

The orchestrator uses `WaitFor()` to depend on the Jira, Zulip, GitHub, and
FHIR source services. Confluence, all four processors, the terminology server,
the Dev UI, the MCP HTTP server, and the CLI use `WithExplicitStart()` to allow
manual triggering. The shared ticket-workflow dependency catalog applies
`Preparer -> Jira` and
`Planner -> Jira, GitHub` to processor `WaitFor()` topology and is also
validated against Orchestrator `RequiredServices` readiness metadata. The
processors additionally wait for the Orchestrator; the Applier waits for Jira,
the Orchestrator, and Planner.

The Dev UI references and waits for the Orchestrator but deliberately does not
control Aspire lifecycle or introspect AppHost state. Its readiness refresh is
an Orchestrator health sweep only. All endpoints use `isProxied: false` so
services listen on their own ports directly (no Aspire reverse proxy).
