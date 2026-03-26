# FHIR Augury v2 — Architecture

## Solution Structure

```
fhir-augury.slnx
├── src/
│   ├── FhirAugury.Common/                   # Shared types, gRPC protos, utilities
│   │
│   ├── FhirAugury.Source.Jira/              # Jira source service (self-contained)
│   │   ├── Api/                             #   gRPC + HTTP endpoints
│   │   ├── Ingestion/                       #   Download, parse, normalize
│   │   ├── Cache/                           #   File-system response cache
│   │   ├── Database/                        #   SQLite schema, generated CRUD, FTS5
│   │   ├── Indexing/                        #   Internal FTS5, BM25, related-issue linking
│   │   └── Program.cs                       #   Service host
│   │
│   ├── FhirAugury.Source.Zulip/             # Zulip source service (self-contained)
│   │   ├── Api/
│   │   ├── Ingestion/
│   │   ├── Cache/
│   │   ├── Database/
│   │   ├── Indexing/
│   │   └── Program.cs
│   │
│   ├── FhirAugury.Source.Confluence/         # Confluence source service (self-contained)
│   │   ├── Api/
│   │   ├── Ingestion/
│   │   ├── Cache/
│   │   ├── Database/
│   │   ├── Indexing/
│   │   └── Program.cs
│   │
│   ├── FhirAugury.Source.GitHub/             # GitHub source service (self-contained)
│   │   ├── Api/
│   │   ├── Ingestion/
│   │   ├── Cache/
│   │   ├── Database/
│   │   ├── Indexing/
│   │   └── Program.cs
│   │
│   ├── FhirAugury.Orchestrator/             # Cross-referencing & query orchestrator
│   │   ├── Api/                             #   HTTP + gRPC endpoints (unified search, xref, etc.)
│   │   ├── CrossRef/                        #   Cross-reference linker
│   │   ├── Search/                          #   Unified search (fan-out to sources)
│   │   ├── Database/                        #   SQLite for xref index + orchestrator state
│   │   └── Program.cs
│   │
│   ├── FhirAugury.Mcp/                      # MCP server (talks to orchestrator + sources)
│   └── FhirAugury.Cli/                      # CLI (talks to orchestrator + sources)
│
├── protos/                                   # Shared gRPC proto definitions
│   ├── common.proto
│   ├── source_service.proto                  # Common source service contract
│   ├── jira.proto                            # Jira-specific messages
│   ├── zulip.proto                           # Zulip-specific messages
│   ├── confluence.proto                      # Confluence-specific messages
│   ├── github.proto                          # GitHub-specific messages
│   └── orchestrator.proto                    # Orchestrator contract
│
└── tests/
    ├── FhirAugury.Source.Jira.Tests/
    ├── FhirAugury.Source.Zulip.Tests/
    ├── FhirAugury.Source.Confluence.Tests/
    ├── FhirAugury.Source.GitHub.Tests/
    ├── FhirAugury.Orchestrator.Tests/
    └── FhirAugury.Integration.Tests/
```

### Project Summary

| Project | Purpose |
|---------|---------|
| `FhirAugury.Common` | Shared types, gRPC proto definitions, utilities |
| `FhirAugury.Source.Jira` | Jira source service (self-contained) |
| `FhirAugury.Source.Zulip` | Zulip source service (self-contained) |
| `FhirAugury.Source.Confluence` | Confluence source service (self-contained) |
| `FhirAugury.Source.GitHub` | GitHub source service (self-contained) |
| `FhirAugury.Orchestrator` | Cross-referencing & query orchestrator |
| `FhirAugury.Mcp` | MCP server (connects to orchestrator + sources via gRPC) |
| `FhirAugury.Cli` | CLI (connects to orchestrator + sources via gRPC/HTTP) |

---

## Service Topology

```
                         ┌─────────────────────────────┐
                         │      External Clients        │
                         │  (CLI, MCP, HTTP consumers)  │
                         └──────────────┬──────────────┘
                                        │
                                        ▼
                         ┌─────────────────────────────┐
                         │    Orchestrator Service      │
                         │                             │
                         │  Port: 5150 (HTTP)          │
                         │  Port: 5151 (gRPC)          │
                         │                             │
                         │  Endpoints:                 │
                         │  • /api/v1/search           │
                         │  • /api/v1/related          │
                         │  • /api/v1/xref             │
                         │  • /api/v1/query            │
                         │  • /api/v1/stats            │
                         │  • /api/v1/ingest/trigger   │
                         └──────┬───┬───┬───┬──────────┘
                                │   │   │   │
             ┌──────────────────┘   │   │   └──────────────────┐
             │          ┌───────────┘   └───────────┐          │
             ▼          ▼                           ▼          ▼
   ┌─────────────┐ ┌─────────────┐ ┌───────────────────┐ ┌─────────────┐
   │Jira Service │ │Zulip Service│ │Confluence Service │ │GitHub       │
   │             │ │             │ │                   │ │  Service    │
   │Port: 5160   │ │Port: 5170   │ │Port: 5180         │ │Port: 5190   │
   │gRPC: 5161   │ │gRPC: 5171   │ │gRPC: 5181         │ │gRPC: 5191   │
   │             │ │             │ │                   │ │             │
   │┌───────────┐│ │┌───────────┐│ │┌─────────────────┐│ │┌───────────┐│
   ││ Cache Dir ││ ││ Cache Dir ││ ││   Cache Dir     ││ ││ Cache Dir ││
   │└───────────┘│ │└───────────┘│ │└─────────────────┘│ │└───────────┘│
   │┌───────────┐│ │┌───────────┐│ │┌─────────────────┐│ │┌───────────┐│
   ││ SQLite DB ││ ││ SQLite DB ││ ││   SQLite DB     ││ ││ SQLite DB ││
   │└───────────┘│ │└───────────┘│ │└─────────────────┘│ │└───────────┘│
   └─────────────┘ └─────────────┘ └───────────────────┘ └─────────────┘
```

---

## Communication Patterns

### Inter-Service: gRPC

Source services expose a common gRPC `SourceService` interface plus
source-specific extensions. The orchestrator calls source services via gRPC
to fetch data, run searches, and retrieve content.

```
Orchestrator ──gRPC──► Jira Service
             ──gRPC──► Zulip Service
             ──gRPC──► Confluence Service
             ──gRPC──► GitHub Service
```

**Why gRPC over HTTP?**
- Strongly typed contracts via proto files
- Efficient binary serialization (Protobuf)
- Streaming support for large result sets (e.g., paginating through all Jira
  issues matching a query)
- Built-in deadline/cancellation propagation
- Generated client/server code in C#

### External: HTTP/JSON, MCP, and CLI

Every external-facing capability is available through all three consumer
interfaces to allow flexibility in deployment:

| Interface | Transport | Primary Consumers |
|-----------|-----------|-------------------|
| **HTTP/JSON API** | REST over HTTP | Scripts, browser, external integrations |
| **MCP Server** | stdio (Model Context Protocol) | LLM agents (e.g., Copilot, Claude) |
| **CLI** | gRPC or HTTP to orchestrator | Human operators, shell scripts |

All three interfaces delegate to the same underlying gRPC services, ensuring
feature parity. Any capability exposed through one interface must be exposed
through all three. Source services also expose HTTP endpoints for direct
access during development or standalone use.

### Service Discovery

For local deployment (the primary use case), services are configured with
static addresses in `appsettings.json` or environment variables:

```json
{
  "Services": {
    "Jira":       { "GrpcAddress": "http://localhost:5161" },
    "Zulip":      { "GrpcAddress": "http://localhost:5171" },
    "Confluence":  { "GrpcAddress": "http://localhost:5181" },
    "GitHub":     { "GrpcAddress": "http://localhost:5191" }
  }
}
```

For Docker Compose, services are addressed by container name:

```json
{
  "Services": {
    "Jira":       { "GrpcAddress": "http://source-jira:5161" },
    "Zulip":      { "GrpcAddress": "http://source-zulip:5171" },
    "Confluence":  { "GrpcAddress": "http://source-confluence:5181" },
    "GitHub":     { "GrpcAddress": "http://source-github:5191" }
  }
}
```

---

## Deployment Models

### 1. Docker Compose (Recommended for Production)

All services run as separate containers orchestrated by `docker-compose.yml`.
Cache and database directories are mounted as volumes for persistence.

```yaml
services:
  source-jira:
    build: { context: ., dockerfile: src/FhirAugury.Source.Jira/Dockerfile }
    volumes:
      - jira-cache:/app/cache
      - jira-data:/app/data
    ports: ["5160:5160", "5161:5161"]

  source-zulip:
    build: { context: ., dockerfile: src/FhirAugury.Source.Zulip/Dockerfile }
    volumes:
      - zulip-cache:/app/cache
      - zulip-data:/app/data
    ports: ["5170:5170", "5171:5171"]

  source-confluence:
    build: { context: ., dockerfile: src/FhirAugury.Source.Confluence/Dockerfile }
    volumes:
      - confluence-cache:/app/cache
      - confluence-data:/app/data
    ports: ["5180:5180", "5181:5181"]

  source-github:
    build: { context: ., dockerfile: src/FhirAugury.Source.GitHub/Dockerfile }
    volumes:
      - github-cache:/app/cache
      - github-data:/app/data
    ports: ["5190:5190", "5191:5191"]

  orchestrator:
    build: { context: ., dockerfile: src/FhirAugury.Orchestrator/Dockerfile }
    depends_on: [source-jira, source-zulip, source-confluence, source-github]
    volumes:
      - orchestrator-data:/app/data
    ports: ["5150:5150", "5151:5151"]
```

### 2. Local Development (All-in-One Process)

For development, all services can be hosted in a single process using the
.NET hosting abstractions. Each service registers its gRPC services and
Kestrel endpoints on different ports, sharing the same process for simplified
debugging. Multiple Kestrel endpoints are configured so each service listens
on its own port pair (HTTP + gRPC), matching the production port layout.

```csharp
// Development host that runs all services in-process
// Each service gets its own Kestrel endpoint pair (HTTP + gRPC)
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Orchestrator
    kestrel.ListenLocalhost(5150); // HTTP
    kestrel.ListenLocalhost(5151, o => o.Protocols = HttpProtocols.Http2); // gRPC

    // Jira
    kestrel.ListenLocalhost(5160); // HTTP
    kestrel.ListenLocalhost(5161, o => o.Protocols = HttpProtocols.Http2); // gRPC

    // Zulip
    kestrel.ListenLocalhost(5170); // HTTP
    kestrel.ListenLocalhost(5171, o => o.Protocols = HttpProtocols.Http2); // gRPC

    // Confluence
    kestrel.ListenLocalhost(5180); // HTTP
    kestrel.ListenLocalhost(5181, o => o.Protocols = HttpProtocols.Http2); // gRPC

    // GitHub
    kestrel.ListenLocalhost(5190); // HTTP
    kestrel.ListenLocalhost(5191, o => o.Protocols = HttpProtocols.Http2); // gRPC
});

builder.Services.AddJiraSource(config);
builder.Services.AddZulipSource(config);
builder.Services.AddConfluenceSource(config);
builder.Services.AddGitHubSource(config);
builder.Services.AddOrchestrator(config);
```

### 3. Standalone Source Service

A single source service (e.g., Jira) can run standalone for teams that only
need one data source. The service exposes its own HTTP API directly and
doesn't require the orchestrator. Cross-source features are unavailable but
single-source search, retrieval, and internal referencing work fully.

### 4. Subset Deployment

Run only the source services you need plus the orchestrator. For example,
a team working on FHIR ballot feedback might run only Jira + Zulip +
Orchestrator, skipping Confluence and GitHub entirely.

---

## Data Ownership

| Data | Owner | Storage |
|------|-------|---------|
| Jira issues, comments, custom fields | Jira Service | `jira.db` + `cache/jira/` |
| Zulip streams, messages, topics | Zulip Service | `zulip.db` + `cache/zulip/` |
| Confluence spaces, pages, comments | Confluence Service | `confluence.db` + `cache/confluence/` |
| GitHub repos, issues, PRs, comments | GitHub Service | `github.db` + `cache/github/` |
| Cross-reference links | Orchestrator | `orchestrator.db` |
| Unified search index | Orchestrator | `orchestrator.db` (derived) |
| Ingestion schedules (per source) | Each source service | Source service's DB |
| Sync state (per source) | Each source service | Source service's DB |

### Data Flow

```
Remote API ──► Source Service ──► Cache (filesystem)
                    │
                    ▼
              Source SQLite DB
              (normalized + FTS5 indexed)
                    │
                    ▼
              Source gRPC API
                    │
                    ▼
              Orchestrator ──► Orchestrator SQLite DB
                                (xref links, unified index cache)
                    │
                    ▼
              External Clients (CLI, MCP, HTTP)
```
