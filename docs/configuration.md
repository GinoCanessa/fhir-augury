# Configuration Reference

Complete configuration reference for all FHIR Augury v2 services.

> For task-oriented setup (credentials, common environment variables, sync
> schedules), see the [Configuration guide](user/configuration.md). This page is
> the canonical, exhaustive key reference.

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

## Nullable Source list configuration

Source API list filters and ingestion selection lists use the [null-as-default, empty-as-explicit-all convention](source-filter-conventions.md). For defaulted ingestion lists, remove the key or set it to `null` to use defaults; `[]` is an explicit opt-out.

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
| `RunIngestionOnStartupOnly` | bool | `false` | When `true`, run ingestion exactly once at startup (honoring `MinSyncAge` and `IngestionPaused`) then exit the worker loop. HTTP endpoints stay available. |
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
| `RunIngestionOnStartupOnly` | bool | `false` | When `true`, run ingestion exactly once at startup (honoring `MinSyncAge` and `IngestionPaused`) then exit the worker loop. HTTP endpoints stay available. |
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
    "CachePath": "./cache",
    "DatabasePath": "./data/confluence.db",
    "SyncSchedule": "1.00:00:00",
    "MinSyncAge": "04:00:00",
    "SweepPageSize": 200,
    "SpaceSweepMaxAge": "00:00:00",
    "AttachmentMaxBytes": 104857600,
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
| `Spaces` | string[]? | `null` | Spaces to index. `null` discovers **every non-archived global space on the instance** (~140); `[]` indexes none. See [source filter conventions](source-filter-conventions.md). |
| `SweepPageSize` | int | `200` | Page size for the body-less sweep. HL7's Confluence honours 200 verbatim, so the whole instance enumerates in roughly 1,660 requests (~5.5 minutes at 5 req/s). Must be greater than zero. |
| `SpaceSweepMaxAge` | TimeSpan | `00:00:00` | A space whose manifest is younger than this is skipped by the sweep and its previous manifest reused. The shipped default re-sweeps every space on every run. This is an *age threshold*, not a per-run request budget. |
| `AttachmentMaxBytes` | long | `104857600` | Attachment blobs larger than this are not downloaded; their metadata is still swept, cached, replayed and indexed. `0` means unlimited; a **negative value is rejected at startup**. The cap gates *downloading*, not *keeping*: **lowering** it never removes bytes already on disk, and **raising** it turns a previously skipped blob into a gap that closes on the next run. |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/confluence.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `1.00:00:00` | Auto-sync interval (1 day) |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `RunIngestionOnStartupOnly` | bool | `false` | When `true`, run ingestion exactly once at startup (honoring `MinSyncAge` and `IngestionPaused`) then exit the worker loop. HTTP endpoints stay available. |
| `Ports.Http` | int | `5180` | HTTP listen port |
| `RateLimiting.MaxRequestsPerSecond` | int | `5` | Rate limit, enforced by a dedicated delegating handler on every physical send |
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

### Cache completeness verdicts

`GET /api/v1/cache/reconcile-report` (proxied by the orchestrator at
`/api/v1/confluence/cache/reconcile-report`) answers "is my cache complete, and
what is missing?" from local disk, with no network. It is answerable at any
moment, including part-way through a long initial pull.

Each space reports one of four verdicts:

| Verdict | Meaning |
|-|-|
| `complete` | Every item the manifest names is cached at the current version and fidelity profile. |
| `complete_with_skips` | Every item is accounted for, **but some attachment bytes were excluded by `AttachmentMaxBytes`**. `skippedByPolicy` and `skippedBytes` in the report give the detail. A distinct value rather than a footnote on `complete`, because the verdict travels alone to surfaces that carry no skip counts. |
| `partial` | Something the manifest names is missing or stale. `missing`, `stale` and `missingIds` say what. |
| `unknown` | The space has no manifest, a malformed one, an incomplete sweep, or a **failed sweep attempt more recent than the last good manifest**. A space that has never been successfully enumerated reports `unknown` — never `complete`, and never "empty". |

The overall verdict is the least complete of the per-space verdicts.

`/api/v1/stats` surfaces the same counts through `additionalCounts`
(`manifest_items`, `cached`, `stale`, `missing`, `vanished`,
`skipped_by_policy`, `attachments`). `skippedBytes` is deliberately **not**
there: that dictionary is `Dictionary<string, int>` and a byte total would
overflow it.

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
    "JiraSpecArtifactsRepositories": [],
    "ManualLinks": [],
    "Provider": "gh-cli",
    "GhCli": {
      "ExecutablePath": "gh",
      "Limit": 1000,
      "Hostname": null,
      "ProcessTimeout": "00:05:00",
      "MaxConcurrentProcesses": 1,
      "BackfillLimit": 5000,
      "BackfillCheckpointInterval": 250,
      "BackfillMaxRepairPasses": 3
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
| `JiraSpecArtifactsRepositories` | string[] | `[]` | Repositories holding Jira-tracked spec artifacts (attributed to the `FHIR` project by default) |
| `ManualLinks` | string[] | `[]` | Manual cross-reference link overrides |
| `Provider` | string | `gh-cli` | Data provider: `rest` (REST API) or `gh-cli` (GitHub CLI) |
| `GhCli.ExecutablePath` | string | `gh` | Path to the gh CLI executable |
| `GhCli.Limit` | int | `1000` | Maximum items per gh CLI query |
| `GhCli.Hostname` | string? | `null` | GitHub Enterprise hostname (null for github.com) |
| `GhCli.ProcessTimeout` | TimeSpan | `00:05:00` | Timeout for gh CLI processes |
| `GhCli.MaxConcurrentProcesses` | int | `1` | Maximum concurrent `gh` processes. Default `1` prevents CLI state-file contention and rate-limit pressure |
| `GhCli.BackfillLimit` | int | `5000` | Maximum items per `gh` list command during a per-repo history backfill. A phase whose returned count *equals* this value is treated as truncated and left incomplete (with a warning) rather than marked done |
| `GhCli.BackfillCheckpointInterval` | int | `250` | Items processed between durable backfill checkpoint writes. A hard kill loses at most one interval; the graceful path always checkpoints on exit |
| `GhCli.BackfillMaxRepairPasses` | int | `3` | Consecutive resume passes that may fail to shrink the pending-retry set before the repo is marked complete anyway, with a warning naming the abandoned items |
| `Auth.Token` | string | | GitHub PAT (direct) |
| `Auth.TokenEnvVar` | string | `GITHUB_TOKEN` | Env var containing PAT |
| `CachePath` | string | `./cache` | File-system cache directory |
| `DatabasePath` | string | `./data/github.db` | SQLite database path |
| `SyncSchedule` | TimeSpan | `02:00:00` | Auto-sync interval |
| `MinSyncAge` | TimeSpan | `04:00:00` | Minimum time between syncs (prevents over-syncing) |
| `MaxInitialCommits` | int | `500` | Global cap on how many commits the *first* (no prior SHA) commit-file extraction walks back from HEAD. Incremental runs (a prior SHA exists) ignore it and walk `{lastSha}..HEAD`. `0`/negative = full history. Overridable per repo below. |
| `ReloadFromCacheOnStartup` | bool | `false` | Rebuild database from cached data on startup |
| `OrchestratorAddress` | string? | `null` | Orchestrator HTTP address for ingestion notifications |
| `IngestionPaused` | bool | `false` | Pause automatic ingestion sync |
| `RunIngestionOnStartupOnly` | bool | `false` | When `true`, run ingestion exactly once at startup (honoring `MinSyncAge` and `IngestionPaused`) then exit the worker loop. HTTP endpoints stay available. |
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
| `BareNumberAttributionEnabled` | bool | `true` | Master switch for repo-scoped bare-integer Jira attribution. When `false`, bare numbers in commit/issue/comment prose are never resolved (prefixed `FHIR-N`/URL extraction is unaffected) |
| `JiraProjectKeyByCategory` | map | (see below) | Default Jira project key per repo category for the bare-number pass. Defaults: `FhirCore`/`FhirExtensionsPack`/`Incubator`/`Ig`/`JiraSpecArtifacts` → `FHIR`, `Utg` → `UP` |
| `JiraNumberRanges` | map | `FHIR [2839,70000]`, `UP [40,2000]`, `UPSM [10,2000]` | Inclusive numeric range per project key; a standalone integer only resolves to `KEY-N` when it falls within the key's range (UP/UPSM uppers held below calendar years) |
| `RepoOverrides.<owner/repo>.JiraProjectKey` | string? | `null` | Explicit Jira project key for a repo's bare-number resolution (wins over category default and `TerminologyProjectKey`) |
| `RepoOverrides.<owner/repo>.TerminologyProjectKey` | string? | `null` | For Utg repos, selects `UP` vs `UPSM` for bare-number resolution |
| `RepoOverrides.<owner/repo>.MaxInitialCommits` | int? | `null` | Per-repo initial commit-extraction cap; `0`/negative = full history plus automatic backward deepening of an already-ingested slice (dedup, no teardown). Falls back to the global `MaxInitialCommits` when unset. `HL7/fhir` ships set to `0`. |

---

## FHIR Spec Source Service

**Prefix:** `FHIR_AUGURY_FHIR_`
**Port:** 5195

Read-only source that serves FHIR specification reference data
(StructureDefinitions and other canonical resources) to the orchestrator. Unlike
the ingesting sources it has no rate-limited upstream client; its content is
loaded from a prepared FHIR spec database and exposed over FTS.

### appsettings.json

```json
{
  "Fhir": {
    "DatabasePath": "./cache/fhir-spec.db",
    "SidecarDatabasePath": "./data/fhir-spec-fts.db",
    "DefaultRelease": null,
    "RebuildFtsOnStartup": true,
    "OrchestratorAddress": "http://localhost:5150",
    "Ports": {
      "Http": 5195
    },
    "Bm25": {
      "FtsTokenizer": null
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Fhir.DatabasePath` | string | `./cache/fhir-spec.db` | Prepared FHIR specification SQLite database (read-only content source) |
| `Fhir.SidecarDatabasePath` | string | `./data/fhir-spec-fts.db` | Sidecar SQLite database holding the FTS index built from the spec DB |
| `Fhir.DefaultRelease` | string? | `null` | Default FHIR release to serve when a request omits one (null = auto) |
| `Fhir.RebuildFtsOnStartup` | bool | `true` | Rebuild the FTS sidecar from the spec DB on startup |
| `Fhir.OrchestratorAddress` | string | `http://localhost:5150` | Orchestrator HTTP address for ingestion notifications |
| `Fhir.Ports.Http` | int | `5195` | HTTP listen port |
| `Fhir.Bm25.FtsTokenizer` | string? | `null` | Custom FTS5 tokenizer (null uses default) |

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

## Processing Services

**Prefixes:** `FHIR_AUGURY_PREPARER_`,
`FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_`,
`FHIR_AUGURY_PROCESSOR_JIRA_FHIR_APPLIER_`
**Ports:** 5171 (Preparer), 5172 (Planner), 5173 (Applier)

The three Jira FHIR processors — Preparer, Planner, and Applier — share the
`Processing` configuration shape. The Preparer
and Planner each queue Jira tickets matching their filters, invoke an agent CLI
command per ticket, and persist structured output (overwriting prior output for a
ticket without history). The Applier instead auto-discovers completed plans from
the Planner database and applies each in a per-(ticket, repo) git worktree. All
expose `/health`, `/status`, `/processing/start`, `/processing/stop`,
`/processing/queue`, and `POST /processing/tickets/{key}`.

### appsettings.json (Planner shown; Preparer omits the `Planner` block)

```json
{
  "Processing": {
    "DatabasePath": "./data/processor.jira.fhir.planner.db",
    "SyncSchedule": "00:01:00",
    "MaxConcurrentProcessingThreads": 3,
    "StartProcessingOnStartup": true,
    "OrchestratorAddress": "http://localhost:5150",
    "Ports": {
      "Http": 5172
    },
    "Jira": {
      "TicketStatusesToProcess": ["Resolved - change required"],
      "ProjectsToInclude": ["FHIR"],
      "SpecificationsToInclude": [],
      "WorkGroupsToInclude": null,
      "TicketTypesToProcess": null,
      "AgentCliCommand": "copilot -p '/ticket-plan {ticketKey} --db {dbPath} --repos {repoFilters} ...'",
      "JiraSourceAddress": "http://localhost:5160",
      "OrchestratorAddress": "http://localhost:5150",
      "DiscoverySource": "DirectJiraSource",
      "SourceTicketShape": "fhir"
    },
    "Planner": {
      "RepoFilters": null
    },
    "Hydration": {
      "BackfillOnStartup": true,
      "MaxParallelism": 4
    }
  }
}
```

The `AgentCliCommand` above is abbreviated — the shipped default carries a long
`--allow-tool`/`--deny-tool` allow-list; consult the project's `appsettings.json`
for the full value.

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Processing.DatabasePath` | string | per-processor | Structured processor output SQLite DB (overwritten per ticket without history) |
| `Processing.SyncSchedule` | TimeSpan | `00:01:00` | Queue poll / discovery interval |
| `Processing.MaxConcurrentProcessingThreads` | int | `3`–`8` | Max tickets processed concurrently (per-processor default) |
| `Processing.StartProcessingOnStartup` | bool | `true` | Begin processing the queue on boot |
| `Processing.OrchestratorAddress` | string | `http://localhost:5150` | Orchestrator HTTP address |
| `Processing.Ports.Http` | int | per-processor | HTTP listen port (5171 / 5172 / 5173) |
| `Processing.Jira.TicketStatusesToProcess` | string[] | per-processor | Jira statuses to pull (Preparer: `Triaged`/`Submitted`; Planner: `Resolved - change required`) |
| `Processing.Jira.ProjectsToInclude` | string[] | `["FHIR"]` | Jira projects to include |
| `Processing.Jira.SpecificationsToInclude` | string[] | `[]` | Restrict to specific specifications (empty = all) |
| `Processing.Jira.WorkGroupsToInclude` | string[]? | `null` | Restrict to specific work groups (null = all) |
| `Processing.Jira.TicketTypesToProcess` | string[]? | `null` | Restrict to specific ticket types (null = all) |
| `Processing.Jira.AgentCliCommand` | string | per-processor | Agent command template run per ticket (`{ticketKey}`, `{dbPath}`, `{repoFilters}` tokens; no shell expansion) |
| `Processing.Jira.JiraSourceAddress` | string | `http://localhost:5160` | Jira source used for direct discovery |
| `Processing.Jira.DiscoverySource` | string | `DirectJiraSource` | Where tickets are discovered (`DirectJiraSource` or the orchestrator) |
| `Processing.Jira.SourceTicketShape` | string | `fhir` | Source ticket DTO shape |
| `Processing.Planner.RepoFilters` | string[]? | `null` | Planner-only exact `owner/repo` allow-list passed to `ticket-plan` via `{repoFilters}` (null = no restriction) |
| `Processing.Hydration.BackfillOnStartup` | bool | `true` | Backfill hydrated evidence for existing output on boot |
| `Processing.Hydration.MaxParallelism` | int | `4` | Max units hydrated concurrently |

> For the operational flow (kick-off curl, monitoring, output locations), see the
> [processors runbook](technical/processors.md).

---

## BallotNotes Processor Service

**Prefix:** `FHIR_AUGURY_BALLOTNOTES_`
**Port:** 5174

Commit-triggered processor that hydrates ballot-note evidence for a GitHub repo
+ since-commit window (commit-window walk, ticket attribution, source-file
resolution, current-note capture) and serves read/query + prose write-back
endpoints under `/api/v1/ballot-notes`. The `notes-site` tool reads its database
directly to emit the static review SPA. Registered in the AppHost as
`processor-github-fhir-ballotnotes` with `WithExplicitStart()`.

> To run a hydration (and for the Preparer/Planner/Applier operational flow),
> see the [processors runbook](technical/processors.md).

### appsettings.json

```json
{
  "BallotNotes": {
    "DatabasePath": "./cache/ballot-notes.db",
    "Ports": {
      "Http": 5174
    },
    "Hydration": {
      "CloneRoot": "./cache/github/repos",
      "OrchestratorAddress": "http://localhost:5150",
      "JiraSourceAddress": "http://localhost:5160",
      "MaxParallelism": 4
    }
  }
}
```

### Configuration Options

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DatabasePath` | string | `./cache/ballot-notes.db` | SQLite notes DB path (read directly by the `notes-site` renderer) |
| `Ports.Http` | int | `5174` | HTTP listen port |
| `Hydration.CloneRoot` | string | `./cache/github/repos` | Root holding per-repo clones (`<owner>_<name>/clone`) |
| `Hydration.OrchestratorAddress` | string | `http://localhost:5150` | Primary attribution upstream (cross-references + ticket details) |
| `Hydration.JiraSourceAddress` | string | `http://localhost:5160` | Fallback attribution upstream when the orchestrator is unreachable |
| `Hydration.MaxParallelism` | int | `4` | Max units hydrated concurrently |

---

## MCP Server (Stdio) — `FhirAugury.McpStdio`

The stdio MCP server connects to services via HTTP and is configured through
environment variables. It is packaged as the `fhir-augury-mcp` dotnet tool.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |

---

## MCP Server (HTTP) — `FhirAugury.McpHttp`

The HTTP MCP server runs as a long-lived ASP.NET service exposing the MCP
endpoint at `/mcp` via HTTP/SSE transport. It is included in the Aspire AppHost
on port 5200.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |

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
