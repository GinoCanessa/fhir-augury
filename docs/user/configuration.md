# Configuration

FHIR Augury uses a microservices architecture where each service has its own
configuration file and environment variables. This guide covers how to configure
each service for your deployment.

> For complete configuration tables and all available options, see the
> [Configuration Reference](../configuration.md).

## Configuration Priority

Each service reads configuration from multiple sources. Later sources override
earlier ones:

1. **`appsettings.json`** — Default settings shipped with the service
2. **`appsettings.local.json`** — Local overrides (gitignored)
3. **Environment variables** — Per-service prefixed variables
4. **User secrets** — Development environment only

## Environment Variable Naming

Environment variables follow the standard
[ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/)
pattern. Each service has its own prefix:

```
FHIR_AUGURY_{SERVICE}__{Section}__{Key}
```

Double underscores (`__`) separate nested keys.

**Quick reference — env var prefixes:**

| Service | Prefix | Config Section |
|---------|--------|----------------|
| Jira Source | `FHIR_AUGURY_JIRA_` | `Jira` |
| Zulip Source | `FHIR_AUGURY_ZULIP_` | `Zulip` |
| Confluence Source | `FHIR_AUGURY_CONFLUENCE_` | `Confluence` |
| GitHub Source | `FHIR_AUGURY_GITHUB_` | `GitHub` |
| Orchestrator | `FHIR_AUGURY_ORCHESTRATOR_` | `Orchestrator` |

---

## Source Services

Each source service runs independently with its own database, cache, and ports.

### Jira Source (`:5160` HTTP / `:5161` gRPC)

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "cookie",
    "Cookie": "",
    "ApiToken": "",
    "Email": "",
    "CachePath": "./cache/jira",
    "DatabasePath": "./data/jira.db",
    "SyncSchedule": "01:00:00",
    "DefaultProject": "FHIR",
    "Ports": { "Http": 5160, "Grpc": 5161 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

**Authentication:** Choose one of two modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Jira session cookie
  (`JSESSIONID=...`)
- **`apitoken`** — Set `Email` and `ApiToken`

```bash
# Cookie auth
FHIR_AUGURY_JIRA__Jira__AuthMode=cookie
FHIR_AUGURY_JIRA__Jira__Cookie=JSESSIONID=ABC123...

# API token auth
FHIR_AUGURY_JIRA__Jira__AuthMode=apitoken
FHIR_AUGURY_JIRA__Jira__Email=you@example.com
FHIR_AUGURY_JIRA__Jira__ApiToken=your-token
```

### Zulip Source (`:5170` HTTP / `:5171` gRPC)

```json
{
  "Zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "Email": "",
    "ApiKey": "",
    "CredentialFile": "~/.zuliprc",
    "CachePath": "./cache/zulip",
    "DatabasePath": "./data/zulip.db",
    "SyncSchedule": "04:00:00",
    "Ports": { "Http": 5170, "Grpc": 5171 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

**Authentication:** Provide either `Email` + `ApiKey`, or a path to a
`CredentialFile` (`.zuliprc` format):

```bash
FHIR_AUGURY_ZULIP__Zulip__Email=bot@example.com
FHIR_AUGURY_ZULIP__Zulip__ApiKey=your-api-key
```

### Confluence Source (`:5180` HTTP / `:5181` gRPC)

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "cookie",
    "Cookie": "",
    "Username": "",
    "ApiToken": "",
    "Spaces": ["FHIR", "FHIRI", "SOA"],
    "CachePath": "./cache/confluence",
    "DatabasePath": "./data/confluence.db",
    "SyncSchedule": "1.00:00:00",
    "Ports": { "Http": 5180, "Grpc": 5181 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

**Authentication:** Choose one of two modes via `AuthMode`:

- **`cookie`** — Set `Cookie` to your Confluence session cookie
- **`basic`** — Set `Username` and `ApiToken`

```bash
# Cookie auth
FHIR_AUGURY_CONFLUENCE__Confluence__AuthMode=cookie
FHIR_AUGURY_CONFLUENCE__Confluence__Cookie=JSESSIONID=...

# Basic auth
FHIR_AUGURY_CONFLUENCE__Confluence__AuthMode=basic
FHIR_AUGURY_CONFLUENCE__Confluence__Username=username
FHIR_AUGURY_CONFLUENCE__Confluence__ApiToken=your-token
```

### GitHub Source (`:5190` HTTP / `:5191` gRPC)

```json
{
  "GitHub": {
    "RepoMode": "core",
    "Repositories": ["HL7/fhir"],
    "Auth": {
      "Token": null,
      "TokenEnvVar": "GITHUB_TOKEN"
    },
    "CachePath": "./cache/github",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "Ports": { "Http": 5190, "Grpc": 5191 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 5,
      "MaxRetries": 5,
      "RespectRateLimitHeaders": true
    }
  }
}
```

**Authentication:** The GitHub source reads your token from the `GITHUB_TOKEN`
environment variable by default (via the `Auth.TokenEnvVar` setting). You can
also set the token directly:

```bash
# Use the standard GITHUB_TOKEN env var (recommended)
GITHUB_TOKEN=ghp_...

# Or set the token directly in config
FHIR_AUGURY_GITHUB__GitHub__Auth__Token=ghp_...
```

---

## Orchestrator (`:5150` HTTP / `:5151` gRPC)

The orchestrator aggregates results from source services and provides unified
search, cross-references, and related-item discovery.

```json
{
  "Orchestrator": {
    "DatabasePath": "./data/orchestrator.db",
    "Ports": { "Http": 5150, "Grpc": 5151 },
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
      "MaxLimit": 100
    },
    "Related": {
      "DefaultLimit": 20
    }
  }
}
```

Configure which source services the orchestrator connects to:

```bash
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__GrpcAddress=http://localhost:5161
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Jira__Enabled=true
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Zulip__GrpcAddress=http://localhost:5171
FHIR_AUGURY_ORCHESTRATOR__Orchestrator__Services__Zulip__Enabled=true
```

---

## MCP Server Configuration

The MCP server (`FhirAugury.Mcp`) connects to the orchestrator and source
services via gRPC. It is configured entirely through environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5151` | Orchestrator gRPC address |
| `FHIR_AUGURY_JIRA_GRPC` | `http://localhost:5161` | Jira source gRPC address |
| `FHIR_AUGURY_ZULIP_GRPC` | `http://localhost:5171` | Zulip source gRPC address |
| `FHIR_AUGURY_CONFLUENCE_GRPC` | `http://localhost:5181` | Confluence source gRPC address |
| `FHIR_AUGURY_GITHUB_GRPC` | `http://localhost:5191` | GitHub source gRPC address |

See [MCP Tools](mcp-tools.md) for client configuration and tool documentation.

## CLI Configuration

The CLI connects to the orchestrator for queries. Configure the endpoint with:

- **Flag:** `--orchestrator http://localhost:5151`
- **Environment variable:** `FHIR_AUGURY_ORCHESTRATOR=http://localhost:5151`

The flag takes precedence over the environment variable.

## Sync Schedule Defaults

Each source service manages its own sync schedule independently:

| Source | Default Interval | Rationale |
|--------|-----------------|-----------|
| Jira | 1 hour | Changes frequently during ballots and WGMs |
| Zulip | 4 hours | High volume but append-only |
| Confluence | 24 hours | Pages change infrequently |
| GitHub | 2 hours | Moderate update frequency |
