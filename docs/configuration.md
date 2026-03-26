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
**Ports:** HTTP 5160, gRPC 5161

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
    "Ports": {
      "Http": 5160,
      "Grpc": 5161
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
| `Ports.Http` | int | `5160` | HTTP listen port |
| `Ports.Grpc` | int | `5161` | gRPC listen port |
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
**Ports:** HTTP 5170, gRPC 5171

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
    "RebuildFromCacheOnStartup": false,
    "ReindexTicketsOnStartup": false,
    "ExcludedStreamIds": [],
    "Ports": {
      "Http": 5170,
      "Grpc": 5171
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
| `CredentialFile` | string | `~/.zuliprc` | Path to .zuliprc credentials file |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/zulip.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `04:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `RebuildFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `ReindexTicketsOnStartup` | bool | `false` | Force rebuild of Jira ticket reference indexes on startup. Skipped when `RebuildFromCacheOnStartup` is `true` (cache rebuilds already include ticket indexing). |
| `ExcludedStreamIds` | int[] | `[]` | Zulip stream IDs to exclude from ingestion |
| `Ports.Http` | int | `5170` | HTTP listen port |
| `Ports.Grpc` | int | `5171` | gRPC listen port |
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
**Ports:** HTTP 5180, gRPC 5181

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
    "Ports": {
      "Http": 5180,
      "Grpc": 5181
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
| `Ports.Http` | int | `5180` | HTTP listen port |
| `Ports.Grpc` | int | `5181` | gRPC listen port |
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
**Ports:** HTTP 5190, gRPC 5191

### appsettings.json

```json
{
  "GitHub": {
    "RepoMode": "core",
    "Repositories": ["HL7/fhir"],
    "AdditionalRepositories": [],
    "ManualLinks": [],
    "Auth": {
      "Token": null,
      "TokenEnvVar": "GITHUB_TOKEN"
    },
    "CachePath": "./cache",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "MinSyncAge": "04:00:00",
    "Ports": {
      "Http": 5190,
      "Grpc": 5191
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
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `RepoMode` | string | `core` | Repository selection mode |
| `Repositories` | string[] | `["HL7/fhir"]` | Repositories to track |
| `AdditionalRepositories` | string[] | `[]` | Extra repositories |
| `Auth.Token` | string | | GitHub PAT (direct) |
| `Auth.TokenEnvVar` | string | `GITHUB_TOKEN` | Env var containing PAT |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/github.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `02:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `Ports.Http` | int | `5190` | HTTP listen port |
| `Ports.Grpc` | int | `5191` | gRPC listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `10` | Rate limit |
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

---

## Orchestrator Service

**Prefix:** `FHIR_AUGURY_ORCHESTRATOR_`
**Ports:** HTTP 5150, gRPC 5151

### appsettings.json

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": {
      "Http": 5150,
      "Grpc": 5151
    },
    "Services": {
      "Jira": { "GrpcAddress": "http://localhost:5161", "Enabled": true },
      "Zulip": { "GrpcAddress": "http://localhost:5171", "Enabled": true },
      "Confluence": { "GrpcAddress": "http://localhost:5181", "Enabled": true },
      "GitHub": { "GrpcAddress": "http://localhost:5191", "Enabled": true }
    },
    "CrossRef": {
      "ScanIntervalMinutes": 30,
      "ValidateTargets": true
    },
    "Search": {
      "DefaultLimit": 20,
      "MaxLimit": 100,
      "CrossRefBoostFactor": 0.5,
      "FreshnessWeights": { "jira": 0.5, "zulip": 2.0 }
    },
    "Related": {
      "ExplicitXrefWeight": 10.0,
      "ReverseXrefWeight": 8.0,
      "Bm25SimilarityWeight": 3.0,
      "SharedMetadataWeight": 2.0,
      "DefaultLimit": 20,
      "MaxKeyTerms": 15
    },
    "DictionaryDatabase": {
      "SourcePath": "./cache/dictionary",
      "DatabasePath": "./data/dictionary.db",
      "ForceRebuild": false
    }
  }
}
```

> **Note:** The default `appsettings.json` ships with only Jira and Zulip in
> the `Services` section. Add Confluence and/or GitHub entries when deploying
> those source services.

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DatabasePath` | string | `./data/orchestrator.db` | SQLite database path |
| `Ports.Http` | int | `5150` | HTTP listen port |
| `Ports.Grpc` | int | `5151` | gRPC listen port |
| `Services.{Name}.GrpcAddress` | string | varies | gRPC endpoint for source |
| `Services.{Name}.Enabled` | bool | `true` | Enable/disable source |
| `CrossRef.ScanIntervalMinutes` | int | `30` | Cross-ref scan frequency |
| `CrossRef.ValidateTargets` | bool | `true` | Validate xref targets exist |
| `Search.DefaultLimit` | int | `20` | Default search result limit |
| `Search.MaxLimit` | int | `100` | Maximum search result limit |
| `Search.CrossRefBoostFactor` | double | `0.5` | Boost for cross-referenced items |
| `Related.DefaultLimit` | int | `20` | Default related items limit |
| `Related.MaxKeyTerms` | int | `15` | Max terms for similarity |
| `DictionaryDatabase.SourcePath` | string | `./cache/dictionary` | Source path for dictionary data files |
| `DictionaryDatabase.DatabasePath` | string | `./data/dictionary.db` | SQLite database path for compiled dictionary |
| `DictionaryDatabase.ForceRebuild` | bool | `false` | Force rebuild of dictionary database on startup |

---

## MCP Server

The MCP server (`FhirAugury.Mcp`) connects to services via gRPC and is
configured through environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5151` | Orchestrator gRPC address |
| `FHIR_AUGURY_JIRA_GRPC` | `http://localhost:5161` | Jira gRPC address |
| `FHIR_AUGURY_ZULIP_GRPC` | `http://localhost:5171` | Zulip gRPC address |
| `FHIR_AUGURY_CONFLUENCE_GRPC` | `http://localhost:5181` | Confluence gRPC address |
| `FHIR_AUGURY_GITHUB_GRPC` | `http://localhost:5191` | GitHub gRPC address |

See [MCP setup](../README.md#mcp-setup) for client configuration examples.

---

## Docker Environment Variables

When running in Docker, paths should map to container volumes:

```yaml
environment:
  # Override cache and database paths to use mounted volumes
  - FHIR_AUGURY_JIRA__Jira__CachePath=/app/cache
  - FHIR_AUGURY_JIRA__Jira__DatabasePath=/app/data/jira.db

  # Override orchestrator gRPC addresses to use container names
  - FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__GrpcAddress=http://source-jira:5161

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
