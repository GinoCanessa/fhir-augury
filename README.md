# FHIR Augury

A unified knowledge platform for searching across HL7 FHIR community data
sources. FHIR Augury downloads, indexes, and cross-references content from
Jira, Zulip, Confluence, and GitHub into a single SQLite database with full-text
search powered by FTS5 and BM25 relevance scoring.

## Quick Start

```bash
# Build
dotnet build fhir-augury.slnx

# Download data (example: Jira)
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --db fhir-augury.db --jira-cookie "JSESSIONID=..."

# Build indexes and search
dotnet run --project src/FhirAugury.Cli -- index rebuild-all --db fhir-augury.db
dotnet run --project src/FhirAugury.Cli -- search "FHIR R5 changes" --db fhir-augury.db
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                     Data Sources                        │
│  ┌──────┐  ┌───────┐  ┌────────────┐  ┌────────┐      │
│  │ Jira │  │ Zulip │  │ Confluence │  │ GitHub │      │
│  └──┬───┘  └───┬───┘  └─────┬──────┘  └───┬────┘      │
│     └──────────┼────────────┼──────────────┘           │
│                ▼            ▼                           │
│  ┌──────────────────────────────────────┐              │
│  │         SQLite + FTS5 Database       │              │
│  │  ┌─────────┐ ┌──────┐ ┌──────────┐  │              │
│  │  │ Content │ │ FTS5 │ │ BM25 Idx │  │              │
│  │  │ Tables  │ │ Index│ │ + X-Refs │  │              │
│  │  └─────────┘ └──────┘ └──────────┘  │              │
│  └──────────┬───────────────┬───────────┘              │
│             │               │                          │
│  ┌──────────┴──┐  ┌────────┴────────┐  ┌───────────┐  │
│  │  CLI Tool   │  │  HTTP Service   │  │ MCP Server│  │
│  │ fhir-augury │  │  (background +  │  │ (LLM agent│  │
│  │             │  │   REST API)     │  │  access)  │  │
│  └─────────────┘  └────────────────┘  └───────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Features

- **Unified search** across Jira issues, Zulip chat messages, Confluence wiki
  pages, and GitHub issues/PRs
- **Full-text search** via SQLite FTS5 with BM25 relevance scoring
- **Cross-reference linking** — automatically detects mentions, links, and
  relationships between items across sources
- **Similarity search** — find related content using BM25 keyword vectors
- **FHIR-aware tokenization** — recognizes FHIR paths, operations, and
  vocabulary for better search relevance
- **CLI tool** for downloading, syncing, indexing, and searching
- **Background service** with HTTP API and scheduled sync
- **MCP server** for integration with LLM agents (Claude, Copilot, etc.)
- **Incremental sync** — only fetch new and updated items after initial download

## Components

| Component | Project | Description |
|---|---|---|
| CLI | `src/FhirAugury.Cli` | Command-line interface for all operations |
| Service | `src/FhirAugury.Service` | ASP.NET Core service with HTTP API and background sync |
| MCP Server | `src/FhirAugury.Mcp` | Model Context Protocol server for LLM agents |
| Database | `src/FhirAugury.Database` | SQLite schema, FTS5 setup, source-generated CRUD |
| Indexing | `src/FhirAugury.Indexing` | FTS search, BM25 scoring, cross-reference linking |
| Models | `src/FhirAugury.Models` | Shared interfaces and data models |
| Sources | `src/FhirAugury.Sources.*` | Connectors for Jira, Zulip, Confluence, GitHub |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

## Documentation

### User Guides

| Document | Description |
|---|---|
| [Getting Started](docs/user/getting-started.md) | Setup guide — build, configure, download, and search |
| [CLI Reference](docs/user/cli-reference.md) | Complete command-line interface documentation |
| [Configuration](docs/user/configuration.md) | Full configuration reference for CLI, service, and MCP |
| [API Reference](docs/user/api-reference.md) | HTTP API endpoints for the background service |
| [MCP Tools](docs/user/mcp-tools.md) | MCP tool reference for LLM agent integration |
| [Docker Deployment](docs/user/docker.md) | Running FHIR Augury as a containerized service |

### Technical Documentation

| Document | Description |
|---|---|
| [Architecture](docs/technical/architecture.md) | System architecture, components, and design decisions |
| [Database Schema](docs/technical/database-schema.md) | SQLite schema, FTS5, triggers, and source-generated CRUD |
| [Indexing & Search](docs/technical/indexing-and-search.md) | FTS5, BM25 scoring, cross-references, and FHIR-aware tokenization |
| [Data Sources](docs/technical/data-sources.md) | Source connector architecture and guide for adding new sources |
| [Development Guide](docs/technical/development-guide.md) | Dev environment setup, building, testing, and contributing |
| [Project Structure](docs/technical/project-structure.md) | Code organization and project dependencies |

## Tech Stack

- **Language:** C# 14 / .NET 10
- **Database:** SQLite with FTS5 and WAL mode
- **CLI framework:** System.CommandLine
- **Service:** ASP.NET Core minimal APIs
- **MCP:** Model Context Protocol (stdio transport)
- **Code generation:** CsLightDbGen for database CRUD

## License

[MIT](LICENSE) — Copyright (c) Gino Canessa
