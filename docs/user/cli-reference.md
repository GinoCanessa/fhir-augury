# CLI Reference

The FHIR Augury CLI (`fhir-augury`) connects to the orchestrator service via
HTTP to search, browse, and manage FHIR community data across all source
services. All input and output uses JSON.

## Usage

```bash
fhir-augury --json '{"command":"<name>", ...}' [--pretty] [--output <file>]
fhir-augury --input <file> [--pretty] [--output <file>]
fhir-augury --help [command] [--pretty] [--output <file>]
fhir-augury --json @file.json [--pretty] [--output <file>]
fhir-augury --json @- [--pretty] [--output <file>]
```

## Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `--json <string>` | Yes (unless `--input` or `--help`) | JSON string containing the command request. Use `@file.json` to read from a file, `@-` to read from stdin. Mutually exclusive with `--input`. |
| `--input <file>` | Yes (unless `--json` or `--help`) | Path to a JSON file containing the command request. Mutually exclusive with `--json`. |
| `--output <file>` | No | Write JSON output to the specified file instead of stdout. |
| `--pretty` | No | Pretty-print JSON output. Default is compact single-line JSON. |
| `--help [command]` | No | Output JSON describing available commands and their schemas. With a command name, returns only that command's schemas. |

## Environment Variables

| Variable | Description |
|----------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | Orchestrator HTTP endpoint (default: `http://localhost:5150`) |

The orchestrator address can also be set per-request via the `orchestrator`
field in the JSON input.

---

## Input JSON Format

Every request is a JSON object with a required `command` field:

```jsonc
{
  "command": "<command-name>",    // required
  "orchestrator": "http://...",  // optional override
  "verbose": false               // optional; include timing in metadata
}
```

## Output JSON Format

All responses use a consistent envelope:

```jsonc
// Success
{
  "success": true,
  "command": "search",
  "data": { /* command-specific */ },
  "metadata": { "elapsedMs": 142, "orchestrator": "http://localhost:5150", "version": "1.2.0" },
  "warnings": []
}

// Error
{
  "success": false,
  "command": "search",
  "error": { "code": "CONNECTION_FAILED", "message": "...", "details": "..." },
  "metadata": { "orchestrator": "http://localhost:5150", "version": "1.2.0" }
}
```

---

## Commands

### `search` — Unified search

```jsonc
{
  "command": "search",
  "query": "patient matching algorithm",   // required
  "sources": ["jira", "zulip"],            // optional (default: all)
  "limit": 20                              // optional (default: 20)
}
```

### `get` — Get full item details

```jsonc
{
  "command": "get",
  "source": "jira",              // required
  "id": "FHIR-43499",           // required
  "includeComments": true,       // optional (default: true)
  "includeContent": false,       // optional (default: false)
  "includeSnapshot": false       // optional (default: false)
}
```

### `refers-to` — Outgoing cross-references

Returns items that the specified item refers to (outgoing links).

```jsonc
{
  "command": "refers-to",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `referred-by` — Incoming cross-references

Returns items that refer to the specified item (incoming links).

```jsonc
{
  "command": "referred-by",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `cross-referenced` — All cross-references

Returns both outgoing and incoming cross-references for the specified item.

```jsonc
{
  "command": "cross-referenced",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `list` — List items from a source

```jsonc
{
  "command": "list",
  "source": "jira",              // required
  "limit": 20,                   // optional (default: 20)
  "sortBy": "updated_at",       // optional (default: "updated_at")
  "sortOrder": "desc",          // optional (default: "desc")
  "filters": { "status": "Open" } // optional
}
```

### `query-jira` — Structured Jira query

```jsonc
{
  "command": "query-jira",
  "query": "R5 breaking change",       // optional
  "statuses": ["Open", "Reopened"],    // optional
  "workGroups": ["FHIR Infrastructure"], // optional
  "specifications": ["FHIR Core"],     // optional
  "types": ["Bug", "New Feature"],     // optional
  "priorities": ["Critical", "Major"], // optional
  "labels": ["connectathon"],          // optional
  "assignees": ["jsmith"],             // optional
  "sortBy": "updated_at",             // optional (default: "updated_at")
  "sortOrder": "desc",                // optional (default: "desc")
  "limit": 20,                        // optional (default: 20)
  "updatedAfter": "2025-01-01T00:00:00Z" // optional (ISO 8601)
}
```

### `query-zulip` — Structured Zulip query

```jsonc
{
  "command": "query-zulip",
  "query": "US Core",                // optional
  "streams": ["implementers"],       // optional
  "topic": "US Core",               // optional (exact match)
  "topicKeyword": "core",           // optional (partial match)
  "senders": ["john@example.com"],  // optional
  "sortBy": "timestamp",            // optional (default: "timestamp")
  "sortOrder": "desc",              // optional (default: "desc")
  "limit": 20,                      // optional (default: 20)
  "after": "2025-06-01T00:00:00Z", // optional (ISO 8601)
  "before": "2025-12-31T00:00:00Z" // optional (ISO 8601)
}
```

### `ingest` — Ingestion management

```jsonc
// Trigger sync
{ "command": "ingest", "action": "trigger", "sources": ["jira"], "type": "incremental" }

// Restrict a Jira sync to a single project
{ "command": "ingest", "action": "trigger", "sources": ["jira"], "jiraProject": "FHIR" }

// Check status
{ "command": "ingest", "action": "status" }

// Rebuild from cache (formerly "rebuild" — renamed in the 2026-04 sync; no alias)
{ "command": "ingest", "action": "reingest", "sources": ["jira"] }

// Rebuild indexes (formerly "index" — renamed in the 2026-04 sync; no alias)
{ "command": "ingest", "action": "reindex", "sources": ["jira"], "indexType": "bm25" }
```

**Actions:** `trigger`, `status`, `reingest`, `reindex`

> **Breaking change.** The `rebuild` and `index` action names were renamed
> to `reingest` and `reindex` respectively in the 2026-04 sync. There is
> no backwards-compatibility alias — callers using the old names will get
> `Unknown ingest action`. The wire-level orchestrator routes
> (`POST /api/v1/rebuild`, `POST /api/v1/rebuild-index`) keep their
> historical names; only the CLI / MCP surface was renamed.

**Optional fields:**

| Field | Type | Applies to | Description |
|-------|------|------------|-------------|
| `sources` | string[] | `trigger`, `reingest`, `reindex` | Comma-separated source names. Omit for all enabled sources. |
| `type` | string | `trigger` | `incremental` (default), `full`, or `rebuild`. |
| `jiraProject` | string | `trigger`, `reingest` | Restrict the run to a single Jira project key. Forwarded only to the Jira leg of the fan-out; ignored by other sources. Surfaced over HTTP as `?jira-project=`. |
| `indexType` | string | `reindex` | `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents`. |

**Index types:** `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents`

### `services` — Service management

```jsonc
// Service health
{ "command": "services", "action": "status" }

// Aggregate statistics
{ "command": "services", "action": "stats" }
```

### Source-scoped commands (added in the 2026-04 sync)

The CLI now ships per-source command families that mirror the typed
orchestrator proxies (`/api/v1/{name}/...`) and the MCP tool families
(see [MCP Tools](mcp-tools.md)). Every command takes a single JSON
object on stdin and emits a single JSON envelope on stdout. Use
`--help <command>` for the full per-command schema.

| Command | Hits | Purpose |
|---------|------|---------|
| `jira-items` | `/api/v1/jira/items[/{key}/...]` | List / get Jira items, related, snapshot, content, comments, links |
| `jira-dimension` | `/api/v1/jira/{labels,statuses,users,inpersons}` | Dimension lookups (replaces `list-jira-dimension`-style ad-hoc calls) |
| `jira-workgroup` | `/api/v1/jira/work-groups[/{code}/issues]` | Work-group enumeration and per-work-group issue lists |
| `jira-project` | `/api/v1/jira/projects[/{key}]` | List, get, and update Jira project metadata |
| `jira-local-processing` | `/api/v1/jira/local-processing/...` | Local processing queue (tickets, random-ticket, set-processed, clear-all-processed) |
| `jira-specs` | `/api/v1/github/jira-specs/...` | Jira-spec ↔ GitHub-artifact resolution |
| `zulip-items` | `/api/v1/zulip/items[/{id}/...]` | Zulip item shape (with `comments` / `links` returning `[]` shape stubs) |
| `zulip-messages` | `/api/v1/zulip/messages[...]` | Single message, by-user lists, paged listings |
| `zulip-streams` | `/api/v1/zulip/streams[/{id}\|/{name}/topics]` | Stream catalog and per-stream topic enumeration |
| `zulip-threads` | `/api/v1/zulip/threads/{stream}/{topic}[/snapshot]` | Topic-thread retrieval |
| `confluence-pages` | `/api/v1/confluence/pages[/{id}/...]` | Pages, related, snapshot, content, comments, children, ancestors, linked, by-label |
| `confluence-items` | `/api/v1/confluence/items[/{id}/...]` | Confluence-side item shape |
| `github-items` | `/api/v1/github/items/{action}/{**key}` | Action-first item layout (catch-all key carries `owner/name#123`) |
| `github-repos` | `/api/v1/github/repos[/{owner}/{name}/tags...]` | Repo catalog and per-repo tag/file lookups |

The `--jira-project <key>` option is also accepted by the renamed
`reingest` verb on the `ingest` command (see above).

### `version` — Show version

```jsonc
{ "command": "version" }
```

### `show-schemas` — Output JSON schemas

Returns the full set of JSON schemas describing the CLI's input and output
contracts.

```jsonc
{ "command": "show-schemas" }
```

### `save-schemas` — Save schemas to disk

Exports schema files to a directory.

```jsonc
{ "command": "save-schemas", "outputDirectory": "./schemas" }
```

---

## Examples

```bash
# Inline JSON search
fhir-augury --json '{"command":"search","query":"patient matching","limit":5}'

# Pretty-printed output
fhir-augury --json '{"command":"search","query":"patient matching"}' --pretty

# Read from file
fhir-augury --input request.json

# Write output to file
fhir-augury --json '{"command":"search","query":"patient matching"}' --output results.json

# File-to-file pipeline
fhir-augury --input request.json --output results.json --pretty

# Read from file via @-prefix
fhir-augury --json @request.json

# Read from stdin
echo '{"command":"version"}' | fhir-augury --json @-

# Get help for all commands (JSON)
fhir-augury --help --pretty

# Get help for a specific command
fhir-augury --help search --pretty
```
