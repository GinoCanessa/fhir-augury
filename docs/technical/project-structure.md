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
│   └── (18 projects)
└── tests/                         # Test code
    ├── Directory.Build.props      # Test-specific build properties
    └── (16 test projects)
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
- **`JiraProcessingContracts`** — Jira issue summary and local-processing DTOs shared by Source.Jira, orchestrator proxies, and Processing.Jira services

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
│                             #   IngestionContracts, ServiceContracts,
│                             #   ContentFormats, JiraProcessingContracts)
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
Additionally includes FHIR artifact parsing (`StructureDefinitionIndexer`,
`CanonicalArtifactIndexer`, `FshArtifactIndexer`) and file content indexing
(`GitHubFileContentIndexer`, `GitHubRepoCloner`) using the Parsing.Fhir and
Parsing.Fsh libraries.

### `FhirAugury.Parsing.Fhir`

FHIR resource parsing library using the Firely .NET SDK (Hl7.Fhir.R5).

```
FhirAugury.Parsing.Fhir/
├── FhirContentParser.cs          # TryParseStructureDefinition, TryParseCanonicalArtifact,
│                                 #   TryParseBundle — auto-detects XML/JSON format
├── ArtifactClassifier.cs         # Classifies SDs by kind/derivation/type → PrimitiveType,
│                                 #   LogicalModel, Extension, ComplexType, Profile, Resource
├── Models/
│   ├── StructureDefinitionInfo.cs # Full SD metadata (url, kind, elements, WG, FMM, etc.)
│   ├── CanonicalArtifactInfo.cs   # CodeSystem, ValueSet, ConceptMap, SearchParameter, etc.
│   ├── ElementInfo.cs             # Differential element details
│   ├── ElementTypeInfo.cs         # Element type references
│   └── ExtensionContext.cs        # Extension context metadata
└── FhirAugury.Parsing.Fhir.csproj
```

### `FhirAugury.Parsing.Fsh`

FSH (FHIR Shorthand) parsing library using ANTLR4 via the fsh-processor.

```
FhirAugury.Parsing.Fsh/
├── FshContentParser.cs           # ParseFile/ParseContent — extracts Profile, Extension,
│                                 #   Resource, Logical, CodeSystem, ValueSet, Instance defs;
│                                 #   ConstructCanonicalUrl — builds URLs from def + config
├── SushiConfigParser.cs          # TryParse — line-by-line YAML parser for sushi-config.yaml;
│                                 #   extracts id, canonical, name, fhirVersion, resources
├── Models/
│   ├── FshDefinitionInfo.cs      # Kind, Name, Id, Parent, Title, Description, URL, etc.
│   ├── FshDefinitionKind.cs      # Enum: Profile, Extension, Resource, Logical, CodeSystem,
│   │                             #   ValueSet, DefinitionalInstance
│   └── SushiConfig.cs            # Id, Canonical, Name, FhirVersion, PathResource, etc.
└── FhirAugury.Parsing.Fsh.csproj
```

### `FhirAugury.Processing.Common`

Shared substrate for future `Processing.*` services. It provides Processing-service configuration, API contracts, lifecycle state, queue/store/handler abstractions, a concurrency-limited runner, hosted-service integration, endpoint mapping helpers, and CsLightDbGen base records for common processing columns. It does not implement a concrete Jira, preparer, or planner processor.

```
FhirAugury.Processing.Common/
├── Api/                      # Processing status, lifecycle, and queue-stat contracts
├── Configuration/            # ProcessingServiceOptions
├── Database/                 # ProcessingDatabase and generated-record base columns
├── Hosting/                  # DI, lifecycle, hosted service, endpoint mappings
└── Queue/                    # Work-item store/handler contracts and runner
```


### `FhirAugury.Processing.Jira.Common`

Shared Jira-specific processing layer used by future concrete `Processing.Jira.*` services. It consumes `FhirAugury.Processing.Common` for lifecycle/queue execution and adds Jira filters, source-ticket persistence, upstream discovery, agent command rendering, and the uniform `POST /processing/tickets/{key}` enqueue endpoint.

```
FhirAugury.Processing.Jira.Common/
├── Agent/                    # Command rendering, CLI process runner, token providers
├── Api/                      # Single-ticket enqueue endpoint contracts and mappings
├── Configuration/            # Processing:Jira options and discovery-source enum
├── Database/                 # SQLite source-ticket queue and lifecycle store
├── Discovery/                # Source.Jira/orchestrator clients and sync service
├── Filtering/                # Null/default/empty/restrict filter semantics
├── Hosting/                  # DI registrations for concrete Jira processors
└── Processing/               # Work-item handler that invokes the agent runner
```

### `FhirAugury.Processor.Jira.Fhir.Preparer`

Concrete Jira/FHIR Processing service (HTTP :5171) that queues triaged Jira
issues, invokes `ticket-prep`, and persists structured prepared-ticket output.
It composes `FhirAugury.Processing.Common`, `FhirAugury.Processing.Jira.Common`,
and its persistence project without owning common queue mechanics.

### `FhirAugury.Processor.Jira.Fhir.Planner`

Concrete Jira/FHIR Processing service (HTTP :5172) that queues resolved
change-required Jira issues, invokes `ticket-plan`, and stores structured
implementation-plan output in normalized SQLite tables.

```
FhirAugury.Processor.Jira.Fhir.Planner/
├── Configuration/             # Planner defaults plus Processing:Planner:RepoFilters validation/rendering
├── Database/                  # PlannerDatabase, output schema, records, ReplacementLineJson
├── Processing/                # Planner token provider and cleanup-aware Jira ticket handler
├── Program.cs                 # HTTP-only Processing service composition
└── appsettings.json           # Defaults: DB path, Jira filters, port 5172
```

The planner uses the shared Processing/Jira layers for lifecycle endpoints,
source-ticket queueing, filters, command rendering, and agent execution. It is a
sibling of the preparer and does not depend on preparer code or records.

### `FhirAugury.Processor.Jira.Fhir.Applier`

Concrete Jira/FHIR Processing service (HTTP :5173) that consumes completed
plans from the Planner database and runs an agent in a per-(ticket, repo)
git worktree to actually apply each planned change. After the agent finishes,
the applier runs the per-repo `BuildCommand`, diffs the build output against
a pre-built repo baseline, copies the surviving differences into a per-ticket
output directory, and locally commits the worktree (success or failure). A
push HTTP API (`POST /api/v1/applied-tickets/{ticketKey}/push`) lets an
operator move successful local commits to the upstream remote on demand.

```
FhirAugury.Processor.Jira.Fhir.Applier/
├── Configuration/             # ApplierOptions / ApplierAuthOptions / per-repo settings + Jira defaults
├── Controllers/               # AppliedTicketsController (push API)
├── Database/                  # ApplierDatabase, applied_* records, planner read-only DB, write store
├── Processing/                # PlannerWorkQueue + ApplierTicketHandler (per-ticket orchestrator)
├── Push/                      # IGitPushService / GitPushService + push-response DTOs
├── Workspace/                 # Repo workspace lifecycle (clone, baseline, worktree, lock, diff, commit)
├── Program.cs                 # HTTP composition + queue runner + hosted services
└── appsettings.json           # Defaults: working dir, planner DB, repos, commit templates, port 5173
```

The applier polls the Planner database via `PlannerWorkQueue` to discover
completed plans, queues them in its own SQLite `applied_ticket_queue_items`
table, and processes each item via the shared
`ProcessingHostedService<AppliedTicketQueueItemRecord>` runner. Per-(ticket,
repo) outcomes (`Success` / `AgentFailed` / `BuildFailed` / `DiffFailed` /
`WorktreeFailed` / `RepoNotConfigured`) live in the `applied_*` tables; the
queue's `ProcessingStatus` reflects only transport / runtime outcome so a
genuinely-failed agent run still completes the queue item normally.

### `FhirAugury.Orchestrator`

Central coordinator (HTTP :5150).

```
FhirAugury.Orchestrator/
├── Api/                      # ContentController, IngestionController, ServicesController, LifecycleController (HTTP API)
├── Controllers/Proxies/      # Typed per-source proxy controllers — JiraProxyController,
│                             #   ZulipProxyController, ConfluenceProxyController,
│                             #   GitHubProxyController. Cover ~98 source endpoints under
│                             #   /api/v1/{name}/...; replace the removed
│                             #   GenericSourceProxyController.
├── Controllers/              # OrchestratorSelfController (preserves /api/v1/source/orchestrator/...
│                             #   for self-metadata; the only surviving "source/" route)
├── Configuration/            # Orchestrator settings, source and Processing endpoints
├── Database/                 # Orchestrator SQLite DB (scan state)
├── Health/                   # ServiceHealthMonitor (parallel source/Processing checks)
├── Related/                  # RelatedItemFinder (multi-signal ranking)
├── Routing/                  # SourceHttpClient and ProcessingHttpClient named clients
├── Search/                   # FreshnessDecay, ScoreNormalizer
├── Workers/                  # HealthCheckWorker, SourceReconnectionWorker
├── Program.cs                # Kestrel HTTP server, DI registration
├── appsettings.json          # Default configuration
└── Dockerfile                # Service container image
```

### `FhirAugury.McpShared`

Shared MCP library containing tool implementations across 16 tool classes
(2 cross-source — `UnifiedTools`, `ContentTools` — plus 14 source-scoped
families added in the 2026-04 sync that mirror the typed orchestrator
proxies one-for-one: `JiraItemsTools`, `JiraDimensionTools`,
`JiraWorkGroupTools`, `JiraProjectTools`, `JiraLocalProcessingTools`,
`JiraSpecsTools`, `ZulipItemsTools`, `ZulipMessagesTools`,
`ZulipStreamsTools`, `ZulipThreadsTools`, `ConfluenceItemsTools`,
`ConfluencePagesTools`, `GitHubItemsTools`, `GitHubReposTools`; the
legacy umbrella `JiraTools` and `ZulipTools` classes remain for
backwards-compatible cross-source helpers).

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

Command-line interface (JSON-in/JSON-out, HTTP to orchestrator). Each
handler under `Dispatch/Handlers/` corresponds 1:1 to one logical command;
the source-scoped families (`JiraItemsHandler`, `JiraDimensionHandler`,
`JiraWorkGroupHandler`, `JiraProjectHandler`, `JiraLocalProcessingHandler`,
`JiraSpecsHandler`, `ZulipItemsHandler`, `ZulipMessagesHandler`,
`ZulipStreamsHandler`, `ZulipThreadsHandler`, `ConfluenceItemsHandler`,
`ConfluencePagesHandler`, `GitHubItemsHandler`, `GitHubReposHandler`)
were added in the 2026-04 sync alongside the typed orchestrator proxies.
The `ingest` handler's actions were renamed at the same time
(`rebuild`→`reingest`, `index`→`reindex`; no aliases).

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
application host. Orchestrates source services, Processing services, and developer
tools for local development with an integrated dashboard. Confluence, Dev UI, MCP HTTP, and CLI use `WithExplicitStart()`
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
| `FhirAugury.Parsing.Fhir.Tests` | FHIR resource parsing: StructureDefinitions, canonical artifacts |
| `FhirAugury.Parsing.Fsh.Tests` | FSH parsing: definitions, sushi-config, canonical URL construction |
| `FhirAugury.Processor.Jira.Fhir.Preparer.Tests` | Preparer service: persistence, handler, API, smoke tests |
| `FhirAugury.Processor.Jira.Fhir.Planner.Tests` | Planner service: options, schema, handler, ticket-plan DB contract |
| `FhirAugury.Processor.Jira.Fhir.Applier.Tests` | Applier service: schema, planner-discovery store, workspace lifecycle, output diff, commit, handler, push |

## Build Configuration

- **`src/common.props`** — Shared by all source projects: targets `net10.0`,
  C# 14, nullable enabled, implicit usings, timestamp-based versioning
  (`yyyy.MMdd.HHmm`)
- **`src/Directory.Build.props`** — Imports `common.props` for all source
  projects
- **`tests/Directory.Build.props`** — Configures test projects: `net10.0`,
  C# 14, `IsPackable=false`
