# FHIR Augury v2 — Implementation Plan

Detailed implementation plan for the v2 service-oriented architecture, derived
from [proposal/v2/](../../proposal/v2/).

## Documents

| # | Document | Description |
|---|----------|-------------|
| 01 | [Phase 1: Foundation](phase-1-foundation.md) | Common project, proto contracts, shared infrastructure |
| 02 | [Phase 2: Jira Source Service](phase-2-jira-source.md) | First source service — reference implementation |
| 03 | [Phase 3: Zulip Source Service](phase-3-zulip-source.md) | Second source service — validates patterns |
| 04 | [Phase 4: Orchestrator, MCP & CLI](phase-4-orchestrator-mcp-cli.md) | Cross-referencing, unified search, consumer interfaces |
| 05 | [Phase 5: Confluence & GitHub Sources](phase-5-confluence-github.md) | Remaining source services |
| 06 | [Phase 6: Docker & Deployment](phase-6-docker-deployment.md) | Containerization, compose, documentation |

## Architecture Summary

v2 transforms the existing monolith into five discrete services:

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ Jira Service │  │Zulip Service │  │ Confluence   │  │GitHub Service│
│  :5160/:5161 │  │  :5170/:5171 │  │   Service    │  │  :5190/:5191 │
│              │  │              │  │  :5180/:5181 │  │              │
└──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘
       └────────┬────────┴────────┬────────┘                 │
                │      gRPC      │                          │
                ▼                ▼                          ▼
        ┌───────────────────────────────────────────────────┐
        │           Orchestrator Service :5150/:5151        │
        └──────────────────────┬────────────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
        ┌──────────┐    ┌──────────┐    ┌──────────┐
        │   CLI    │    │   MCP    │    │  HTTP    │
        └──────────┘    └──────────┘    └──────────┘
```

## Key Differences from v1

| Aspect | v1 (Current) | v2 (Target) |
|--------|-------------|-------------|
| Architecture | Monolith, single process | 5 independent services |
| Database | Single shared SQLite | Per-service SQLite |
| Communication | Direct method calls | gRPC (internal) + HTTP (external) |
| MCP | Direct DB reads | gRPC → Orchestrator → Sources |
| CLI | Direct DB reads | gRPC/HTTP → Services |
| Ingestion | Centralized queue | Per-service workers |
| Cross-referencing | In-process | Orchestrator service |
| Deployment | Single container | Docker Compose multi-container |
| Project naming | `FhirAugury.Sources.{X}` | `FhirAugury.Source.{X}` |
| Shared types | `FhirAugury.Models` | `FhirAugury.Common` |

## Technology Stack

- **Language:** C# 14 / .NET 10.0
- **Databases:** SQLite per service (`cslightdbgen.sqlitegen`)
- **Inter-service:** gRPC (Protobuf)
- **External API:** ASP.NET Minimal API (HTTP/JSON)
- **Caching:** File-system response cache per source
- **MCP:** `ModelContextProtocol` SDK (stdio transport)
- **CLI:** `System.CommandLine`
- **Hosting:** ASP.NET + `BackgroundService` per service

## Build Order

```
Phase 1: Foundation & Common Infrastructure
    └──▶ Phase 2: Jira Source Service
         └──▶ Phase 3: Zulip Source Service
              └──▶ Phase 4: Orchestrator, MCP & CLI (Jira + Zulip)
                   └──▶ Phase 5: Confluence & GitHub Source Services
                        └──▶ Phase 6: Docker Compose & Deployment
```

Each phase is independently verifiable. Phase 4 is the key integration
milestone — if cross-source works with two services, Phases 5–6 follow
the established patterns.
