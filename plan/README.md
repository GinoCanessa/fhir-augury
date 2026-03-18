# FHIR Augury — Implementation Plan

A phased implementation plan for the FHIR Augury unified knowledge platform.
Derived from the [Proposal v1](../proposal/v1/README.md).

## Plan Documents

| Phase | Document | Goal | Dependencies | Status |
|-------|----------|------|--------------|--------|
| 1 | [Foundation](phase-1-foundation.md) | Project scaffolding, database layer, Jira source, CLI basics | — | ✅ Complete |
| 2 | [Zulip Integration](phase-2-zulip.md) | Zulip source, unified search across two sources | Phase 1 | ✅ Complete |
| 3 | [Cross-Referencing & BM25](phase-3-cross-ref-bm25.md) | Cross-source linking, BM25 keyword scoring, "find related" | Phase 2 | ✅ Complete |
| 4 | [Service Layer](phase-4-service.md) | Background service, ingestion queue, HTTP API | Phase 3 | ✅ Complete |
| 5 | [Confluence & GitHub Sources](phase-5-confluence-github.md) | Complete four-source coverage | Phase 4 | ✅ Complete |
| 6 | [MCP Server](phase-6-mcp.md) | MCP server for LLM agent integration | Phase 5 | Pending |
| 7 | [Polish & Optimization](phase-7-polish.md) | Performance, error handling, packaging, docs | Phase 6 | Pending |

## Architecture Summary

```
fhir-augury.slnx
├── src/
│   ├── FhirAugury.Models/              # Shared models, enums, IDataSource
│   ├── FhirAugury.Database/            # SQLite schema, generated CRUD, FTS5
│   ├── FhirAugury.Sources.Zulip/       # Zulip ingestion (zulip-cs-lib)
│   ├── FhirAugury.Sources.Jira/        # Jira ingestion (REST + XML)
│   ├── FhirAugury.Sources.Confluence/  # Confluence ingestion (REST)
│   ├── FhirAugury.Sources.GitHub/      # GitHub ingestion (REST)
│   ├── FhirAugury.Indexing/            # FTS5, BM25, cross-ref linker
│   ├── FhirAugury.Service/             # Background service + HTTP API
│   ├── FhirAugury.Cli/                 # CLI application
│   └── FhirAugury.Mcp/                 # MCP server
└── tests/
    ├── FhirAugury.Database.Tests/
    ├── FhirAugury.Sources.Tests/
    ├── FhirAugury.Indexing.Tests/
    └── FhirAugury.Integration.Tests/
```

## Technology Stack

| Component | Technology |
|-----------|------------|
| Language | C# 14 / .NET 10.0 |
| Database | SQLite + FTS5 |
| ORM | `cslightdbgen.sqlitegen` (compile-time source gen) |
| Zulip client | `zulip-cs-lib` |
| CLI framework | `System.CommandLine` |
| MCP SDK | `ModelContextProtocol` |
| Service host | ASP.NET Minimal API + `BackgroundService` |

## Sequencing Rationale

1. **Jira first** — most complex data model (custom fields, dual API support),
   two reference implementations to port from. De-risks the architecture.
2. **Zulip second** — highest volume source, exercises FTS5 at scale, validates
   `zulip-cs-lib` integration early.
3. **Cross-referencing after two sources** — need real multi-source data to test
   linking and BM25 similarity.
4. **Service after sources** — the queue, scheduling, and incremental update
   logic needs real data sources to test against.
5. **Confluence & GitHub deferred** — they follow established patterns, just
   different APIs.
6. **MCP last** — read-only consumer, benefits from all data and indexes being
   available.
