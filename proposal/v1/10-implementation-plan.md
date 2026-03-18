# FHIR Augury — Implementation Plan

## Phase 1: Foundation

**Goal:** Project scaffolding, database layer, and first data source (Jira).

### Tasks

1. **Project scaffolding** — Create all projects in the solution, configure
   `common.props`, set up `cslightdbgen.sqlitegen` references, and verify
   the source generator works with a trivial test table.

2. **Database models** — Define all `partial record class` types in
   `FhirAugury.Database` with `cslightdbgen.sqlitegen` attributes. Start with
   the ingestion metadata tables (`sync_state`, `ingestion_log`) and Jira
   tables (`jira_issues`, `jira_comments`). Verify generated CRUD compiles.

3. **Jira source** — Implement `JiraSource : IDataSource` in
   `FhirAugury.Sources.Jira`. Support both JSON REST API and XML bulk export.
   Implement custom field mapping. Handle authentication (cookie + API token).
   Port the proven patterns from `temp/JiraFhirUtils`.

4. **FTS5 indexing (Jira)** — Implement FTS5 table creation and population for
   Jira issues and comments. Include content-synced triggers.

5. **CLI (Jira subset)** — Implement `download`, `index build-fts`, `search`,
   `get`, and `snapshot` commands for Jira only. Verify end-to-end:
   download → load → index → search.

6. **Tests** — Unit tests for database CRUD, Jira parsing, and FTS5 queries.

---

## Phase 2: Zulip Integration

**Goal:** Add Zulip as a second data source.

### Tasks

1. **Zulip source** — Implement `ZulipSource : IDataSource` using `zulip-cs-lib`.
   Full download with stream iteration, incremental update via last-seen ID,
   on-demand topic fetch.

2. **Zulip database tables** — Define `zulip_streams` and `zulip_messages`
   record types. FTS5 table with content-synced triggers.

3. **CLI (Zulip)** — Extend CLI with Zulip-specific download and search.
   Add `snapshot` support for Zulip threads.

4. **Unified search** — Implement the cross-source search combining Jira and
   Zulip FTS5 results with score normalization.

5. **Tests** — Zulip parsing, FTS5, and unified search tests.

---

## Phase 3: Cross-Referencing & BM25

**Goal:** Cross-source linking and advanced relevance scoring.

### Tasks

1. **Cross-reference linker** — Implement pattern-based extraction of Jira keys,
   Zulip URLs, etc. from text fields. Populate `xref_links` table.

2. **BM25 keyword scoring** — Implement tokenization, lemmatization, TF/IDF
   computation, and BM25 score storage. Port the proven approach from
   `temp/JiraFhirUtils`.

3. **"Find related" feature** — Implement the BM25-based similarity search
   combined with explicit cross-references.

4. **CLI extensions** — Add `related`, `index build-bm25`, `index build-xref`
   commands.

5. **Tests** — Cross-reference extraction, BM25 computation, related-item
   queries.

---

## Phase 4: Service Layer

**Goal:** Long-running background service with HTTP API.

### Tasks

1. **Service host** — Set up ASP.NET Minimal API with `BackgroundService`
   workers. Implement `IngestionQueue` using `System.Threading.Channels`.

2. **Ingestion worker** — Background task that reads from the queue and
   dispatches to sources. Includes index updating and cross-ref linking
   after each ingestion.

3. **Scheduled sync** — `ScheduledIngestionService` that periodically enqueues
   incremental sync requests.

4. **HTTP API** — Implement all endpoints: ingestion control, search, item
   retrieval, cross-references, statistics.

5. **CLI client mode** — Add `--service` flag to CLI commands, enabling them
   to use the HTTP API instead of direct database access.

6. **Tests** — API endpoint tests, queue tests, integration tests.

---

## Phase 5: Confluence & GitHub Sources

**Goal:** Complete the four-source coverage.

### Tasks

1. **Confluence source** — Implement `ConfluenceSource : IDataSource`. REST API
   client for spaces, pages, comments. CQL-based incremental updates. HTML/XML
   stripping for indexing.

2. **Confluence database tables** — Spaces, pages, comments. FTS5 with triggers.

3. **GitHub source** — Implement `GitHubSource : IDataSource`. REST API client
   for issues, PRs, comments, reviews. Rate-limit aware pagination.

4. **GitHub database tables** — Repos, issues, comments. FTS5 with triggers.

5. **Update cross-ref linker** — Add Confluence URL and GitHub URL patterns.
   Re-run cross-referencing to link all four sources.

6. **CLI & MCP extensions** — Add Confluence and GitHub commands to CLI. Add
   corresponding MCP tools.

7. **Tests** — Confluence and GitHub parsing, full cross-source integration.

---

## Phase 6: MCP Server

**Goal:** Full MCP server for LLM agent integration.

### Tasks

1. **MCP host** — Set up `ModelContextProtocol` with stdio transport.
   Configure read-only database access.

2. **Search tools** — Unified search, per-source search with filters.

3. **Retrieval tools** — Get/snapshot for all four sources.

4. **Relationship tools** — `find_related`, `get_cross_references`.

5. **Listing tools** — Issue lists, stream lists, space lists with filters.

6. **HTTP transport option** — Alternative hosting via HTTP at `/mcp`.

7. **Tests** — Tool invocation tests, output format validation.

---

## Phase 7: Polish & Optimization

**Goal:** Production readiness.

### Tasks

1. **Performance tuning** — Optimize batch inserts (transactions), FTS5
   rebuild speed, BM25 computation for large corpora.

2. **Error handling** — Graceful handling of API failures, rate limits,
   authentication expiry. Retry policies with exponential backoff.

3. **Logging** — Structured logging throughout. Ingestion progress reporting.

4. **Documentation** — README updates, CLI help text, MCP tool descriptions,
   configuration guide.

5. **Packaging** — `dotnet tool` packaging for CLI. Docker support for the
   service (optional). NuGet packaging for the library projects (optional).

---

## Implementation Notes

- **Start with Jira** because it has the most complex data model (custom fields,
  multiple ID mappings) and two proven reference implementations to port from.
  Getting this right first de-risks the architecture.

- **Add Zulip second** because it's the highest-volume source and exercises the
  FTS5 pipeline at scale. The `zulip-cs-lib` dependency also needs early
  validation.

- **Defer Confluence and GitHub** to Phase 5 because they follow the same
  patterns established in Phases 1–2, just with different APIs.

- **Build the service layer in Phase 4** after having two working sources,
  because the service needs real data to test the queue, scheduling, and
  incremental update logic.

- **MCP server in Phase 6** because it's a read-only consumer of the database
  and benefits from having all data sources and indexes available.
