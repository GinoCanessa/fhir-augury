# Phase 4: Orchestrator, MCP & CLI

**Goal:** Build the orchestrator service and all consumer interfaces with the
first two source services (Jira + Zulip). This is the key integration
milestone — if cross-source operations work with two services, adding the
remaining sources in Phase 5 is straightforward.

**Proposal references:**
[04-orchestrator-service](../../proposal/v2/04-orchestrator-service.md),
[05-api-contracts](../../proposal/v2/05-api-contracts.md) (`orchestrator.proto`),
[07-mcp-cli](../../proposal/v2/07-mcp-cli.md)

**Depends on:** Phases 2 and 3

---

## 4.1 — Orchestrator Project Setup

### 4.1.1 — Create `FhirAugury.Orchestrator` project

Create `src/FhirAugury.Orchestrator/` as a standalone ASP.NET web application:

```
FhirAugury.Orchestrator/
├── Api/
│   ├── OrchestratorGrpcService.cs
│   └── OrchestratorHttpApi.cs
├── CrossRef/
│   ├── CrossRefLinker.cs          # Text-based cross-reference scanner
│   ├── CrossRefPatterns.cs        # Regex patterns for identifiers
│   └── StructuralLinker.cs        # JIRA-Spec-Artifacts structural links
├── Search/
│   ├── UnifiedSearchService.cs    # Fan-out search + merge
│   ├── ScoreNormalizer.cs         # Min-max normalization across sources
│   ├── CrossRefBooster.cs         # Cross-reference score boost
│   └── FreshnessDecay.cs          # Per-source freshness decay
├── Related/
│   └── RelatedItemFinder.cs       # Multi-signal related item discovery
├── Routing/
│   └── SourceRouter.cs            # Route proxied calls to source services
├── Health/
│   └── ServiceHealthMonitor.cs    # Monitor source service health
├── Database/
│   ├── OrchestratorDatabase.cs    # Cross-ref index + scan state
│   └── Records/
│       ├── CrossRefLinkRecord.cs
│       └── XrefScanStateRecord.cs
├── Workers/
│   ├── XRefScanWorker.cs          # Background cross-reference scanning
│   └── HealthCheckWorker.cs       # Periodic source health polling
├── Configuration/
│   └── OrchestratorOptions.cs
├── Program.cs
├── appsettings.json
└── FhirAugury.Orchestrator.csproj
```

**Dependencies:**
- `FhirAugury.Common` (project reference)
- `Microsoft.Data.Sqlite`
- `cslightdbgen.sqlitegen`
- `Grpc.AspNetCore`
- `Grpc.Net.Client`

### 4.1.2 — Configuration schema

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": { "Http": 5150, "Grpc": 5151 },
    "Services": {
      "Jira":  { "GrpcAddress": "http://localhost:5161", "Enabled": true },
      "Zulip": { "GrpcAddress": "http://localhost:5171", "Enabled": true }
    },
    "CrossRef": {
      "ScanIntervalMinutes": 30,
      "ValidateTargets": true
    },
    "Search": {
      "DefaultLimit": 20,
      "MaxLimit": 100,
      "CrossRefBoostFactor": 0.5,
      "FreshnessWeights": { "jira": 0.5, "zulip": 2.0 }
    },
    "Related": {
      "ExplicitXrefWeight": 10.0,
      "ReverseXrefWeight": 8.0,
      "Bm25SimilarityWeight": 3.0,
      "SharedMetadataWeight": 2.0,
      "DefaultLimit": 20,
      "MaxKeyTerms": 15
    }
  }
}
```

---

## 4.2 — Cross-Reference Linking

### 4.2.1 — Implement text-based cross-reference scanner

The `CrossRefLinker` periodically scans text content from source services
for identifiers belonging to other sources.

**Process:**

1. Call each source service's `StreamSearchableText` gRPC method
2. Only process items added/updated since last scan (tracked in
   `xref_scan_state`)
3. Apply regex patterns to extract cross-references:

   | Pattern | Target Source | Example |
   |---------|--------------|---------|
   | `\bFHIR-\d+\b` | Jira | "See FHIR-43499" |
   | `https?://jira\.hl7\.org/browse/(FHIR-\d+)` | Jira (URL) | Full Jira link |
   | `https?://chat\.fhir\.org/#narrow/stream/(\d+)` | Zulip (URL) | Zulip link |
   | `https?://confluence\.hl7\.org/.*/(\d+)` | Confluence (URL) | Confluence link |
   | `https?://github\.com/HL7/[^/]+/(?:issues\|pull)/(\d+)` | GitHub (URL) | GitHub link |
   | `HL7/[a-zA-Z0-9_-]+#\d+` | GitHub (short ref) | "HL7/fhir#823" |

4. Optionally validate references by calling target source's `GetItem`
5. Store validated links in `xref_links` table
6. Update `xref_scan_state` with latest cursor per source

Adapted from v1's `CrossRefLinker` in `FhirAugury.Indexing`, but now operates
over gRPC streams instead of direct database queries.

### 4.2.2 — Implement structural linking

Use JIRA-Spec-Artifacts data (fetched from Jira service via
`ListSpecArtifacts`) to create structural cross-reference links between
Jira specifications and GitHub repositories, without text scanning.

### 4.2.3 — Implement cross-reference database schema

```csharp
[LdgSQLiteTable("xref_links")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId))]
public partial record class CrossRefLinkRecord
{
    [LdgSQLiteKey] public required int Id { get; set; }
    public required string SourceType { get; set; }   // "zulip", "jira", etc.
    public required string SourceId { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string LinkType { get; set; }     // "mentions", "references", "url_link"
    public required string? Context { get; set; }     // surrounding text snippet
    public required DateTimeOffset DiscoveredAt { get; set; }
}
```

---

## 4.3 — Unified Search

### 4.3.1 — Implement fan-out search

Dispatch search queries to all enabled source services in parallel via
gRPC `Search` method. Collect results using `Task.WhenAll`.

Handle partial failures: if a source service is unavailable, return results
from healthy sources with a warning in the `SearchResponse.warnings` field.

### 4.3.2 — Implement score normalization

FTS5/BM25 scores from different corpora are not directly comparable.
Apply min-max normalization within each source group, then merge.

Adapted from v1's `ScoreNormalizer` and `FtsSearchService.SearchAll()`.

### 4.3.3 — Implement cross-reference boost

Items with cross-references get a configurable score boost:

```
boosted_score = normalized_score × (1 + xref_boost × log(1 + xref_count))
```

### 4.3.4 — Implement freshness decay

Per-source freshness decay applied at query time:

```
age_days = (now - item.updated_at).TotalDays
decay = 1 / (1 + freshness_weight × (age_days / 365.0)²)
final_score = boosted_score × decay
```

Default weights: Zulip=2.0, Jira=0.5 (Confluence=0.1, GitHub=1.0 added in
Phase 5).

---

## 4.4 — Related Item Discovery

### 4.4.1 — Implement multi-signal related item finder

Given a seed item, find related items across all sources using four signals:

1. **Explicit cross-references** — items mentioning the seed (from
   `xref_links`). Weight: 10.0
2. **Reverse cross-references** — items the seed mentions. Weight: 8.0
3. **BM25 similarity** — extract key terms from seed, fan out as search
   queries to source services. Weight: 3.0
4. **Shared metadata** — same work group, specification, labels. Weight: 2.0

Merge all signals, deduplicate, apply weights, return top-N.

Adapted from v1's `SimilaritySearchService` but now operates across services
via gRPC instead of direct database access.

---

## 4.5 — Routing & Proxying

### 4.5.1 — Implement hybrid routing model

The orchestrator uses a **hybrid routing model**:

1. **Cross-source operations** (handled directly):
   `UnifiedSearch`, `FindRelated`, `GetCrossReferences`, `TriggerSync`,
   `GetServicesStatus`, `TriggerXRefScan`

2. **Proxied common operations** (routed to source services):
   `GetItem`, `GetSnapshot`, `GetContent` — routed based on `source` field

3. **Source-specific queries** (direct client access):
   `QueryIssues`, `GetThread`, `QueryByArtifact`, etc. — clients connect
   directly to source services

### 4.5.2 — Implement `SourceRouter`

Routes proxied calls to the appropriate source service gRPC client based on
the `source` field in the request. Handles:

- Source name resolution ("jira" → Jira gRPC client)
- Unknown source error handling
- Disabled source handling

---

## 4.6 — Ingestion Coordination

### 4.6.1 — Implement `TriggerSync`

Call each source service's `TriggerIngestion` gRPC method. Support sync
types: `incremental`, `full`, `rebuild`, `xref-scan`.

### 4.6.2 — Implement post-ingestion callback

Source services call the orchestrator's `NotifyIngestionComplete` gRPC method
when ingestion finishes. On receipt, the orchestrator triggers a cross-reference
scan for the newly ingested items.

### 4.6.3 — Implement ingestion status polling

While ingestion is running, the orchestrator periodically polls
`GetIngestionStatus` on the source service (default: every 30 seconds)
to detect failures.

---

## 4.7 — Service Health Monitoring

### 4.7.1 — Implement health monitoring

Background worker that periodically checks each source service's
`HealthCheck` gRPC method. Maintains aggregate health status exposed via
`GET /api/v1/services`.

---

## 4.8 — Orchestrator API Layer

### 4.8.1 — Implement orchestrator gRPC service

All RPCs from `orchestrator.proto`:

| RPC | Implementation |
|-----|---------------|
| `UnifiedSearch` | Fan-out to sources, normalize, boost, decay |
| `FindRelated` | Multi-signal related item discovery |
| `GetCrossReferences` | Query `xref_links` by source/id |
| `GetItem` | Proxy to source service |
| `GetSnapshot` | Proxy to source service |
| `GetContent` | Proxy to source service |
| `TriggerSync` | Trigger ingestion on source services |
| `GetServicesStatus` | Aggregate health and stats |
| `TriggerXRefScan` | Start cross-reference scan |
| `NotifyIngestionComplete` | Handle post-ingestion callback |

### 4.8.2 — Implement orchestrator HTTP API

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/v1/search?q={query}&sources={csv}&limit={n}` | Unified search |
| `GET` | `/api/v1/related/{source}/{id}?limit={n}` | Find related items |
| `GET` | `/api/v1/xref/{source}/{id}?direction={dir}` | Cross-references |
| `GET` | `/api/v1/items/{source}/{id}` | Get item (proxied) |
| `GET` | `/api/v1/items/{source}/{id}/snapshot` | Snapshot (proxied) |
| `GET` | `/api/v1/items/{source}/{id}/content?format={fmt}` | Content (proxied) |
| `POST` | `/api/v1/ingest/trigger` | Trigger sync |
| `GET` | `/api/v1/services` | Service health |
| `GET` | `/api/v1/stats` | Aggregate statistics |
| `POST` | `/api/v1/jira/query` | Jira structured query (proxied) |
| `POST` | `/api/v1/zulip/query` | Zulip structured query (proxied) |

---

## 4.9 — MCP Server

### 4.9.1 — Update `FhirAugury.Mcp`

Rewrite the MCP server to communicate via gRPC instead of direct database
access. The MCP server connects to the orchestrator for cross-source
operations and to source services for source-specific operations.

**Hosting:**
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddGrpcClient<OrchestratorService.OrchestratorServiceClient>(
    opts => opts.Address = new Uri(config.OrchestratorGrpcAddress));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
```

### 4.9.2 — Implement unified MCP tools

| Tool | Orchestrator RPC |
|------|-----------------|
| `search` | `UnifiedSearch` |
| `find_related` | `FindRelated` |
| `get_cross_references` | `GetCrossReferences` |
| `get_stats` | `GetServicesStatus` |
| `trigger_sync` | `TriggerSync` |

### 4.9.3 — Implement Jira MCP tools

| Tool | Source / RPC |
|------|-------------|
| `search_jira` | Jira `Search` |
| `get_jira_issue` | Orchestrator `GetItem` (proxied) |
| `get_jira_comments` | Jira `GetIssueComments` |
| `query_jira_issues` | Jira `QueryIssues` |
| `snapshot_jira_issue` | Orchestrator `GetSnapshot` (proxied) |
| `list_jira_issues` | Jira `ListItems` |

### 4.9.4 — Implement Zulip MCP tools

| Tool | Source / RPC |
|------|-------------|
| `search_zulip` | Zulip `Search` |
| `get_zulip_thread` | Zulip `GetThread` |
| `query_zulip_messages` | Zulip `QueryMessages` |
| `list_zulip_streams` | Zulip `ListStreams` |
| `list_zulip_topics` | Zulip `ListTopics` |
| `snapshot_zulip_thread` | Zulip `GetThreadSnapshot` |

### 4.9.5 — Implement direct source access mode

For standalone deployments (no orchestrator), the MCP server can connect
directly to a single source service:

```
--mode direct --source jira
```

In this mode, only single-source tools are available. Cross-source tools
are disabled.

---

## 4.10 — CLI

### 4.10.1 — Update `FhirAugury.Cli`

Rewrite the CLI to communicate via gRPC/HTTP instead of direct database
access. The CLI connects to the orchestrator for cross-source operations
and directly to source services for source-specific queries.

### 4.10.2 — Implement CLI commands

Full command tree (Jira + Zulip scope for Phase 4):

```
fhir-augury
├── search           # Unified search (orchestrator)
├── related          # Find related items (orchestrator)
├── get              # Get item details (orchestrator proxy)
├── snapshot         # Rich markdown snapshot (orchestrator proxy)
├── xref             # Cross-references (orchestrator)
├── ingest
│   ├── trigger      # Trigger sync (orchestrator)
│   ├── status       # Ingestion status
│   └── rebuild      # Rebuild from cache
├── list             # List items (source service)
├── query-jira       # Jira structured query (Jira service direct)
├── query-zulip      # Zulip structured query (Zulip service direct)
├── services
│   ├── status       # Service health (orchestrator)
│   ├── stats        # Aggregate stats (orchestrator)
│   └── xref-scan    # Trigger xref scan (orchestrator)
└── (global options)
    ├── --orchestrator   # Orchestrator URL
    ├── --format         # Output: table | json | markdown
    └── --verbose
```

### 4.10.3 — Implement output formatters

All commands support three output formats:
- **table** (default) — human-readable tabular output
- **json** — machine-readable JSON
- **markdown** — formatted markdown for copy/paste

---

## 4.11 — Interface Parity Validation

### 4.11.1 — Verify triple interface parity

Every capability must be available through all three interfaces (HTTP, MCP,
CLI). Create a test or checklist that verifies parity for each capability
added in this phase. Reference the parity matrix in
[07-mcp-cli](../../proposal/v2/07-mcp-cli.md).

---

## 4.12 — Tests

### 4.12.1 — Orchestrator unit tests

- Cross-reference extraction (regex patterns)
- Score normalization (min-max across sources)
- Cross-reference boost calculation
- Freshness decay calculation
- Related item signal merging and deduplication
- Source routing logic

### 4.12.2 — Orchestrator integration tests

- Unified search across Jira and Zulip (mock gRPC services)
- Cross-reference scan with mock source data
- Post-ingestion callback triggers xref scan
- Partial results when one source is unavailable
- Service health aggregation

### 4.12.3 — MCP tool tests

- Each MCP tool invocation (adapted from v1 MCP tests)
- Direct source access mode
- Error handling for unavailable services

### 4.12.4 — CLI tests

- Command parsing and option validation
- Output formatting (table, JSON, markdown)
- Error handling

---

## Phase 4 Verification

- [ ] Orchestrator starts on ports 5150/5151
- [ ] All three services (Jira, Zulip, Orchestrator) communicate via gRPC
- [ ] Unified search across Jira and Zulip returns merged, re-ranked results
- [ ] Cross-references link Jira issues ↔ Zulip messages bidirectionally
- [ ] `FindRelated` for a Jira issue discovers relevant Zulip threads
- [ ] Freshness decay correctly weights recent content higher
- [ ] Partial results returned when one source is unavailable
- [ ] MCP tools work end-to-end (orchestrator → source services)
- [ ] CLI commands work against the service architecture
- [ ] All three interfaces (HTTP, MCP, CLI) expose the same capabilities
- [ ] `TriggerSync` triggers ingestion across sources
- [ ] Post-ingestion callback triggers cross-reference scan
- [ ] All tests pass
