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
│   └── (12 projects)
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
- `GetItemCrossReferences` — cross-references for a specific item

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
- **`zulip.proto`** (`ZulipService`) — `GetThread`, `ListStreams`, `GetStream`,
  `UpdateStream`, `ListTopics`, `GetMessagesByUser`, `QueryMessages`,
  `GetThreadSnapshot`
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

### `FhirAugury.McpShared`

Shared MCP library containing all 16 tool implementations and service
registration logic.

```
FhirAugury.McpShared/
├── Tools/                    # UnifiedTools.cs, JiraTools.cs, ZulipTools.cs
├── McpServiceRegistration.cs # Shared DI registration for MCP tools and gRPC clients
└── FhirAugury.McpShared.csproj
```

### `FhirAugury.McpStdio`

Stdio-based MCP server (generic .NET Host, packaged as `fhir-augury-mcp`
dotnet tool). No Aspire dependency.

```
FhirAugury.McpStdio/
├── Program.cs                # Entry point: generic Host, stdio transport
└── FhirAugury.McpStdio.csproj
```

### `FhirAugury.McpHttp`

HTTP/SSE-based MCP server (ASP.NET Core WebApplication, port 5200, `/mcp`
endpoint). Includes Aspire ServiceDefaults.

```
FhirAugury.McpHttp/
├── Program.cs                # Entry point: WebApplication, HTTP/SSE transport
├── appsettings.json          # Default configuration
├── Properties/
│   └── launchSettings.json   # Launch profile (port 5200)
└── FhirAugury.McpHttp.csproj
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

### `FhirAugury.ServiceDefaults`

Shared [Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) project
referenced by all web services. Provides OpenTelemetry, health checks, service
discovery, and HTTP resilience.

```
FhirAugury.ServiceDefaults/
├── Extensions.cs                    # AddServiceDefaults(), ConfigureOpenTelemetry(),
│                                    #   AddDefaultHealthChecks(), MapDefaultEndpoints()
└── FhirAugury.ServiceDefaults.csproj  # IsAspireSharedProject=true
```

Key capabilities:

- **OpenTelemetry** — Logging (formatted messages, scopes), metrics (ASP.NET
  Core, HTTP client, runtime), tracing (ASP.NET Core, gRPC, HTTP) with OTLP
  export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set
- **Health endpoints** — `/health` (readiness) and `/alive` (liveness)
- **Service discovery** — Aspire `AddServiceDiscovery()` for HTTP clients
- **HTTP resilience** — `AddStandardResilienceHandler()` for all HTTP clients

### `FhirAugury.AppHost`

[.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) distributed
application host. Orchestrates all six services for local development with an
integrated dashboard. Confluence uses `WithExplicitStart()` and must be started
manually from the dashboard.

```
FhirAugury.AppHost/
├── AppHost.cs                       # Registers all projects with fixed HTTP/gRPC ports
├── aspire.config.json               # Points to AppHost project
├── appsettings.json                 # Logging overrides (suppresses Aspire.Hosting.Dcp)
├── Properties/
│   └── launchSettings.json          # Dashboard and resource service endpoints
└── FhirAugury.AppHost.csproj        # Sdk="Aspire.AppHost.Sdk/13.2.0"
```

Uses `Aspire.AppHost.Sdk`. Registers each service project with pinned
HTTP/gRPC ports (`isProxied: false`) and configures the orchestrator to
`WaitFor()` all source services.

## Test Projects

| Project | Description |
|---------|-------------|
| `FhirAugury.Common.Tests` | Shared library: caching, database helpers, text utilities |
| `FhirAugury.Source.Jira.Tests` | Jira source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Zulip.Tests` | Zulip source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.Confluence.Tests` | Confluence source service: ingestion, indexing, gRPC API |
| `FhirAugury.Source.GitHub.Tests` | GitHub source service: ingestion, indexing, gRPC API |
| `FhirAugury.Orchestrator.Tests` | Orchestrator: unified search, cross-refs, related items |
| `FhirAugury.McpShared.Tests` | MCP shared library: tool functions (xUnit + NSubstitute + Grpc.Core.Testing) |

## Build Configuration

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, implicit usings, timestamp-based versioning
  (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`
