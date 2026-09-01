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
| Terminology Server | [5300](http://localhost:5300/health) | Compose + Aspire | THO overlap check for submitted CodeSystem / ValueSet resources |

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
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5150"
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
| `terminology` | Terminology Server | THO overlap check standalone (no dependency on the Source/Orchestrator stack) |

```bash
docker compose --profile full up -d         # Everything
docker compose --profile processing up -d   # Jira + preparer API/queue
docker compose --profile jira-zulip up -d   # Subset
docker compose --profile jira-only up -d    # Single source
docker compose --profile terminology up -d  # Terminology server only
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
| Terminology Server | `src/FhirAugury.Server.Terminology` | Server-class service: scores a submitted FHIR CodeSystem/ValueSet against THO (`hl7.terminology.r4`/`r5`) and returns ranked overlap candidates (`/api/v1/terminology/check`) |
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
| Notes site | [`tools/notes-site`](tools/notes-site/README.md) | Read-only `dotnet`-run utility that renders the BallotNotes processor's notes database into a self-contained, searchable static HTML review SPA (opens from `file://`, no server). |
| BallotNotes reallocate-WG | [`tools/ballotnotes-reallocate-wg`](tools/ballotnotes-reallocate-wg/README.md) | One-off, idempotent maintenance command that re-stamps the owning Work Group on existing ballot-note rows by re-running only the deterministic resolver — no re-hydration, re-attribution, or authoring. |
| FHIR spec review | [`tools/fhir-spec-review`](tools/fhir-spec-review/README.md) | Read-only `dotnet`-run utility that runs FMG-style content-quality checks over the current HL7/fhir build and emits a self-contained, searchable report SPA. |

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

The table above is a curated subset; the repository ships two dozen-plus skills
under `.github/skills/` — browse that directory for the full set (orchestration,
indexing, ballot-notes, and `dev-*` workflow skills).

If a repo is miscategorized for `repo-analysis`, fix it in
`src/FhirAugury.Source.GitHub/appsettings.json` (under the appropriate
`*Repositories` list) — the skill does not re-derive categories.

### Technical Documentation

Deep implementation and reference docs — architecture, project structure, the
development guide, data sources, the source-endpoint reference, database schema,
indexing/search, and the processors runbook. See the
**[Technical Documentation index](docs/technical/README.md)**.

## FHIR Release Indexes

Quick reference for bounding FHIR-version-specific work (ballot notes, ticket
attribution, spec diffing, "what changed in R5") to a single core release.
**Commit bounds** are in the [`HL7/fhir`](https://github.com/HL7/fhir) repository,
shown as `date · tag/ref · SHA` where the short SHA links to the full commit on
GitHub. **Ticket windows** are the lowest/highest `FHIR-` Jira ticket *applied*
during the release. Both are **best-effort / rough bounds**, not complete or
ordered lists — see the notes below the table. Resolved 2026-07-14 against
`HL7/fhir` `HEAD` = [`94dbe68`](https://github.com/HL7/fhir/commit/94dbe68f231ca265f33905f9b04433c6e0422f18)
(2026-07-10); a documented snapshot, not auto-regenerated.

| Release | Released | Interim builds | Lowest commit | Highest commit | FHIR- ticket window (rough) |
|---------|----------|----------------|---------------|----------------|------------------------------|
| R6 (6.0.0-ballot4) | in ballot | 2023-12-19, 2024-08-13, 2025-04-03, 2025-12-18, CI → next ballot (~2026-07-17) | 2023-03-26 · master · [`ee061c5`](https://github.com/HL7/fhir/commit/ee061c5a5d524fbbe58d95dfa4fb8472804a34e2) | 2026-07-10 · master@HEAD · [`94dbe68`](https://github.com/HL7/fhir/commit/94dbe68f231ca265f33905f9b04433c6e0422f18) — ongoing | FHIR-9197 → FHIR-57749 (ongoing as of 2026-07-10) |
| R5 (5.0.0) | 2023-03-26 | 2019-12-31, 2020-05-04, 2020-08-20, 2021-04-05, 2021-12-19, 2022-09-10, 2022-12-14, 2023-03-01 | 2019-11-02 · master · [`959acd1`](https://github.com/HL7/fhir/commit/959acd13e7964a6f7cfeb607d29fe458460254e3) | 2023-03-26 · v5.0.0 · [`eca054d`](https://github.com/HL7/fhir/commit/eca054db690594b98b3cf81ff52634f2bbc69822) | FHIR-3177 → FHIR-40664 |
| R4B (4.3.0) | 2022-05-28 | 2021-03-11, 2021-12-20 | 2021-01-21 · R4B · [`c69d71a`](https://github.com/HL7/fhir/commit/c69d71aa213de86d55e0261339c15e4bacd509e6) ⚠ | 2022-05-28 · R4B · [`d685d85`](https://github.com/HL7/fhir/commit/d685d8588a75141177ae8b93141986678017d7d8) ⚠ | FHIR-19955 → FHIR-36705 |
| R4 (4.0.1) | 2019-10-30 | 2018-04-02, 2018-08-20, 2018-11-09 | 2017-04-19 · master · [`cf39f66`](https://github.com/HL7/fhir/commit/cf39f6678ba74f1dd2177ce6149ba7fe78bed8ca) (approx.) | 2019-10-27 · master · [`b635715`](https://github.com/HL7/fhir/commit/b63571578b4560de7ad5507a1cc847a62f5d52ae) | FHIR-3160 → FHIR-25029 |

Notes:

- ⚠ **R4B** was split off the R4-era line while R5 was developed on `master`, so its
  commits are **not** a clean contiguous `master` range — its window is a best-effort
  span on branch `origin/R4B` (the post-GA branch tip `d63c3542`, 2022-08-15, is
  excluded). This non-sequential-commits caveat applies to **R4B only**.
- **Commit bounds** are **first-parent** mainline anchors on `master` (R4B is
  best-effort on its own branch), so they land on true integration commits rather
  than side-branch commits that merely sort by date. The R5 high bound is the
  annotated `v5.0.0` release tag. The **R4 low bound is approximate** (`approx.`) —
  the pre-R4 boundary is not precisely tracked and pre-R4 releases are out of scope.
- **Ticket windows are non-contiguous** — a rough indication of the work applied
  during the release, not every `FHIR-` number in the range and not in numeric order.
  They are derived from `FHIR-` references in commit messages within each window
  (a lighter-weight proxy than the BallotNotes attributor), then filtered to real
  `FHIR-` Jira keys, so bogus tokens are dropped.
- **R6** is still in development; its upper bounds are "as of" the last update
  (`HEAD` = `94dbe68`, 2026-07-10) and are marked **ongoing**, not final. Its ticket
  upper bound reflects the current Jira snapshot; newer `FHIR-` references may exist
  in `HL7/fhir` above it.

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
