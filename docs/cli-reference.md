# CLI Reference

Complete command reference for the `fhir-augury` CLI tool.

**Run via:** `dotnet run --project src/FhirAugury.Cli -- <command> [options]`

## Global Options

These options can be used with any command:

```
--db <path>       Database file path (default: fhir-augury.db)
--verbose         Enable verbose output
--json            Force JSON output format
--quiet           Suppress all output except errors
```

## Commands

### `download` — Full download from a data source

Downloads all available data from a source into the database. Use this for
initial population.

```bash
fhir-augury download --source <jira|zulip|confluence|github> [auth-options]
```

| Option | Description |
|---|---|
| `--source` | **Required.** Source to download: `jira`, `zulip`, `confluence`, `github` |
| `--filter` | Source-specific filter (e.g., JQL query for Jira) |

**Source-specific auth options:** See [Authentication Options](#authentication-options).

**Examples:**

```bash
# Download all Jira issues
fhir-augury download --source jira --jira-cookie "JSESSIONID=..."

# Download Jira with a custom JQL filter
fhir-augury download --source jira --filter "project = FHIR AND status = Open" \
  --jira-api-token "token" --jira-email "you@example.com"

# Download Zulip using a .zuliprc file
fhir-augury download --source zulip --zulip-rc ~/.zuliprc

# Download GitHub issues
fhir-augury download --source github --github-pat "ghp_..."
```

---

### `sync` — Incremental update since last sync

Fetches only new and updated items since the last successful sync. Much faster
than a full download for ongoing use.

```bash
fhir-augury sync --source <jira|zulip|confluence|github|all> [auth-options]
```

| Option | Description |
|---|---|
| `--source` | **Required.** Source to sync, or `all` for all sources |
| `--since` | Override the sync-from date (`DateTimeOffset`). If omitted, uses the last recorded sync time. |

**Examples:**

```bash
# Incremental sync for Jira
fhir-augury sync --source jira --jira-cookie "JSESSIONID=..."

# Sync all sources
fhir-augury sync --source all --jira-cookie "..." --zulip-rc ~/.zuliprc \
  --confluence-cookie "..." --github-pat "ghp_..."

# Sync from a specific date
fhir-augury sync --source zulip --since "2024-01-01T00:00:00Z" \
  --zulip-email "bot@example.com" --zulip-api-key "key"
```

---

### `ingest` — Ingest a single item by identifier

Downloads and indexes a single item by its source-specific identifier. Useful
for on-demand updates of specific items.

```bash
fhir-augury ingest --source <jira|zulip|confluence|github> --id <identifier> [auth-options]
```

| Option | Description |
|---|---|
| `--source` | **Required.** Source type |
| `--id` | **Required.** Item identifier |

**Identifier formats by source:**

| Source | Format | Example |
|---|---|---|
| Jira | Issue key | `FHIR-43499` |
| Zulip | `stream:topic` | `implementers:Patient search` |
| Confluence | Page ID | `12345678` |
| GitHub | `owner/repo#number` | `HL7/fhir#1234` |

**Example:**

```bash
fhir-augury ingest --source jira --id FHIR-43499 --jira-cookie "..."
```

---

### `index` — Build or rebuild search indexes

Manages the FTS5 full-text, BM25 keyword, and cross-reference indexes.

```bash
fhir-augury index <subcommand>
```

| Subcommand | Description |
|---|---|
| `build-fts` | Populate FTS5 virtual tables from content tables |
| `build-bm25` | Build or rebuild BM25 keyword index with IDF scores |
| `build-xref` | Build or rebuild cross-reference links across sources |
| `rebuild-all` | Full rebuild of all indexes (FTS5 + BM25 + cross-references) |

**Examples:**

```bash
# Full rebuild (recommended after initial download)
fhir-augury index rebuild-all --db fhir-augury.db

# Rebuild only the FTS index
fhir-augury index build-fts --db fhir-augury.db

# Rebuild cross-references after new data
fhir-augury index build-xref --db fhir-augury.db
```

---

### `search` — Search the knowledge base

Performs a unified full-text search across all indexed sources.

```bash
fhir-augury search <query> [options]
```

| Option | Short | Default | Description |
|---|---|---|---|
| `--query` | `-q` | — | **Required.** Search query text |
| `--source` | `-s` | all | Filter to source(s): `jira`, `zulip`, `jira-comment`, `confluence`, `github` (comma-separated) |
| `--limit` | `-n` | `20` | Maximum number of results |
| `--format` | `-f` | `table` | Output format: `table`, `json`, `markdown` |

**Examples:**

```bash
# Basic search
fhir-augury search "FHIR R5 patient resource"

# Search Jira only, output as JSON
fhir-augury search "terminology binding" --source jira --format json

# Search multiple sources with a result limit
fhir-augury search "validation" --source jira,zulip --limit 5

# Markdown output for pasting
fhir-augury search "extensions" --format markdown
```

---

### `get` — Retrieve a specific item by identifier

Fetches and displays a single item from the local database.

```bash
fhir-augury get --source <source> --id <identifier> [options]
```

| Option | Short | Default | Description |
|---|---|---|---|
| `--source` | — | — | **Required.** Source type |
| `--id` | — | — | **Required.** Item identifier |
| `--format` | `-f` | `table` | Output format: `table`, `json`, `markdown` |

**Example:**

```bash
fhir-augury get --source jira --id FHIR-43499
fhir-augury get --source zulip --id "implementers:Patient search" --format json
```

---

### `snapshot` — Detailed markdown snapshot of an item

Renders a comprehensive markdown view of an item including all metadata,
description, comments, and optionally cross-references.

```bash
fhir-augury snapshot --source <source> --id <identifier> [options]
```

| Option | Default | Description |
|---|---|---|
| `--source` | — | **Required.** Source type |
| `--id` | — | **Required.** Item identifier |
| `--include-xref` | `false` | Include cross-references in the output |

**Example:**

```bash
fhir-augury snapshot --source jira --id FHIR-43499 --include-xref
fhir-augury snapshot --source zulip --id "implementers:Patient search"
```

---

### `stats` — Show database statistics

Displays counts for all content tables, sync state, and database file size.

```bash
fhir-augury stats [options]
```

| Option | Description |
|---|---|
| `--source` | Filter to a specific source |

**Example output:**

```
Jira issues:        12,345
Jira comments:      45,678
Zulip streams:         142
Zulip messages:    234,567
Confluence spaces:      12
Confluence pages:    3,456
GitHub repos:            5
GitHub issues:       8,901
GitHub comments:    23,456
Database size:      1.2 GB
```

---

### `related` — Find items related to a given item

Uses BM25 keyword similarity and cross-reference links to find related content
across all sources.

```bash
fhir-augury related --source <source> --id <identifier> [options]
```

| Option | Short | Default | Description |
|---|---|---|---|
| `--source` | — | — | **Required.** Source type: `jira`, `zulip` |
| `--id` | — | — | **Required.** Item identifier |
| `--limit` | `-n` | `20` | Maximum number of results |
| `--format` | `-f` | `table` | Output format: `table`, `json`, `markdown` |

**Example:**

```bash
fhir-augury related --source jira --id FHIR-43499 --limit 10
```

---

### `service` — Interact with a running FHIR Augury service

Commands for managing and querying a running background service instance.

```bash
fhir-augury service <subcommand> [options]
```

| Option | Short | Default | Description |
|---|---|---|---|
| `--service` | `-s` | `http://localhost:5100` | Service base URL |

#### `service status`

Check service health and ingestion status.

```bash
fhir-augury service status
fhir-augury service status --service http://my-server:5100
```

#### `service trigger`

Trigger an ingestion run.

| Option | Description |
|---|---|
| `--source` | Source to trigger |
| `--type` | Ingestion type: `Full`, `Incremental` (default: `Incremental`) |

```bash
fhir-augury service trigger --source jira --type Incremental
```

#### `service schedule`

View or update sync schedules.

| Option | Description |
|---|---|
| `--source` | Source to update (for setting schedules) |
| `--interval` | New sync interval (TimeSpan format, e.g., `"00:30:00"`) |

```bash
# View all schedules
fhir-augury service schedule

# Update Jira sync to every 30 minutes
fhir-augury service schedule --source jira --interval "00:30:00"
```

#### `service search`

Search via the service API.

| Option | Short | Default | Description |
|---|---|---|---|
| `--query` | `-q` | — | **Required.** Search query |
| `--limit` | `-n` | `20` | Max results |

```bash
fhir-augury service search -q "patient matching" -n 10
```

#### `service stats`

Get database statistics via the service API.

| Option | Description |
|---|---|
| `--source` | Filter to a specific source |

```bash
fhir-augury service stats
fhir-augury service stats --source jira
```

---

## Authentication Options

These options are available on `download`, `sync`, and `ingest` commands:

### Jira

| Option | Description |
|---|---|
| `--jira-cookie` | Session cookie (e.g., `"JSESSIONID=abc123..."`) |
| `--jira-api-token` | API token (use with `--jira-email`) |
| `--jira-email` | Email address for API token auth |

### Zulip

| Option | Description |
|---|---|
| `--zulip-email` | Email address (use with `--zulip-api-key`) |
| `--zulip-api-key` | API key for Zulip authentication |
| `--zulip-rc` | Path to `.zuliprc` credential file |

### Confluence

| Option | Description |
|---|---|
| `--confluence-cookie` | Session cookie for cookie auth |
| `--confluence-user` | Username for basic auth (use with `--confluence-token`) |
| `--confluence-token` | API token for basic auth |

### GitHub

| Option | Description |
|---|---|
| `--github-pat` | Personal access token with `repo` scope |
