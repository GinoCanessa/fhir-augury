# FHIR Augury

A unified knowledge platform for searching across HL7 FHIR community data
sources. FHIR Augury downloads, indexes, and cross-references content from
Jira, Zulip, Confluence, and GitHub with full-text search powered by SQLite
FTS5 and BM25 relevance scoring.

## Architecture (v2)

FHIR Augury v2 uses a microservices architecture where each data source runs as
an independent gRPC service with its own database and cache. The Orchestrator
aggregates results and manages cross-references across sources.

```
┌──────────────────────────────────────────────────────────────┐
│  Clients                                                     │
│  ┌─────────┐  ┌───────────┐  ┌──────────────────────────┐   │
│  │   CLI   │  │ MCP Server│  │   Direct gRPC Clients    │   │
│  └────┬────┘  └─────┬─────┘  └────────────┬─────────────┘   │
│       └──────────────┼─────────────────────┘                 │
│                      ▼                                       │
│  ┌───────────────────────────────────────────┐               │
│  │          Orchestrator (:5150/:5151)       │               │
│  │  Unified search · Cross-references ·     │               │
│  │  Related items · Source aggregation       │               │
│  └───┬──────────┬──────────┬──────────┬─────┘               │
│      │          │          │          │         gRPC         │
│  ┌───▼───┐  ┌──▼───┐  ┌──▼────────┐ ┌▼──────┐              │
│  │ Jira  │  │Zulip │  │Confluence │ │GitHub │              │
│  │:5160  │  │:5170 │  │  :5180    │ │:5190  │              │
│  │:5161  │  │:5171 │  │           │ │:5191  │              │
│  └───────┘  └──────┘  └──────────┘ └───────┘              │
│    Each service: SQLite + FTS5 + Cache + gRPC               │
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

| Service | HTTP | gRPC | Description |
|---------|------|------|-------------|
| Orchestrator | [5150](http://localhost:5150/health) | 5151 | Unified search, cross-references, aggregation |
| Jira | [5160](http://localhost:5160/health) | 5161 | HL7 Jira issues and comments |
| Zulip | [5170](http://localhost:5170/health) | 5171 | FHIR Zulip chat messages |
| Confluence | [5180](http://localhost:5180/health) | — | HL7 Confluence wiki pages |
| GitHub | [5190](http://localhost:5190/health) | 5191 | HL7 GitHub issues, PRs, and commits |
| MCP (HTTP) | [5200](http://localhost:5200/mcp) | — | MCP server (HTTP/SSE transport) |
| Dev UI | [5210](http://localhost:5210) | — | Blazor Server operational dashboard |

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
- **MCP servers** — stdio and HTTP/SSE transports for integration with LLM agents
- **CLI tool** for searching and managing services via gRPC
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
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5151",
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161",
        "FHIR_AUGURY_ZULIP_GRPC": "http://localhost:5171",
        "FHIR_AUGURY_CONFLUENCE_HTTP": "http://localhost:5180",
        "FHIR_AUGURY_GITHUB_GRPC": "http://localhost:5191"
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
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161"
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
| GitHub Source | `src/FhirAugury.Source.GitHub` | GitHub issues, PRs, commits ingestion |
| Common | `src/FhirAugury.Common` | Shared types, protobuf definitions, utilities |
| MCP Server (stdio) | `src/FhirAugury.McpStdio` | MCP server for LLM agents (stdio transport, e.g., Claude Desktop) |
| MCP Server (HTTP) | `src/FhirAugury.McpHttp` | MCP server for LLM agents (HTTP/SSE transport) |
| MCP Shared | `src/FhirAugury.McpShared` | Shared MCP tool implementations and gRPC client registration |
| CLI | `src/FhirAugury.Cli` | Command-line interface via gRPC |
| Dev UI | `src/FhirAugury.DevUi` | Blazor Server operational dashboard |
| Service Defaults | `src/FhirAugury.ServiceDefaults` | Shared Aspire defaults (OpenTelemetry, health checks, resilience) |
| App Host | `src/FhirAugury.AppHost` | .NET Aspire orchestrator for local development |

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

### User Guides

| Document | Description |
|----------|-------------|
| [Getting Started](docs/user/getting-started.md) | Setup, configure, download, search |
| [CLI Reference](docs/user/cli-reference.md) | Command-line interface documentation |
| [Configuration](docs/user/configuration.md) | User configuration guide |
| [API Reference](docs/user/api-reference.md) | gRPC and HTTP API endpoints |
| [MCP Tools](docs/user/mcp-tools.md) | MCP tool reference for LLM agents |
| [Docker](docs/user/docker.md) | Docker Compose deployment |

### Technical Documentation

| Document | Description |
|----------|-------------|
| [Architecture](docs/technical/architecture.md) | System design and components |
| [Database Schema](docs/technical/database-schema.md) | SQLite, FTS5, source-generated CRUD |
| [Indexing & Search](docs/technical/indexing-and-search.md) | FTS5, BM25, cross-references |
| [Data Sources](docs/technical/data-sources.md) | Source connector architecture |
| [Development Guide](docs/technical/development-guide.md) | Contributing and code conventions |
| [Project Structure](docs/technical/project-structure.md) | Code organization |

## Tech Stack

- **Language:** C# 14 / .NET 10
- **Database:** SQLite with FTS5 and WAL mode (per service)
- **Communication:** gRPC (inter-service), HTTP (health/REST)
- **Protobuf:** Shared definitions in `protos/`
- **CLI framework:** JSON-in/JSON-out interface via gRPC
- **MCP:** Model Context Protocol (stdio and HTTP/SSE transports)
- **Containerization:** Docker with multi-stage builds
- **Orchestration:** .NET Aspire (optional, for development)
- **Observability:** OpenTelemetry (via Aspire ServiceDefaults)
- **Code generation:** CsLightDbGen for database CRUD

## License

[MIT](LICENSE) — Copyright (c) Gino Canessa
