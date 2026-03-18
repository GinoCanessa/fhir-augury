# FHIR Augury — Proposal v1

A unified knowledge platform for the HL7 FHIR specification ecosystem.
Consolidates Zulip, Jira, Confluence, and GitHub into a single searchable
index with CLI, MCP, and long-running service interfaces.

## Documents

| # | Document | Description |
|---|----------|-------------|
| 01 | [Overview](01-overview.md) | Vision, problem statement, goals, constraints |
| 02 | [Architecture](02-architecture.md) | Solution structure, layer diagram, key design decisions |
| 03 | [Data Sources](03-data-sources.md) | API details for Zulip, Jira, Confluence, GitHub |
| 04 | [Database Schema](04-database-schema.md) | SQLite table definitions with `cslightdbgen.sqlitegen` |
| 05 | [Service & API](05-service-api.md) | Long-running service, ingestion queue, HTTP endpoints |
| 06 | [CLI](06-cli.md) | Command structure, usage examples, output formats |
| 07 | [MCP Server](07-mcp-server.md) | MCP tools, hosting, LLM agent workflow |
| 08 | [Indexing & Search](08-indexing-search.md) | FTS5, BM25, cross-references, unified search |
| 09 | [Dependencies](09-dependencies.md) | NuGet packages and dependency philosophy |
| 10 | [Implementation Plan](10-implementation-plan.md) | Phased delivery plan (7 phases) |

## Technology Stack

- **Language:** C# 14 / .NET 10.0
- **Database:** SQLite + FTS5
- **ORM:** `cslightdbgen.sqlitegen` (compile-time source generator)
- **Zulip:** `zulip-cs-lib`
- **MCP:** `ModelContextProtocol` SDK
- **CLI:** `System.CommandLine`
- **Service:** ASP.NET Minimal API + `BackgroundService`

## Key Architectural Principles

1. **Single SQLite database** — all sources in one file, cross-source JOINs
2. **Source-generated data access** — zero-reflection, AOT-compatible CRUD
3. **Content-synced FTS5** — live index updates via triggers on INSERT/UPDATE/DELETE
4. **Queue-based ingestion** — `System.Threading.Channels` for async work dispatch
5. **Read-only MCP** — separate process with read-only DB access, safe concurrency
6. **Extensible source interface** — `IDataSource` abstraction for adding new sources
