# MCP Tools

FHIR Augury includes a
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that
exposes the knowledge base to LLM agents such as Claude, GitHub Copilot, and
others. The MCP server connects via HTTP to the orchestrator and source
services, providing tools across the following families:

**Cross-source families** — `Unified` (status, ingestion control), `Content`
(unified search, cross-references, item lookup).

**Source-scoped families** (added in the 2026-04 sync, one per typed
orchestrator proxy) — `JiraItems`, `JiraDimension`, `JiraWorkGroup`,
`JiraProject`, `JiraLocalProcessing`, `JiraSpecs`, `ZulipItems`,
`ZulipMessages`, `ZulipStreams`, `ZulipThreads`, `ConfluenceItems`,
`ConfluencePages`, `GitHubItems`, `GitHubRepos`.

Each MCP tool family corresponds 1:1 to a CLI command family (see
[CLI Reference](cli-reference.md)) and to a typed orchestrator proxy
controller under `src/FhirAugury.Orchestrator/Controllers/Proxies/`.

## Setup

### Building

```bash
# Build all projects (recommended)
dotnet build fhir-augury.slnx

# Or build only the shared MCP library
dotnet build src/FhirAugury.McpShared
```

The MCP server is split into three projects:

- **`FhirAugury.McpStdio`** — stdio-based MCP server (also packaged as the
  `fhir-augury-mcp` dotnet tool)
- **`FhirAugury.McpHttp`** — HTTP/SSE-based MCP server (ASP.NET Core, runs on
  port 5200, endpoint `/mcp`)
- **`FhirAugury.McpShared`** — shared library with tool implementations

### Configuration

Both MCP servers (`McpStdio` and `McpHttp`) register HTTP clients via
environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5150` | Orchestrator HTTP address |
| `FHIR_AUGURY_JIRA` | `http://localhost:5160` | Jira HTTP address |
| `FHIR_AUGURY_ZULIP` | `http://localhost:5170` | Zulip HTTP address |
| `FHIR_AUGURY_CONFLUENCE` | `http://localhost:5180` | Confluence HTTP address |
| `FHIR_AUGURY_GITHUB` | `http://localhost:5190` | GitHub HTTP address |

### Stdio Transport (Claude Desktop, etc.)

The stdio server (`McpStdio`) uses stdio transport and sends all logging to
stderr. Run it with:

```bash
dotnet run --project src/FhirAugury.McpStdio
```

#### Connecting Claude Desktop

Add to your Claude Desktop configuration
(`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS,
or `%APPDATA%\Claude\claude_desktop_config.json` on Windows):

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/fhir-augury/src/FhirAugury.McpStdio"
      ],
      "env": {
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5150",
        "FHIR_AUGURY_JIRA": "http://localhost:5160",
        "FHIR_AUGURY_ZULIP": "http://localhost:5170",
        "FHIR_AUGURY_CONFLUENCE": "http://localhost:5180",
        "FHIR_AUGURY_GITHUB": "http://localhost:5190"
      }
    }
  }
}
```

#### Direct Mode (Single Source)

To connect directly to a single source service, bypassing the orchestrator:

```json
{
  "mcpServers": {
    "fhir-augury-jira": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/fhir-augury/src/FhirAugury.McpStdio",
        "--", "--mode", "direct", "--source", "jira"
      ],
      "env": {
        "FHIR_AUGURY_JIRA": "http://localhost:5160"
      }
    }
  }
}
```

> **Tip:** See `mcp-config-examples/` in the repository for ready-to-use
> configuration files.

### HTTP Transport (VS Code, Copilot, etc.)

The HTTP server (`McpHttp`) is an ASP.NET Core application that exposes the MCP
endpoint via HTTP/SSE. It includes Aspire ServiceDefaults integration. Run it
with:

```bash
dotnet run --project src/FhirAugury.McpHttp
```

The server runs on port 5200 with the MCP endpoint at `/mcp`.

For MCP clients that support HTTP-based transport (VS Code, Copilot, etc.),
connect to the McpHttp server:

```json
{
  "mcpServers": {
    "fhir-augury": {
      "url": "http://localhost:5200/mcp"
    }
  }
}
```

---

## Unified Tools

Cross-source tools provided through the orchestrator.

### `GetStats`

Get status and statistics of all connected services — item counts, database
sizes, sync times, and service health.

No parameters.

### `TriggerSync`

Trigger synchronization/ingestion across source services.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sources` | string | No | all | Comma-separated sources to sync (e.g., `jira,zulip`) |
| `type` | string | No | `incremental` | Sync type: `incremental`, `full`, or `rebuild`. Note: the CLI verb for `rebuild` is exposed as `reingest`; the MCP/HTTP wire value remains `rebuild`. |
| `jiraProject` | string | No | | Restrict the run to a single Jira project key. Forwarded only to the Jira leg of the fan-out; ignored by other sources. |

**Example:** Trigger a full re-sync of Jira data:
```
TriggerSync(sources: "jira", type: "full")
```

### `RebuildIndex`

Rebuild specific indexes on source services without re-downloading data.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sources` | string | No | all | Comma-separated sources: `jira`, `zulip`, `github`, `confluence` |
| `indexType` | string | No | `all` | Index type: `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents` |

**Example:** Rebuild BM25 indexes on Jira:
```
RebuildIndex(sources: "jira", indexType: "bm25")
```

---

## Content Tools

Cross-source content search and cross-reference tools provided through the
orchestrator.

### `ContentSearch`

Search across all FHIR community sources using multi-value content search.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `values` | string | Yes | | Comma-separated search values |
| `sources` | string | No | all | Comma-separated source filter (e.g., `jira,zulip`) |
| `limit` | int | No | `20` | Maximum results |

**Example:** Search for patient matching across Jira and Zulip:
```
ContentSearch(values: "patient matching", sources: "jira,zulip", limit: 10)
```

### `RefersTo`

Find what a specific item refers to (outgoing cross-references).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `value` | string | Yes | | Item identifier (e.g., `FHIR-43499`) |
| `sourceType` | string | No | | Filter by source type |
| `limit` | int | No | `50` | Maximum results |

**Example:** Find what a Jira issue links to:
```
RefersTo(value: "FHIR-43499", limit: 10)
```

### `ReferredBy`

Find what refers to a specific item (incoming cross-references).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `value` | string | Yes | | Item identifier |
| `sourceType` | string | No | | Filter by source type |
| `limit` | int | No | `50` | Maximum results |

**Example:** Find all items that reference a Jira issue:
```
ReferredBy(value: "FHIR-43499", sourceType: "zulip")
```

### `CrossReferenced`

Find all cross-references for a value (both incoming and outgoing).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `value` | string | Yes | | Item identifier |
| `sourceType` | string | No | | Filter by source type |
| `limit` | int | No | `50` | Maximum results |

**Example:** Get all references for a Jira issue:
```
CrossReferenced(value: "FHIR-43499")
```

### `GetItem`

Get full details of a content item from any source, with optional content body,
comments, and markdown snapshot.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `source` | string | Yes | | Source type (e.g., `jira`, `zulip`, `confluence`, `github`) |
| `id` | string | Yes | | Item identifier (e.g., `FHIR-43499`) |
| `includeContent` | bool | No | `false` | Include the full content body |
| `includeComments` | bool | No | `false` | Include item comments |
| `includeSnapshot` | bool | No | `false` | Include a markdown snapshot |

**Example:** Get a Jira issue with comments and snapshot:
```
GetItem(source: "jira", id: "FHIR-43499", includeComments: true, includeSnapshot: true)
```

---

## Jira Tools

Source-specific tools that talk to the Jira service directly.

### `GetJiraComments`

Get comments on a Jira issue.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `key` | string | Yes | | Issue key (e.g., `FHIR-43499`) |
| `limit` | int | No | `50` | Maximum comments |

### `QueryJiraIssues`

Query Jira issues with structured filters.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `statuses` | string | No | | Comma-separated status filter |
| `workGroups` | string | No | | Comma-separated work group filter |
| `specifications` | string | No | | Comma-separated specification filter |
| `types` | string | No | | Comma-separated issue type filter |
| `priorities` | string | No | | Comma-separated priority filter |
| `query` | string | No | | Text query for additional filtering |
| `sortBy` | string | No | `updated_at` | Sort by field |
| `sortOrder` | string | No | `desc` | Sort order: asc or desc |
| `limit` | int | No | `20` | Maximum results |

### `ListJiraIssues`

List Jira issues with optional filters and sorting.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sortBy` | string | No | `updated_at` | Sort by field |
| `sortOrder` | string | No | `desc` | Sort order: asc or desc |
| `limit` | int | No | `20` | Maximum results |
| `status` | string | No | | Filter by status |
| `workGroup` | string | No | | Filter by work group |

### `ListJiraLabels`

List all available Jira labels with issue counts.

No parameters.

---

Source-specific tools that talk to the Zulip service directly.

### `GetZulipThread`

Get a full Zulip topic thread with all messages.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `stream` | string | Yes | | Stream name |
| `topic` | string | Yes | | Topic name |
| `limit` | int | No | `100` | Maximum messages |

### `QueryZulipMessages`

Query Zulip messages with structured filters.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `streams` | string | No | | Comma-separated stream filter |
| `topic` | string | No | | Topic name filter |
| `topicKeyword` | string | No | | Topic keyword (partial match) |
| `senders` | string | No | | Comma-separated sender filter |
| `query` | string | No | | Text query |
| `sortBy` | string | No | `timestamp` | Sort by field |
| `sortOrder` | string | No | `desc` | Sort order: asc or desc |
| `limit` | int | No | `20` | Maximum results |

### `ListZulipStreams`

List available Zulip streams. No parameters.

### `ListZulipTopics`

List topics in a Zulip stream.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `stream` | string | Yes | | Stream name |
| `limit` | int | No | `50` | Maximum topics |

Returns topic names with message counts and last activity.

---

## GitHub Work-Group Tools

### `github_workgroup_for_path`

Resolve the canonical HL7 work-group attribution for a file path within a
GitHub repository. Walks the same four-stage lookup chain used by
`WorkGroupResolutionPass`: exact-file match → longest directory-prefix
match → artifact-table match → repo-default → none.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `repo` | string | Yes | | Repository full name (e.g., `HL7/fhir`) |
| `path` | string | Yes | | Repository-relative file path (forward slashes) |

Returns a JSON object:

```json
{
  "repoFullName": "HL7/fhir",
  "path": "source/patient/patient-introduction.md",
  "workGroup": "fhir-i",
  "workGroupRaw": null,
  "matchedStage": "exact-file"
}
```

`matchedStage` is one of `exact-file`, `directory-prefix`, `artifact`,
`repo-default`, or `none`. `workGroupRaw` carries the original, un-resolved
input when it didn't match a canonical HL7 work-group code.

---

## Recommended Workflow for LLM Agents

The tools are designed for a progressive discovery pattern:

1. **Search** — Use `ContentSearch` for broad cross-source queries, or
   `QueryJiraIssues`/`QueryZulipMessages` for source-specific structured search
2. **Cross-references** — Use `RefersTo`, `ReferredBy`, or `CrossReferenced`
   to discover connected items across sources
3. **Deep dive** — Use `GetItem` for full item details from any source, or
   `GetJiraComments` and `GetZulipThread` for source-specific detail
4. **Browse** — Use `ListZulipStreams`, `ListZulipTopics`, and `ListJiraIssues`
   for structured browsing
5. **Admin** — Use `GetStats` to check service health, `TriggerSync` to refresh
   data, and `RebuildIndex` to rebuild search indexes

This pattern lets agents efficiently navigate the FHIR community knowledge base
without needing to understand the underlying data structure.
