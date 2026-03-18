# Phase 6: MCP Server

**Goal:** Full MCP server for LLM agent integration with read-only database
access, supporting search, retrieval, relationship, and listing tools.

**Depends on:** Phase 5 (Confluence & GitHub Sources)

---

## 6.1 — MCP Project Setup

### Objective

Create the `FhirAugury.Mcp` project with MCP SDK hosting.

### Tasks

#### 6.1.1 Create project

Create `src/FhirAugury.Mcp/` as a console app.

NuGet references:
```xml
<PackageReference Include="ModelContextProtocol" Version="1.0.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`,
`FhirAugury.Indexing`

#### 6.1.2 `Program.cs`

MCP host setup:
- Build `Host` with `AddMcpServer()`, `WithStdioServerTransport()`,
  `WithToolsFromAssembly()`
- Register `DatabaseService` as singleton with `readOnly: true`
- Configure logging to stderr (required for stdio transport)
- Support `--db` argument for database path
- Support `FHIR_AUGURY_DB` environment variable

#### 6.1.3 Update solution file

Add to `fhir-augury.slnx`.

### Acceptance Criteria

- [ ] MCP server starts and responds to tool discovery via stdio
- [ ] Database opens in read-only mode
- [ ] Logging goes to stderr, not stdout (which is the MCP transport)

---

## 6.2 — Search Tools

### Files to Create in `FhirAugury.Mcp/`

#### 6.2.1 `Tools/SearchTools.cs`

```csharp
[McpServerToolType]
public static class SearchTools
{
    [McpServerTool, Description("Search across all FHIR community sources (Zulip, Jira, Confluence, GitHub) using full-text search.")]
    public static string Search(
        DatabaseService db,
        [Description("Search query text")] string query,
        [Description("Comma-separated source filter: zulip,jira,confluence,github")] string? sources = null,
        [Description("Maximum results to return (default 20)")] int limit = 20)
    { ... }

    [McpServerTool, Description("Search Zulip chat messages.")]
    public static string SearchZulip(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific stream name")] string? stream = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    { ... }

    [McpServerTool, Description("Search Jira issues.")]
    public static string SearchJira(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter by HL7 work group")] string? workGroup = null,
        [Description("Filter by status (e.g., Open, Closed)")] string? status = null,
        [Description("Filter by specification")] string? specification = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    { ... }

    [McpServerTool, Description("Search Confluence wiki pages.")]
    public static string SearchConfluence(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific Confluence space key")] string? space = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    { ... }

    [McpServerTool, Description("Search GitHub issues and pull requests.")]
    public static string SearchGithub(
        DatabaseService db,
        [Description("Search query")] string query,
        [Description("Filter to specific repository (e.g., HL7/fhir)")] string? repo = null,
        [Description("Filter by state: open, closed")] string? state = null,
        [Description("Maximum results (default 20)")] int limit = 20)
    { ... }
}
```

### Acceptance Criteria

- [ ] `search` returns results from all sources, ranked by relevance
- [ ] Source-specific tools apply correct filters
- [ ] Results include source, ID, title, snippet, score, URL, date

---

## 6.3 — Retrieval Tools

### Files to Create

#### 6.3.1 `Tools/RetrievalTools.cs`

```csharp
[McpServerToolType]
public static class RetrievalTools
{
    [McpServerTool, Description("Get full details of a Jira issue by its key.")]
    public static string GetJiraIssue(DatabaseService db,
        [Description("Issue key, e.g. FHIR-43499")] string key)
    { ... }

    [McpServerTool, Description("Get comments on a Jira issue.")]
    public static string GetJiraComments(DatabaseService db,
        [Description("Issue key")] string key,
        [Description("Maximum comments (default 50)")] int limit = 50)
    { ... }

    [McpServerTool, Description("Get a full Zulip topic thread with all messages.")]
    public static string GetZulipThread(DatabaseService db,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic)
    { ... }

    [McpServerTool, Description("Get full content of a Confluence page.")]
    public static string GetConfluencePage(DatabaseService db,
        [Description("Page ID")] int? pageId = null,
        [Description("Page title (use with space)")] string? title = null,
        [Description("Space key (use with title)")] string? space = null)
    { ... }

    [McpServerTool, Description("Get full details of a GitHub issue or pull request.")]
    public static string GetGithubIssue(DatabaseService db,
        [Description("Repository full name (e.g., HL7/fhir)")] string repo,
        [Description("Issue or PR number")] int number)
    { ... }
}
```

### Acceptance Criteria

- [ ] Each retrieval tool returns formatted item details
- [ ] Missing items return helpful "not found" messages
- [ ] Confluence page can be found by ID or by title+space

---

## 6.4 — Snapshot Tools

### Files to Create

#### 6.4.1 `Tools/SnapshotTools.cs`

Rich markdown renderings of items with full context:

```csharp
[McpServerToolType]
public static class SnapshotTools
{
    [McpServerTool, Description("Get a detailed markdown snapshot of a Jira issue including metadata, description, comments, and cross-references.")]
    public static string SnapshotJiraIssue(DatabaseService db,
        [Description("Issue key")] string key,
        [Description("Include comments (default true)")] bool includeComments = true,
        [Description("Include cross-references from other sources")] bool includeXrefs = true)
    { ... }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Zulip topic thread.")]
    public static string SnapshotZulipThread(DatabaseService db,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        [Description("Include cross-references")] bool includeXrefs = true)
    { ... }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Confluence page.")]
    public static string SnapshotConfluencePage(DatabaseService db,
        [Description("Page ID")] int pageId,
        [Description("Include cross-references")] bool includeXrefs = true)
    { ... }
}
```

Snapshot format includes:
- Title and metadata header
- Full content body
- Comments (if applicable)
- Cross-references section (if includeXrefs)
- Links to original source

### Acceptance Criteria

- [ ] Snapshots are well-formatted markdown
- [ ] Cross-reference section shows related items from other sources
- [ ] Content is readable by LLM agents (no excessive HTML/formatting)

---

## 6.5 — Relationship Tools

### Files to Create

#### 6.5.1 `Tools/RelationshipTools.cs`

```csharp
[McpServerToolType]
public static class RelationshipTools
{
    [McpServerTool, Description("Find items related to a given item across all sources, using keyword similarity and cross-references.")]
    public static string FindRelated(DatabaseService db,
        [Description("Source type: zulip, jira, confluence, github")] string source,
        [Description("Item identifier")] string id,
        [Description("Maximum results (default 20)")] int limit = 20)
    { ... }

    [McpServerTool, Description("Get explicit cross-references for an item (mentions, links from/to other sources).")]
    public static string GetCrossReferences(DatabaseService db,
        [Description("Source type")] string source,
        [Description("Item identifier")] string id)
    { ... }
}
```

### Acceptance Criteria

- [ ] `FindRelated` combines BM25 similarity + cross-references
- [ ] `GetCrossReferences` shows both inbound and outbound references
- [ ] Results span all four sources

---

## 6.6 — Listing Tools

### Files to Create

#### 6.6.1 `Tools/ListingTools.cs`

```csharp
[McpServerToolType]
public static class ListingTools
{
    [McpServerTool, Description("List Jira issues with filters and pagination.")]
    public static string ListJiraIssues(DatabaseService db,
        [Description("Filter by work group")] string? workGroup = null,
        [Description("Filter by status")] string? status = null,
        [Description("Filter by resolution")] string? resolution = null,
        [Description("Filter by specification")] string? specification = null,
        [Description("Sort by: updated, created, key")] string sort = "updated",
        [Description("Maximum results")] int limit = 50,
        [Description("Offset for pagination")] int offset = 0)
    { ... }

    [McpServerTool, Description("List available Zulip streams.")]
    public static string ListZulipStreams(DatabaseService db)
    { ... }

    [McpServerTool, Description("List topics in a Zulip stream.")]
    public static string ListZulipTopics(DatabaseService db,
        [Description("Stream name")] string stream)
    { ... }

    [McpServerTool, Description("List indexed Confluence spaces.")]
    public static string ListConfluenceSpaces(DatabaseService db)
    { ... }

    [McpServerTool, Description("List tracked GitHub repositories.")]
    public static string ListGithubRepos(DatabaseService db)
    { ... }
}
```

### Acceptance Criteria

- [ ] Listing tools return paginated, filterable results
- [ ] Empty results return helpful messages
- [ ] Sort and filter parameters work correctly

---

## 6.7 — Admin Tools

### Files to Create

#### 6.7.1 `Tools/AdminTools.cs`

```csharp
[McpServerToolType]
public static class AdminTools
{
    [McpServerTool, Description("Get database statistics: item counts, last sync times, database size.")]
    public static string GetStats(DatabaseService db,
        [Description("Optional: filter to specific source")] string? source = null)
    { ... }

    [McpServerTool, Description("Get sync status and schedule for all data sources.")]
    public static string GetSyncStatus(DatabaseService db)
    { ... }

    [McpServerTool, Description("Trigger an incremental sync for a data source (requires running service).")]
    public static string TriggerSync(DatabaseService db,
        [Description("Source to sync (omit for all)")] string? source = null)
    { ... }
}
```

Note: `TriggerSync` needs a way to communicate with the service. Options:
1. HTTP call to service API (if service URL is configured)
2. Return instructions for the user to trigger manually

### Acceptance Criteria

- [ ] Stats tool returns accurate counts and sizes
- [ ] Sync status shows per-source last sync and schedule
- [ ] Trigger sync communicates with service or provides clear instructions

---

## 6.8 — HTTP Transport (Optional)

### Files to Update

#### 6.8.1 Update `Program.cs`

Add optional HTTP transport alongside stdio:
- When `--http` flag or `FHIR_AUGURY_MCP_HTTP=true`, use `WithHttpTransport()`
- Map MCP endpoint at `/mcp`
- Can run as part of the service process or standalone

### Acceptance Criteria

- [ ] MCP server can run via stdio (default) or HTTP transport
- [ ] HTTP transport accessible at configured URL

---

## 6.9 — MCP Client Configuration

### Files to Create

#### 6.9.1 `mcp-config-examples/claude-desktop.json`

Example configuration for Claude Desktop / Copilot (stdio).

#### 6.9.2 `mcp-config-examples/http-client.json`

Example configuration for HTTP transport clients.

### Acceptance Criteria

- [ ] Example configs are valid and include all required fields
- [ ] README references the example configs

---

## 6.10 — Tests

### New Test Files

- `tests/FhirAugury.Mcp.Tests/` — new project
  - `SearchToolsTests.cs` — tool invocation with test database
  - `RetrievalToolsTests.cs` — get/snapshot tools
  - `RelationshipToolsTests.cs` — find related, cross-references
  - `ListingToolsTests.cs` — listing tools with filters

### Test Approach

Each test:
1. Creates in-memory SQLite database
2. Populates with known test data
3. Invokes the tool method directly (no MCP transport needed)
4. Asserts output contains expected content

### Acceptance Criteria

- [ ] All MCP tool tests pass
- [ ] Tools handle missing data gracefully (no crashes)
- [ ] Output format is suitable for LLM consumption
