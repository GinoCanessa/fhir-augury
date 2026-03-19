# CLI Reference

FHIR Augury provides a command-line interface for downloading, indexing,
searching, and managing FHIR community data. The CLI operates directly against
a local SQLite database or communicates with a running FHIR Augury service.

## Usage

```bash
dotnet run --project src/FhirAugury.Cli -- [command] [options]
```

Or if installed as a .NET tool:

```bash
fhir-augury [command] [options]
```

## Global Options

These options apply to all commands:

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--db` | string | `fhir-augury.db` | Path to the SQLite database file |
| `--verbose` | flag | `false` | Enable verbose output |
| `--json` | flag | `false` | Force JSON output format |
| `--quiet` | flag | `false` | Suppress all output except errors |

---

## Commands

### `download` — Full download from a data source

Downloads all data from a specified source into the database.

```bash
fhir-augury download --source <source> [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--source` | **(Required)** Source to download: `jira`, `zulip`, `confluence`, `github` |
| `--filter` | Source-specific filter (e.g., JQL for Jira) |
| `--cache-path` | Cache root directory (default: `./cache`) |
| `--cache-mode` | `Disabled`, `WriteThrough` (default), `CacheOnly`, `WriteOnly` |

**Authentication options** — see [Authentication](#authentication) below.

**Examples:**

```bash
# Download all Jira issues
fhir-augury download --source jira --jira-cookie "JSESSIONID=..."

# Download with a JQL filter
fhir-augury download --source jira --jira-cookie "..." \
  --filter "project = FHIR AND status = Open"

# Download using cache-only mode (no network, use previously cached responses)
fhir-augury download --source jira --cache-mode CacheOnly
```

---

### `sync` — Incremental update since last sync

Fetches only new and updated items since the last successful sync.

```bash
fhir-augury sync --source <source> [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--source` | **(Required)** Source to sync: `jira`, `zulip`, `confluence`, `github`, `all` |
| `--since` | Override: sync from a specific date/time (ISO 8601) |

Plus all authentication and cache options from `download`.

**Examples:**

```bash
# Sync all sources
fhir-augury sync --source all --jira-cookie "..." --zulip-rc ~/.zuliprc

# Sync Jira since a specific date
fhir-augury sync --source jira --jira-cookie "..." --since "2025-01-01"
```

---

### `ingest` — Ingest a single item

Downloads and stores a single item by its identifier.

```bash
fhir-augury ingest --source <source> --id <identifier> [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--source` | **(Required)** Source: `jira`, `zulip`, `confluence`, `github` |
| `--id` | **(Required)** Item identifier (format varies by source) |

**Identifier formats:**

| Source | Format | Example |
|--------|--------|---------|
| Jira | Issue key | `FHIR-43499` |
| Zulip | `stream:topic` | `implementers:US Core` |
| Confluence | Page ID | `12345678` |
| GitHub | `owner/repo#number` | `HL7/fhir#1234` |

---

### `index` — Build or rebuild search indexes

Manages the FTS5, BM25, and cross-reference indexes.

```bash
fhir-augury index <subcommand>
```

**Subcommands:**

| Subcommand | Description |
|------------|-------------|
| `rebuild-all` | Full rebuild of all indexes (FTS5 + BM25 + cross-references) |
| `build-fts` | Rebuild FTS5 full-text search tables |
| `build-bm25` | Rebuild BM25 keyword scoring index |
| `build-xref` | Rebuild cross-reference links |

**Examples:**

```bash
# Rebuild everything (recommended after initial download)
fhir-augury index rebuild-all

# Rebuild only cross-references
fhir-augury index build-xref
```

---

### `search` — Search the knowledge base

Performs a full-text search across all indexed sources.

```bash
fhir-augury search -q <query> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `-q`, `--query` | string | **(Required)** | Search query text |
| `-s`, `--source` | string | all | Filter to source(s): `jira`, `zulip`, `jira-comment` (comma-separated) |
| `-n`, `--limit` | int | `20` | Maximum number of results |
| `-f`, `--format` | string | `table` | Output format: `table`, `json`, `markdown` |

**Examples:**

```bash
# Search everything
fhir-augury search -q "patient matching algorithm"

# Search only Jira, limit to 5 results
fhir-augury search -q "R5 breaking change" -s jira -n 5

# JSON output for scripting
fhir-augury search -q "terminology" -f json
```

---

### `get` — Retrieve a specific item

Fetches a single item from the local database by its identifier.

```bash
fhir-augury get --source <source> --id <identifier> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--source` | string | **(Required)** | `jira`, `zulip`, `confluence`, `github` |
| `--id` | string | **(Required)** | Item identifier |
| `-f`, `--format` | string | `table` | Output format: `table`, `json`, `markdown` |

---

### `snapshot` — Render a detailed view

Renders a rich Markdown-style snapshot of an item, including metadata,
descriptions, comments, URLs, and optionally cross-references.

```bash
fhir-augury snapshot --source <source> --id <identifier> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--source` | string | **(Required)** | `jira`, `zulip`, `confluence`, `github` |
| `--id` | string | **(Required)** | Item identifier |
| `--include-xref` | flag | `false` | Include cross-referenced related items |

---

### `related` — Find related items

Finds items related to a given item using BM25 keyword similarity and
cross-reference boosting.

```bash
fhir-augury related --source <source> --id <identifier> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--source` | string | **(Required)** | `jira`, `zulip` |
| `--id` | string | **(Required)** | Item identifier |
| `-n`, `--limit` | int | `20` | Maximum number of results |
| `-f`, `--format` | string | `table` | Output format: `table`, `json`, `markdown` |

---

### `stats` — Show database statistics

Displays item counts, last sync times, and database file size.

```bash
fhir-augury stats [options]
```

**Options:**

| Option | Description |
|--------|-------------|
| `--source` | Filter to a specific source |

---

### `service` — Interact with a running service

Communicates with a running FHIR Augury background service via its HTTP API.

```bash
fhir-augury service <subcommand> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `-s`, `--service` | string | `http://localhost:5100` | Service base URL |

**Subcommands:**

#### `service status`

Check service health and ingestion status.

#### `service trigger`

Trigger an ingestion sync.

| Option | Description |
|--------|-------------|
| `--source` | Source to sync (omit for all) |
| `--type` | Ingestion type (default: `Incremental`) |

#### `service schedule`

View or update sync schedules.

| Option | Description |
|--------|-------------|
| `--source` | Source to view/update |
| `--interval` | New sync interval (e.g., `00:30:00` for 30 minutes) |

#### `service search`

Search via the service's HTTP API.

| Option | Description |
|--------|-------------|
| `-q`, `--query` | **(Required)** Search query |
| `-n`, `--limit` | Maximum results (default: `20`) |

#### `service stats`

Get database statistics via the service API.

| Option | Description |
|--------|-------------|
| `--source` | Filter to a specific source |

---

### `cache` — Manage the response cache

Manages the file-system response cache used during downloads.

**Subcommands:**

#### `cache stats`

Show cache size and file counts per source.

| Option | Description |
|--------|-------------|
| `--cache-path` | Cache root directory (default: `./cache`) |

#### `cache clear`

Clear cached responses.

| Option | Description |
|--------|-------------|
| `--source` | Clear only this source's cache (`jira`, `zulip`, `confluence`); omit for all |
| `--cache-path` | Cache root directory (default: `./cache`) |

---

## Authentication

All authentication is provided via command-line options. No configuration files
or environment variables are used by the CLI (the service uses environment
variables — see [Configuration](configuration.md)).

### Jira

| Option | Description |
|--------|-------------|
| `--jira-cookie` | Session cookie string |
| `--jira-api-token` | Atlassian API token |
| `--jira-email` | Email for API token auth |

If both `--jira-api-token` and `--jira-email` are provided, API token mode is
used. Otherwise, cookie mode is used.

### Zulip

| Option | Description |
|--------|-------------|
| `--zulip-email` | Zulip bot/user email |
| `--zulip-api-key` | Zulip API key |
| `--zulip-rc` | Path to `.zuliprc` credential file |

### Confluence

| Option | Description |
|--------|-------------|
| `--confluence-cookie` | Session cookie string |
| `--confluence-user` | Username for Basic auth |
| `--confluence-token` | API token for Basic auth |

If both `--confluence-user` and `--confluence-token` are provided, Basic auth is
used. Otherwise, cookie mode is used.

### GitHub

| Option | Description |
|--------|-------------|
| `--github-pat` | Personal access token |

Without a PAT, requests are unauthenticated (60 req/hr limit vs 5,000 with a
token).

---

## Cache Modes

The `--cache-mode` option controls how the file-system response cache behaves
during `download` and `sync` operations:

| Mode | Behavior |
|------|----------|
| `Disabled` | No caching; all requests go to the network |
| `WriteThrough` | Read from cache if available; fetch from network and update cache on miss (default) |
| `CacheOnly` | Read only from cache; no network requests. Useful for offline development |
| `WriteOnly` | Fetch from network and write to cache, but don't read from cache |
