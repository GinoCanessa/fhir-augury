# FHIR Augury — Architecture

## Solution Structure

```
fhir-augury.slnx
├── src/
│   ├── FhirAugury.Models/           # Shared data models, enums, constants
│   ├── FhirAugury.Database/         # SQLite schema, generated CRUD, FTS, BM25
│   ├── FhirAugury.Sources.Zulip/    # Zulip ingestion (via zulip-cs-lib)
│   ├── FhirAugury.Sources.Jira/     # Jira ingestion (REST API)
│   ├── FhirAugury.Sources.Confluence/ # Confluence ingestion (REST API)
│   ├── FhirAugury.Sources.GitHub/   # GitHub ingestion (REST API / GraphQL)
│   ├── FhirAugury.Indexing/         # FTS5 indexing, BM25 scoring, cross-ref
│   ├── FhirAugury.Service/          # Long-running background service + API
│   ├── FhirAugury.Cli/              # CLI application
│   └── FhirAugury.Mcp/              # MCP server
└── tests/
    ├── FhirAugury.Database.Tests/
    ├── FhirAugury.Sources.Tests/
    ├── FhirAugury.Indexing.Tests/
    └── FhirAugury.Integration.Tests/
```

## Layer Diagram

```
┌──────────────────────────────────────────────────────┐
│                   User Interfaces                    │
│  ┌──────────────┐  ┌──────────────┐                  │
│  │  CLI (Cli)   │  │  MCP (Mcp)   │                  │
│  └──────┬───────┘  └──────┬───────┘                  │
│         │                 │                          │
├─────────┼─────────────────┼──────────────────────────┤
│         └────────┬────────┘                          │
│                  ▼                                   │
│  ┌──────────────────────────────┐                    │
│  │   Background Service (API)  │  ← HTTP + queues   │
│  │   - Ingestion scheduler     │                    │
│  │   - On-demand submit API    │                    │
│  │   - Index update pipeline   │                    │
│  └──────────────┬──────────────┘                    │
│                 │                                   │
├─────────────────┼───────────────────────────────────┤
│                 ▼                                   │
│  ┌──────────────────────────────┐                    │
│  │       Indexing Layer         │                    │
│  │  - FTS5 population          │                    │
│  │  - BM25 keyword extraction  │                    │
│  │  - Cross-source linking     │                    │
│  └──────────────┬──────────────┘                    │
│                 │                                   │
├─────────────────┼───────────────────────────────────┤
│                 ▼                                   │
│  ┌─────────┬─────────┬──────────┬────────┐          │
│  │  Zulip  │  Jira   │Confluence│ GitHub │  Sources │
│  │ Source  │ Source  │  Source  │ Source │          │
│  └────┬────┴────┬────┴────┬─────┴───┬────┘          │
│       │         │         │         │               │
├───────┼─────────┼─────────┼─────────┼───────────────┤
│       ▼         ▼         ▼         ▼               │
│  ┌──────────────────────────────────────┐            │
│  │   Database Layer (SQLite + FTS5)    │            │
│  │   - Source-generated CRUD           │            │
│  │   - cslightdbgen.sqlitegen          │            │
│  │   - Microsoft.Data.Sqlite           │            │
│  └─────────────────────────────────────┘            │
└──────────────────────────────────────────────────────┘
```

## Key Design Decisions

### 1. Source-Generated Database Access

Use `cslightdbgen.sqlitegen` to generate all SQLite CRUD code at compile time
from decorated `partial record class` types. This gives us:

- Zero-reflection, AOT-compatible data access
- Compile-time schema validation
- Strongly typed queries with no raw SQL strings in business logic
- Performance superior to EF Core and Dapper

```csharp
[LdgSQLiteTable("zulip_messages")]
public partial record class ZulipMessageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required int StreamId { get; set; }
    public required string StreamName { get; set; }
    public required string Topic { get; set; }
    public required string SenderName { get; set; }
    public required string Content { get; set; }
    public required long Timestamp { get; set; }
    // ... generated: CreateTable, Insert, SelectList, etc.
}
```

### 2. Single Database, Multiple Tables

All sources share a single SQLite database file. This enables:

- Cross-source JOINs (e.g., find Zulip messages mentioning a Jira key)
- Single backup/copy for the entire knowledge base
- Consistent FTS5 configuration across sources
- Simpler operational model

The database will contain table groups prefixed by source:
- `zulip_*` — streams, messages, message FTS
- `jira_*` — issues, comments, issue FTS
- `confluence_*` — spaces, pages, page FTS
- `github_*` — repos, issues, PRs, comments, PR FTS
- `xref_*` — cross-reference linking tables
- `index_*` — keyword corpus, BM25 scores

### 3. Source Abstraction

Each data source implements a common interface:

```csharp
public interface IDataSource
{
    string SourceName { get; }

    /// Full download of all available data.
    Task<IngestionResult> DownloadAllAsync(
        IngestionOptions options,
        CancellationToken ct);

    /// Incremental update since a given timestamp.
    Task<IngestionResult> DownloadIncrementalAsync(
        DateTimeOffset since,
        IngestionOptions options,
        CancellationToken ct);

    /// Ingest a single item by identifier (for on-demand submission).
    Task<IngestionResult> IngestItemAsync(
        string identifier,
        IngestionOptions options,
        CancellationToken ct);
}
```

### 4. Long-Running Service with API

The `FhirAugury.Service` project runs as a .NET `BackgroundService` hosted in
an ASP.NET Minimal API application. It provides:

- **Scheduled ingestion** — configurable polling intervals per source
- **HTTP API** — endpoints to trigger ingestion, submit individual items,
  query ingestion status, and search
- **Queue-based processing** — ingestion requests are queued via
  `System.Threading.Channels` and processed by background workers
- **Live index updates** — FTS5 and BM25 indexes are updated incrementally
  as new data arrives

### 5. MCP Server as a Separate Host

The MCP server (`FhirAugury.Mcp`) is a separate process that opens the
database in read-only mode. It exposes search and retrieval tools to LLM
agents via the Model Context Protocol. It can run alongside the service
(which holds the write lock) because SQLite supports concurrent readers.

### 6. CLI as a Standalone Tool

The CLI (`FhirAugury.Cli`) can operate independently of the service for
batch operations (full downloads, index rebuilds, ad-hoc searches) or it
can communicate with the service's API for live operations.
