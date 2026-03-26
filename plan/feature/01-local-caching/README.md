# Feature: Local Response Caching — Implementation Plan

**Goal:** Add a file-system cache layer between data sources (Jira, Zulip,
Confluence) and their remote APIs so that raw API responses are persisted
locally, re-launches skip already-fetched data, and pre-populated directories
can be ingested without network access.

**Proposal:** [proposal/feature/01-local-caching](../../../proposal/feature/01-local-caching/README.md)

**Status:** Pending

## Plan Documents

| Section | Document | Goal | Dependencies |
|---------|----------|------|--------------|
| 1 | [Core Abstractions](section-1-core-abstractions.md) | `IResponseCache`, `CacheMode`, configuration records | — |
| 2 | [Cache Implementation](section-2-cache-implementation.md) | `FileSystemResponseCache`, file naming, metadata | Section 1 |
| 3 | [Jira Integration](section-3-jira-integration.md) | Write-through & cache-only for Jira source | Section 2 |
| 4 | [Zulip Integration](section-4-zulip-integration.md) | Write-through & cache-only for Zulip source (weekly + daily) | Section 2 |
| 5 | [Confluence Integration](section-5-confluence-integration.md) | Write-through & cache-only for Confluence source | Section 2 |
| 6 | [Configuration & DI](section-6-configuration-di.md) | Config binding, service wiring, `appsettings.json` | Sections 1–5 |
| 7 | [CLI Surface](section-7-cli-surface.md) | `--cache-path`, `--cache-mode` options, `cache` commands | Section 6 |
| 8 | [Tests](section-8-tests.md) | Unit tests, integration tests, test data | All above |
| 9 | [Documentation & Docker](section-9-docs-docker.md) | Docs updates, Dockerfile, docker-compose | All above |

## Architecture Impact

```
                  ┌──────────────┐
                  │  CLI / API   │
                  └──────┬───────┘
                         │
                  ┌──────▼───────┐
                  │  IDataSource │  (Jira, Zulip, Confluence)
                  └──────┬───────┘
                         │
              ┌──────────▼──────────┐
              │   IResponseCache    │  ◄── NEW
              │ FileSystemResponse  │
              │       Cache         │
              └──────────┬──────────┘
                         │
           ┌─────────────┼─────────────┐
           │             │             │
     ┌─────▼─────┐ ┌────▼────┐ ┌──────▼──────┐
     │ Local FS  │ │  HTTP   │ │  Pre-seeded │
     │  Cache    │ │  API    │ │  Directory  │
     └───────────┘ └─────────┘ └─────────────┘
```

## Key Design Decisions (from Proposal)

1. **GitHub excluded** — GitHub data lives in cloned git repos; no API cache needed
2. **Date-based batch files** for Jira and Zulip; one-file-per-entity for Confluence
3. **Weekly batches** (`_WeekOf_`) for Zulip initial download; daily (`DayOf_`) for incremental
4. **Oldest-to-newest** loading order with upsert semantics
5. **Per-source metadata files** (`_meta_*.json`), not per-file sidecars
6. **No automatic eviction** — unlimited cache; users run `cache clear` to reclaim space
7. **Four cache modes:** `Disabled`, `WriteThrough`, `CacheOnly`, `WriteOnly`

## New & Modified Files Summary

| Action | Path | Description |
|--------|------|-------------|
| Create | `src/FhirAugury.Models/Caching/IResponseCache.cs` | Cache interface |
| Create | `src/FhirAugury.Models/Caching/CacheMode.cs` | Enum |
| Create | `src/FhirAugury.Models/Caching/CacheConfiguration.cs` | Config records |
| Create | `src/FhirAugury.Models/Caching/CacheFileNaming.cs` | Shared file naming/sorting |
| Create | `src/FhirAugury.Models/Caching/CacheMetadata.cs` | Metadata record types |
| Create | `src/FhirAugury.Models/Caching/FileSystemResponseCache.cs` | Concrete implementation |
| Modify | `src/FhirAugury.Sources.Jira/JiraSourceOptions.cs` | Add cache properties |
| Modify | `src/FhirAugury.Sources.Jira/JiraSource.cs` | Wire cache into download loop |
| Modify | `src/FhirAugury.Sources.Zulip/ZulipSourceOptions.cs` | Add cache properties |
| Modify | `src/FhirAugury.Sources.Zulip/ZulipSource.cs` | Wire cache into download loop |
| Modify | `src/FhirAugury.Sources.Confluence/ConfluenceSourceOptions.cs` | Add cache properties |
| Modify | `src/FhirAugury.Sources.Confluence/ConfluenceSource.cs` | Wire cache into download loop |
| Modify | `src/FhirAugury.Service/AuguryConfiguration.cs` | Add `CacheConfiguration` |
| Modify | `src/FhirAugury.Service/appsettings.json` | Add `Cache` section |
| Modify | `src/FhirAugury.Service/Program.cs` | Register `IResponseCache` in DI |
| Modify | `src/FhirAugury.Cli/Commands/DownloadCommand.cs` | Add `--cache-path`, `--cache-mode` |
| Create | `src/FhirAugury.Cli/Commands/CacheCommand.cs` | `cache stats`, `cache clear` |
| Create | `tests/FhirAugury.Sources.Tests/Caching/` | Cache unit tests |
| Create | `tests/FhirAugury.Integration.Tests/CacheIntegrationTests.cs` | Cache-only ingestion tests |
| Modify | `docs/configuration.md` | Cache settings documentation |
| Modify | `docs/cli-reference.md` | New CLI commands |
