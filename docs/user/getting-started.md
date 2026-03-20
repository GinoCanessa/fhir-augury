# Getting Started

This guide walks you through setting up FHIR Augury v2, configuring data source
credentials, starting the microservices, and running your first search.

## Architecture Overview

FHIR Augury v2 uses a microservices architecture:

- **Orchestrator** — central hub (HTTP :5150 / gRPC :5151) that coordinates
  queries across sources
- **Jira source** — (HTTP :5160 / gRPC :5161) indexes jira.hl7.org
- **Zulip source** — (HTTP :5170 / gRPC :5171) indexes chat.fhir.org
- **Confluence source** — (HTTP :5180 / gRPC :5181) indexes confluence.hl7.org
- **GitHub source** — (HTTP :5190 / gRPC :5191) indexes HL7 GitHub repos

Each source service maintains its own SQLite database, FTS5 indexes, and
response cache. The CLI and MCP tools connect to the orchestrator via gRPC.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Docker](https://www.docker.com/) (recommended, optional for running from source)

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
dotnet run --project src/FhirAugury.Cli -- search "patient"
```

## Option B: Run from Source

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

> **Note:** You only need to start the sources you want to use. The orchestrator
> will work with whatever sources are available.

### 4. Verify health

```bash
curl http://localhost:5150/health
# → { "status": "healthy", "service": "orchestrator", "version": "2.0.0" }
```

Services auto-download and index data on startup via their built-in
`ScheduledIngestionWorker`. No manual download or index-build step is required.

### 5. Run your first search

```bash
dotnet run --project src/FhirAugury.Cli -- search "patient"
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

## Example Workflows

### Search across all sources

```bash
dotnet run --project src/FhirAugury.Cli -- search "FHIR R5 patient resource"
```

### Filter search to specific sources

```bash
dotnet run --project src/FhirAugury.Cli -- search "subscription" --sources jira,zulip
```

### Get full details of an item

```bash
dotnet run --project src/FhirAugury.Cli -- get jira FHIR-43499
dotnet run --project src/FhirAugury.Cli -- get jira FHIR-43499 --comments
```

### Find related items

```bash
dotnet run --project src/FhirAugury.Cli -- related jira FHIR-43499
dotnet run --project src/FhirAugury.Cli -- related jira FHIR-43499 --target-sources zulip
```

### View cross-references

```bash
dotnet run --project src/FhirAugury.Cli -- xref jira FHIR-43499
dotnet run --project src/FhirAugury.Cli -- xref jira FHIR-43499 --direction both
```

### Generate a Markdown snapshot

```bash
dotnet run --project src/FhirAugury.Cli -- snapshot jira FHIR-43499 --comments
```

### Check service health

```bash
dotnet run --project src/FhirAugury.Cli -- services status
```

### Output as JSON

```bash
dotnet run --project src/FhirAugury.Cli -- search "terminology" --format json
```

## MCP Integration

FHIR Augury can be used as an MCP (Model Context Protocol) server, enabling LLM
agents to search and browse HL7 community data. See [MCP Tools](mcp-tools.md)
for setup instructions and available tools.

## Next Steps

- [CLI Reference](cli-reference.md) — all commands and options
- [API Reference](api-reference.md) — HTTP and gRPC API details
- [MCP Tools](mcp-tools.md) — integrate with LLM agents
- [Configuration](configuration.md) — full configuration reference
- [Docker Deployment](docker.md) — advanced Docker Compose options
