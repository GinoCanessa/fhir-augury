# MCP Tools

FHIR Augury provides a Model Context Protocol (MCP) server that exposes the
knowledge base to LLM agents. The server operates in **read-only** mode against
the SQLite database.

## Configuration

### Database Path

The MCP server resolves the database path in this order:

1. `--db <path>` CLI argument
2. `FHIR_AUGURY_DB` environment variable
3. `fhir-augury.db` (default, in working directory)

### Running the MCP Server

```bash
dotnet run --project src/FhirAugury.Mcp -- --db /path/to/fhir-augury.db
```

The server uses **stdio** transport by default.

### Client Configuration

#### Claude Desktop

Add to your Claude Desktop config (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/fhir-augury/src/FhirAugury.Mcp",
        "--",
        "--db",
        "/path/to/fhir-augury.db"
      ]
    }
  }
}
```

#### VS Code (Copilot / Continue)

Add to `.vscode/mcp.json` or your user MCP config:

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/fhir-augury/src/FhirAugury.Mcp",
        "--",
        "--db",
        "/path/to/fhir-augury.db"
      ]
    }
  }
}
```

#### HTTP Transport

If the FHIR Augury service is running, clients that support HTTP-based MCP can
connect directly:

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

## Tools Reference

### Search Tools

#### `Search`

Search across all FHIR community sources using full-text search.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | `string` | Yes | — | Search query text |
| `sources` | `string` | No | all | Comma-separated source filter |
| `limit` | `int` | No | `20` | Maximum results |

**Example prompt:** *"Search for discussions about patient matching algorithms"*

#### `SearchZulip`

Search Zulip chat messages.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | `string` | Yes | — | Search query |
| `stream` | `string` | No | all | Filter to a specific stream |
| `limit` | `int` | No | `20` | Maximum results |

#### `SearchJira`

Search Jira issues.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | `string` | Yes | — | Search query |
| `status` | `string` | No | all | Filter by issue status |
| `limit` | `int` | No | `20` | Maximum results |

#### `SearchConfluence`

Search Confluence wiki pages.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | `string` | Yes | — | Search query |
| `space` | `string` | No | all | Filter by space key |
| `limit` | `int` | No | `20` | Maximum results |

#### `SearchGithub`

Search GitHub issues and pull requests.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `query` | `string` | Yes | — | Search query |
| `repo` | `string` | No | all | Filter by repository |
| `state` | `string` | No | all | Filter by state (`open`, `closed`) |
| `limit` | `int` | No | `20` | Maximum results |

---

### Retrieval Tools

#### `GetJiraIssue`

Get full details of a Jira issue by key.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `key` | `string` | Yes | Jira issue key (e.g., `FHIR-43499`) |

#### `GetJiraComments`

Get comments on a Jira issue.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `key` | `string` | Yes | — | Jira issue key |
| `limit` | `int` | No | `50` | Maximum comments |

#### `GetZulipThread`

Get a full Zulip topic thread with all messages.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `stream` | `string` | Yes | Stream name |
| `topic` | `string` | Yes | Topic name |

#### `GetConfluencePage`

Get full content of a Confluence page.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `pageId` | `string` | No | Page ID (use one of `pageId`, `title`, or `space`) |
| `title` | `string` | No | Page title to search for |
| `space` | `string` | No | Space key to narrow title search |

#### `GetGithubIssue`

Get full details of a GitHub issue or pull request.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `repo` | `string` | Yes | Repository (`owner/repo`, e.g., `HL7/fhir`) |
| `number` | `int` | Yes | Issue or PR number |

---

### Snapshot Tools

Snapshot tools render comprehensive markdown views of items, ideal for giving
LLMs rich context about a specific item.

#### `SnapshotJiraIssue`

Detailed markdown snapshot of a Jira issue including metadata, description,
resolution, and optionally comments and cross-references.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `key` | `string` | Yes | — | Jira issue key |
| `includeComments` | `bool` | No | `true` | Include issue comments |
| `includeXrefs` | `bool` | No | `true` | Include cross-references |

#### `SnapshotZulipThread`

Detailed markdown snapshot of a Zulip topic with all messages.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `stream` | `string` | Yes | — | Stream name |
| `topic` | `string` | Yes | — | Topic name |
| `includeXrefs` | `bool` | No | `true` | Include cross-references |

#### `SnapshotConfluencePage`

Detailed markdown snapshot of a Confluence page.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `pageId` | `string` | Yes | — | Confluence page ID |
| `includeXrefs` | `bool` | No | `true` | Include cross-references |

---

### Listing Tools

#### `ListJiraIssues`

List Jira issues with filters and pagination.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `workGroup` | `string` | No | all | Filter by work group |
| `status` | `string` | No | all | Filter by status |
| `resolution` | `string` | No | all | Filter by resolution |
| `specification` | `string` | No | all | Filter by specification |
| `sort` | `string` | No | `updated` | Sort by: `updated`, `created`, `key` |
| `limit` | `int` | No | `50` | Maximum results |
| `offset` | `int` | No | `0` | Pagination offset |

#### `ListZulipStreams`

List all available Zulip streams. No parameters.

#### `ListZulipTopics`

List topics in a Zulip stream.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `stream` | `string` | Yes | Stream name |

#### `ListConfluenceSpaces`

List all indexed Confluence spaces. No parameters.

#### `ListGithubRepos`

List all tracked GitHub repositories. No parameters.

---

### Relationship Tools

#### `FindRelated`

Find items related to a given item across all sources using BM25 keyword
similarity and cross-reference links.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `source` | `string` | Yes | — | Source type of the item |
| `id` | `string` | Yes | — | Item identifier |
| `limit` | `int` | No | `20` | Maximum results |

#### `GetCrossReferences`

Get explicit cross-references (mentions, links, URLs) for an item.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | `string` | Yes | Source type |
| `id` | `string` | Yes | Item identifier |

---

### Admin Tools

#### `GetStats`

Get database statistics including item counts, sync times, and database size.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `source` | `string` | No | Filter to a specific source |

#### `GetSyncStatus`

Get sync status and schedule information for all sources. No parameters.

---

## Recommended Workflow

When using the MCP tools in an LLM agent, follow this pattern for best results:

### 1. Search

Start with a broad search to find relevant items:

```
→ Search("patient matching")
```

### 2. Snapshot

Get rich context for the most relevant results:

```
→ SnapshotJiraIssue("FHIR-43499")
→ SnapshotZulipThread("implementers", "Patient matching")
```

### 3. Explore Related

Follow cross-references to discover connected discussions:

```
→ FindRelated("jira", "FHIR-43499")
→ GetCrossReferences("jira", "FHIR-43499")
```

### 4. Deep Dive

Retrieve specific details as needed:

```
→ GetJiraComments("FHIR-43499")
→ GetConfluencePage(pageId: "12345678")
```

This workflow lets the agent efficiently move from discovery to deep
understanding of any FHIR community topic.
