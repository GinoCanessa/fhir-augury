# CLI Reference

The FHIR Augury CLI (`FhirAugury.Cli`) connects to the orchestrator service via
gRPC to search, browse, and manage FHIR community data across all source
services.

## Usage

```bash
dotnet run --project src/FhirAugury.Cli -- [command] [options]
```

## Global Options

These options apply to all commands:

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--orchestrator` | string | `http://localhost:5151` | Orchestrator gRPC endpoint (or set `FHIR_AUGURY_ORCHESTRATOR` env var) |
| `--format` | string | `table` | Output format: `table`, `json`, `markdown` |
| `--verbose` | flag | `false` | Enable verbose output |

## Environment Variables

| Variable | Description |
|----------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | Default orchestrator gRPC endpoint (overridden by `--orchestrator`) |

---

## Search & Discovery Commands

### `search` — Unified search

Performs a full-text search across all indexed sources via the orchestrator.

```bash
dotnet run --project src/FhirAugury.Cli -- search <query> [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--sources` | string | all | Comma-separated source filter: `jira`, `zulip`, `confluence`, `github` |
| `--limit` | int | `20` | Maximum number of results |

**Examples:**

```bash
# Search everything
dotnet run --project src/FhirAugury.Cli -- search "patient matching algorithm"

# Search only Jira and Zulip, limit to 5 results
dotnet run --project src/FhirAugury.Cli -- search "R5 breaking change" --sources jira,zulip --limit 5

# JSON output for scripting
dotnet run --project src/FhirAugury.Cli -- search "terminology" --format json
```

---

### `get` — Get full item details

Retrieves full details of a specific item from a source service.

```bash
dotnet run --project src/FhirAugury.Cli -- get <source> <id> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `source` | Source name: `jira`, `zulip`, `confluence`, `github` |
| `id` | Item identifier (e.g., `FHIR-43499`, `implementers:US Core`) |

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--comments` | flag | `false` | Include comments in output |

**Examples:**

```bash
# Get a Jira issue
dotnet run --project src/FhirAugury.Cli -- get jira FHIR-43499

# Get with comments
dotnet run --project src/FhirAugury.Cli -- get jira FHIR-43499 --comments
```

---

### `related` — Find related items

Finds items related to a given item using keyword similarity and cross-reference
boosting.

```bash
dotnet run --project src/FhirAugury.Cli -- related <source> <id> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `source` | Source of the reference item: `jira`, `zulip`, `confluence`, `github` |
| `id` | Item identifier |

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--target-sources` | string | all | Comma-separated target sources to search |
| `--limit` | int | `20` | Maximum number of results |

**Examples:**

```bash
# Find items related to a Jira issue across all sources
dotnet run --project src/FhirAugury.Cli -- related jira FHIR-43499

# Find related items only in Zulip
dotnet run --project src/FhirAugury.Cli -- related jira FHIR-43499 --target-sources zulip
```

---

### `snapshot` — Markdown snapshot

Renders a rich Markdown snapshot of an item, including metadata, description,
and optionally comments.

```bash
dotnet run --project src/FhirAugury.Cli -- snapshot <source> <id> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `source` | Source name: `jira`, `zulip`, `confluence`, `github` |
| `id` | Item identifier |

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--comments` | flag | `false` | Include comments in snapshot |

**Examples:**

```bash
# Snapshot a Jira issue
dotnet run --project src/FhirAugury.Cli -- snapshot jira FHIR-43499

# Snapshot with comments
dotnet run --project src/FhirAugury.Cli -- snapshot jira FHIR-43499 --comments
```

---

### `xref` — Cross-references

Shows cross-references for an item — links between items across different
sources.

```bash
dotnet run --project src/FhirAugury.Cli -- xref <source> <id> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `source` | Source name: `jira`, `zulip`, `confluence`, `github` |
| `id` | Item identifier |

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--direction` | string | `outgoing` | Cross-reference direction: `outgoing`, `incoming`, `both` |

**Examples:**

```bash
# Outgoing cross-references (default)
dotnet run --project src/FhirAugury.Cli -- xref jira FHIR-43499

# All cross-references in both directions
dotnet run --project src/FhirAugury.Cli -- xref jira FHIR-43499 --direction both
```

---

## Structured Query Commands

### `query-jira` — Structured Jira query

Query Jira issues with structured filter options.

```bash
dotnet run --project src/FhirAugury.Cli -- query-jira [options]
```

**Options (12 filter parameters):**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--statuses` | string | | Filter by statuses (comma-separated) |
| `--work-groups` | string | | Filter by work groups (comma-separated) |
| `--specs` | string | | Filter by specifications (comma-separated) |
| `--types` | string | | Filter by issue types (comma-separated) |
| `--priorities` | string | | Filter by priorities (comma-separated) |
| `--labels` | string | | Filter by labels (comma-separated) |
| `--assignees` | string | | Filter by assignees (comma-separated) |
| `--query` | string | | Text query for additional filtering |
| `--sort-by` | string | `updated_at` | Sort by field |
| `--sort-order` | string | `desc` | Sort order: `asc` or `desc` |
| `--limit` | int | `20` | Maximum results |
| `--updated-after` | date | | Only issues updated after this date (ISO 8601) |

**Examples:**

```bash
# Find open FHIR Infrastructure issues
dotnet run --project src/FhirAugury.Cli -- query-jira \
  --work-groups "FHIR Infrastructure" --statuses "Open,Reopened"

# Find high-priority issues updated recently
dotnet run --project src/FhirAugury.Cli -- query-jira \
  --priorities "Critical,Major" --updated-after "2025-01-01"
```

---

### `query-zulip` — Structured Zulip query

Query Zulip messages with structured filter options.

```bash
dotnet run --project src/FhirAugury.Cli -- query-zulip [options]
```

**Options (10 filter parameters):**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--streams` | string | | Filter by stream names (comma-separated) |
| `--topic` | string | | Filter by exact topic name |
| `--topic-keyword` | string | | Filter by topic keyword (partial match) |
| `--senders` | string | | Filter by sender names (comma-separated) |
| `--query` | string | | Text query |
| `--sort-by` | string | `timestamp` | Sort by field |
| `--sort-order` | string | `desc` | Sort order: `asc` or `desc` |
| `--limit` | int | `20` | Maximum results |
| `--after` | date | | Only messages after this date (ISO 8601) |
| `--before` | date | | Only messages before this date (ISO 8601) |

**Examples:**

```bash
# Find messages in the implementers stream about US Core
dotnet run --project src/FhirAugury.Cli -- query-zulip \
  --streams implementers --topic "US Core"

# Recent messages from a specific sender
dotnet run --project src/FhirAugury.Cli -- query-zulip \
  --senders "john@example.com" --after "2025-06-01"
```

---

### `list` — List items with filters

List items from a source with filtering and sorting.

```bash
dotnet run --project src/FhirAugury.Cli -- list <source> [options]
```

**Arguments:**

| Argument | Description |
|----------|-------------|
| `source` | Source to list: `jira`, `zulip`, `confluence`, `github` |

**Examples:**

```bash
# List Jira issues
dotnet run --project src/FhirAugury.Cli -- list jira

# List Zulip messages
dotnet run --project src/FhirAugury.Cli -- list zulip --format json
```

---

## Ingestion Commands

### `ingest trigger` — Trigger sync

Triggers an ingestion sync for one or more source services.

```bash
dotnet run --project src/FhirAugury.Cli -- ingest trigger [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--sources` | string | all | Comma-separated sources to sync |
| `--type` | string | `incremental` | Ingestion type: `full`, `incremental` |

**Examples:**

```bash
# Incremental sync for all sources
dotnet run --project src/FhirAugury.Cli -- ingest trigger

# Full sync for Jira only
dotnet run --project src/FhirAugury.Cli -- ingest trigger --sources jira --type full
```

---

### `ingest status` — Ingestion status

Shows the current ingestion status across all source services.

```bash
dotnet run --project src/FhirAugury.Cli -- ingest status
```

---

### `ingest rebuild` — Rebuild from cache

Rebuilds source indexes from cached data without re-downloading.

```bash
dotnet run --project src/FhirAugury.Cli -- ingest rebuild [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--sources` | string | all | Comma-separated sources to rebuild |

**Examples:**

```bash
# Rebuild all sources
dotnet run --project src/FhirAugury.Cli -- ingest rebuild

# Rebuild only Jira
dotnet run --project src/FhirAugury.Cli -- ingest rebuild --sources jira
```

---

## Service Management Commands

### `services status` — Service health

Shows the health status of the orchestrator and all source services.

```bash
dotnet run --project src/FhirAugury.Cli -- services status
```

---

### `services stats` — Aggregate statistics

Shows aggregate statistics across all source services.

```bash
dotnet run --project src/FhirAugury.Cli -- services stats
```

---

### `services xref-scan` — Trigger cross-reference scan

Triggers a cross-reference scan across sources to discover links between items.

```bash
dotnet run --project src/FhirAugury.Cli -- services xref-scan [options]
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--full` | flag | `false` | Run a full scan (default is incremental) |

**Examples:**

```bash
# Incremental scan
dotnet run --project src/FhirAugury.Cli -- services xref-scan

# Full rescan
dotnet run --project src/FhirAugury.Cli -- services xref-scan --full
```
