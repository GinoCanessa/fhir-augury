# Configuration

FHIR Augury can be configured through command-line options (CLI), configuration
files (service), and environment variables (service and Docker).

## CLI Configuration

The CLI uses command-line options exclusively — no configuration files or
environment variables. See the [CLI Reference](cli-reference.md) for all options.

## Service Configuration

The background service (`FhirAugury.Service`) reads configuration from multiple
sources in this order (later sources override earlier ones):

1. `appsettings.json` (required)
2. `appsettings.local.json` (optional, gitignored)
3. Environment variables prefixed with `FHIR_AUGURY_`
4. User secrets (Development environment only)

### Configuration Structure

All settings live under the `FhirAugury` section:

```json
{
  "FhirAugury": {
    "DatabasePath": "fhir-augury.db",
    "Cache": {
      "RootPath": "./cache",
      "DefaultMode": "WriteThrough"
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75
    },
    "Api": {
      "Port": 5100,
      "CorsOrigins": ["*"]
    },
    "Sources": {
      "jira": {
        "Enabled": true,
        "SyncSchedule": "01:00:00",
        "BaseUrl": "https://jira.hl7.org",
        "AuthMode": "Cookie",
        "Cookie": "",
        "ApiToken": "",
        "Email": "",
        "DefaultJql": "project = \"FHIR Specification Feedback\""
      },
      "zulip": {
        "Enabled": true,
        "SyncSchedule": "04:00:00",
        "BaseUrl": "https://chat.fhir.org",
        "Email": "",
        "ApiKey": "",
        "CredentialFile": "",
        "OnlyWebPublic": true
      },
      "confluence": {
        "Enabled": true,
        "SyncSchedule": "24:00:00",
        "BaseUrl": "https://confluence.hl7.org",
        "AuthMode": "Cookie",
        "Cookie": "",
        "Username": "",
        "ApiToken": "",
        "Spaces": ["FHIR", "FHIRI"],
        "PageSize": 25
      },
      "github": {
        "Enabled": true,
        "SyncSchedule": "02:00:00",
        "PersonalAccessToken": "",
        "Repositories": ["HL7/fhir", "HL7/fhir-ig-publisher"],
        "PageSize": 100,
        "RateLimitBuffer": 100
      }
    }
  }
}
```

### General Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `DatabasePath` | string | `fhir-augury.db` | Path to the SQLite database file |
| `Cache.RootPath` | string | `./cache` | File-system response cache directory |
| `Cache.DefaultMode` | string | `WriteThrough` | Cache mode: `Disabled`, `WriteThrough` |
| `Bm25.K1` | double | `1.2` | BM25 term frequency saturation parameter |
| `Bm25.B` | double | `0.75` | BM25 document length normalization parameter |

### API Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Api.Port` | int | `5100` | HTTP listen port |
| `Api.CorsOrigins` | string[] | `["*"]` | Allowed CORS origins. `["*"]` allows all origins |

### Per-Source Settings

Each source under `Sources.<name>` supports:

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | bool | `true` | Enable or disable this source |
| `SyncSchedule` | TimeSpan | varies | Interval between automatic syncs |

#### Jira

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://jira.hl7.org` | Jira server URL |
| `AuthMode` | string | `Cookie` | `Cookie` or `ApiToken` |
| `Cookie` | string | | Session cookie for Cookie auth |
| `ApiToken` | string | | API token for ApiToken auth |
| `Email` | string | | Email for ApiToken auth |
| `DefaultJql` | string | `project = "FHIR Specification Feedback"` | Default JQL filter |

#### Zulip

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://chat.fhir.org` | Zulip server URL |
| `Email` | string | | Bot/user email |
| `ApiKey` | string | | API key |
| `CredentialFile` | string | | Path to `.zuliprc` file |
| `OnlyWebPublic` | bool | `true` | Only download web-public streams |

#### Confluence

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `https://confluence.hl7.org` | Confluence server URL |
| `AuthMode` | string | `Cookie` | `Cookie` or `Basic` |
| `Cookie` | string | | Session cookie for Cookie auth |
| `Username` | string | | Username for Basic auth |
| `ApiToken` | string | | API token for Basic auth |
| `Spaces` | string[] | `["FHIR", "FHIRI"]` | Confluence spaces to index |
| `PageSize` | int | `25` | API page size |

#### GitHub

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `PersonalAccessToken` | string | | GitHub PAT |
| `Repositories` | string[] | `["HL7/fhir", "HL7/fhir-ig-publisher"]` | Repositories to track |
| `PageSize` | int | `100` | API page size |
| `RateLimitBuffer` | int | `100` | Pause when remaining API calls drop below this |

## Environment Variables

Environment variables use the `FHIR_AUGURY_` prefix with double-underscore (`__`)
separators for nested keys. This is the standard
[ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/)
pattern.

**Examples:**

```bash
# Database path
FHIR_AUGURY_FhirAugury__DatabasePath=/data/db/fhir-augury.db

# Cache settings
FHIR_AUGURY_FhirAugury__Cache__RootPath=/data/cache
FHIR_AUGURY_FhirAugury__Cache__DefaultMode=WriteThrough

# API port
FHIR_AUGURY_FhirAugury__Api__Port=5100

# Jira credentials
FHIR_AUGURY_FhirAugury__Sources__jira__Cookie=JSESSIONID=...
FHIR_AUGURY_FhirAugury__Sources__jira__AuthMode=ApiToken
FHIR_AUGURY_FhirAugury__Sources__jira__Email=you@example.com
FHIR_AUGURY_FhirAugury__Sources__jira__ApiToken=your-token

# Zulip credentials
FHIR_AUGURY_FhirAugury__Sources__zulip__Email=bot@example.com
FHIR_AUGURY_FhirAugury__Sources__zulip__ApiKey=your-api-key

# Confluence credentials
FHIR_AUGURY_FhirAugury__Sources__confluence__Cookie=JSESSIONID=...

# GitHub credentials
FHIR_AUGURY_FhirAugury__Sources__github__PersonalAccessToken=ghp_...
```

> **Note:** The `docker-compose.yml` uses a shorter environment variable format
> (see [Docker Deployment](docker.md)).

## MCP Server Configuration

The MCP server (`FhirAugury.Mcp`) accepts:

| Source | Variable / Argument | Description |
|--------|---------------------|-------------|
| CLI argument | `--db <path>` | Database file path |
| Environment variable | `FHIR_AUGURY_DB` | Database file path (fallback) |
| Default | | `fhir-augury.db` |

The MCP server opens the database in **read-only** mode. See
[MCP Tools](mcp-tools.md) for client configuration.

## Sync Schedule Defaults

Different sources have different recommended sync intervals based on their
typical update frequency:

| Source | Default Interval | Rationale |
|--------|-----------------|-----------|
| Jira | 1 hour | Changes frequently during ballots and WGMs |
| Zulip | 4 hours | High volume but append-only |
| Confluence | 24 hours | Pages change infrequently |
| GitHub | 2 hours | Moderate update frequency |

Schedules can be updated at runtime via the HTTP API:

```bash
curl -X PUT http://localhost:5100/api/v1/ingest/jira/schedule \
  -H "Content-Type: application/json" \
  -d '{"SyncInterval": "00:30:00"}'
```
