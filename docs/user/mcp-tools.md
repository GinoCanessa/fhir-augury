# MCP Tools

FHIR Augury includes a
[Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that
exposes the knowledge base to LLM agents such as Claude, GitHub Copilot, and
others. The MCP server provides 20 tools across 6 categories.

## Setup

### Building the MCP Server

```bash
dotnet build src/FhirAugury.Mcp
```

### Connecting Claude Desktop

Add to your Claude Desktop configuration
(`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS,
or `%APPDATA%\Claude\claude_desktop_config.json` on Windows):

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/fhir-augury/src/FhirAugury.Mcp",
        "--", "--db", "/path/to/fhir-augury.db"
      ]
    }
  }
}
```

### Connecting via HTTP (VS Code, Copilot, etc.)

For MCP clients that support HTTP-based transport:

```json
{
  "mcpServers": {
    "fhir-augury": {
      "url": "http://localhost:5200/mcp"
    }
  }
}
```

### Database Path

The MCP server resolves the database path in this order:

1. `--db <path>` command-line argument
2. `FHIR_AUGURY_DB` environment variable
3. Default: `fhir-augury.db` in the current directory

> **Note:** The MCP server opens the database in **read-only** mode. It can run
> alongside the background service (which handles writes) thanks to SQLite's
> WAL mode supporting concurrent readers.

---

## Tool Categories

### Search Tools

Tools for full-text search across FHIR community data.

#### `Search`

Search across all FHIR community sources using full-text search.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `sources` | string | No | all | Comma-separated source filter |
| `limit` | int | No | `20` | Maximum results |

#### `SearchJira`

Search Jira issues.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `status` | string | No | | Filter by status |
| `limit` | int | No | `20` | Maximum results |

#### `SearchZulip`

Search Zulip chat messages.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `stream` | string | No | | Filter to a specific stream |
| `limit` | int | No | `20` | Maximum results |

#### `SearchConfluence`

Search Confluence wiki pages.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `space` | string | No | | Filter by space key |
| `limit` | int | No | `20` | Maximum results |

#### `SearchGithub`

Search GitHub issues and pull requests.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | | Search query |
| `repo` | string | No | | Filter by repository |
| `state` | string | No | | Filter by state |
| `limit` | int | No | `20` | Maximum results |

---

### Retrieval Tools

Tools for fetching full details of individual items.

#### `GetJiraIssue`

Get full details of a Jira issue.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `key` | string | Yes | Issue key (e.g., `FHIR-43499`) |

Returns metadata, description, resolution, URL, and custom fields.

#### `GetJiraComments`

Get comments on a Jira issue.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `key` | string | Yes | | Issue key |
| `limit` | int | No | `50` | Maximum comments |

#### `GetZulipThread`

Get a full Zulip topic thread.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `stream` | string | Yes | Stream name |
| `topic` | string | Yes | Topic name |

Returns all messages with participants and URL.

#### `GetConfluencePage`

Get a Confluence page by ID or title.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `pageId` | string | No | Confluence page ID |
| `title` | string | No | Page title (alternative lookup) |
| `space` | string | No | Space key (used with title) |

#### `GetGithubIssue`

Get a GitHub issue or pull request.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `repo` | string | Yes | Repository (e.g., `HL7/fhir`) |
| `number` | int | Yes | Issue or PR number |

Returns body, comments, labels, and PR-specific branch info.

---

### Listing Tools

Tools for browsing and discovering content.

#### `ListJiraIssues`

List Jira issues with filters and sorting.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `workGroup` | string | No | | Filter by work group |
| `status` | string | No | | Filter by status |
| `resolution` | string | No | | Filter by resolution |
| `specification` | string | No | | Filter by specification |
| `sort` | string | No | `updated` | Sort field |
| `limit` | int | No | `50` | Maximum results |
| `offset` | int | No | `0` | Pagination offset |

#### `ListZulipStreams`

List all available Zulip streams. No parameters.

#### `ListZulipTopics`

List topics in a Zulip stream.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `stream` | string | Yes | Stream name |

Returns topic names with message counts and last activity.

#### `ListConfluenceSpaces`

List indexed Confluence spaces. No parameters.

#### `ListGithubRepos`

List tracked GitHub repositories. No parameters.

---

### Relationship Tools

Tools for discovering connections between items.

#### `FindRelated`

Find items related to a given item using keyword similarity and cross-reference
boosting.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `source` | string | Yes | | Source type |
| `id` | string | Yes | | Item identifier |
| `limit` | int | No | `20` | Maximum results |

Returns related items ranked by combined BM25 similarity + cross-reference
scores.

#### `GetCrossReferences`

Get explicit cross-references (mentions and links) for an item.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `source` | string | Yes | Source type |
| `id` | string | Yes | Item identifier |

Returns inbound and outbound references with context snippets.

---

### Snapshot Tools

Tools for generating rich, detailed views of items. Designed for the
**Search → Snapshot → Explore** workflow.

#### `SnapshotJiraIssue`

Detailed snapshot of a Jira issue including metadata, description, comments,
and cross-references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `key` | string | Yes | | Issue key |
| `includeComments` | bool | No | `true` | Include issue comments |
| `includeXrefs` | bool | No | `true` | Include cross-references |

#### `SnapshotZulipThread`

Detailed snapshot of a Zulip topic thread.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `stream` | string | Yes | | Stream name |
| `topic` | string | Yes | | Topic name |
| `includeXrefs` | bool | No | `true` | Include cross-references |

#### `SnapshotConfluencePage`

Detailed snapshot of a Confluence page with comments.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pageId` | string | Yes | | Confluence page ID |
| `includeXrefs` | bool | No | `true` | Include cross-references |

---

### Admin Tools

Tools for monitoring the knowledge base.

#### `GetStats`

Get database statistics: item counts and sync times.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `source` | string | No | Filter to a specific source |

#### `GetSyncStatus`

Get sync status and schedule for all sources. No parameters.

Returns a table with source name, status, last sync time, items ingested,
schedule interval, and next run time.

---

## Recommended Workflow for LLM Agents

The tools are designed for a progressive discovery pattern:

1. **Search** — Use `Search` or source-specific search tools to find relevant
   items
2. **Snapshot** — Use snapshot tools to get detailed views of interesting items
3. **Explore** — Use `FindRelated` and `GetCrossReferences` to discover
   connected items across sources
4. **List** — Use listing tools to browse available streams, spaces, and
   repositories

This pattern lets agents efficiently navigate the FHIR community knowledge base
without needing to understand the underlying data structure.
