# Getting Started

This guide walks you through setting up FHIR Augury v2, configuring data source
credentials, starting the microservices, and running your first search.

## Architecture Overview

FHIR Augury v2 uses a microservices architecture:

- **Orchestrator** — central hub (`:5150`) that coordinates queries across
  sources
- **Jira source** — (`:5160`) indexes jira.hl7.org
- **Zulip source** — (`:5170`) indexes chat.fhir.org
- **Confluence source** — (`:5180`) indexes confluence.hl7.org
- **GitHub source** — (`:5190`) indexes HL7 GitHub repos

Each source service maintains its own SQLite database, FTS5 indexes, and
response cache. The CLI and MCP tools connect to the orchestrator via HTTP.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Docker](https://www.docker.com/) (recommended, optional for running from source)
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/) (optional, for orchestrated development)

Verify your installation:

```bash
dotnet --version
# Should output 10.0.x or later
```

## Option A: Docker Compose (Recommended)

The fastest way to get started is with Docker Compose.

### 1. Clone and start services

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury

# Start all services (orchestrator + all 4 sources)
docker compose --profile full up -d
```

Other available profiles:

| Profile | Services |
|---------|----------|
| `full` | Orchestrator + Jira + Zulip + Confluence + GitHub |
| `jira-zulip` | Orchestrator + Jira + Zulip |
| `jira-only` | Jira (standalone, no orchestrator) |

### 2. Check health

```bash
# Verify the orchestrator is running
curl http://localhost:5150/health
# → { "status": "healthy", "service": "orchestrator", "version": "2.0.0" }
```

### 3. Configure credentials

Each source service requires credentials to access its upstream platform. You
only need to configure credentials for the sources you want to use.

See [Configure Credentials](#configure-credentials) below for details on each
source.

### 4. Run your first search

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"patient"}' --pretty
```

## Option B: .NET Aspire (Recommended for Development)

Aspire provides an integrated dashboard with logs, traces, and metrics, and
starts all services with a single command.

### 1. Install the Aspire workload

```bash
dotnet workload install aspire
```

### 2. Clone and start services

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury

dotnet run --project src/FhirAugury.AppHost
```

The Aspire dashboard URL is shown in the console output. Eight projects are
registered: four sources, the orchestrator, the MCP HTTP server, the Dev UI,
and the CLI tool. Confluence, Dev UI, the MCP HTTP server, and the CLI use
`WithExplicitStart()` and must be started manually from the Aspire dashboard.
The orchestrator waits for Jira, Zulip, and GitHub to be healthy before starting.

### 3. Configure credentials

Each source service requires credentials to access its upstream platform. You
only need to configure credentials for the sources you want to use.

See [Configure Credentials](#configure-credentials) below for details on each
source.

### 4. Run your first search

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"patient"}' --pretty
```

## Option C: Run from Source

### 1. Clone and build

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury
dotnet build fhir-augury.slnx
```

### 2. Configure credentials

Create an `appsettings.local.json` file in each source service project
directory with the appropriate credentials. See
[Configure Credentials](#configure-credentials) below.

### 3. Start the services

Start each service in a separate terminal:

```bash
# Start source services
dotnet run --project src/FhirAugury.Source.Jira
dotnet run --project src/FhirAugury.Source.Zulip
dotnet run --project src/FhirAugury.Source.Confluence
dotnet run --project src/FhirAugury.Source.GitHub

# Start the orchestrator
dotnet run --project src/FhirAugury.Orchestrator
```

> **Tip:** Consider using [.NET Aspire](#option-b-net-aspire-recommended-for-development)
> instead of starting each service manually — it starts all services with a
> single command and provides a dashboard.

> **Note:** You only need to start the sources you want to use. The orchestrator
> will work with whatever sources are available.

### 4. Verify health

```bash
curl http://localhost:5150/health
# → { "status": "healthy", "service": "orchestrator", "version": "2.0.0" }
```

Services auto-download and index data on startup via their built-in
`ScheduledIngestionWorker`. No manual download or index-build step is required.

### 5. (Optional) Configure auxiliary databases

For improved search quality, you can provide auxiliary databases with extended
stop words, lemmatization data, and FHIR vocabulary. Create
`appsettings.local.json` in each source service directory:

```json
{
  "Jira": {
    "AuxiliaryDatabase": {
      "AuxiliaryDatabasePath": "/path/to/auxiliary.db",
      "FhirSpecDatabasePath": "/path/to/fhir-spec.db"
    }
  }
}
```

This is optional — the system works with built-in defaults when no auxiliary
databases are configured. See [Configuration](configuration.md#auxiliary-database-optional)
for details.

### 6. Run your first search

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"patient"}' --pretty
```

## Configure Credentials

Each source service reads credentials from its `appsettings.local.json` file.
You only need credentials for the sources you want to use.

### Jira (jira.hl7.org)

Create `src/FhirAugury.Source.Jira/appsettings.local.json`:

**Session cookie** (quickest to set up):

1. Log in to [jira.hl7.org](https://jira.hl7.org) in your browser
2. Open Developer Tools → Application → Cookies
3. Copy the `JSESSIONID` value

```json
{
  "Jira": {
    "Cookie": "JSESSIONID=ABC123..."
  }
}
```

**API token** (recommended for long-term use):

```json
{
  "Jira": {
    "AuthMode": "apitoken",
    "Email": "you@example.com",
    "ApiToken": "your-token"
  }
}
```

### Zulip (chat.fhir.org)

Create `src/FhirAugury.Source.Zulip/appsettings.local.json`:

**Email + API key:**

1. Log in to [chat.fhir.org](https://chat.fhir.org)
2. Go to Settings → Your bots → Add a new bot (or use your personal API key
   from Settings → Account & privacy)

```json
{
  "Zulip": {
    "Email": "bot@example.com",
    "ApiKey": "your-api-key"
  }
}
```

**Or reference a `.zuliprc` file:**

```json
{
  "Zulip": {
    "CredentialFile": "~/.zuliprc"
  }
}
```

### Confluence (confluence.hl7.org)

Create `src/FhirAugury.Source.Confluence/appsettings.local.json`:

**Session cookie:**

```json
{
  "Confluence": {
    "Cookie": "JSESSIONID=..."
  }
}
```

**Basic auth (username + API token):**

```json
{
  "Confluence": {
    "AuthMode": "basic",
    "Username": "your-username",
    "ApiToken": "your-token"
  }
}
```

### GitHub (github.com)

Set the `GITHUB_TOKEN` environment variable, or create
`src/FhirAugury.Source.GitHub/appsettings.local.json`:

```json
{
  "GitHub": {
    "Auth": {
      "Token": "ghp_..."
    }
  }
}
```

A personal access token is recommended. Without one, GitHub limits you to 60 API
requests per hour (vs. 5,000 with a token).

## Configure Cache Locations

Each source service caches HTTP responses to the local file system, reducing
load on upstream platforms and speeding up re-indexing. The orchestrator, CLI,
and MCP server do not use a cache.

### Defaults

When running from source, each service caches to `./cache/` relative to the
project root. The `FileSystemResponseCache` internally organizes files into
source-specific subdirectories (e.g., `./cache/jira/`, `./cache/zulip/`).

| Service | Default `CachePath` |
|---------|---------------------|
| Jira | `./cache` |
| Zulip | `./cache` |
| Confluence | `./cache` |
| GitHub | `./cache` |

When running via Docker Compose, each service uses a named Docker volume mounted
at `/app/cache` inside the container (e.g., `jira-cache`, `zulip-cache`).

### Overriding cache locations (run from source)

To change a service's cache path, add a `CachePath` entry to the service's
`appsettings.local.json`. For example, to store the Jira cache in a custom
directory:

```json
{
  "Jira": {
    "CachePath": "/data/fhir-augury/jira-cache"
  }
}
```

The pattern is the same for each source — use the service's configuration
section name (`Jira`, `Zulip`, `Confluence`, or `GitHub`):

```json
{
  "Zulip": {
    "CachePath": "/data/fhir-augury/zulip-cache"
  }
}
```

### Overriding cache locations (Docker Compose)

In Docker Compose, each service's cache path is set via an environment variable.
You can override these in a `docker-compose.override.yml` or by exporting
environment variables before running `docker compose up`:

| Service | Environment Variable |
|---------|---------------------|
| Jira | `FHIR_AUGURY_JIRA__Jira__CachePath` |
| Zulip | `FHIR_AUGURY_ZULIP__Zulip__CachePath` |
| Confluence | `FHIR_AUGURY_CONFLUENCE__Confluence__CachePath` |
| GitHub | `FHIR_AUGURY_GITHUB__GitHub__CachePath` |

To bind-mount a host directory instead of using a named volume, add a
`docker-compose.override.yml`:

```yaml
services:
  source-jira:
    volumes:
      - /data/jira-cache:/app/cache
```

> **Note:** The service creates the cache directory automatically on startup if
> it does not exist. All resolved cache paths are validated to stay within the
> configured root to prevent path-traversal issues.

## Example Workflows

### Search across all sources

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"FHIR R5 patient resource"}' --pretty
```

### Filter search to specific sources

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"subscription","sources":["jira","zulip"]}' --pretty
```

### Get full details of an item

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"get","source":"jira","id":"FHIR-43499"}' --pretty
```

### Find related items

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"related","source":"jira","id":"FHIR-43499"}' --pretty
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"related","source":"jira","id":"FHIR-43499","targetSources":["zulip"]}' --pretty
```

### View cross-references

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"xref","source":"jira","id":"FHIR-43499"}' --pretty
```

### Generate a Markdown snapshot

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"snapshot","source":"jira","id":"FHIR-43499","includeComments":true}' --pretty
```

### Check service health

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"services","action":"status"}' --pretty
```

### Pretty-print output

```bash
dotnet run --project src/FhirAugury.Cli -- --json '{"command":"search","query":"terminology"}' --pretty
```

## MCP Integration

FHIR Augury can be used as an MCP (Model Context Protocol) server, enabling LLM
agents to search and browse HL7 community data. See [MCP Tools](mcp-tools.md)
for setup instructions and available tools.

## Next Steps

- [CLI Reference](cli-reference.md) — all commands and options
- [API Reference](api-reference.md) — HTTP API details
- [MCP Tools](mcp-tools.md) — integrate with LLM agents
- [Configuration](configuration.md) — full configuration reference
- [Docker Deployment](docker.md) — advanced Docker Compose options
- [Deployment Guide](../deployment.md) — Docker Compose and .NET Aspire deployment
