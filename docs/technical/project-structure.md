# Project Structure

This document describes the code organization of FHIR Augury.

## Repository Layout

```
fhir-augury/
├── fhir-augury.slnx              # Solution file (.slnx modern XML format)
├── README.md                      # Project overview
├── LICENSE                        # MIT license
├── Dockerfile                     # Multi-stage Docker build
├── docker-compose.yml             # Docker Compose (5 services, 3 profiles, 9 volumes)
├── cache/                         # Response cache directory (gitignored)
├── docs/                          # Documentation
│   ├── user/                      # User-facing documentation
│   └── technical/                 # Developer documentation
├── mcp-config-examples/           # Example MCP client configurations
├── plan/                          # Implementation plans
├── proposal/                      # Design proposals
├── protos/                        # Protocol Buffer definitions (6 files)
│   ├── source_service.proto       # SourceService — common contract for all sources
│   ├── orchestrator.proto         # OrchestratorService — unified API
│   ├── jira.proto                 # JiraService — Jira-specific operations
│   ├── zulip.proto                # ZulipService — Zulip-specific operations
│   ├── confluence.proto           # ConfluenceService — Confluence-specific operations
│   └── github.proto               # GitHubService — GitHub-specific operations
├── src/                           # Source code
│   ├── common.props               # Shared MSBuild properties (versioning, TFM, lang)
│   ├── Directory.Build.props      # Auto-imports common.props
│   └── (8 projects)
└── tests/                         # Test code
    ├── Directory.Build.props      # Test-specific build properties
    └── (7 test projects)
```

## Proto Files

Six Protocol Buffer files define the gRPC service contracts:

### `source_service.proto`

Common contract implemented by all source services:

- `Search`, `GetItem`, `ListItems`, `GetRelated` — query operations
- `GetSnapshot`, `GetContent` — content retrieval
- `StreamSearchableText` — streams text for cross-reference scanning
- `TriggerIngestion`, `GetIngestionStatus`, `RebuildFromCache` — ingestion control
- `GetStats`, `HealthCheck` — monitoring

### `orchestrator.proto`

Orchestrator's unified API:

- `UnifiedSearch`, `FindRelated`, `GetCrossReferences` — aggregated queries
- `GetItem`, `GetSnapshot`, `GetContent` — proxied to appropriate source
- `TriggerSync`, `GetServicesStatus` — service management
- `TriggerXRefScan`, `NotifyIngestionComplete` — cross-reference system
- `GetServiceEndpoints` — service discovery for direct access

### Source-Specific Protos

- **`jira.proto`** (`JiraService`) — `GetIssueComments`, `GetIssueLinks`,
  `ListByWorkGroup`, `ListBySpecification`, `QueryIssues`, `ListSpecArtifacts`,
  `GetIssueNumbers`, `GetIssueSnapshot`
- **`zulip.proto`** (`ZulipService`) — `GetThread`, `ListStreams`, `ListTopics`,
  `GetMessagesByUser`, `QueryMessages`, `GetThreadSnapshot`
- **`confluence.proto`** (`ConfluenceService`) — `GetPageComments`,
  `GetPageChildren`, `GetPageAncestors`, `ListSpaces`, `GetLinkedPages`,
  `GetPagesByLabel`, `GetPageSnapshot`
- **`github.proto`** (`GitHubService`) — `GetIssueComments`,
  `GetPullRequestDetails`, `GetRelatedCommits`, `GetPullRequestForCommit`,
  `GetCommitsForPullRequest`, `SearchCommits`, `GetJiraReferences`,
  `ListRepositories`, `ListByLabel`, `ListByMilestone`, `QueryByArtifact`,
  `GetIssueSnapshot`

## Source Projects

### `FhirAugury.Common`

Shared library compiled by all other projects. Compiles the proto files and
provides reusable infrastructure.

```
FhirAugury.Common/
├── Caching/                  # IResponseCache, FileSystemResponseCache, CacheMode
├── Configuration/            # Shared configuration types (AuxiliaryDatabaseOptions,
│                             #   Bm25Options, DictionaryDatabaseOptions)
├── Database/                 # SourceDatabase abstract: SQLite WAL, FTS5 helpers;
│                             #   AuxiliaryDatabase: read-only stop words, lemmas,
│                             #   FHIR vocab loader;
│                             #   DictionaryDatabase: compiled dictionary builder
├── Grpc/                     # gRPC client helpers, GrpcErrorMapper,
│                             #   AtlassianAuthHandler, SourceServiceLifecycle
├── Ingestion/                # IIngestionPipeline, IngestionWorkQueue,
│                             #   ScheduledIngestionWorker<T>
├── Text/                     # CrossRefPatterns, FhirVocabulary (100+ resources,
│                             #   30+ operations, extensible via aux DB),
│                             #   Tokenizer, TokenCounter (shared count+classify),
│                             #   Lemmatizer (inflection→lemma normalization),
│                             #   StopWords (extensible via aux DB),
│                             #   TextSanitizer, KeywordClassifier, CsvParser,
│                             #   FormatHelpers, FtsQueryHelper, TextPatterns
└── HttpRetryHelper.cs        # Retry with exponential backoff
```

### `FhirAugury.Source.Jira`

Jira source service (HTTP :5160, gRPC :5161).

```
FhirAugury.Source.Jira/
├── Api/                      # JiraGrpcService (SourceService + JiraService impl)
├── Cache/                    # Jira-specific cache configuration
├── Configuration/            # JiraSourceOptions, auth settings
├── Database/                 # SQLite schema, record types, source-generated CRUD
├── Indexing/                 # FTS5 search and indexing
├── Ingestion/                # Download pipeline: fetch → cache → parse → store
├── Workers/                  # ScheduledIngestionWorker
├── Program.cs                # Dual-port Kestrel (HTTP + gRPC), DI registration
├── appsettings.json          # Default configuration
└── Dockerfile                # Service container image
```

### `FhirAugury.Source.Zulip`

Zulip source service (HTTP :5170, gRPC :5171). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Source.Confluence`

Confluence source service (HTTP :5180, gRPC :5181). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Source.GitHub`

GitHub source service (HTTP :5190, gRPC :5191). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Orchestrator`

Central coordinator (HTTP :5150, gRPC :5151).

```
FhirAugury.Orchestrator/
├── Api/                      # OrchestratorGrpcService, OrchestratorHttpApi
├── Configuration/            # Orchestrator settings, source endpoints
├── CrossRef/                 # CrossRefLinker, StructuralLinker
├── Database/                 # Orchestrator SQLite DB (cross-references)
├── Health/                   # ServiceHealthMonitor (parallel checks, per-service timeouts)
├── Related/                  # RelatedItemFinder (4-signal ranking)
├── Routing/                  # SourceRouter — creates gRPC channels to sources
├── Search/                   # UnifiedSearchService, CrossRefBooster, FreshnessDecay,
│                             #   ScoreNormalizer
├── Workers/                  # HealthCheckWorker, XRefScanWorker (every 30 min)
├── Program.cs                # Dual-port Kestrel, DI registration
├── appsettings.json          # Default configuration
└── Dockerfile                # Service container image
```

### `FhirAugury.Mcp`

MCP server for LLM agents (stdio transport, 17 tools via gRPC to orchestrator).

```
FhirAugury.Mcp/
├── Tools/                    # MCP tool implementations (search, retrieval, etc.)
├── Program.cs                # Entry point: DI, stdio transport, tool discovery
└── FhirAugury.Mcp.csproj
```

### `FhirAugury.Cli`

Command-line interface (10+ commands via gRPC to orchestrator).

```
FhirAugury.Cli/
├── Commands/                 # Command implementations
├── OutputFormatters/         # Table/JSON/Markdown formatting
├── GrpcClientFactory.cs      # Creates gRPC channel to orchestrator
├── Program.cs                # Entry point: RootCommand with subcommands
└── FhirAugury.Cli.csproj
```

## Test Projects

| Project | Description |
|---------|-------------|
| `FhirAugury.Common.Tests` | Shared library: caching, database helpers, text utilities |
| `FhirAugury.Source.Jira.Tests` | Jira source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Zulip.Tests` | Zulip source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Confluence.Tests` | Confluence source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.GitHub.Tests` | GitHub source service: ingestion, indexing, gRPC API |
| `FhirAugury.Orchestrator.Tests` | Orchestrator: unified search, cross-refs, related items |
| `FhirAugury.Mcp.Tests` | MCP server tool functions |

## Build Configuration

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, implicit usings, timestamp-based versioning
  (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`
