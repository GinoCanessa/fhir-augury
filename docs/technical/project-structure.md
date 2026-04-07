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
├── src/                           # Source code
│   ├── common.props               # Shared MSBuild properties (versioning, TFM, lang)
│   ├── Directory.Build.props      # Auto-imports common.props
│   └── (13 projects)
└── tests/                         # Test code
    ├── Directory.Build.props      # Test-specific build properties
    └── (7 test projects)
```

## API Contracts

Shared HTTP API contract classes are defined in `FhirAugury.Common/Api/` and
used by all services for inter-service communication:

- **`SearchContracts`** — Search request/response types
- **`ItemContracts`** — Item retrieval contracts
- **`CrossReferenceContracts`** — Cross-reference query/response types
- **`IngestionContracts`** — Ingestion trigger and status contracts
- **`ServiceContracts`** — Service status, health, and endpoint contracts
- **`ContentFormats`** — Content format definitions

These contracts define the HTTP API surface that all source services implement
and that the orchestrator uses for fan-out communication.

## Source Projects

### `FhirAugury.Common`

Shared library compiled by all other projects. Provides shared API contracts
and reusable infrastructure.

```
FhirAugury.Common/
├── Caching/                  # IResponseCache, FileSystemResponseCache, CacheMode
├── Configuration/            # Shared configuration types (AuxiliaryDatabaseOptions,
│                             #   Bm25Options, DictionaryDatabaseOptions)
├── Database/                 # SourceDatabase abstract: SQLite WAL, FTS5 helpers;
│                             #   AuxiliaryDatabase: read-only stop words, lemmas,
│                             #   FHIR vocab loader;
│                             #   DictionaryDatabase: compiled dictionary builder
├── Api/                      # Shared HTTP API contracts (SearchContracts,
│                             #   ItemContracts, CrossReferenceContracts,
│                             #   IngestionContracts, ServiceContracts, ContentFormats)
├── Http/                     # HTTP helpers: AtlassianAuthHandler,
│                             #   HttpServiceLifecycle, HttpErrorMapper,
│                             #   TransientHttpExtensions
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

Jira source service (HTTP :5160).

```
FhirAugury.Source.Jira/
├── Api/                      # HTTP API controllers (common + Jira-specific endpoints)
├── Cache/                    # Jira-specific cache configuration
├── Configuration/            # JiraSourceOptions, auth settings
├── Database/                 # SQLite schema, record types, source-generated CRUD
├── Indexing/                 # FTS5 search and indexing
├── Ingestion/                # Download pipeline: fetch → cache → parse → store
├── Workers/                  # ScheduledIngestionWorker
├── Program.cs                # Kestrel HTTP server, DI registration
├── appsettings.json          # Default configuration
└── Dockerfile                # Service container image
```

### `FhirAugury.Source.Zulip`

Zulip source service (HTTP :5170). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Source.Confluence`

Confluence source service (HTTP :5180). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Source.GitHub`

GitHub source service (HTTP :5190). Same internal structure as
Source.Jira: `Api/`, `Cache/`, `Configuration/`, `Database/`, `Indexing/`,
`Ingestion/`, `Workers/`, `Program.cs`, `appsettings.json`, `Dockerfile`.

### `FhirAugury.Orchestrator`

Central coordinator (HTTP :5150).

```
FhirAugury.Orchestrator/
├── Api/                      # ContentController, IngestionController, ServicesController, SourceProxyController (HTTP API)
├── Configuration/            # Orchestrator settings, source endpoints
├── Database/                 # Orchestrator SQLite DB (scan state)
├── Health/                   # ServiceHealthMonitor (parallel checks, per-service timeouts)
├── Related/                  # RelatedItemFinder (multi-signal ranking)
├── Routing/                  # SourceHttpClient — named HTTP clients to source services
├── Search/                   # FreshnessDecay, ScoreNormalizer
├── Workers/                  # HealthCheckWorker, SourceReconnectionWorker
├── Program.cs                # Kestrel HTTP server, DI registration
├── appsettings.json          # Default configuration
└── Dockerfile                # Service container image
```

### `FhirAugury.McpShared`

Shared MCP library containing 15 tool implementations in 4 tool classes.

```
FhirAugury.McpShared/
├── Tools/                    # UnifiedTools.cs, ContentTools.cs, JiraTools.cs, ZulipTools.cs
├── McpHttpRegistration.cs    # Shared DI registration for MCP HTTP clients
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

Command-line interface (13 commands via JSON-in/JSON-out, HTTP to orchestrator).

```
FhirAugury.Cli/
├── Dispatch/                 # CommandDispatcher and handler implementations
│   └── Handlers/             # Per-command handlers (SearchHandler, GetHandler, etc.)
├── Models/                   # Request/response models and OutputEnvelope
├── Schemas/                  # SchemaGenerator for JSON schema output
├── HttpServiceClient.cs      # Creates HTTP connection to orchestrator
├── Program.cs                # Entry point: JSON-in/JSON-out with --json, --input, --help
└── FhirAugury.Cli.csproj
```

### `FhirAugury.DevUi`

Blazor Server operational dashboard (HTTP :5210). Connects to the orchestrator
via HTTP to display service status, trigger index rebuilds, and browse data.

```
FhirAugury.DevUi/
├── Components/               # App.razor, Layout, Pages, Routes.razor
├── Services/                 # OrchestratorClient (HTTP wrapper)
├── Program.cs                # Entry point: Blazor Server with ServiceDefaults
├── appsettings.json          # Default configuration
└── FhirAugury.DevUi.csproj
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
  Core, HTTP client, runtime), tracing (ASP.NET Core, HTTP) with OTLP
  export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set
- **Health endpoints** — `/health` (readiness) and `/alive` (liveness)
- **Service discovery** — Aspire `AddServiceDiscovery()` for HTTP clients
- **HTTP resilience** — `AddStandardResilienceHandler()` for all HTTP clients

### `FhirAugury.AppHost`

[.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) distributed
application host. Orchestrates all eight projects for local development with an
integrated dashboard. Confluence, Dev UI, MCP HTTP, and CLI use `WithExplicitStart()`
and must be started manually from the dashboard.

```
FhirAugury.AppHost/
├── AppHost.cs                       # Registers all projects with fixed HTTP ports
├── aspire.config.json               # Points to AppHost project
├── appsettings.json                 # Logging overrides (suppresses Aspire.Hosting.Dcp)
├── Properties/
│   └── launchSettings.json          # Dashboard and resource service endpoints
└── FhirAugury.AppHost.csproj        # Sdk="Aspire.AppHost.Sdk/13.2.1"
```

Uses `Aspire.AppHost.Sdk`. Registers each service project with pinned
HTTP ports (`isProxied: false`) and configures the orchestrator to
`WaitFor()` Jira, Zulip, and GitHub source services. Zulip and GitHub
also wait for Jira. Confluence, Dev UI, MCP HTTP, and CLI use `WithExplicitStart()`.

## Test Projects

| Project | Description |
|---------|-------------|
| `FhirAugury.Common.Tests` | Shared library: caching, database helpers, text utilities |
| `FhirAugury.Source.Jira.Tests` | Jira source service: ingestion, indexing, HTTP API |
| `FhirAugury.Source.Zulip.Tests` | Zulip source service: ingestion, indexing, HTTP API |
| `FhirAugury.Source.Confluence.Tests` | Confluence source service: ingestion, indexing, HTTP API |
| `FhirAugury.Source.GitHub.Tests` | GitHub source service: ingestion, indexing, HTTP API |
| `FhirAugury.Orchestrator.Tests` | Orchestrator: unified search, cross-refs, related items |
| `FhirAugury.McpShared.Tests` | MCP shared library: tool functions (xUnit + NSubstitute) |

## Build Configuration

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, implicit usings, timestamp-based versioning
  (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`
