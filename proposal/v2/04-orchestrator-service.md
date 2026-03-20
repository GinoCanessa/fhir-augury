# FHIR Augury v2 — Orchestrator Service

## Purpose

The orchestrator service is the "brain" of FHIR Augury v2. It does not ingest
or store source data itself — instead, it queries the four source services via
gRPC and provides higher-level capabilities that require correlating data across
multiple sources.

The orchestrator handles:

1. **Cross-reference linking** — scanning source data for mentions of identifiers
   from other sources (e.g., `FHIR-12345` in a Zulip message, a Confluence URL
   in a Jira issue)
2. **Unified search** — fanning out a search query to all source services and
   merging/ranking the results
3. **Related item discovery** — finding items across all sources that relate to
   a given item (combining cross-references with BM25 similarity)
4. **User query answering** — handling higher-level questions like "what's been
   discussed about FHIRPath normative readiness?" that span multiple sources

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                     Orchestrator Service                         │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │                  External API Layer                       │    │
│  │                                                          │    │
│  │  HTTP: /api/v1/search       — Unified full-text search   │    │
│  │  HTTP: /api/v1/related      — Cross-source related items │    │
│  │  HTTP: /api/v1/xref/{s}/{id}— Cross-references for item  │    │
│  │  HTTP: /api/v1/query        — Natural language query      │    │
│  │  HTTP: /api/v1/stats        — Aggregate statistics        │    │
│  │  HTTP: /api/v1/ingest/trigger— Trigger sync across sources│    │
│  │  HTTP: /api/v1/services     — Health/status of sources    │    │
│  │                                                          │    │
│  │  gRPC: OrchestratorService  — Same capabilities via gRPC │    │
│  └──────────────────────┬───────────────────────────────────┘    │
│                         │                                        │
│  ┌──────────────────────▼───────────────────────────────────┐    │
│  │                  Core Logic                               │    │
│  │                                                          │    │
│  │  ┌─────────────────┐  ┌──────────────────┐              │    │
│  │  │ Unified Search  │  │  Cross-Ref Linker │              │    │
│  │  │                 │  │                    │              │    │
│  │  │ Fan-out queries │  │ Scan source text   │              │    │
│  │  │ to all sources, │  │ for identifiers    │              │    │
│  │  │ merge + re-rank │  │ from other sources │              │    │
│  │  └────────┬────────┘  └─────────┬──────────┘              │    │
│  │           │                     │                        │    │
│  │  ┌────────▼─────────────────────▼────────────┐           │    │
│  │  │          Related Item Finder               │           │    │
│  │  │                                            │           │    │
│  │  │  Combine xrefs + BM25 similarity from     │           │    │
│  │  │  each source to find the most relevant     │           │    │
│  │  │  items across the entire knowledge base    │           │    │
│  │  └────────────────────┬───────────────────────┘           │    │
│  └───────────────────────┼──────────────────────────────────┘    │
│                          │                                       │
│  ┌───────────────────────▼──────────────────────────────────┐    │
│  │             Source Service Clients (gRPC)                  │    │
│  │                                                          │    │
│  │  JiraClient ──► Jira Service (port 5161)                 │    │
│  │  ZulipClient ──► Zulip Service (port 5171)               │    │
│  │  ConfluenceClient ──► Confluence Service (port 5181)     │    │
│  │  GitHubClient ──► GitHub Service (port 5191)             │    │
│  └──────────────────────────────────────────────────────────┘    │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │  Orchestrator SQLite Database (orchestrator.db)           │    │
│  │                                                          │    │
│  │  • xref_links — cross-source reference links             │    │
│  │  • xref_scan_state — tracking which items have been      │    │
│  │    scanned for cross-references                          │    │
│  │  • unified_search_cache — optional result caching        │    │
│  └──────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────┘
```

---

## Cross-Reference Linking

### How It Works

The orchestrator periodically (or on demand) scans text content from each
source service looking for identifiers belonging to other sources.

**Process:**

1. **Fetch scannable text** — The orchestrator calls each source service's
   `StreamSearchableText` RPC, which streams back all items' searchable text
   fields along with their identifiers. Only items that have been
   added/updated since the last scan are returned (tracked via
   `xref_scan_state`).

2. **Extract cross-references** — For each item's text, apply regex patterns
   to find identifiers from other sources:

   | Pattern | Target Source | Example |
   |---------|--------------|---------|
   | `\bFHIR-\d+\b` | Jira | "See FHIR-43499" |
   | `https?://jira\.hl7\.org/browse/(FHIR-\d+)` | Jira (URL) | Full Jira link |
   | `https?://chat\.fhir\.org/#narrow/stream/(\d+).*/topic/(.+?)(?:\s\|$)` | Zulip (URL) | Full Zulip link |
   | `https?://confluence\.hl7\.org/.*/(\d+)` | Confluence (URL) | Full Confluence link |
   | `https?://github\.com/HL7/[^/]+/(?:issues\|pull)/(\d+)` | GitHub (URL) | Full GitHub link |
   | `HL7/[a-zA-Z0-9_-]+#\d+` | GitHub (short ref) | "HL7/fhir#823" |

3. **Validate references** — Optionally verify that the target item exists by
   calling the target source service's `GetItem` RPC. This prevents phantom
   references.

4. **Store links** — Insert validated cross-references into `xref_links` table.

5. **Track scan progress** — Update `xref_scan_state` with the latest item
   timestamps per source, so the next scan only processes new/updated items.

### Cross-Reference Schema

```csharp
[LdgSQLiteTable("xref_links")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId))]
[LdgSQLiteIndex(nameof(TargetType), nameof(TargetId), nameof(SourceType))]
public partial record class CrossRefLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceType { get; set; }     // "zulip", "jira", etc.
    public required string SourceId { get; set; }       // message ID, issue key, etc.
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string LinkType { get; set; }       // "mentions", "references", "url_link"
    public required string? Context { get; set; }       // surrounding text snippet
    public required DateTimeOffset DiscoveredAt { get; set; }
}

[LdgSQLiteTable("xref_scan_state")]
public partial record class XrefScanStateRecord
{
    [LdgSQLiteKey]
    public required string SourceName { get; set; }
    public required DateTimeOffset LastScannedAt { get; set; }
    public required string? LastCursor { get; set; }
    public required int ItemsScanned { get; set; }
}
```

---

## Unified Search

### Fan-Out Strategy

When a user searches for "FHIRPath normative ballot", the orchestrator:

1. **Dispatches** the query to all (or filtered) source services in parallel
   via their `Search` gRPC method.
2. **Collects** ranked results from each source.
3. **Normalizes** scores across sources (FTS5/BM25 scores from different
   corpora are not directly comparable — use min-max scaling within each
   source, then merge).
4. **Enriches** results with cross-reference counts (items that are
   cross-referenced by many other items get a boost).
5. **Returns** a unified, re-ranked result set.

```csharp
public async Task<UnifiedSearchResponse> SearchAsync(
    string query,
    IReadOnlySet<string>? sources,
    int limit,
    CancellationToken ct)
{
    // Fan out to all enabled source services in parallel
    var tasks = new List<Task<SourceSearchResult>>();

    if (sources is null || sources.Contains("jira"))
        tasks.Add(_jiraClient.SearchAsync(query, limit, ct));
    if (sources is null || sources.Contains("zulip"))
        tasks.Add(_zulipClient.SearchAsync(query, limit, ct));
    if (sources is null || sources.Contains("confluence"))
        tasks.Add(_confluenceClient.SearchAsync(query, limit, ct));
    if (sources is null || sources.Contains("github"))
        tasks.Add(_githubClient.SearchAsync(query, limit, ct));

    var allResults = await Task.WhenAll(tasks);

    // Normalize scores across sources
    var merged = NormalizeAndMerge(allResults);

    // Boost items with cross-references
    var boosted = ApplyCrossRefBoost(merged);

    return new UnifiedSearchResponse
    {
        Query = query,
        TotalResults = boosted.Count,
        Results = boosted.Take(limit).ToList()
    };
}
```

### Cross-Reference Boost

Items that appear in cross-references get a configurable score boost:

```
boosted_score = normalized_score × (1 + xref_boost × log(1 + xref_count))
```

Where `xref_count` is how many items from other sources reference this item.
This naturally surfaces "hub" items that are heavily discussed across platforms.

---

## Related Item Discovery

The "find related" query is the orchestrator's most powerful feature. Given a
seed item (e.g., Jira issue FHIR-43499), it finds related items across all
sources using multiple signals:

### Signal Sources

1. **Explicit cross-references** — Items that directly mention the seed item's
   identifier (from `xref_links` table). Highest confidence.

2. **Reverse cross-references** — Items that the seed item mentions. For
   example, if a Zulip message mentions FHIR-43499, both the Zulip message
   and FHIR-43499 are related to each other.

3. **BM25 similarity within each source** — Ask each source service for items
   similar to the seed item's content. The orchestrator fetches the seed item's
   content, extracts key terms, and passes them as a query to each source
   service's `Search` method.

4. **Shared metadata** — Items sharing the same Jira work group, specification,
   labels, or Confluence space as the seed item.

### Orchestration Flow

```
1. Fetch seed item's full content from its source service
2. Query xref_links for explicit references to/from seed item
3. Extract key terms from seed item content
4. Fan out BM25 similarity queries to all source services
5. Merge all signals with configurable weights:
   - Explicit xref:    weight = 10.0
   - Reverse xref:     weight = 8.0
   - BM25 similarity:  weight = 3.0
   - Shared metadata:  weight = 2.0
6. Deduplicate and return top-N results
```

---

## Ingestion Coordination

The orchestrator can trigger ingestion across source services:

### Trigger Sync All

```
POST /api/v1/ingest/trigger
POST /api/v1/ingest/trigger?sources=jira,zulip
```

The orchestrator calls each source service's `TriggerIngestion` gRPC method,
which initiates an incremental sync within that service. The orchestrator
tracks the status of each sync and can report aggregate progress.

### Post-Ingestion Cross-Reference Scan

After a source service completes an ingestion run (notified via gRPC
streaming or polling), the orchestrator triggers a cross-reference scan
for the newly ingested items. This ensures cross-references are kept
up-to-date as new data arrives.

---

## Service Health Monitoring

The orchestrator monitors the health of all source services and exposes
aggregate status:

```
GET /api/v1/services
```

```json
{
  "services": [
    {
      "name": "jira",
      "status": "healthy",
      "grpcAddress": "http://localhost:5161",
      "lastSyncAt": "2026-03-18T14:00:00Z",
      "itemCount": 48234,
      "dbSizeBytes": 524288000
    },
    {
      "name": "zulip",
      "status": "healthy",
      "grpcAddress": "http://localhost:5171",
      "lastSyncAt": "2026-03-18T10:00:00Z",
      "itemCount": 1023456,
      "dbSizeBytes": 2147483648
    },
    {
      "name": "confluence",
      "status": "degraded",
      "grpcAddress": "http://localhost:5181",
      "lastSyncAt": "2026-03-17T08:00:00Z",
      "itemCount": 3456,
      "dbSizeBytes": 209715200,
      "lastError": "Auth token expired"
    },
    {
      "name": "github",
      "status": "healthy",
      "grpcAddress": "http://localhost:5191",
      "lastSyncAt": "2026-03-18T12:00:00Z",
      "itemCount": 2100,
      "dbSizeBytes": 104857600
    }
  ],
  "crossRefLinks": 145678,
  "lastXrefScanAt": "2026-03-18T14:30:00Z"
}
```

---

## Configuration

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": { "Http": 5150, "Grpc": 5151 },

    "Services": {
      "Jira":       { "GrpcAddress": "http://localhost:5161", "Enabled": true },
      "Zulip":      { "GrpcAddress": "http://localhost:5171", "Enabled": true },
      "Confluence":  { "GrpcAddress": "http://localhost:5181", "Enabled": true },
      "GitHub":     { "GrpcAddress": "http://localhost:5191", "Enabled": true }
    },

    "CrossRef": {
      "ScanIntervalMinutes": 30,
      "ValidateTargets": true,
      "Patterns": {
        "JiraKey": "\\bFHIR-\\d+\\b",
        "JiraUrl": "https?://jira\\.hl7\\.org/browse/(FHIR-\\d+)",
        "ZulipUrl": "https?://chat\\.fhir\\.org/#narrow/stream/(\\d+)",
        "ConfluenceUrl": "https?://confluence\\.hl7\\.org/.+/(\\d+)",
        "GitHubUrl": "https?://github\\.com/HL7/[^/]+/(?:issues|pull)/(\\d+)"
      }
    },

    "Search": {
      "DefaultLimit": 20,
      "MaxLimit": 100,
      "CrossRefBoostFactor": 0.5,
      "ScoreNormalization": "min-max"
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
