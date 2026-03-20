# FHIR Augury v2 — Service-Oriented Architecture Proposal

A major refactor of FHIR Augury from a monolithic single-database application
into a service-oriented architecture with discrete, independently deployable
services per content type, unified by a cross-referencing orchestrator.

## Motivation

The v1 architecture couples all four data sources, their indexing, and the
cross-referencing logic into a single process sharing a single SQLite database.
This makes the system hard to develop incrementally, slow to rebuild, fragile
to schema changes, and impossible to scale or deploy independently per source.
See [01-overview.md](01-overview.md) for the full problem statement.

## Documents

| # | Document | Description |
|---|----------|-------------|
| 01 | [Overview](01-overview.md) | Problem statement, goals, design principles |
| 02 | [Architecture](02-architecture.md) | Service topology, communication patterns, deployment |
| 03 | [Source Services](03-source-services.md) | Design of the four source services (Jira, Zulip, Confluence, GitHub) |
| 04 | [Orchestrator Service](04-orchestrator-service.md) | Cross-referencing, unified search, and query orchestration |
| 05 | [API Contracts](05-api-contracts.md) | Inter-service gRPC/HTTP contracts, data models |
| 06 | [Caching & Storage](06-caching-storage.md) | Local caching, database-per-service, rebuild resilience |
| 07 | [MCP & CLI](07-mcp-cli.md) | How CLI and MCP server interact with the new architecture |
| 08 | [Migration Plan](08-migration-plan.md) | Phased migration from v1 to v2 |

## Technology Stack

- **Language:** C# 14 / .NET 10.0
- **Databases:** SQLite per service (source-generated CRUD via `cslightdbgen.sqlitegen`)
- **Inter-service:** gRPC (primary) + HTTP/JSON (fallback/external)
- **Caching:** File-system response cache per source service
- **Zulip:** `zulip-cs-lib`
- **MCP:** `ModelContextProtocol` SDK
- **CLI:** `System.CommandLine`
- **Hosting:** ASP.NET Minimal API + `BackgroundService` per service

## Key Architectural Shifts from v1

| Aspect | v1 (Monolith) | v2 (Services) |
|--------|---------------|---------------|
| **Database** | Single shared SQLite file | One SQLite database per source service + orchestrator |
| **Deployment** | Single process | Independent processes (compose or standalone) |
| **Source coupling** | All sources share schema, indexing, cross-ref | Each source owns its data, schema, and internal indexing |
| **Cross-referencing** | Direct SQL JOINs across source tables | Orchestrator queries source services and builds xref index |
| **Failure isolation** | One source failure can block all ingestion | Source services fail independently |
| **Rebuild** | Full rebuild re-downloads everything | Local cache per service; rebuild reads from cache |
| **Scalability** | Single-threaded ingestion queue | Per-service ingestion; orchestrator coordinates |
| **Development** | Must build/test entire solution | Work on one service at a time |
