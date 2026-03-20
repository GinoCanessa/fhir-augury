# FHIR Augury v2 — Overview

## Problem Statement

The v1 architecture of FHIR Augury is a monolithic application where all four
data sources (Jira, Zulip, Confluence, GitHub), the indexing pipeline, the
cross-referencing engine, and the serving layer are tightly coupled through a
single SQLite database and a shared process boundary.

This creates several problems as the system matures:

### 1. Monolithic Coupling

All source ingestion logic, schema definitions, FTS5 indexes, and BM25 scoring
live in a single database file. A schema change to one source's tables requires
rebuilding the entire database. Adding a new field to Jira records means
re-running ingestion for all sources if the schema migration touches shared
infrastructure.

### 2. All-or-Nothing Rebuilds

When the database schema changes or indexes need to be rebuilt, the entire
knowledge base must be re-downloaded and re-indexed. Even with incremental
sync, a schema migration that requires a fresh database means fetching 48k+
Jira issues, 1M+ Zulip messages, and thousands of Confluence pages from
scratch — a process that takes hours and strains rate-limited APIs.

### 3. Development Friction

Working on the Zulip source requires building and understanding the Jira
source, the Confluence source, the GitHub source, the shared database layer,
and the indexing pipeline. The blast radius of any change is the entire
solution.

### 4. No Fault Isolation

If the Jira API is down or authentication expires, the single ingestion queue
backs up and may block scheduled syncs for other sources. All sources share
the same worker threads and database connection pool.

### 5. Deployment Inflexibility

The system can only be deployed as a single unit. There is no way to run
just the Jira service for a team that only needs Jira data, or to scale
Zulip ingestion independently when processing 1M+ messages.

---

## Vision

FHIR Augury v2 splits the system into **five discrete services**:

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ Jira Service │  │Zulip Service │  │ Confluence   │  │GitHub Service│
│              │  │              │  │   Service    │  │              │
│ • Ingest     │  │ • Ingest     │  │ • Ingest     │  │ • Ingest     │
│ • Cache      │  │ • Cache      │  │ • Cache      │  │ • Cache      │
│ • Index      │  │ • Index      │  │ • Index      │  │ • Index      │
│ • Serve      │  │ • Serve      │  │ • Serve      │  │ • Serve      │
└──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘
       │                 │                 │                 │
       └────────┬────────┴────────┬────────┘                 │
                │                 │                          │
                ▼                 ▼                          ▼
        ┌─────────────────────────────────────────────────────┐
        │              Orchestrator Service                   │
        │                                                     │
        │  • Cross-reference linking across sources           │
        │  • Unified search (fan-out to source services)      │
        │  • "Find related" queries across sources            │
        │  • Jira ↔ Zulip ↔ Confluence ↔ GitHub correlation   │
        │  • User query answering                             │
        └──────────────────────┬──────────────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
        ┌──────────┐    ┌──────────┐    ┌──────────┐
        │   CLI    │    │   MCP    │    │  HTTP    │
        │          │    │  Server  │    │  API     │
        └──────────┘    └──────────┘    └──────────┘
```

Each **source service** is a self-contained unit that:

1. **Ingests** data from its remote API (full and incremental downloads)
2. **Caches** raw API responses to the local file system
3. **Stores** normalized, indexed data in its own SQLite database
4. **Indexes** its data internally (FTS5, internal cross-references, BM25)
5. **Serves** queries about its data via gRPC (and optionally HTTP)

The **orchestrator service** is the "brain" that:

1. **Queries** source services to correlate data across sources
2. **Builds** a cross-reference index from identifiers found in source data
3. **Answers** higher-level questions that span multiple sources
4. **Provides** the unified search, "find related", and user-facing APIs

---

## Goals

### G1: Independent Source Services

Each source service (Jira, Zulip, Confluence, GitHub) is a self-contained
process with its own database, cache, ingestion pipeline, and API. It can
be developed, tested, deployed, and scaled independently.

### G2: Complete Content Serving

Each source service can serve the full details of any item in its domain.
Requesting a Jira issue returns the complete issue with all fields, comments,
and metadata. Requesting a Confluence page returns the full page content,
labels, and hierarchy. No partial data — each service is the authoritative
source for its content type.

### G3: Internal Indexing and Referencing

Each source service maintains its own FTS5 full-text search index and can
answer queries within its domain: searching Jira issues by keyword, finding
related tickets by BM25 similarity, browsing Zulip threads by stream/topic,
navigating Confluence page hierarchies, etc.

### G4: Local Cache Resilience

Each source service caches raw API responses to the file system. If the
database needs to be rebuilt (schema change, corruption, migration), the
service can re-populate from cache without any network calls. Pre-populated
cache directories (e.g., from a colleague's export or CI artifact) are
ingested directly.

### G5: Cross-Source Orchestration

The orchestrator service handles all cross-source intelligence: finding Zulip
threads that discuss a Jira ticket, finding GitHub commits related to a Jira
issue, finding Confluence pages that reference a specific topic, answering
free-form user queries that span multiple sources.

### G6: Composable Deployment

The system can be deployed in multiple configurations:
- **Full stack:** All five services via `docker compose`
- **Subset:** Only the source services you need (e.g., Jira + Zulip + orchestrator)
- **Single source:** One source service standalone, for teams that only need
  one data source
- **In-process:** All services in a single process for development/testing

---

## Design Principles

### 1. Own Your Data

Each source service owns its data end-to-end. It downloads it, caches it,
normalizes it, indexes it, and serves it. No other service writes to its
database. The orchestrator reads from source services but never modifies
their data.

### 2. Cache Everything

Every API response fetched from a remote source is cached locally. The cache
is the ground truth for rebuilds. The database is a derived, queryable
projection of the cached data.

### 3. gRPC First

Inter-service communication uses gRPC for type safety, performance, and
streaming support. HTTP/JSON is available as a fallback for external
consumers and debugging, but services talk to each other over gRPC.

### 4. Triple Interface

Every external-facing capability must be available through **all three**
consumer interfaces — HTTP API, MCP, and CLI. This ensures flexibility in
deployment: scripts and integrations use HTTP, LLM agents use MCP, and
human operators use the CLI. No capability should be exclusive to a single
interface. The three interfaces are thin layers over the same underlying
gRPC services, so feature parity is maintained by design.

### 5. Source-Generated Data Access

Continue using `cslightdbgen.sqlitegen` for zero-reflection, AOT-compatible
database access in each service. Each service has its own database schema
defined by its own model types.

### 6. Fail Independently

If the Zulip service crashes, the Jira service continues operating. If the
orchestrator is down, source services still serve their own data. The system
degrades gracefully.

### 7. Rebuild from Cache

A database rebuild for any service reads from the local cache directory. This
means schema changes, index rebuilds, and fresh container starts are fast
(minutes, not hours) and don't require network access or API credentials.

### 8. Link to Source Material

Every item returned to callers — whether via HTTP, MCP, or CLI — must include
a `url` field containing a direct link to the original content in the source
system (e.g., the Jira issue page, the Zulip message, the Confluence page,
the GitHub commit or PR). This lets consumers navigate from search results
or cross-references directly to the authoritative source when needed.
