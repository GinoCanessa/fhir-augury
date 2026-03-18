# Configuration

FHIR Augury can be configured through CLI options, `appsettings.json` (for the
service), and environment variables. This document covers all configuration
options.

## CLI Global Options

These options apply to all CLI commands:

| Option | Type | Default | Description |
|---|---|---|---|
| `--db` | `string` | `fhir-augury.db` | Path to the SQLite database file |
| `--verbose` | `bool` | `false` | Enable verbose output |
| `--json` | `bool` | `false` | Force JSON output format |
| `--quiet` | `bool` | `false` | Suppress all output except errors |

## Service Configuration

The background service reads configuration from multiple sources in order of
increasing priority:

1. `appsettings.json`
2. `appsettings.local.json` (git-ignored, for local overrides)
3. Environment variables (prefixed with `FHIR_AUGURY_`)
4. User secrets (development only)

All settings live under the `FhirAugury` section.

### Full Schema

```json
{
  "FhirAugury": {
    "DatabasePath": "fhir-augury.db",
    "Sources": {
      "jira": { },
      "zulip": { },
      "confluence": { },
      "github": { }
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75
    },
    "Api": {
      "Port": 5100,
      "CorsOrigins": ["*"]
    }
  }
}
```

### Top-Level Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `DatabasePath` | `string` | `"fhir-augury.db"` | Path to the SQLite database |
| `Sources` | `Dictionary<string, SourceConfiguration>` | — | Per-source configuration (see below) |
| `Bm25` | `Bm25Configuration` | — | BM25 scoring parameters |
| `Api` | `ApiConfiguration` | — | HTTP API settings |

## Source Configuration

Each source in the `Sources` dictionary shares these common properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Whether the source is active for scheduled sync |
| `SyncSchedule` | `TimeSpan?` | — | Interval between automatic syncs (e.g., `"01:00:00"` for 1 hour) |
| `BaseUrl` | `string` | `""` | Base URL of the source API |

### Jira

| Property | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | `string` | `"https://jira.hl7.org"` | Jira server URL |
| `AuthMode` | `string` | — | `"Cookie"` or `"ApiToken"` |
| `Cookie` | `string?` | — | Session cookie for cookie auth |
| `ApiToken` | `string?` | — | API token for token-based auth |
| `Email` | `string?` | — | Email address (required with `ApiToken`) |
| `DefaultJql` | `string?` | — | Default JQL filter for downloads |
| `SyncSchedule` | `TimeSpan?` | `"01:00:00"` | Sync every hour |

```json
{
  "jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "Cookie",
    "Cookie": "JSESSIONID=abc123...",
    "DefaultJql": "project = \"FHIR Specification Feedback\"",
    "SyncSchedule": "01:00:00"
  }
}
```

### Zulip

| Property | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | `string` | `"https://chat.fhir.org"` | Zulip server URL |
| `Email` | `string?` | — | Bot or user email address |
| `ApiKey` | `string?` | — | API key for authentication |
| `CredentialFile` | `string?` | — | Path to `.zuliprc` file (alternative to email+apikey) |
| `OnlyWebPublic` | `bool` | `true` | Only index web-public streams |
| `SyncSchedule` | `TimeSpan?` | `"00:30:00"` | Sync every 30 minutes |

```json
{
  "zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "Email": "bot@example.com",
    "ApiKey": "your-api-key",
    "OnlyWebPublic": true,
    "SyncSchedule": "00:30:00"
  }
}
```

### Confluence

| Property | Type | Default | Description |
|---|---|---|---|
| `BaseUrl` | `string` | `"https://confluence.hl7.org"` | Confluence server URL |
| `AuthMode` | `string` | — | `"Cookie"` or `"Basic"` |
| `Cookie` | `string?` | — | Session cookie for cookie auth |
| `Username` | `string?` | — | Username for basic auth |
| `ApiToken` | `string?` | — | API token for basic auth |
| `Spaces` | `List<string>` | `["FHIR", "FHIRI"]` | Space keys to index |
| `PageSize` | `int` | `25` | Pages per API request |
| `SyncSchedule` | `TimeSpan?` | `"02:00:00"` | Sync every 2 hours |

```json
{
  "confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "Cookie",
    "Cookie": "JSESSIONID=abc123...",
    "Spaces": ["FHIR", "FHIRI"],
    "PageSize": 25,
    "SyncSchedule": "02:00:00"
  }
}
```

### GitHub

| Property | Type | Default | Description |
|---|---|---|---|
| `PersonalAccessToken` | `string?` | — | GitHub PAT with `repo` scope |
| `Repositories` | `List<string>` | `["HL7/fhir", "HL7/fhir-ig-publisher"]` | Repositories to track (`owner/repo` format) |
| `PageSize` | `int` | `100` | Items per API request |
| `RateLimitBuffer` | `int` | `100` | Stop requests when remaining rate limit falls below this |
| `SyncSchedule` | `TimeSpan?` | `"01:00:00"` | Sync every hour |

```json
{
  "github": {
    "PersonalAccessToken": "ghp_...",
    "Repositories": ["HL7/fhir", "HL7/fhir-ig-publisher"],
    "PageSize": 100,
    "RateLimitBuffer": 100,
    "SyncSchedule": "01:00:00"
  }
}
```

## Environment Variables

The service supports standard .NET configuration binding from environment
variables using the `FHIR_AUGURY_` prefix. The double-underscore (`__`)
separator maps to nested JSON keys.

| Variable | Maps To |
|---|---|
| `FHIR_AUGURY_DatabasePath` | `FhirAugury:DatabasePath` |
| `FHIR_AUGURY_Sources__jira__Cookie` | `FhirAugury:Sources:jira:Cookie` |
| `FHIR_AUGURY_Sources__jira__ApiToken` | `FhirAugury:Sources:jira:ApiToken` |
| `FHIR_AUGURY_Sources__zulip__ApiKey` | `FhirAugury:Sources:zulip:ApiKey` |
| `FHIR_AUGURY_Sources__github__PersonalAccessToken` | `FhirAugury:Sources:github:PersonalAccessToken` |
| `FHIR_AUGURY_Api__Port` | `FhirAugury:Api:Port` |
| `FHIR_AUGURY_Bm25__K1` | `FhirAugury:Bm25:K1` |

The MCP server uses a single environment variable:

| Variable | Description | Default |
|---|---|---|
| `FHIR_AUGURY_DB` | Path to the SQLite database | `fhir-augury.db` |

## BM25 Tuning

The BM25 scoring algorithm uses two parameters that control relevance ranking:

| Parameter | Default | Description |
|---|---|---|
| `K1` | `1.2` | Term frequency saturation. Higher values give more weight to repeated terms. Typical range: 1.0–2.0. |
| `B` | `0.75` | Document length normalization. `1.0` = full normalization; `0.0` = no length penalty. Typical range: 0.0–1.0. |

The IDF (inverse document frequency) formula used is:

```
IDF = log((N - df + 0.5) / (df + 0.5))
```

Where `N` is total documents and `df` is the number of documents containing the
term.

These parameters are configured in the `Bm25` section:

```json
{
  "FhirAugury": {
    "Bm25": {
      "K1": 1.5,
      "B": 0.5
    }
  }
}
```

## SyncSchedule Format

The `SyncSchedule` property uses .NET `TimeSpan` string format:

| Format | Example | Description |
|---|---|---|
| `HH:MM:SS` | `"01:00:00"` | 1 hour |
| `HH:MM:SS` | `"00:30:00"` | 30 minutes |
| `D.HH:MM:SS` | `"1.00:00:00"` | 1 day |

Set `SyncSchedule` to `null` or omit it to disable automatic sync for a source.

## API Configuration

| Property | Type | Default | Description |
|---|---|---|---|
| `Port` | `int` | `5100` | HTTP listen port |
| `CorsOrigins` | `string[]` | `["*"]` | Allowed CORS origins |

```json
{
  "FhirAugury": {
    "Api": {
      "Port": 8080,
      "CorsOrigins": ["https://my-app.example.com"]
    }
  }
}
```

## Example: Complete `appsettings.local.json`

```json
{
  "FhirAugury": {
    "DatabasePath": "/data/fhir-augury.db",
    "Sources": {
      "jira": {
        "Enabled": true,
        "AuthMode": "ApiToken",
        "Email": "you@example.com",
        "ApiToken": "your-jira-api-token",
        "SyncSchedule": "01:00:00"
      },
      "zulip": {
        "Enabled": true,
        "CredentialFile": "/home/user/.zuliprc",
        "SyncSchedule": "00:30:00"
      },
      "confluence": {
        "Enabled": true,
        "AuthMode": "Basic",
        "Username": "you@example.com",
        "ApiToken": "your-confluence-token",
        "Spaces": ["FHIR", "FHIRI"],
        "SyncSchedule": "02:00:00"
      },
      "github": {
        "Enabled": true,
        "PersonalAccessToken": "ghp_...",
        "Repositories": ["HL7/fhir", "HL7/fhir-ig-publisher"],
        "SyncSchedule": "01:00:00"
      }
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75
    },
    "Api": {
      "Port": 5100,
      "CorsOrigins": ["*"]
    }
  }
}
```
