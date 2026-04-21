# FHIR Augury

A unified knowledge platform for searching across HL7 FHIR community data
sources. FHIR Augury downloads, indexes, and cross-references content from
Jira, Zulip, Confluence, and GitHub with full-text search powered by SQLite
FTS5 and BM25 relevance scoring.

## Architecture (v2)

FHIR Augury v2 uses a microservices architecture where each data source runs as
an independent HTTP service with its own database and cache. The Orchestrator
aggregates results and manages cross-references across sources.

```
┌──────────────────────────────────────────────────────────────┐
│  Clients                                                     │
│  ┌─────────┐  ┌───────────┐  ┌──────────────────────────┐   │
│  │   CLI   │  │ MCP Server│  │   HTTP API Clients       │   │
│  └────┬────┘  └─────┬─────┘  └────────────┬─────────────┘   │
│       └──────────────┼─────────────────────┘                 │
│                      ▼                                       │
│  ┌───────────────────────────────────────────┐               │
│  │          Orchestrator (:5150)             │               │
│  │  Unified search · Cross-references ·     │               │
│  │  Related items · Source aggregation       │               │
│  └───┬──────────┬──────────┬──────────┬─────┘               │
│      │          │          │          │         HTTP         │
│  ┌───▼───┐  ┌──▼───┐  ┌──▼────────┐ ┌▼──────┐              │
│  │ Jira  │  │Zulip │  │Confluence │ │GitHub │              │
│  │:5160  │  │:5170 │  │  :5180    │ │:5190  │              │
│  └───────┘  └──────┘  └──────────┘ └───────┘              │
│    Each service: SQLite + FTS5 + Cache + HTTP API           │
└──────────────────────────────────────────────────────────────┘
```

## Quick Start

### Docker Compose (recommended for production)

```bash
# Start all services
docker compose --profile full up -d

# Check health
curl http://localhost:5150/health

# View logs
docker compose --profile full logs -f
```

### .NET Aspire (recommended for development)

```bash
# Start all services with the Aspire dashboard
dotnet run --project src/FhirAugury.AppHost
```

The Aspire dashboard provides real-time service health, logs, traces, and
metrics at the URL shown in the console output. Confluence, Dev UI, MCP HTTP,
and CLI use explicit start and must be started manually from the dashboard.

### From Source

```bash
# Prerequisites: .NET 10 SDK
dotnet build fhir-augury.slnx

# Start individual services
dotnet run --project src/FhirAugury.Source.Jira
dotnet run --project src/FhirAugury.Source.Zulip
dotnet run --project src/FhirAugury.Orchestrator
```

## Services

| Service | Port | Description |
|---------|------|-------------|
| Orchestrator | [5150](http://localhost:5150/health) | Unified search, cross-references, aggregation |
| Jira | [5160](http://localhost:5160/health) | HL7 Jira issues and comments |
| Zulip | [5170](http://localhost:5170/health) | FHIR Zulip chat messages |
| Confluence | [5180](http://localhost:5180/health) | HL7 Confluence wiki pages |
| GitHub | [5190](http://localhost:5190/health) | HL7 GitHub issues, PRs, and commits |
| MCP (HTTP) | [5200](http://localhost:5200/mcp) | MCP server (HTTP/SSE transport) |
| Dev UI | [5210](http://localhost:5210) | Blazor Server operational dashboard |

## Features

- **Unified search** across Jira issues, Zulip chat, Confluence wiki, and GitHub
- **Full-text search** via SQLite FTS5 with BM25 relevance scoring
- **Lemmatization** — normalizes inflected words to base forms for better recall
- **Configurable BM25** — per-service K1/B tuning for different content types
- **Auxiliary database** — optional external stop words, lemmas, and FHIR spec data
- **Cross-reference linking** — detects mentions and links between sources
- **Related items** — find similar content using BM25 keyword vectors
- **FHIR-aware tokenization** — recognizes FHIR paths, operations, and terms
- **Independent services** — each source runs standalone with its own database
- **FHIR artifact parsing** — indexes StructureDefinitions, canonical artifacts, and FSH definitions from cloned repositories
- **MCP servers** — stdio and HTTP/SSE transports for integration with LLM agents
- **CLI tool** for searching and managing services via HTTP
- **Docker Compose** deployment with profiles for subset stacks
- **.NET Aspire** orchestration with dashboard, OpenTelemetry, and service discovery

## MCP Setup

Configure your MCP client to connect to the running services:

### Stdio Mode (Full Stack)

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/fhir-augury/src/FhirAugury.McpStdio"],
      "env": {
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5150",
        "FHIR_AUGURY_JIRA": "http://localhost:5160",
        "FHIR_AUGURY_ZULIP": "http://localhost:5170",
        "FHIR_AUGURY_CONFLUENCE": "http://localhost:5180",
        "FHIR_AUGURY_GITHUB": "http://localhost:5190"
      }
    }
  }
}
```

### Stdio Mode (Direct — Single Source)

```json
{
  "mcpServers": {
    "fhir-augury-jira": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/fhir-augury/src/FhirAugury.McpStdio",
               "--", "--mode", "direct", "--source", "jira"],
      "env": {
        "FHIR_AUGURY_JIRA": "http://localhost:5160"
      }
    }
  }
}
```

### HTTP Mode

Start the HTTP MCP server (included in Aspire, or run manually):

```bash
dotnet run --project src/FhirAugury.McpHttp
```

Then configure your MCP client:

```json
{
  "mcpServers": {
    "fhir-augury": {
      "url": "http://localhost:5200/mcp"
    }
  }
}
```

See `mcp-config-examples/` for ready-to-use configuration files.

## Docker Compose Profiles

| Profile | Services | Use Case |
|---------|----------|----------|
| `full` | All 5 services | Production / full development |
| `jira-zulip` | Jira + Zulip + Orchestrator | Common subset |
| `jira-only` | Jira only | Single source standalone |

```bash
docker compose --profile full up -d        # Everything
docker compose --profile jira-zulip up -d  # Subset
docker compose --profile jira-only up -d   # Single source
```

## Components

| Component | Project | Description |
|-----------|---------|-------------|
| Orchestrator | `src/FhirAugury.Orchestrator` | Aggregator, cross-references, unified search |
| Jira Source | `src/FhirAugury.Source.Jira` | Jira issue ingestion and search |
| Zulip Source | `src/FhirAugury.Source.Zulip` | Zulip message ingestion and search |
| Confluence Source | `src/FhirAugury.Source.Confluence` | Confluence page ingestion and search |
| GitHub Source | `src/FhirAugury.Source.GitHub` | GitHub issues, PRs, commits, FHIR artifacts |
| Common | `src/FhirAugury.Common` | Shared types, API contracts, utilities |
| Parsing (FHIR) | `src/FhirAugury.Parsing.Fhir` | FHIR XML/JSON StructureDefinition and canonical artifact parsing |
| Parsing (FSH) | `src/FhirAugury.Parsing.Fsh` | FSH (FHIR Shorthand) and sushi-config.yaml parsing |
| MCP Server (stdio) | `src/FhirAugury.McpStdio` | MCP server for LLM agents (stdio transport, e.g., Claude Desktop) |
| MCP Server (HTTP) | `src/FhirAugury.McpHttp` | MCP server for LLM agents (HTTP/SSE transport) |
| MCP Shared | `src/FhirAugury.McpShared` | Shared MCP tool implementations and HTTP client registration |
| CLI | `src/FhirAugury.Cli` | Command-line interface via HTTP |
| Dev UI | `src/FhirAugury.DevUi` | Blazor Server operational dashboard |
| Service Defaults | `src/FhirAugury.ServiceDefaults` | Shared Aspire defaults (OpenTelemetry, health checks, resilience) |
| App Host | `src/FhirAugury.AppHost` | .NET Aspire orchestrator for local development |

## Discovery

Every service publishes an OpenAPI 3.1 document at
`/api/v1/openapi.json` (and `.yaml`). The orchestrator additionally serves a
**merged** document at `/api/v1/openapi.json` that describes its own
endpoints plus every enabled source's endpoints exposed through the typed
per-source proxies under `/api/v1/{name}/...` (e.g. `/api/v1/jira/items`,
`/api/v1/github/repos`).

The orchestrator self-metadata routes (`/api/v1/source/orchestrator/openapi.json`
and `/api/v1/source/orchestrator/list-sources`) are preserved by design; the
generic `/api/v1/source/{name}/...` reverse proxy was removed in the
2026-04 sync (see [docs/changelog/2026-04-sync.md](docs/changelog/2026-04-sync.md)).

The CLI uses this document to enumerate and invoke any operation
generically — no new code is required to call a newly added endpoint:

```bash
augury sources                                       # list enabled sources
augury commands [--source jira] [--tag T]            # enumerate operations
augury schema source=jira operation=query            # show request/response schema
augury call source=jira operation=query body=@q.json # invoke any operation
```

See [docs/openapi.md](docs/openapi.md) for endpoint details, vendor
extensions (`x-augury-command`, `x-augury-streaming`, `x-augury-visibility`,
`x-augury-since`, `x-augury-until`, `x-augury-source-status`), and the CI
quality gate.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/) (optional, for Aspire orchestration)
- Docker (optional, for containerized deployment)

## Documentation

| Document | Description |
|----------|-------------|
| [Deployment](docs/deployment.md) | Docker Compose, profiles, volumes |
| [Development](docs/development.md) | Dev setup, building, running, testing |
| [Configuration](docs/configuration.md) | All config options per service |
| [OpenAPI Discovery](docs/openapi.md) | Per-service & merged OpenAPI docs, vendor extensions, generic CLI `call` |

### User Guides

| Document | Description |
|----------|-------------|
| [Getting Started](docs/user/getting-started.md) | Setup, configure, download, search |
| [CLI Reference](docs/user/cli-reference.md) | Command-line interface documentation |
| [Configuration](docs/user/configuration.md) | User configuration guide |
| [API Reference](docs/user/api-reference.md) | HTTP API endpoints |
| [MCP Tools](docs/user/mcp-tools.md) | MCP tool reference for LLM agents |
| [Docker](docs/user/docker.md) | Docker Compose deployment |

### Agent Skills

The repository ships a set of project skills under `.github/skills/` for
LLM coding agents (Copilot CLI, Claude Code, etc.). The CLI is the
default integration surface; MCP, direct HTTP, and `appsettings.json`
are documented fallbacks (see the `fhir-augury-cli` skill).

| Skill | Purpose |
|-------|---------|
| [`fhir-augury-cli`](.github/skills/fhir-augury-cli/SKILL.md) | Reference for invoking the `fhir-augury` CLI (recipes, fallback order). |
| [`repo-analysis`](.github/skills/repo-analysis/SKILL.md) | On-demand generator that writes per-repo briefings to `cache/github/repos/<owner>_<name>/repo-analysis/`. |
| [`ticket-prep`](.github/skills/ticket-prep/SKILL.md) | Prepares Jira tickets for workgroup review. |
| [`ticket-plan`](.github/skills/ticket-plan/SKILL.md) | Plans the implementation of a resolved Jira ticket; consumes saved per-repo briefings. |
| [`orchestrate-prep`](.github/skills/orchestrate-prep/SKILL.md) | Bulk ticket-prep over a worklist. |
| [`orchestrate-plan`](.github/skills/orchestrate-plan/SKILL.md) | Bulk ticket-plan over a worklist. |

If a repo is miscategorized for `repo-analysis`, fix it in
`src/FhirAugury.Source.GitHub/appsettings.json` (under the appropriate
`*Repositories` list) — the skill does not re-derive categories.

### Technical Documentation

| Document | Description |
|----------|-------------|
| [Architecture](docs/technical/architecture.md) | System design and components |
| [Database Schema](docs/technical/database-schema.md) | SQLite, FTS5, source-generated CRUD |
| [Indexing & Search](docs/technical/indexing-and-search.md) | FTS5, BM25, cross-references |
| [Data Sources](docs/technical/data-sources.md) | Source connector architecture |
| [Source Endpoint Reference](docs/technical/source-endpoint-reference.md) | Per-source HTTP route catalog (post 2026-04 sync) |
| [Development Guide](docs/technical/development-guide.md) | Contributing and code conventions |
| [Project Structure](docs/technical/project-structure.md) | Code organization |

### Changelog

- [2026-04 HTTP API Sync](docs/changelog/2026-04-sync.md) — typed proxies, generic-proxy removal, CLI verb rename, deleted `HttpServiceClient` methods.

## Tech Stack

- **Language:** C# 14 / .NET 10
- **Database:** SQLite with FTS5 and WAL mode (per service)
- **Communication:** HTTP/REST with JSON (inter-service and client-facing)
- **CLI framework:** JSON-in/JSON-out interface via HTTP
- **MCP:** Model Context Protocol (stdio and HTTP/SSE transports)
- **Containerization:** Docker with multi-stage builds
- **Orchestration:** .NET Aspire (optional, for development)
- **Observability:** OpenTelemetry (via Aspire ServiceDefaults)
- **Code generation:** CsLightDbGen for database CRUD

## License

[MIT](LICENSE) — Copyright (c) Gino Canessa
