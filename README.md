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

Two component groups are omitted from the diagram to keep it readable:

- **FHIR spec source** (`source-fhir`, :5195) — a read-only source that serves
  FHIR specification reference data (StructureDefinitions, canonical resources)
  to the orchestrator alongside the four ingesting sources.
- **Processors** (:5171–:5174) — the Preparer, Planner, Applier, and BallotNotes
  services consume source data to produce derived artifacts (ticket prep, plans,
  applied changes, ballot notes).

See [Architecture](docs/technical/architecture.md) for the full component and
data-flow diagrams.

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

| Service | Port | Availability | Description |
|---------|------|--------------|-------------|
| Orchestrator | [5150](http://localhost:5150/health) | Compose + Aspire | Unified search, cross-references, aggregation |
| Jira | [5160](http://localhost:5160/health) | Compose + Aspire | HL7 Jira issues and comments |
| Zulip | [5170](http://localhost:5170/health) | Compose + Aspire | FHIR Zulip chat messages |
| Jira FHIR Preparer | [5171](http://localhost:5171/health) | Compose + Aspire | Processing service for Triaged FHIR Jira ticket prep outputs (`/api/v1/prepared-tickets`) |
| Jira FHIR Planner | [5172](http://localhost:5172/health) | Aspire only | Processing service that queues resolved change-required tickets and runs `ticket-plan` (Tickets for Applying) |
| Jira FHIR Applier | [5173](http://localhost:5173/health) | Aspire only | Processing service that applies planned changes in per-(ticket, repo) git worktrees (`/api/v1/applied-tickets`) |
| BallotNotes Processor | [5174](http://localhost:5174/health) | Aspire only | Commit-triggered ballot-note hydration + authoring API backing the `notes-site` renderer (`/api/v1/ballot-notes`) |
| Confluence | [5180](http://localhost:5180/health) | Compose + Aspire | HL7 Confluence wiki pages |
| GitHub | [5190](http://localhost:5190/health) | Compose + Aspire | HL7 GitHub issues, PRs, and commits |
| FHIR Spec | [5195](http://localhost:5195/health) | Aspire only | Read-only FHIR specification reference data (StructureDefinitions, canonical resources) |
| MCP (HTTP) | [5200](http://localhost:5200/mcp) | Aspire only | MCP server (HTTP/SSE transport) |
| Dev UI | [5210](http://localhost:5210) | Aspire only | Blazor Server operational dashboard |

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
| `full` | All services, including the optional preparer | Production / full development |
| `processing` | Jira + Jira FHIR Preparer | Local processing API/queue; agent runtime must be supplied separately |
| `jira-zulip` | Jira + Zulip + Orchestrator | Common subset |
| `jira-only` | Jira only | Single source standalone |

```bash
docker compose --profile full up -d        # Everything
docker compose --profile processing up -d  # Jira + preparer API/queue
docker compose --profile jira-zulip up -d  # Subset
docker compose --profile jira-only up -d   # Single source
```

## Components

| Component | Project | Description |
|-----------|---------|-------------|
| Orchestrator | `src/FhirAugury.Orchestrator` | Aggregator, cross-references, unified search |
| Jira Source | `src/FhirAugury.Source.Jira` | Jira issue ingestion and search |
| Jira FHIR Preparer | `src/FhirAugury.Processor.Jira.Fhir.Preparer` | Processing service that defaults to Triaged FHIR Jira tickets, overwrites structured prep output without history, and exposes read/query APIs; Docker processing requires a host-provided agent runtime |
| Jira FHIR Planner | `src/FhirAugury.Processor.Jira.Fhir.Planner` | Processing service that queues resolved change-required tickets and runs `ticket-plan` to produce implementation plans (Tickets for Applying) |
| Jira FHIR Applier | `src/FhirAugury.Processor.Jira.Fhir.Applier` | Processing service that applies planned changes in per-(ticket, repo) git worktrees and can push on demand |
| BallotNotes Processor | `src/FhirAugury.Processor.GitHub.Fhir.BallotNotes` | Commit-triggered processor that hydrates ballot-note evidence (commit window, ticket attribution, source-file resolution) and serves read/author APIs under `/api/v1/ballot-notes`; consumed by the `notes-site` renderer |
| Processor satellites | `src/FhirAugury.Processor.*.{Persistence,Hydration}`, `*.Hydration.Common` | Per-family persistence + hydration support libraries for the Preparer / Planner / BallotNotes processors |
| Processing Common | `src/FhirAugury.Processing.Common` | Shared processing substrate (queue lifecycle, agent invocation, HTTP surface) for all processors |
| Processing (Jira Common) | `src/FhirAugury.Processing.Jira.Common` | Jira-specific processing base (source-ticket persistence, filter conventions, discovery) |
| Zulip Source | `src/FhirAugury.Source.Zulip` | Zulip message ingestion and search |
| Confluence Source | `src/FhirAugury.Source.Confluence` | Confluence page ingestion and search |
| GitHub Source | `src/FhirAugury.Source.GitHub` | GitHub issues, PRs, commits, FHIR artifacts |
| FHIR Source | `src/FhirAugury.Source.Fhir` | Read-only FHIR specification reference data (StructureDefinitions, canonical resources) served on `:5195` |
| Common | `src/FhirAugury.Common` | Shared types, API contracts, utilities |
| Common (OpenAPI) | `src/FhirAugury.Common.OpenApi` | Shared OpenAPI 3.1 document generation and Scalar UI wiring |
| Parsing (FHIR) | `src/FhirAugury.Parsing.Fhir` | FHIR XML/JSON StructureDefinition and canonical artifact parsing |
| Parsing (FSH) | `src/FhirAugury.Parsing.Fsh` | FSH (FHIR Shorthand) and sushi-config.yaml parsing |
| Parsing (XML) | `src/FhirAugury.Parsing.Xml` | Low-level streaming XML reader shared by the parsers |
| MCP Server (stdio) | `src/FhirAugury.McpStdio` | MCP server for LLM agents (stdio transport, e.g., Claude Desktop) |
| MCP Server (HTTP) | `src/FhirAugury.McpHttp` | MCP server for LLM agents (HTTP/SSE transport) |
| MCP Shared | `src/FhirAugury.McpShared` | Shared MCP tool implementations and HTTP client registration |
| CLI | `src/FhirAugury.Cli` | Command-line interface via HTTP |
| Dev UI | `src/FhirAugury.DevUi` | Blazor Server operational dashboard |
| Service Defaults | `src/FhirAugury.ServiceDefaults` | Shared Aspire defaults (OpenTelemetry, health checks, resilience) |
| App Host | `src/FhirAugury.AppHost` | .NET Aspire orchestrator for local development |

## Local utilities

| Utility | Project | Description |
|---------|---------|-------------|
| Ticket site | [`tools/ticket-site`](tools/ticket-site/README.md) | One-shot `dotnet`-run utility that turns a `cache/jira-preparer.db` (Tickets for Discussion) or a `cache/jira-planner.db` (Tickets for Applying) into a self-contained static HTML review sub-site (sql.js in the browser; opens from `file://`). A chooser landing page at `<out>/index.html` links into whichever sub-site(s) have been built. |
| Dictionary build | [`tools/dictionary-build`](tools/dictionary-build/README.md) | One-shot `dotnet`-run utility that rebuilds `cache/dictionary.db` from the spell-check source files under `dictionary/`. Run after editing anything under `dictionary/`. |

## Discovery

Every service publishes an OpenAPI 3.1 document at
`/api/v1/openapi.json` (and `.yaml`). The orchestrator additionally serves a
**merged** document at `/api/v1/openapi.json` that describes its own
endpoints plus every enabled source's endpoints exposed through the typed
per-source proxies under `/api/v1/{name}/...` (e.g. `/api/v1/jira/items`,
`/api/v1/github/repos`).

The orchestrator self-metadata routes (`/api/v1/source/orchestrator/openapi.json`
and `/api/v1/source/orchestrator/list-sources`) are preserved by design; there is
no generic `/api/v1/source/{name}/...` reverse proxy — per-source operations are
exposed through the typed proxies.

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

The docs are organized in three tiers:

- **Reference** (`docs/`) — canonical, cross-cutting references for a topic.
- **User guides** (`docs/user/`) — task-oriented "how do I…" guides that link
  down to the reference for full detail.
- **Technical docs** (`docs/technical/`) — deep implementation and reference
  material for contributors.

**Reference**

| Document | Description |
|----------|-------------|
| [Configuration](docs/configuration.md) | Canonical reference for every config option, per service |
| [Deployment](docs/deployment.md) | Docker Compose, profiles, volumes, and .NET Aspire |
| [Development](docs/development.md) | Build / run / test quickstart |
| [OpenAPI Discovery](docs/openapi.md) | Per-service & merged OpenAPI docs, vendor extensions, generic CLI `call` |

### User Guides

Task-oriented guides for getting started, the CLI, the HTTP API, MCP tools,
Docker, and the output-generation pipelines. See the
**[User Guides index](docs/user/README.md)**.

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

Deep implementation and reference docs — architecture, project structure, the
development guide, data sources, the source-endpoint reference, database schema,
indexing/search, and the processors runbook. See the
**[Technical Documentation index](docs/technical/README.md)**.

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
