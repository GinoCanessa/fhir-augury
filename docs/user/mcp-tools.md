# MCP Tools

FHIR Augury includes a
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that
exposes the knowledge base to LLM agents such as Claude, GitHub Copilot, and
others. The MCP server connects via gRPC to the orchestrator and source
services, providing 18 tools across 3 categories (Unified, Jira, Zulip).

> **Note:** Confluence and GitHub tools are not yet implemented. The gRPC client
> configuration for those services is present but no MCP tools expose them yet.

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

Both MCP servers (`McpStdio` and `McpHttp`) register gRPC clients via
environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | `http://localhost:5151` | Orchestrator gRPC address |
| `FHIR_AUGURY_JIRA_GRPC` | `http://localhost:5161` | Jira source gRPC address |
| `FHIR_AUGURY_ZULIP_GRPC` | `http://localhost:5171` | Zulip source gRPC address |
| `FHIR_AUGURY_CONFLUENCE_GRPC` | `http://localhost:5181` | Confluence source gRPC address |
| `FHIR_AUGURY_GITHUB_GRPC` | `http://localhost:5191` | GitHub source gRPC address |

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
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5151",
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161",
        "FHIR_AUGURY_ZULIP_GRPC": "http://localhost:5171",
        "FHIR_AUGURY_CONFLUENCE_GRPC": "http://localhost:5181",
        "FHIR_AUGURY_GITHUB_GRPC": "http://localhost:5191"
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
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161"
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

### `Search`

Unified cross-source search using full-text search.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `sources` | string | No | all | Comma-separated source filter (e.g., `jira,zulip`) |
| `limit` | int | No | `20` | Maximum results |

**Example:** Search for patient matching across Jira and Zulip:
```
Search(query: "patient matching", sources: "jira,zulip", limit: 10)
```

### `FindRelated`

Find items related to a given item using keyword similarity and cross-reference
boosting.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `source` | string | Yes | | Source type (e.g., `jira`) |
| `id` | string | Yes | | Item identifier (e.g., `FHIR-43499`) |
| `targetSources` | string | No | all | Comma-separated target sources to search |
| `limit` | int | No | `20` | Maximum results |

**Example:** Find Zulip discussions related to a Jira issue:
```
FindRelated(source: "jira", id: "FHIR-43499", targetSources: "zulip", limit: 5)
```

### `GetCrossReferences`

Get explicit cross-references (mentions and links) for an item.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `source` | string | Yes | | Source type |
| `id` | string | Yes | | Item identifier |
| `direction` | string | No | `both` | `outgoing`, `incoming`, or `both` |

**Example:** Get all references pointing to a Jira issue:
```
GetCrossReferences(source: "jira", id: "FHIR-43499", direction: "incoming")
```

### `GetStats`

Get service status and statistics — item counts, sync times, and service
health.

No required parameters.

### `TriggerSync`

Trigger a data sync for one or more sources.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sources` | string | No | all | Comma-separated sources to sync |
| `type` | string | No | `incremental` | `full` or `incremental` |

**Example:** Trigger a full re-sync of Jira data:
```
TriggerSync(sources: "jira", type: "full")
```

### `RebuildIndex`

Rebuild specific indexes on source services without re-downloading data.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sources` | string | No | all | Comma-separated sources to rebuild |
| `indexType` | string | No | `all` | Index type: `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links` |

**Example:** Rebuild BM25 indexes on Jira:
```
RebuildIndex(sources: "jira", indexType: "bm25")
```

---

## Jira Tools

Source-specific tools for Jira issue tracking data.

### `SearchJira`

Full-text search across Jira issues.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `status` | string | No | | Filter by status |
| `limit` | int | No | `20` | Maximum results |

### `GetJiraIssue`

Get full details of a Jira issue including metadata, description, and comments.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `key` | string | Yes | Issue key (e.g., `FHIR-43499`) |
| `includeComments` | bool | No | `true` | Include issue comments |

### `GetJiraComments`

Get streaming comments on a Jira issue.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `key` | string | Yes | | Issue key |
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

### `SnapshotJiraIssue`

Generate a rich markdown snapshot of a Jira issue with metadata, description,
comments, and cross-references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `key` | string | Yes | | Issue key |
| `includeComments` | bool | No | `true` | Include issue comments |

### `ListJiraIssues`

List Jira issues with sorting and filters.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sortBy` | string | No | `updated_at` | Sort by field |
| `sortOrder` | string | No | `desc` | Sort order: asc or desc |
| `limit` | int | No | `20` | Maximum results |
| `status` | string | No | | Filter by status |
| `workGroup` | string | No | | Filter by work group |

---

## Zulip Tools

Source-specific tools for Zulip chat data.

### `SearchZulip`

Full-text search across Zulip messages.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `stream` | string | No | | Filter to a specific stream |
| `limit` | int | No | `20` | Maximum results |

### `GetZulipThread`

Get a full Zulip topic thread with participants and timestamps.

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

List all available Zulip streams. No parameters.

### `ListZulipTopics`

List topics in a Zulip stream.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `stream` | string | Yes | | Stream name |
| `limit` | int | No | `50` | Maximum topics |

Returns topic names with message counts and last activity.

### `SnapshotZulipThread`

Generate a rich markdown snapshot of a Zulip topic thread with cross-references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `stream` | string | Yes | | Stream name |
| `topic` | string | Yes | | Topic name |

---

## Recommended Workflow for LLM Agents

The tools are designed for a progressive discovery pattern:

1. **Search** — Use `Search` for broad cross-source queries, or
   `SearchJira`/`SearchZulip` for source-specific full-text search
2. **Snapshot** — Use `SnapshotJiraIssue` or `SnapshotZulipThread` to get rich
   markdown views of interesting items
3. **Explore** — Use `FindRelated` and `GetCrossReferences` to discover
   connected items across sources
4. **Deep dive** — Use `GetJiraIssue`, `GetJiraComments`, `GetZulipThread` for
   full item details
5. **Browse** — Use `ListZulipStreams`, `ListZulipTopics`, `ListJiraIssues`,
   and `QueryJiraIssues`/`QueryZulipMessages` for structured browsing

This pattern lets agents efficiently navigate the FHIR community knowledge base
without needing to understand the underlying data structure.
