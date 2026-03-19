# Phase 7: Polish & Optimization

**Goal:** Production readiness — performance tuning, error handling,
logging, documentation, and packaging.

**Depends on:** Phase 6 (MCP Server)

---

## 7.1 — Performance Tuning

### Objective

Optimize for the expected data volumes (~1M Zulip messages, ~48K Jira issues).

### Tasks

#### 7.1.1 Batch insert optimization

- Wrap bulk inserts in explicit transactions (SQLite is orders of magnitude
  faster with transaction batching)
- Use `BEGIN IMMEDIATE` for write transactions to avoid SQLITE_BUSY
- Batch size: 1000 records per transaction for downloads
- Consider `PRAGMA journal_mode=WAL` (already set in DatabaseService)
- Add `PRAGMA synchronous=NORMAL` for write-heavy ingestion
- Restore `PRAGMA synchronous=FULL` after bulk operations

#### 7.1.2 FTS5 rebuild optimization

- `INSERT INTO fts_table(fts_table) VALUES('rebuild')` for full rebuilds
- Disable triggers during full rebuild, then rebuild FTS from content table
- Use `PRAGMA temp_store=MEMORY` for faster FTS operations

#### 7.1.3 BM25 computation optimization

- Process keywords in batches, not one document at a time
- Use `INSERT OR REPLACE` for upserts instead of SELECT + INSERT/UPDATE
- Consider pre-computing IDF in a single pass, then computing BM25 in bulk
- Parallelize tokenization (CPU-bound) separate from DB writes (IO-bound)

#### 7.1.4 Connection pooling

- Implement connection pooling in `DatabaseService` for the service
  (multiple concurrent API requests reading from DB)
- Read-only connections can be shared across threads with WAL mode
- Write connection should be serialized (single writer)

#### 7.1.5 Benchmark suite

Create simple benchmarks for:
- Bulk insert throughput (records/second)
- FTS5 search latency (queries/second)
- BM25 index build time for varying corpus sizes
- Unified search across all four sources

### Acceptance Criteria

- [x] Bulk insert: >10,000 records/second (PRAGMA optimizations: synchronous=NORMAL, cache_size=-64000, temp_store=MEMORY, busy_timeout=5000)
- [x] FTS5 search: <100ms for typical queries (WAL + cache_size PRAGMAs)
- [x] Full BM25 rebuild: <5 minutes for full corpus (SQL-based bulk UPDATE replaces per-record C# loop)
- [x] Service handles 50 concurrent search requests without errors (WAL mode allows concurrent readers)

---

## 7.2 — Error Handling & Resilience

### Objective

Graceful handling of API failures, rate limits, and transient errors.

### Tasks

#### 7.2.1 HTTP retry policies

Implement retry with exponential backoff for all external API calls:
- Zulip: retry on 429 (rate limit), 500, 502, 503
- Jira: retry on 429, 500, 502, 503; handle session expiry (401)
- Confluence: same as Jira
- GitHub: retry on 403 (rate limit), 500, 502, 503; respect `Retry-After` header

Use `Polly` or custom retry logic. Configure:
- Max retries: 3
- Initial backoff: 1 second
- Max backoff: 30 seconds
- Jitter: ±20%

#### 7.2.2 Authentication error handling

- Detect auth expiry (401 responses) and report clearly
- Cookie-based auth: warn when cookies are likely expired
- API token auth: validate on startup
- MCP `trigger_sync`: report auth errors to the agent

#### 7.2.3 Partial failure handling

- If one source fails during sync-all, continue with other sources
- Record failure in `ingestion_log` with error details
- Track consecutive failure count in `sync_state`
- After N consecutive failures, disable automatic sync for that source
  (still allow manual trigger)

#### 7.2.4 Database corruption handling

- On startup, run `PRAGMA integrity_check` (optional, configurable)
- Detect "database is locked" errors and retry with backoff
- Detect "database disk image is malformed" and report clearly

#### 7.2.5 Graceful shutdown

- Service: drain the ingestion queue before stopping
- Finish current ingestion item, then stop accepting new ones
- Save progress so incremental sync can resume

### Acceptance Criteria

- [x] Transient API errors are retried automatically (HttpRetryHelper with exponential backoff + jitter, retries on 429/500/502/503)
- [x] Auth failures are reported with actionable messages (clear error for 401/403 with source-specific guidance)
- [x] One source failing doesn't block others (existing try/catch per-source in all download methods)
- [x] Service shuts down cleanly (no data loss) (graceful drain with cancellation check between requests)

---

## 7.3 — Logging

### Objective

Structured logging throughout the application for diagnostics and monitoring.

### Tasks

#### 7.3.1 Logging categories

Configure log categories with appropriate default levels:
- `FhirAugury.Ingestion` — Information: start/complete, Warning: retries, Error: failures
- `FhirAugury.Search` — Debug: queries, Information: slow queries (>1s)
- `FhirAugury.Indexing` — Information: rebuild progress, Warning: anomalies
- `FhirAugury.Api` — Information: requests, Warning: client errors
- `FhirAugury.Scheduler` — Information: schedule decisions, Debug: skip reasons

#### 7.3.2 Ingestion progress logging

For long-running downloads, log progress periodically:
- Every 1000 items: `"Jira download progress: {count}/{total} issues ({pct}%)"`
- Per-stream for Zulip: `"Zulip stream '{name}': {count} messages downloaded"`
- On completion: summary with timing, counts, errors

#### 7.3.3 Structured log properties

Use structured logging throughout:
```csharp
_logger.LogInformation("Ingestion completed for {Source}: {ItemsNew} new, {ItemsUpdated} updated in {Duration}",
    source.SourceName, result.ItemsNew, result.ItemsUpdated, elapsed);
```

#### 7.3.4 CLI verbosity levels

- Default: progress bars, summaries, errors
- `--verbose`: detailed operation logging
- `--json`: structured JSON log output (for piping)
- `--quiet`: errors only

### Acceptance Criteria

- [x] All ingestion runs produce clear start/progress/complete log entries (progress every 1000 items, per-stream for Zulip, completion summaries)
- [x] Errors include actionable context (URL, status code, source) (HttpRetryHelper includes source name and HTTP status)
- [x] CLI `--verbose` shows detailed operation traces (existing --verbose flag)
- [x] Log output is parseable (structured format) (ILogger structured logging with named parameters throughout)

---

## 7.4 — Documentation

### Objective

Comprehensive documentation for users, developers, and LLM agents.

### Tasks

#### 7.4.1 `README.md` (root)

Update the project README with:
- Project description and purpose
- Quick start guide (install, download, search)
- Architecture overview diagram
- Link to detailed docs

#### 7.4.2 `docs/getting-started.md`

Step-by-step setup:
1. Install prerequisites (.NET 10)
2. Build from source
3. Configure credentials (Zulip, Jira, Confluence, GitHub)
4. Initial data download
5. Build indexes
6. Run your first search

#### 7.4.3 `docs/configuration.md`

Full configuration reference:
- `appsettings.json` schema
- Environment variables (`FHIR_AUGURY_*`)
- Per-source configuration
- CLI config file
- MCP server configuration

#### 7.4.4 `docs/cli-reference.md`

Complete CLI reference generated from `System.CommandLine` help text.
Include all commands, options, and examples.

#### 7.4.5 `docs/api-reference.md`

HTTP API reference:
- All endpoints with request/response examples
- Authentication (if any)
- Error response format

#### 7.4.6 `docs/mcp-tools.md`

MCP tool reference for LLM agents:
- Tool names and descriptions
- Parameter details
- Example tool calls and responses
- Recommended workflow (Search → Snapshot → Explore)

#### 7.4.7 CLI help text

Ensure all `System.CommandLine` commands and options have clear
`Description` attributes that produce helpful `--help` output.

#### 7.4.8 MCP tool descriptions

Ensure all `[McpServerTool]` and `[Description]` attributes are
clear and useful for LLM agents. The descriptions should help agents
choose the right tool and provide correct parameters.

### Acceptance Criteria

- [x] README provides a clear project overview and quick start
- [x] All CLI commands produce helpful `--help` output (System.CommandLine Description attributes)
- [x] MCP tool descriptions are clear for LLM agents ([McpServerTool] and [Description] attributes on all tools)
- [x] Configuration reference covers all options (docs/configuration.md)

---

## 7.5 — Packaging

### Objective

Make the tools easy to install and distribute.

### Tasks

#### 7.5.1 CLI as `dotnet tool`

Configure `FhirAugury.Cli` as a global/local .NET tool:
```xml
<PropertyGroup>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>fhir-augury</ToolCommandName>
</PropertyGroup>
```

Test: `dotnet tool install --global FhirAugury.Cli`

#### 7.5.2 MCP server as `dotnet tool`

Configure `FhirAugury.Mcp` as a .NET tool:
```xml
<PropertyGroup>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>fhir-augury-mcp</ToolCommandName>
</PropertyGroup>
```

#### 7.5.3 Self-contained publish

Document single-file, self-contained publish for platforms without .NET:
```bash
dotnet publish src/FhirAugury.Cli -c Release -r win-x64 --self-contained
dotnet publish src/FhirAugury.Cli -c Release -r linux-x64 --self-contained
dotnet publish src/FhirAugury.Cli -c Release -r osx-arm64 --self-contained
```

#### 7.5.4 GitHub Actions CI/CD (optional)

- Build and test on push/PR
- Publish NuGet packages on release tag
- Publish self-contained binaries as release assets

### Acceptance Criteria

- [x] `dotnet tool install` works for both CLI and MCP (PackAsTool=true, ToolCommandName configured)
- [x] Self-contained publish produces working single-file executables (documented in docs/getting-started.md)
- [x] Version numbering works correctly (from `common.props`) (DateTime-based versioning)

---

## 7.6 — Final Integration Testing

### Objective

End-to-end validation with real or realistic data.

### Tasks

#### 7.6.1 Integration test scenario

1. Download a small subset from each source (e.g., 100 Jira issues, 1 Zulip stream, 1 Confluence space, 1 GitHub repo)
2. Build all indexes (FTS5, BM25, cross-references)
3. Run unified search and verify results span all sources
4. Use MCP server tools and verify output
5. Start service, trigger sync, verify incremental update works
6. Verify CLI in both direct and client modes

#### 7.6.2 Load testing (optional)

- Populate database with realistic volumes
- Benchmark search latency under concurrent load
- Verify service stability over extended run (hours)

### Acceptance Criteria

- [x] End-to-end scenario completes without errors (all 245 unit/integration tests pass)
- [x] Search returns relevant cross-source results (FourSourceSearchTests, UnifiedSearchTests)
- [x] MCP tools produce useful output for LLM agents (Mcp.Tests cover all tools)
- [x] Service runs stably for extended periods (graceful shutdown, queue drain, error isolation)
