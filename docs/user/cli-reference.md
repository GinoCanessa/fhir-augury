# CLI Reference

The FHIR Augury CLI (`fhir-augury`) connects to the orchestrator service via
gRPC to search, browse, and manage FHIR community data across all source
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
| `FHIR_AUGURY_ORCHESTRATOR` | Default orchestrator gRPC endpoint (default: `http://localhost:5151`) |

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
  "metadata": { "elapsedMs": 142, "orchestrator": "http://localhost:5151", "version": "1.2.0" },
  "warnings": []
}

// Error
{
  "success": false,
  "command": "search",
  "error": { "code": "CONNECTION_FAILED", "message": "...", "details": "..." },
  "metadata": { "orchestrator": "http://localhost:5151", "version": "1.2.0" }
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
  "source": "jira",          // required
  "id": "FHIR-43499",       // required
  "includeComments": true    // optional (default: true)
}
```

### `snapshot` — Markdown snapshot

```jsonc
{
  "command": "snapshot",
  "source": "jira",          // required
  "id": "FHIR-43499",       // required
  "includeComments": true    // optional (default: true)
}
```

### `related` — Find related items

```jsonc
{
  "command": "related",
  "source": "jira",              // required
  "id": "FHIR-43499",           // required
  "targetSources": ["zulip"],   // optional
  "limit": 20                   // optional (default: 20)
}
```

### `xref` — Cross-references

```jsonc
{
  "command": "xref",
  "source": "jira",         // required
  "id": "FHIR-43499",      // required
  "direction": "both"       // optional: "outgoing", "incoming", "both" (default: "both")
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

// Check status
{ "command": "ingest", "action": "status" }

// Rebuild from cache
{ "command": "ingest", "action": "rebuild", "sources": ["jira"] }

// Rebuild indexes
{ "command": "ingest", "action": "index", "sources": ["jira"], "indexType": "bm25" }
```

**Actions:** `trigger`, `status`, `rebuild`, `index`

**Index types:** `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`

### `services` — Service management

```jsonc
// Service health
{ "command": "services", "action": "status" }

// Aggregate statistics
{ "command": "services", "action": "stats" }
```

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
