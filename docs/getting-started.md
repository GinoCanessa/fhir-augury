# Getting Started

This guide walks you through setting up FHIR Augury from scratch — building the
project, downloading data from HL7 FHIR community sources, building search
indexes, and running your first query.

## Prerequisites

| Requirement | Version |
|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0** or later |
| SQLite | Bundled (no separate install needed) |

Verify your .NET installation:

```bash
dotnet --version   # should print 10.0.x
```

## Clone and Build

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury
dotnet build fhir-augury.slnx
```

The solution produces three runnable projects:

| Project | Description |
|---|---|
| `FhirAugury.Cli` | Command-line tool (`fhir-augury`) |
| `FhirAugury.Service` | Background service with HTTP API |
| `FhirAugury.Mcp` | MCP server for LLM agents |

## Configure Credentials

Each data source requires its own authentication. You only need to configure the
sources you plan to use.

### Jira (jira.hl7.org)

Choose **one** authentication method:

```bash
# Option A: Browser cookie (copy from an authenticated session)
--jira-cookie "JSESSIONID=abc123..."

# Option B: API token + email
--jira-api-token "your-token" --jira-email "you@example.com"
```

### Zulip (chat.fhir.org)

Choose **one** authentication method:

```bash
# Option A: Email + API key
--zulip-email "you@example.com" --zulip-api-key "your-api-key"

# Option B: .zuliprc credential file
--zulip-rc "/path/to/.zuliprc"
```

### Confluence (confluence.hl7.org)

Choose **one** authentication method:

```bash
# Option A: Browser cookie
--confluence-cookie "JSESSIONID=abc123..."

# Option B: Basic auth (username + API token)
--confluence-user "you@example.com" --confluence-token "your-token"
```

### GitHub

```bash
--github-pat "ghp_your_personal_access_token"
```

Create a [personal access token](https://github.com/settings/tokens) with
`repo` (or `public_repo`) scope.

## Download Data

Run the CLI via `dotnet run` from the CLI project, or use the built binary
directly. All commands accept `--db <path>` to specify the database file
(default: `fhir-augury.db`).

```bash
# Download Jira issues
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --db fhir-augury.db \
  --jira-cookie "JSESSIONID=..."

# Download Zulip messages
dotnet run --project src/FhirAugury.Cli -- \
  download --source zulip --db fhir-augury.db \
  --zulip-email "you@example.com" --zulip-api-key "your-key"

# Download Confluence pages
dotnet run --project src/FhirAugury.Cli -- \
  download --source confluence --db fhir-augury.db \
  --confluence-cookie "JSESSIONID=..."

# Download GitHub issues
dotnet run --project src/FhirAugury.Cli -- \
  download --source github --db fhir-augury.db \
  --github-pat "ghp_..."
```

> **Tip:** Start with one source to get familiar, then add more as needed.

## Build Indexes

After downloading data, build the full-text search (FTS5), BM25 keyword, and
cross-reference indexes:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  index rebuild-all --db fhir-augury.db
```

This runs three steps in sequence:

1. **FTS5** — Populates full-text search virtual tables
2. **BM25** — Builds keyword indexes with IDF scores for relevance ranking
3. **Cross-references** — Detects links between items across sources (Jira keys,
   URLs, mentions)

## Run Your First Search

```bash
dotnet run --project src/FhirAugury.Cli -- \
  search "FHIR R5 changes" --db fhir-augury.db
```

Refine your search with filters:

```bash
# Search only Jira issues, limit to 10 results
dotnet run --project src/FhirAugury.Cli -- \
  search "patient resource" --source jira --limit 10 --db fhir-augury.db

# Output as JSON
dotnet run --project src/FhirAugury.Cli -- \
  search "terminology binding" --format json --db fhir-augury.db
```

## Next Steps

### Run the Background Service

The service provides an HTTP API and handles scheduled sync:

```bash
dotnet run --project src/FhirAugury.Service
```

Configure it via `src/FhirAugury.Service/appsettings.json` or
`appsettings.local.json`. See [Configuration](configuration.md) for the full
reference.

The service listens on `http://localhost:5100` by default and exposes:

- `GET /health` — Health check
- `GET /api/v1/search?q=...` — Search API
- `POST /api/v1/ingest/{source}` — Trigger ingestion

See [API Reference](api-reference.md) for complete endpoint documentation.

### Use the MCP Server

Connect the MCP server to an LLM agent (Claude Desktop, VS Code, etc.) for
AI-powered search:

```bash
dotnet run --project src/FhirAugury.Mcp -- --db fhir-augury.db
```

See [MCP Tools](mcp-tools.md) for setup instructions and the full tool
reference.

### Keep Data Fresh

Use incremental sync to pull only new and updated items:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  sync --source jira --db fhir-augury.db --jira-cookie "..."
```

Or let the background service handle it automatically on a schedule.

See [CLI Reference](cli-reference.md) for all available commands.
