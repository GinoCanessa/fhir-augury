# Configuration Reference

Complete configuration reference for all FHIR Augury v2 services.

## Configuration Sources

Each service reads configuration from (in priority order):

1. `appsettings.json` (built-in defaults)
2. `appsettings.local.json` (optional, gitignored)
3. Environment variables with service-specific prefix
4. User secrets (Development environment only)

## Environment Variable Naming

Environment variables use the service prefix followed by double-underscore
(`__`) separators for nested keys, following the standard
[ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/)
pattern.

**Pattern:** `FHIR_AUGURY_{SERVICE}__{Section}__{Key}`

**Example:** `FHIR_AUGURY_JIRA__Jira__Cookie=JSESSIONID=...`

## Jira Source Service

**Prefix:** `FHIR_AUGURY_JIRA_`
**Port:** 5160

### appsettings.json

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "cookie",
    "CachePath": "./cache",
    "DatabasePath": "./data/jira.db",
    "SyncSchedule": "01:00:00",
    "MinSyncAge": "04:00:00",
    "ReloadFromCacheOnStartup": false,
    "DefaultProject": "FHIR",
    "DefaultJql": null,
    "OrchestratorAddress": null,
    "IngestionPaused": false,
    "Ports": {
      "Http": 5160
    },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75,
      "UseLemmatization": true,
      "FtsTokenizer": null
    },
    "AuxiliaryDatabase": {
      "AuxiliaryDatabasePath": null,
      "FhirSpecDatabasePath": null
    },
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://jira.hl7.org` | Jira server URL |
| `AuthMode` | string | `cookie` | Authentication: `cookie` or `apitoken` |
| `Cookie` | string | | Session cookie for cookie auth |
| `ApiToken` | string | | API token for apitoken auth |
| `Email` | string | | Email for apitoken auth |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/jira.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `01:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `DefaultProject` | string | `FHIR` | Default Jira project |
| `DefaultJql` | string? | `null` | Custom JQL query to use instead of the default |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `Ports.Http` | int | `5160` | HTTP listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `10` | Rate limit |
| `RateLimiting.BackoffBaseSeconds` | int | `2` | Retry backoff base |
| `RateLimiting.MaxRetries` | int | `3` | Maximum retries |
| `Bm25.K1` | double | `1.2` | BM25 term frequency saturation |
| `Bm25.B` | double | `0.75` | BM25 document length normalization |
| `Bm25.UseLemmatization` | bool | `true` | Enable lemmatization during keyword indexing |
| `Bm25.FtsTokenizer` | string? | `null` | Custom FTS5 tokenizer (null uses default) |
| `AuxiliaryDatabase.AuxiliaryDatabasePath` | string? | `null` | Path to auxiliary SQLite DB (stop words + lemmas) |
| `AuxiliaryDatabase.FhirSpecDatabasePath` | string? | `null` | Path to FHIR specification SQLite DB |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |

---

## Zulip Source Service

**Prefix:** `FHIR_AUGURY_ZULIP_`
**Port:** 5170

### appsettings.json

```json
{
  "Zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "CredentialFile": "~/.zuliprc",
    "CachePath": "./cache",
    "DatabasePath": "./data/zulip.db",
    "SyncSchedule": "04:00:00",
    "MinSyncAge": "04:00:00",
    "ReloadFromCacheOnStartup": false,
    "ReindexTicketsOnStartup": false,
    "ExcludedStreamIds": [],
    "OnlyWebPublic": true,
    "StreamBaselineValues": {},
    "OrchestratorAddress": null,
    "IngestionPaused": false,
    "Ports": {
      "Http": 5170
    },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75,
      "UseLemmatization": true,
      "FtsTokenizer": null
    },
    "AuxiliaryDatabase": {
      "AuxiliaryDatabasePath": null,
      "FhirSpecDatabasePath": null
    },
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://chat.fhir.org` | Zulip server URL |
| `Email` | string | | Bot/user email |
| `ApiKey` | string | | API key |
| `CredentialFile` | string | `null` | Path to .zuliprc credentials file |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/zulip.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `04:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `ReindexTicketsOnStartup` | bool | `false` | Force rebuild of Jira ticket reference indexes on startup. Skipped when `ReloadFromCacheOnStartup` is `true` (cache rebuilds already include ticket indexing). |
| `ExcludedStreamIds` | int[] | `[]` | Zulip stream IDs to exclude from ingestion |
| `OnlyWebPublic` | bool | `true` | Restrict ingestion to web-public streams only |
| `StreamBaselineValues` | Dictionary | `{}` | Per-stream baseline multipliers for search ranking (stream name → value 0–10, default 5). Scores are multiplied by `value / 5.0`. |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `Ports.Http` | int | `5170` | HTTP listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `5` | Rate limit |
| `RateLimiting.BackoffBaseSeconds` | int | `2` | Retry backoff base |
| `RateLimiting.MaxRetries` | int | `3` | Maximum retries |
| `Bm25.K1` | double | `1.2` | BM25 term frequency saturation |
| `Bm25.B` | double | `0.75` | BM25 document length normalization |
| `Bm25.UseLemmatization` | bool | `true` | Enable lemmatization during keyword indexing |
| `Bm25.FtsTokenizer` | string? | `null` | Custom FTS5 tokenizer (null uses default) |
| `AuxiliaryDatabase.AuxiliaryDatabasePath` | string? | `null` | Path to auxiliary SQLite DB (stop words + lemmas) |
| `AuxiliaryDatabase.FhirSpecDatabasePath` | string? | `null` | Path to FHIR specification SQLite DB |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |

---

## Confluence Source Service

**Prefix:** `FHIR_AUGURY_CONFLUENCE_`
**Ports:** HTTP 5180

### appsettings.json

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "cookie",
    "Spaces": ["FHIR", "FHIRI", "SOA"],
    "CachePath": "./cache",
    "DatabasePath": "./data/confluence.db",
    "SyncSchedule": "1.00:00:00",
    "MinSyncAge": "04:00:00",
    "ReloadFromCacheOnStartup": false,
    "OrchestratorAddress": null,
    "IngestionPaused": false,
    "Ports": {
      "Http": 5180
    },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75,
      "UseLemmatization": true,
      "FtsTokenizer": null
    },
    "AuxiliaryDatabase": {
      "AuxiliaryDatabasePath": null,
      "FhirSpecDatabasePath": null
    },
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://confluence.hl7.org` | Confluence server URL |
| `AuthMode` | string | `cookie` | Authentication: `cookie` or `basic` |
| `Cookie` | string | | Session cookie for cookie auth |
| `Username` | string | | Username for basic auth |
| `ApiToken` | string | | API token for basic auth |
| `Spaces` | string[] | `["FHIR","FHIRI","SOA"]` | Confluence spaces to index |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/confluence.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `1.00:00:00` | Auto-sync interval (1 day) |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `Ports.Http` | int | `5180` | HTTP listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `5` | Rate limit |
| `RateLimiting.BackoffBaseSeconds` | int | `2` | Retry backoff base |
| `RateLimiting.MaxRetries` | int | `3` | Maximum retries |
| `Bm25.K1` | double | `1.2` | BM25 term frequency saturation |
| `Bm25.B` | double | `0.75` | BM25 document length normalization |
| `Bm25.UseLemmatization` | bool | `true` | Enable lemmatization during keyword indexing |
| `Bm25.FtsTokenizer` | string? | `null` | Custom FTS5 tokenizer (null uses default) |
| `AuxiliaryDatabase.AuxiliaryDatabasePath` | string? | `null` | Path to auxiliary SQLite DB (stop words + lemmas) |
| `AuxiliaryDatabase.FhirSpecDatabasePath` | string? | `null` | Path to FHIR specification SQLite DB |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |

---

## GitHub Source Service

**Prefix:** `FHIR_AUGURY_GITHUB_`
**Port:** 5190

### appsettings.json

```json
{
  "GitHub": {
    "FhirCoreRepositories": ["HL7/fhir"],
    "UtgRepositories": ["HL7/UTG"],
    "FhirExtensionsPackRepositories": ["HL7/fhir-extensions"],
    "IncubatorRepositories": [],
    "IgRepositories": [],
    "ManualLinks": [],
    "Provider": "gh-cli",
    "GhCli": {
      "ExecutablePath": "gh",
      "Limit": 1000,
      "Hostname": null,
      "ProcessTimeout": "00:05:00"
    },
    "Auth": {
      "Token": null,
      "TokenEnvVar": "GITHUB_TOKEN"
    },
    "CachePath": "./cache",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "MinSyncAge": "04:00:00",
    "ReloadFromCacheOnStartup": false,
    "OrchestratorAddress": null,
    "IngestionPaused": false,
    "Ports": {
      "Http": 5190
    },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 5,
      "MaxRetries": 5,
      "RespectRateLimitHeaders": true
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75,
      "UseLemmatization": true,
      "FtsTokenizer": null
    },
    "AuxiliaryDatabase": {
      "AuxiliaryDatabasePath": null,
      "FhirSpecDatabasePath": null
    },
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    },
    "FileContentIndexing": {
      "Enabled": true,
      "MaxFileSizeBytes": 524288,
      "MaxExtractedTextLength": 65536,
      "MaxFilesPerRepo": 50000,
      "AdditionalSkipExtensions": [],
      "AdditionalSkipDirectories": [],
      "IncludeOnlyPaths": [],
      "IgnorePatterns": [
        "**/test-data/**",
        "**/testdata/**",
        "**/*.generated.*",
        "**/vendor/**",
        "**/third_party/**"
      ]
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `FhirCoreRepositories` | string[] | `["HL7/fhir"]` | Core FHIR specification repositories |
| `UtgRepositories` | string[] | `["HL7/UTG"]` | Unified Terminology Governance repositories |
| `FhirExtensionsPackRepositories` | string[] | `["HL7/fhir-extensions"]` | FHIR Extensions Pack repositories |
| `IncubatorRepositories` | string[] | `[]` | Incubator project repositories |
| `IgRepositories` | string[] | `[]` | Implementation Guide repositories |
| `ManualLinks` | string[] | `[]` | Manual cross-reference link overrides |
| `Provider` | string | `gh-cli` | Data provider: `rest` (REST API) or `gh-cli` (GitHub CLI) |
| `GhCli.ExecutablePath` | string | `gh` | Path to the gh CLI executable |
| `GhCli.Limit` | int | `1000` | Maximum items per gh CLI query |
| `GhCli.Hostname` | string? | `null` | GitHub Enterprise hostname (null for github.com) |
| `GhCli.ProcessTimeout` | TimeSpan | `00:05:00` | Timeout for gh CLI processes |
| `Auth.Token` | string | | GitHub PAT (direct) |
| `Auth.TokenEnvVar` | string | `GITHUB_TOKEN` | Env var containing PAT |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/github.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `02:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `Ports.Http` | int | `5190` | HTTP listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `10` | Rate limit |
| `RateLimiting.MaxConcurrentRequests` | int | `1` | Maximum concurrent API requests |
| `RateLimiting.RespectRateLimitHeaders` | bool | `true` | Honor GitHub rate headers |
| `Bm25.K1` | double | `1.2` | BM25 term frequency saturation |
| `Bm25.B` | double | `0.75` | BM25 document length normalization |
| `Bm25.UseLemmatization` | bool | `true` | Enable lemmatization during keyword indexing |
| `Bm25.FtsTokenizer` | string? | `null` | Custom FTS5 tokenizer (null uses default) |
| `AuxiliaryDatabase.AuxiliaryDatabasePath` | string? | `null` | Path to auxiliary SQLite DB (stop words + lemmas) |
| `AuxiliaryDatabase.FhirSpecDatabasePath` | string? | `null` | Path to FHIR specification SQLite DB |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |
| `FileContentIndexing.Enabled` | bool | `true` | Enable repository file content indexing |
| `FileContentIndexing.MaxFileSizeBytes` | int | `524288` | Maximum file size in bytes to index (512 KB) |
| `FileContentIndexing.MaxExtractedTextLength` | int | `65536` | Maximum extracted text length per file (64 KB) |
| `FileContentIndexing.MaxFilesPerRepo` | int | `50000` | Maximum number of files to index per repository |
| `FileContentIndexing.AdditionalSkipExtensions` | string[] | `[]` | Additional file extensions to skip |
| `FileContentIndexing.AdditionalSkipDirectories` | string[] | `[]` | Additional directory names to skip |
| `FileContentIndexing.IncludeOnlyPaths` | string[] | `[]` | When non-empty, only index files under these paths |
| `FileContentIndexing.IgnorePatterns` | string[] | (defaults) | Gitignore-style glob patterns for files/directories to exclude |

---

## Orchestrator Service

**Prefix:** `FHIR_AUGURY_ORCHESTRATOR_`
**Port:** 5150

### appsettings.json

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": {
      "Http": 5150
    },
    "Services": {
      "Jira": { "HttpAddress": "http://localhost:5160", "Enabled": true },
      "Zulip": { "HttpAddress": "http://localhost:5170", "Enabled": true },
      "Confluence": { "HttpAddress": "http://localhost:5180", "Enabled": false },
      "GitHub": { "HttpAddress": "http://localhost:5190", "Enabled": true }
    },
    "Search": {
      "DefaultLimit": 20,
      "MaxLimit": 100,
      "FreshnessWeights": { "jira": 0.5, "zulip": 2.0 }
    },
    "Related": {
      "CrossSourceWeight": 10.0,
      "Bm25SimilarityWeight": 3.0,
      "SharedMetadataWeight": 2.0,
      "DefaultLimit": 20,
      "MaxKeyTerms": 15,
      "PerSourceTimeoutSeconds": 2
    },
    "ReconnectIntervalSeconds": 30,
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    }
  }
}
```

> **Note:** The default `appsettings.json` ships with Jira, Zulip, and GitHub
> enabled. Confluence is present but disabled by default — set
> `Services.Confluence.Enabled` to `true` when deploying the Confluence source
> service.

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DatabasePath` | string | `./data/orchestrator.db` | SQLite database path |
| `Ports.Http` | int | `5150` | HTTP listen port |
| `Services.{Name}.HttpAddress` | string | varies | HTTP endpoint for source |
| `Services.{Name}.Enabled` | bool | `true` | Enable/disable source |
| `Search.DefaultLimit` | int | `20` | Default search result limit |
| `Search.MaxLimit` | int | `100` | Maximum search result limit |
| `Search.FreshnessWeights` | Dictionary | varies | Per-source freshness weight multipliers |
| `Related.CrossSourceWeight` | double | `10.0` | Weight for cross-source references |
| `Related.Bm25SimilarityWeight` | double | `3.0` | Weight for BM25 text similarity |
| `Related.SharedMetadataWeight` | double | `2.0` | Weight for shared metadata |
| `Related.DefaultLimit` | int | `20` | Default related items limit |
| `Related.MaxKeyTerms` | int | `15` | Max terms for similarity |
| `Related.PerSourceTimeoutSeconds` | int | `2` | Timeout in seconds for each source during related item queries |
| `ReconnectIntervalSeconds` | int | `30` | Interval in seconds between reconnection attempts for offline sources. Set to 0 to disable. |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |

---

## MCP Server (Stdio) — `FhirAugury.McpStdio`

The stdio MCP server connects to services via HTTP and is configured through
environment variables. It is packaged as the `fhir-augury-mcp` dotnet tool.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |
| `FHIR_AUGURY_JIRA` | `http://localhost:5160` | Jira HTTP address |
| `FHIR_AUGURY_ZULIP` | `http://localhost:5170` | Zulip HTTP address |
| `FHIR_AUGURY_CONFLUENCE` | `http://localhost:5180` | Confluence HTTP address |
| `FHIR_AUGURY_GITHUB` | `http://localhost:5190` | GitHub HTTP address |

### CLI Arguments

| Argument | Values | Description |
|----------|--------|-------------|
| `--mode` | `orchestrator` (default), `direct` | Operation mode |
| `--source` | `jira`, `zulip`, `confluence`, `github` | Source for direct mode |

Direct mode bypasses the orchestrator and connects to a single source service.

---

## MCP Server (HTTP) — `FhirAugury.McpHttp`

The HTTP MCP server runs as a long-lived ASP.NET service exposing the MCP
endpoint at `/mcp` via HTTP/SSE transport. It is included in the Aspire AppHost
on port 5200.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |
| `FHIR_AUGURY_JIRA` | `http://localhost:5160` | Jira HTTP address |
| `FHIR_AUGURY_ZULIP` | `http://localhost:5170` | Zulip HTTP address |
| `FHIR_AUGURY_CONFLUENCE` | `http://localhost:5180` | Confluence HTTP address |
| `FHIR_AUGURY_GITHUB` | `http://localhost:5190` | GitHub HTTP address |

### appsettings.json

Standard ASP.NET logging configuration. The HTTP port (5200) is configured in
`Properties/launchSettings.json`.

See [MCP setup](../README.md#mcp-setup) for client configuration examples.

---

## Docker Environment Variables

When running in Docker, paths should map to container volumes:

```yaml
environment:
  # Override cache and database paths to use mounted volumes
  - FHIR_AUGURY_JIRA__Jira__CachePath=/app/cache
  - FHIR_AUGURY_JIRA__Jira__DatabasePath=/app/data/jira.db

  # Override orchestrator addresses to use container names
  - FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__HttpAddress=http://source-jira:5160

  # BM25 tuning (optional)
  - FHIR_AUGURY_JIRA__Jira__Bm25__K1=1.2
  - FHIR_AUGURY_JIRA__Jira__Bm25__B=0.75

  # Auxiliary databases (optional — mount the DB files into the container)
  - FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__AuxiliaryDatabasePath=/app/data/auxiliary.db
  - FHIR_AUGURY_JIRA__Jira__AuxiliaryDatabase__FhirSpecDatabasePath=/app/data/fhir-spec.db

  # Dictionary database (shared dictionary data — mounted read-only via Docker Compose)
  - FHIR_AUGURY_JIRA__Jira__DictionaryDatabase__SourcePath=/app/cache/dictionary
  - FHIR_AUGURY_JIRA__Jira__DictionaryDatabase__DatabasePath=/app/data/dictionary.db
```

See [deployment.md](deployment.md) for complete Docker configuration.

---

## Aspire / OpenTelemetry

All web services reference `FhirAugury.ServiceDefaults`, which configures
OpenTelemetry and service discovery automatically. These features are active
both when running under the Aspire AppHost and when running standalone.

### OpenTelemetry Export

Set the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable to enable OTLP
export of logs, metrics, and traces. When running under the Aspire AppHost,
this is configured automatically to send telemetry to the Aspire dashboard.

```bash
# Standalone (export to a custom OTLP collector)
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
  dotnet run --project src/FhirAugury.Source.Jira
```

When the variable is not set, telemetry is collected locally but not exported.

### Health Endpoints

ServiceDefaults maps two health endpoints on all services:

| Endpoint | Purpose |
|----------|---------|
| `/health` | Readiness — all health checks must pass |
| `/alive` | Liveness — only "live"-tagged checks must pass |
