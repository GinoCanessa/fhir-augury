# FHIR Augury v2 — Migration Plan

## Migration Strategy

The migration from v1 (monolith) to v2 (services) is designed to be
incremental. Each phase produces a working system, and existing v1
functionality is preserved until the v2 equivalent is ready.

---

## Phase 1: Foundation & Common Infrastructure

**Goal:** Establish the shared project structure, gRPC contracts, and common
abstractions that all services will use.

### Tasks

1. **Create `FhirAugury.Common`** — shared types, gRPC proto definitions,
   utility code (text sanitization, HTML stripping, FHIR-aware tokenization).
   Extract from existing `FhirAugury.Models` and `FhirAugury.Indexing`.

2. **Define proto contracts** — write `source_service.proto`,
   `orchestrator.proto`, and source-specific proto files. These define the
   inter-service API before any implementation begins.

3. **Create `ResponseCache` base class** — the shared file-system caching
   infrastructure that all source services will use. Port from the v1 local
   caching feature proposal.

4. **Create `SourceDatabase` base class** — common SQLite database management
   (connection pooling, WAL mode, table creation, FTS5 helpers) that each
   source service will extend.

5. **Create service host template** — a reusable pattern for hosting a source
   service (ASP.NET + gRPC + background workers + health checks).

---

## Phase 2: Jira Source Service

**Goal:** Extract the Jira source into a fully independent service.

### Tasks

1. **Create `FhirAugury.Source.Jira` project** — a standalone ASP.NET service
   with its own `Program.cs`.

2. **Move Jira ingestion logic** — port `JiraSource`, `JiraFieldMapper`,
   `JiraCommentParser`, `JiraXmlParser`, and `JiraAuthHandler` from
   `FhirAugury.Sources.Jira` into the new service.

3. **Move Jira database schema** — extract Jira-specific table definitions
   (`JiraIssueRecord`, `JiraCommentRecord`) from `FhirAugury.Database` into
   the service's `Database/` folder. Configure `cslightdbgen.sqlitegen` for
   the service's own schema.

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
- All existing Jira-related CLI commands work against the new service

---

## Phase 3: Zulip Source Service

**Goal:** Extract Zulip into a fully independent service.

### Tasks

Same pattern as Phase 2, adapted for Zulip:

1. Create `FhirAugury.Source.Zulip` project
2. Move Zulip ingestion logic (`ZulipSource`, mappers, auth)
3. Move Zulip database schema (`ZulipStreamRecord`, `ZulipMessageRecord`)
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

## Phase 4: Orchestrator Service (Jira + Zulip)

**Goal:** Build the orchestrator with the first two source services.

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

7. **Update MCP server** — modify `FhirAugury.Mcp` to talk to the
   orchestrator via gRPC instead of accessing the database directly.

8. **Update CLI** — modify `FhirAugury.Cli` to talk to the orchestrator
   via gRPC/HTTP.

9. **Tests** — cross-reference extraction, unified search merging, related
   item discovery, MCP tool invocations.

### Verification

- Unified search across Jira and Zulip returns merged results
- Cross-references link Jira issues ↔ Zulip messages
- `find_related` for a Jira issue discovers relevant Zulip threads
- MCP tools work end-to-end through orchestrator → source services
- CLI commands work against the new service architecture

---

## Phase 5: Confluence & GitHub Source Services

**Goal:** Complete the four-source coverage.

### Tasks

1. **Create `FhirAugury.Source.Confluence` project** — same pattern as
   Phases 2–3, with Confluence-specific features (page hierarchy, labels,
   internal page links, storage-format parsing).

2. **Create `FhirAugury.Source.GitHub` project** — same pattern, with
   GitHub-specific features (issue/PR distinction, commit linking, label
   browsing, milestone grouping, rate limiting).

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
- MCP tools cover all four sources

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

## Phase 7: V1 Deprecation & Cleanup

**Goal:** Remove v1 monolithic code.

### Tasks

1. **Remove v1 projects** — delete `FhirAugury.Database`,
   `FhirAugury.Indexing`, `FhirAugury.Sources.*` (plural), and
   `FhirAugury.Service`.

2. **Remove v1 Models** — delete `FhirAugury.Models` (replaced by
   `FhirAugury.Common`).

3. **Update solution** — clean up `fhir-augury.slnx` to reference only
   v2 projects.

4. **Migration tooling** — if needed, provide a one-time script that exports
   data from the v1 SQLite database into source-service cache format, enabling
   a cache-based rebuild without re-downloading.

5. **Update all documentation** — README, docs/, proposal/.

---

## Migration Timeline Summary

```
Phase 1: Foundation & Common Infrastructure
    └──► Phase 2: Jira Source Service
         └──► Phase 3: Zulip Source Service
              └──► Phase 4: Orchestrator (Jira + Zulip)
                   └──► Phase 5: Confluence & GitHub Source Services
                        └──► Phase 6: Docker Compose & Deployment
                             └──► Phase 7: V1 Deprecation & Cleanup
```

Each phase is independently shippable — the system is functional after every
phase, with increasing capability.

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

## What Stays the Same

Despite the architectural refactor, these v1 decisions carry forward unchanged:

- **C# 14 / .NET 10.0** — same language and runtime
- **SQLite + FTS5** — same database engine and full-text search
- **`cslightdbgen.sqlitegen`** — same source-generated CRUD
- **BM25 keyword scoring** — same relevance algorithm
- **`zulip-cs-lib`** — same Zulip client library
- **`ModelContextProtocol` SDK** — same MCP framework
- **`System.CommandLine`** — same CLI framework
- **Data source API patterns** — same download strategies, auth modes,
  custom field mappings
- **Search → Snapshot → Explore** methodology for MCP agents
