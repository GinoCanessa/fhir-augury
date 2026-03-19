# Project Structure

This document describes the code organization of FHIR Augury.

## Repository Layout

```
fhir-augury/
├── fhir-augury.slnx              # Solution file (.slnx modern XML format)
├── README.md                      # Project overview
├── LICENSE                        # MIT license
├── Dockerfile                     # Multi-stage Docker build
├── docker-compose.yml             # Docker Compose service definition
├── cache/                         # Response cache directory (gitignored)
├── docs/                          # Documentation
│   ├── user/                      # User-facing documentation
│   └── technical/                 # Developer documentation
├── mcp-config-examples/           # Example MCP client configurations
│   ├── claude-desktop.json
│   └── http-client.json
├── plan/                          # Implementation plans
│   ├── v1/                        # V1 phase plans (phases 1-7)
│   └── feature/                   # Feature-specific plans
├── proposal/                      # Design proposals
│   ├── v1/                        # V1 architecture proposals
│   └── feature/                   # Feature proposals
├── src/                           # Source code
│   ├── common.props               # Shared MSBuild properties
│   ├── Directory.Build.props      # Auto-imports common.props
│   └── (10 projects)
└── tests/                         # Test code
    ├── Directory.Build.props      # Test-specific build properties
    ├── TestData/                   # Shared test fixtures
    └── (5 test projects)
```

## Source Projects

### `FhirAugury.Models`

Shared interfaces, enums, and configuration types with no dependencies on other
projects.

```
FhirAugury.Models/
├── IDataSource.cs            # Core interface for all source connectors
├── ISourceOptions.cs         # Configuration interface for sources
├── IResponseCache.cs         # Cache interface
├── IngestionResult.cs        # Result type for ingestion operations
├── AuguryConfiguration.cs    # Root configuration model
├── SourceConfiguration.cs    # Per-source configuration
├── Bm25Configuration.cs      # BM25 tuning parameters
├── CacheMode.cs              # Disabled/WriteThrough/CacheOnly/WriteOnly
└── Caching/
    ├── FileSystemResponseCache.cs  # File-system cache implementation
    └── NullResponseCache.cs        # No-op cache for disabled mode
```

### `FhirAugury.Database`

SQLite schema, FTS5 setup, and source-generated CRUD operations.

```
FhirAugury.Database/
├── DatabaseService.cs        # Singleton: init, connection management, WAL mode
├── FtsSetup.cs               # Creates FTS5 virtual tables and triggers
├── Records/
│   ├── JiraIssueRecord.cs    # Jira issue table (16+ custom fields)
│   ├── JiraCommentRecord.cs  # Jira comment table
│   ├── ZulipStreamRecord.cs  # Zulip stream table
│   ├── ZulipMessageRecord.cs # Zulip message table
│   ├── ConfluenceSpaceRecord.cs    # Confluence space table
│   ├── ConfluencePageRecord.cs     # Confluence page table
│   ├── ConfluenceCommentRecord.cs  # Confluence comment table
│   ├── GitHubRepoRecord.cs        # GitHub repository table
│   ├── GitHubIssueRecord.cs       # GitHub issue/PR table
│   ├── GitHubCommentRecord.cs     # GitHub comment table
│   ├── CrossRefLinkRecord.cs      # Cross-reference links
│   ├── KeywordRecord.cs           # BM25 keyword index
│   ├── CorpusRecord.cs            # BM25 corpus stats
│   ├── DocStatsRecord.cs          # BM25 document stats
│   ├── SyncStateRecord.cs         # Per-source sync tracking
│   └── IngestionLogRecord.cs      # Ingestion run history
└── (source-generated CRUD code at build time)
```

### `FhirAugury.Sources.Jira`

```
FhirAugury.Sources.Jira/
├── JiraSource.cs             # IDataSource: full/incremental/single-item download
├── JiraSourceOptions.cs      # Config: BaseUrl, AuthMode, Cookie, ApiToken, etc.
├── JiraAuthHandler.cs        # DelegatingHandler: cookie or Basic auth
├── JiraFieldMapper.cs        # Maps JSON → JiraIssueRecord/JiraCommentRecord
├── JiraCommentParser.cs      # Comment extraction facade
└── JiraXmlParser.cs          # Parses Jira XML RSS export format
```

### `FhirAugury.Sources.Zulip`

```
FhirAugury.Sources.Zulip/
├── ZulipSource.cs            # IDataSource: streams + message pagination
├── ZulipSourceOptions.cs     # Config: BaseUrl, Email, ApiKey, .zuliprc path
├── ZulipAuthHandler.cs       # DelegatingHandler: Basic auth, .zuliprc parsing
└── ZulipMessageMapper.cs     # Maps JSON → ZulipStreamRecord/ZulipMessageRecord
```

### `FhirAugury.Sources.Confluence`

```
FhirAugury.Sources.Confluence/
├── ConfluenceSource.cs       # IDataSource: spaces + pages + comments
├── ConfluenceSourceOptions.cs # Config: BaseUrl, AuthMode, Spaces, etc.
├── ConfluenceAuthHandler.cs  # DelegatingHandler: cookie or Basic auth
└── ConfluenceContentParser.cs # Confluence storage XHTML → plain text
```

### `FhirAugury.Sources.GitHub`

```
FhirAugury.Sources.GitHub/
├── GitHubSource.cs           # IDataSource: repos + issues/PRs + comments
├── GitHubSourceOptions.cs    # Config: PAT, Repositories, RateLimitBuffer
├── GitHubIssueMapper.cs      # Maps JSON → records, detects PRs
└── GitHubRateLimiter.cs      # DelegatingHandler: Bearer auth + rate limiting
```

### `FhirAugury.Indexing`

```
FhirAugury.Indexing/
├── FtsSearchService.cs       # FTS5 MATCH queries across all sources
├── SimilaritySearchService.cs # Find related items (BM25 + xref boost)
├── CrossRefLinker.cs         # Regex-based cross-reference extraction
├── CrossRefQueryService.cs   # Bidirectional xref queries, graph traversal
├── ScoreNormalizer.cs        # Min-max normalization for cross-source ranking
├── TextSanitizer.cs          # HTML/Markdown stripping, Unicode normalization
└── Bm25/
    ├── Bm25Calculator.cs     # Full/incremental BM25 index builds
    ├── Tokenizer.cs          # FHIR-aware tokenization
    ├── KeywordClassifier.cs  # Token type classification
    ├── FhirVocabulary.cs     # 120+ FHIR resource names, 30+ operations
    └── StopWords.cs          # ~170 English stop words
```

### `FhirAugury.Cli`

```
FhirAugury.Cli/
├── Program.cs                # Entry point: RootCommand with 11 commands
├── ServiceClient.cs          # HTTP client wrapper for the service API
├── Commands/
│   ├── DownloadCommand.cs    # Full download + shared auth builder methods
│   ├── SyncCommand.cs        # Incremental sync with SyncStateRecord tracking
│   ├── IngestCommand.cs      # Single-item ingestion
│   ├── IndexCommand.cs       # FTS5/BM25/xref index management
│   ├── SearchCommand.cs      # FTS search
│   ├── GetCommand.cs         # Direct DB item retrieval
│   ├── SnapshotCommand.cs    # Rich item rendering
│   ├── RelatedCommand.cs     # Similarity-based related items
│   ├── StatsCommand.cs       # DB statistics
│   ├── ServiceCommand.cs     # HTTP client for remote service
│   └── CacheCommand.cs       # File-system cache management
└── OutputFormatters/
    └── OutputFormatter.cs    # Table/JSON/Markdown formatting
```

### `FhirAugury.Service`

```
FhirAugury.Service/
├── Program.cs                # Entry point: config, DI, endpoint mapping
├── IngestionQueue.cs         # Bounded channel (capacity 100)
├── Workers/
│   ├── IngestionWorker.cs    # BackgroundService: dequeues and runs ingestions
│   └── ScheduledIngestionService.cs  # BackgroundService: per-source timers
└── Endpoints/
    ├── AuguryApiExtensions.cs # Maps /api/v1 route group
    ├── SearchEndpoints.cs     # GET /api/v1/search
    ├── IngestEndpoints.cs     # POST/GET ingestion control
    ├── JiraEndpoints.cs       # Jira-specific endpoints
    ├── ZulipEndpoints.cs      # Zulip-specific endpoints
    ├── ConfluenceEndpoints.cs # Confluence-specific endpoints
    ├── GitHubEndpoints.cs     # GitHub-specific endpoints
    ├── XRefEndpoints.cs       # Cross-reference endpoints
    └── StatsEndpoints.cs      # Statistics endpoints
```

### `FhirAugury.Mcp`

```
FhirAugury.Mcp/
├── Program.cs                # Entry point: DI, stdio transport, tool discovery
└── Tools/
    ├── SearchTools.cs        # 5 search tools (unified + per-source)
    ├── RetrievalTools.cs     # 5 retrieval tools (get individual items)
    ├── ListingTools.cs       # 6 listing tools (browse collections)
    ├── RelationshipTools.cs  # 2 relationship tools (related, xrefs)
    ├── SnapshotTools.cs      # 3 snapshot tools (rich composite views)
    └── AdminTools.cs         # 2 admin tools (stats, sync status)
```

## Test Projects

### `FhirAugury.Database.Tests`

~56 tests covering SQLite CRUD operations, table creation, and FTS5 trigger
behavior for all four sources. Uses in-memory SQLite.

### `FhirAugury.Indexing.Tests`

~45 tests covering BM25 scoring, tokenization, cross-reference detection,
score normalization, and unified cross-source search.

### `FhirAugury.Sources.Tests`

~85 tests covering source parsers/mappers (Jira XML/JSON, Zulip, Confluence
XHTML, GitHub), text sanitization, and the file-system caching layer.

### `FhirAugury.Integration.Tests`

~22 tests covering HTTP API endpoints via `WebApplicationFactory<Program>`.
Tests ingestion control, search, and statistics endpoints.

### `FhirAugury.Mcp.Tests`

~44 tests covering all MCP tool functions: search, retrieval, listing,
relationships, snapshots, and admin tools.

### Test Data (`tests/TestData/`)

| File | Description |
|------|-------------|
| `sample-jira-export.xml` | Jira RSS/XML export with 2 issues and comments |
| `sample-jira-issue.json` | Single Jira issue in JSON format |
| `sample-github-issue.json` | GitHub issue with labels, assignees, milestone |
| `sample-github-pr.json` | GitHub PR with merge state and branches |
| `sample-confluence-page.json` | Confluence page in JSON format |
| `sample-confluence-storage.xml` | Confluence XHTML storage format |
| `sample-zulip-messages.json` | Zulip messages in JSON format |
