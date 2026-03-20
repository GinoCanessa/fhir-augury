# FHIR Augury v2 — Build Plan

## Strategy

The v2 architecture is built from the ground up as a service-oriented system.
Development follows a phased approach where each phase validates the
architecture with increasing scope before adding the next layer.

---

## Phase 1: Foundation & Common Infrastructure

**Goal:** Establish the shared project structure, gRPC contracts, and common
abstractions that all services will use.

### Tasks

1. **Create `FhirAugury.Common`** — shared types, gRPC proto definitions,
   utility code (text sanitization, HTML stripping, FHIR-aware tokenization).

2. **Define proto contracts** — write `source_service.proto`,
   `orchestrator.proto`, and source-specific proto files. These define the
   inter-service API before any implementation begins.

3. **Create `ResponseCache` base class** — the shared file-system caching
   infrastructure that all source services will use.

4. **Create `SourceDatabase` base class** — common SQLite database management
   (connection pooling, WAL mode, table creation, FTS5 helpers) that each
   source service will extend.

5. **Create service host template** — a reusable pattern for hosting a source
   service (ASP.NET + gRPC + background workers + health checks).

---

## Phase 2: Jira Source Service

**Goal:** Build the first source service to validate the architecture.

Jira is built first because it has the richest data model (custom fields,
issue links, spec-artifacts integration) and serves as the reference
implementation for the other source services.

### Tasks

1. **Create `FhirAugury.Source.Jira` project** — a standalone ASP.NET service
   with its own `Program.cs`.

2. **Implement Jira ingestion** — `JiraSource`, `JiraFieldMapper`,
   `JiraCommentParser`, `JiraXmlParser`, and `JiraAuthHandler`.

3. **Implement Jira database schema** — Jira-specific table definitions
   (`JiraIssueRecord`, `JiraCommentRecord`). Configure `cslightdbgen.sqlitegen`
   for the service's own schema.

4. **Implement Jira cache integration** — wire the `ResponseCache` into the
   ingestion pipeline so all API responses are cached.

5. **Implement Jira internal indexing** — FTS5 tables with content-synced
   triggers, BM25 keyword extraction, and internal issue-link tracking.

6. **Implement Jira gRPC service** — expose `SourceService` + `JiraService`
   gRPC endpoints.

7. **Implement Jira HTTP API** — lightweight HTTP endpoints for standalone use.

8. **Implement rebuild-from-cache** — the `RebuildFromCache` operation that
   recreates the Jira database from cached API responses.

9. **Tests** — unit tests for ingestion, caching, indexing, gRPC endpoints,
   and rebuild-from-cache.

### Verification

- Jira service starts independently and serves gRPC + HTTP
- Full download populates cache and database
- Incremental sync works
- FTS5 search returns ranked results
- `GetItem` returns complete issue with comments
- `RebuildFromCache` recreates database without network calls

---

## Phase 3: Zulip Source Service

**Goal:** Build the second source service, confirming the patterns generalize.

### Tasks

Same pattern as Phase 2, adapted for Zulip:

1. Create `FhirAugury.Source.Zulip` project
2. Implement Zulip ingestion (`ZulipSource`, mappers, auth)
3. Implement Zulip database schema (`ZulipStreamRecord`, `ZulipMessageRecord`)
4. Implement Zulip cache integration (per-stream date-based batches)
5. Implement Zulip internal indexing (FTS5, BM25, topic threading)
6. Implement Zulip gRPC service (including `GetThread`, `ListStreams`, etc.)
7. Implement Zulip HTTP API
8. Implement rebuild-from-cache
9. Tests

### Verification

- Zulip service handles 1M+ messages efficiently
- Thread retrieval returns complete topic threads
- Stream/topic navigation works
- Rebuild from cache handles per-stream batch files

---

## Phase 4: Orchestrator, MCP & CLI (Jira + Zulip)

**Goal:** Build the orchestrator and all consumer interfaces with the first
two source services.

### Tasks

1. **Create `FhirAugury.Orchestrator` project** — ASP.NET service with gRPC
   clients for Jira and Zulip services.

2. **Implement unified search** — fan-out search queries to Jira and Zulip
   services, merge and re-rank results.

3. **Implement cross-reference linker** — scan Jira and Zulip content for
   mentions of identifiers from each other (Jira keys in Zulip messages,
   Zulip URLs in Jira descriptions).

4. **Implement related item discovery** — combine cross-references with BM25
   similarity to find items related to a given seed across both sources.

5. **Implement orchestrator HTTP API** — unified search, related items,
   cross-references, service health, ingestion triggers.

6. **Implement orchestrator gRPC service** — same capabilities via gRPC.

7. **Build MCP server** — `FhirAugury.Mcp` connecting to the orchestrator
   and source services via gRPC.

8. **Build CLI** — `FhirAugury.Cli` connecting to the orchestrator and
   source services via gRPC/HTTP.

9. **Tests** — cross-reference extraction, unified search merging, related
   item discovery, MCP tool invocations, CLI commands.

### Verification

- Unified search across Jira and Zulip returns merged results
- Cross-references link Jira issues ↔ Zulip messages
- `find_related` for a Jira issue discovers relevant Zulip threads
- MCP tools work end-to-end through orchestrator → source services
- CLI commands work against the service architecture
- All three interfaces (HTTP, MCP, CLI) expose the same capabilities

---

## Phase 5: Confluence & GitHub Source Services

**Goal:** Complete the four-source coverage.

### Tasks

1. **Create `FhirAugury.Source.Confluence` project** — same pattern as
   Phases 2–3, with Confluence-specific features (page hierarchy, labels,
   internal page links, storage-format parsing).

2. **Create `FhirAugury.Source.GitHub` project** — same pattern, with
   GitHub-specific features (issue/PR distinction, commit linking, label
   browsing, milestone grouping, rate limiting, local repo cloning).

3. **Update orchestrator** — add gRPC clients for Confluence and GitHub
   services. Extend cross-reference patterns to include Confluence URLs and
   GitHub URLs/references.

4. **Update MCP and CLI** — add Confluence and GitHub tools/commands.

5. **Tests** — full four-source cross-referencing, unified search across all
   sources.

### Verification

- All four source services run independently
- Orchestrator links all four sources via cross-references
- Unified search spans all four sources
- MCP tools and CLI commands cover all four sources

---

## Phase 6: Docker Compose & Deployment

**Goal:** Productionize the deployment.

### Tasks

1. **Individual Dockerfiles** — one per source service + orchestrator, using
   multi-stage builds for small images.

2. **`docker-compose.yml`** — full-stack composition with volume mounts for
   cache and data persistence.

3. **Health checks** — Docker health checks for each service, orchestrator
   monitors source service health.

4. **Configuration** — environment variable-based configuration for all
   services, with sensible defaults.

5. **Documentation** — deployment guide, configuration reference, MCP setup
   instructions.

---

## Build Order Summary

```
Phase 1: Foundation & Common Infrastructure
    └──► Phase 2: Jira Source Service
         └──► Phase 3: Zulip Source Service
              └──► Phase 4: Orchestrator, MCP & CLI (Jira + Zulip)
                   └──► Phase 5: Confluence & GitHub Source Services
                        └──► Phase 6: Docker Compose & Deployment
```

Each phase validates the architecture at increasing scope. Phase 4 is the
key integration milestone — if the two-source system works end-to-end,
adding the remaining sources in Phase 5 is straightforward.

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| gRPC adds latency vs. direct DB access | Slower queries | gRPC is fast (sub-ms local); cache common query results in orchestrator |
| More processes to manage | Operational complexity | Docker Compose handles orchestration; in-process mode for development |
| Data consistency across services | Stale cross-references | Orchestrator scans on configurable interval; force-scan available via API |
| Increased disk usage (cache + DB per service) | Storage requirements | Cache is compressed; databases are smaller individually than the monolith |
| Proto schema evolution | Breaking changes | Follow proto3 best practices (never reuse field numbers, use `reserved`) |

---

## Technology Choices

- **C# 14 / .NET 10.0** — language and runtime
- **SQLite + FTS5** — database engine and full-text search
- **`cslightdbgen.sqlitegen`** — source-generated CRUD
- **BM25 keyword scoring** — relevance algorithm
- **`zulip-cs-lib`** — Zulip client library
- **`ModelContextProtocol` SDK** — MCP framework
- **`System.CommandLine`** — CLI framework
- **Search → Snapshot → Explore** methodology for MCP agents
