# Getting Started

This guide walks you through building FHIR Augury, configuring data source
credentials, downloading your first data, and running your first search.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

Verify your installation:

```bash
dotnet --version
# Should output 10.0.x or later
```

## Build

Clone the repository and build:

```bash
git clone https://github.com/GinoCanessa/fhir-augury.git
cd fhir-augury
dotnet build fhir-augury.slnx
```

## Configure Credentials

FHIR Augury connects to four HL7 community platforms. Each requires
authentication. You only need credentials for the sources you want to use.

### Jira (jira.hl7.org)

Two authentication methods are supported:

**Session cookie** (quickest to set up):

1. Log in to [jira.hl7.org](https://jira.hl7.org) in your browser
2. Open Developer Tools → Application → Cookies
3. Copy the full cookie string (e.g., `JSESSIONID=...`)

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --db fhir-augury.db \
  --jira-cookie "JSESSIONID=ABC123..."
```

**API token** (recommended for long-term use):

1. Generate a token at your Atlassian account settings
2. Use your email and the token:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --db fhir-augury.db \
  --jira-email "you@example.com" --jira-api-token "your-token"
```

### Zulip (chat.fhir.org)

**Email + API key:**

1. Log in to [chat.fhir.org](https://chat.fhir.org)
2. Go to Settings → Your bots → Add a new bot (or use your personal API key
   from Settings → Account & privacy)

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source zulip --db fhir-augury.db \
  --zulip-email "bot@example.com" --zulip-api-key "your-api-key"
```

**Or use a `.zuliprc` file:**

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source zulip --db fhir-augury.db \
  --zulip-rc ~/.zuliprc
```

The `.zuliprc` file format (standard Zulip bot credential file):

```ini
[api]
email=bot@example.com
key=your-api-key
site=https://chat.fhir.org
```

### Confluence (confluence.hl7.org)

**Session cookie:**

1. Log in to [confluence.hl7.org](https://confluence.hl7.org) in your browser
2. Copy the cookie string from Developer Tools

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source confluence --db fhir-augury.db \
  --confluence-cookie "JSESSIONID=..."
```

**Basic auth (username + API token):**

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source confluence --db fhir-augury.db \
  --confluence-user "username" --confluence-token "your-token"
```

### GitHub (github.com)

A personal access token (PAT) is recommended. Without one, GitHub limits you to
60 API requests per hour (vs. 5,000 with a token).

1. Go to [GitHub Settings → Developer settings → Personal access tokens](https://github.com/settings/tokens)
2. Generate a token with `repo` scope (or `public_repo` for public repos only)

```bash
dotnet run --project src/FhirAugury.Cli -- \
  download --source github --db fhir-augury.db \
  --github-pat "ghp_..."
```

## Download Data

Download data from one or more sources:

```bash
# Download Jira issues
dotnet run --project src/FhirAugury.Cli -- \
  download --source jira --db fhir-augury.db --jira-cookie "..."

# Download Zulip messages
dotnet run --project src/FhirAugury.Cli -- \
  download --source zulip --db fhir-augury.db --zulip-rc ~/.zuliprc

# Download Confluence pages
dotnet run --project src/FhirAugury.Cli -- \
  download --source confluence --db fhir-augury.db --confluence-cookie "..."

# Download GitHub issues and PRs
dotnet run --project src/FhirAugury.Cli -- \
  download --source github --db fhir-augury.db --github-pat "ghp_..."
```

> **Note:** Initial downloads can take significant time depending on the data
> volume. Jira has 48K+ issues, Zulip has 1M+ messages. Use `--verbose` to see
> progress.

## Build Search Indexes

After downloading, build the search indexes:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  index rebuild-all --db fhir-augury.db
```

This builds three indexes:
- **FTS5** — full-text search across all content
- **BM25** — keyword scoring for similarity search
- **Cross-references** — links between items across sources

## Search

Run your first search:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  search -q "FHIR R5 patient resource" --db fhir-augury.db
```

Filter by source:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  search -q "subscription" -s jira --db fhir-augury.db
```

Output as JSON or Markdown:

```bash
dotnet run --project src/FhirAugury.Cli -- \
  search -q "terminology" -f json --db fhir-augury.db
```

## Incremental Sync

After the initial download, use `sync` to fetch only new and updated items:

```bash
# Sync a specific source
dotnet run --project src/FhirAugury.Cli -- \
  sync --source jira --db fhir-augury.db --jira-cookie "..."

# Sync all configured sources
dotnet run --project src/FhirAugury.Cli -- \
  sync --source all --db fhir-augury.db \
  --jira-cookie "..." --zulip-rc ~/.zuliprc
```

## Next Steps

- [CLI Reference](cli-reference.md) — all commands and options
- [Configuration](configuration.md) — full configuration reference
- [API Reference](api-reference.md) — HTTP API for the background service
- [MCP Tools](mcp-tools.md) — integrate with LLM agents
- [Docker Deployment](docker.md) — run as a containerized service
