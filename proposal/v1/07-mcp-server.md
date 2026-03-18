# FHIR Augury — MCP Server

## Overview

The MCP server (`FhirAugury.Mcp`) exposes the FHIR Augury knowledge base to
LLM agents via the Model Context Protocol. It opens the database in read-only
mode and can run alongside the service (which holds write access).

## Hosting

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<DatabaseService>(sp =>
    new DatabaseService(config.DbPath, readOnly: true));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

The MCP server supports both **stdio** transport (for local LLM tools like
Copilot, Claude Desktop, etc.) and **HTTP** transport (via `WithHttpTransport()`
mapped at `/mcp`) for remote access.

## Tools

### Search Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `search` | Unified full-text search across all sources | `query`, `sources` (optional filter), `limit` |
| `search_zulip` | Search Zulip messages | `query`, `stream` (optional), `limit` |
| `search_jira` | Search Jira issues | `query`, `work_group`, `status`, `specification`, `limit` |
| `search_confluence` | Search Confluence pages | `query`, `space` (optional), `limit` |
| `search_github` | Search GitHub issues/PRs | `query`, `repo` (optional), `state`, `limit` |

### Retrieval Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `get_jira_issue` | Full details of a Jira issue | `key` (e.g., "FHIR-43499") |
| `get_jira_comments` | Comments on a Jira issue | `key`, `limit` |
| `get_zulip_thread` | Full thread from Zulip | `stream`, `topic` |
| `get_confluence_page` | Full Confluence page content | `page_id` or `title` + `space` |
| `get_github_issue` | Full GitHub issue/PR details | `repo`, `number` |

### Relationship Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `find_related` | Cross-source related items via BM25 + xrefs | `source`, `id`, `limit` |
| `get_cross_references` | Explicit cross-references for an item | `source`, `id` |

### Listing Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `list_jira_issues` | Filter/paginate Jira issues | `work_group`, `status`, `resolution`, `specification`, `sort`, `limit`, `offset` |
| `list_zulip_streams` | List available Zulip streams | (none) |
| `list_zulip_topics` | List topics in a Zulip stream | `stream` |
| `list_confluence_spaces` | List Confluence spaces | (none) |
| `list_github_repos` | List tracked GitHub repos | (none) |

### Snapshot Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `snapshot_jira_issue` | Rich markdown snapshot of a Jira issue | `key`, `include_comments`, `include_xrefs` |
| `snapshot_zulip_thread` | Rich markdown snapshot of a Zulip thread | `stream`, `topic`, `include_xrefs` |
| `snapshot_confluence_page` | Rich markdown snapshot of a page | `page_id`, `include_xrefs` |

### Admin Tools

| Tool | Description | Key Arguments |
|------|-------------|---------------|
| `get_stats` | Database statistics overview | `source` (optional) |
| `get_sync_status` | Last sync times and schedule per source | (none) |
| `trigger_sync` | Trigger incremental sync for a source (or all) | `source` (optional, defaults to all) |

## Tool Implementation Pattern

Each tool is implemented as a static method decorated with `[McpServerTool]`:

```csharp
[McpServerToolType]
public static class JiraTools
{
    [McpServerTool, Description("Search Jira issues using full-text search.")]
    public static async Task<string> SearchJira(
        DatabaseService db,
        [Description("Search query text")] string query,
        [Description("Filter by work group")] string? workGroup = null,
        [Description("Filter by status")] string? status = null,
        [Description("Filter by specification")] string? specification = null,
        [Description("Max results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = JiraIssueRecord.SearchFts(conn, query, workGroup, status, specification, limit);
        return FormatResults(results);
    }

    [McpServerTool, Description("Get full details of a specific Jira issue.")]
    public static async Task<string> GetJiraIssue(
        DatabaseService db,
        [Description("Issue key, e.g. FHIR-43499")] string key)
    {
        using var conn = db.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(conn, Key: key);
        return issue is null
            ? $"Issue {key} not found."
            : RenderSnapshot(issue);
    }
}
```

## MCP Configuration (for clients)

### Claude Desktop / Copilot (stdio)

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": ["run", "--project", "src/FhirAugury.Mcp"],
      "env": {
        "FHIR_AUGURY_DB": "/path/to/fhir-augury.db"
      }
    }
  }
}
```

### HTTP Transport

```json
{
  "mcpServers": {
    "fhir-augury": {
      "url": "http://localhost:5150/mcp"
    }
  }
}
```

## LLM Agent Workflow

The tools are designed to support a **Search → Snapshot → Explore** methodology
(proven in the josh-fhir-community-search reference):

1. **Search** — Use `search` or `search_jira` to find relevant items
2. **Snapshot** — Use `snapshot_jira_issue` or `snapshot_zulip_thread` for deep
   detail on promising results
3. **Explore** — Use `find_related` and `get_cross_references` to follow
   connections across sources
4. **Iterate** — Refine searches based on what was found

This maps well to how LLM agents naturally explore information.
