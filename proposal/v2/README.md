# FHIR Augury v2 — Service-Oriented Architecture Proposal

A service-oriented architecture for FHIR Augury with discrete, independently
deployable services per content type, unified by a cross-referencing
orchestrator.

## Motivation

A monolithic architecture coupling all four data sources, their indexing, and
cross-referencing logic in a single process with a single SQLite database is
hard to develop incrementally, slow to rebuild, fragile to schema changes, and
impossible to scale or deploy independently per source. See
[01-overview.md](01-overview.md) for the full problem statement.

## Documents

| # | Document | Description |
|---|----------|-------------|
| 01 | [Overview](01-overview.md) | Problem statement, goals, design principles |
| 02 | [Architecture](02-architecture.md) | Service topology, communication patterns, deployment |
| 03 | [Source Services](03-source-services.md) | Design of the four source services (Jira, Zulip, Confluence, GitHub) |
| 04 | [Orchestrator Service](04-orchestrator-service.md) | Cross-referencing, unified search, and query orchestration |
| 05 | [API Contracts](05-api-contracts.md) | Inter-service gRPC/HTTP contracts, data models |
| 06 | [Caching & Storage](06-caching-storage.md) | Local caching, database-per-service, rebuild resilience |
| 07 | [MCP & CLI](07-mcp-cli.md) | How CLI and MCP server interact with the architecture |
| 08 | [Build Plan](08-migration-plan.md) | Phased build plan |

## Technology Stack

- **Language:** C# 14 / .NET 10.0
- **Databases:** SQLite per service (source-generated CRUD via `cslightdbgen.sqlitegen`)
- **Inter-service:** gRPC (primary) + HTTP/JSON (fallback/external)
- **Caching:** File-system response cache per source service
- **Zulip:** `zulip-cs-lib`
- **MCP:** `ModelContextProtocol` SDK
- **CLI:** `System.CommandLine`
- **Hosting:** ASP.NET Minimal API + `BackgroundService` per service

## Key Architectural Properties

| Aspect | Design |
|--------|--------|
| **Database** | One SQLite database per source service + orchestrator |
| **Deployment** | Independent processes (compose or standalone) |
| **Source isolation** | Each source owns its data, schema, and internal indexing |
| **Cross-referencing** | Orchestrator queries source services and builds xref index |
| **Failure isolation** | Source services fail independently |
| **Rebuild** | Local cache per service; rebuild reads from cache, no network required |
| **Scalability** | Per-service ingestion; orchestrator coordinates |
| **Development** | Work on one service at a time |
