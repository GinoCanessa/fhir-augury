# FHIR Augury v2 — MCP & CLI

## Design Principle: Triple Interface

Per the *Triple Interface* principle (see [01-overview](01-overview.md)),
every external-facing capability is available through all three consumer
interfaces — HTTP API, MCP, and CLI. No capability is exclusive to a single
interface. The three interfaces are thin layers over the same underlying
gRPC services:

```
                     ┌─────────────────────────────┐
                     │    Orchestrator (gRPC)       │
                     │    Source Services (gRPC)     │
                     └──────────┬──────────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
     ┌────────────────┐ ┌─────────────┐ ┌────────────────┐
     │   HTTP/JSON    │ │  MCP Server │ │      CLI       │
     │   REST API     │ │   (stdio)   │ │  (terminal)    │
     │                │ │             │ │                │
     │ Scripts,       │ │ LLM agents  │ │ Human          │
     │ integrations,  │ │ (Copilot,   │ │ operators,     │
     │ browser        │ │  Claude)    │ │ shell scripts  │
     └────────────────┘ └─────────────┘ └────────────────┘
```

When a new capability is added to the orchestrator or a source service, it
must be exposed through all three interfaces before it is considered complete.

## Architecture Impact

In v1, the MCP server and CLI accessed the SQLite database directly. In v2,
they communicate with the orchestrator and/or source services via gRPC/HTTP.
This decouples the consumer interfaces from the data storage layer.

```
v1:  CLI/MCP ──► SQLite (direct file access)
v2:  CLI/MCP ──► Orchestrator ──gRPC──► Source Services
                     │
                     └──► Orchestrator's own DB (xrefs)
```

---

## MCP Server

### Hosting

The MCP server (`FhirAugury.Mcp`) is a standalone process that connects to
the orchestrator via gRPC. It exposes tools to LLM agents via the Model
Context Protocol.

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

// gRPC clients for orchestrator (and optionally direct source services)
builder.Services.AddGrpcClient<OrchestratorService.OrchestratorServiceClient>(opts =>
    opts.Address = new Uri(config.OrchestratorGrpcAddress));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

### Tools

The MCP tools are organized into categories that map to the orchestrator's
and source services' capabilities:

#### Unified Tools (via Orchestrator)

| Tool | Description | Orchestrator RPC |
|------|-------------|-----------------|
| `search` | Search across all sources | `UnifiedSearch` |
| `find_related` | Find items related to a given item across all sources | `FindRelated` |
| `get_cross_references` | Get cross-references for an item | `GetCrossReferences` |
| `get_stats` | Aggregate statistics | `GetServicesStatus` |
| `trigger_sync` | Trigger ingestion across sources | `TriggerSync` |

#### Source-Specific Tools (via Orchestrator → Source Services)

| Tool | Description | Source Service |
|------|-------------|---------------|
| `search_jira` | Search Jira issues | Jira `Search` |
| `get_jira_issue` | Get full Jira issue details | Jira `GetItem` |
| `get_jira_comments` | Get comments on a Jira issue | Jira `GetIssueComments` |
| `query_jira_issues` | Structured query for building work-lists (filter by status, workgroup, project, etc.) | Jira `QueryIssues` |
| `snapshot_jira_issue` | Rich markdown snapshot of a Jira issue | Jira `GetSnapshot` |
| `list_jira_issues` | List/filter Jira issues | Jira `ListItems` |
| `search_zulip` | Search Zulip messages | Zulip `Search` |
| `get_zulip_thread` | Get a complete Zulip thread | Zulip `GetThread` |
| `query_zulip_messages` | Structured query for filtering messages (by stream, topic, sender, etc.) | Zulip `QueryMessages` |
| `list_zulip_streams` | List Zulip streams | Zulip `ListStreams` |
| `list_zulip_topics` | List topics in a stream | Zulip `ListTopics` |
| `snapshot_zulip_thread` | Rich markdown snapshot of a Zulip thread | Zulip `GetSnapshot` |
| `search_confluence` | Search Confluence pages | Confluence `Search` |
| `get_confluence_page` | Get full Confluence page content | Confluence `GetItem` |
| `list_confluence_spaces` | List Confluence spaces | Confluence `ListSpaces` |
| `snapshot_confluence_page` | Rich markdown snapshot of a Confluence page | Confluence `GetSnapshot` |
| `search_github` | Search GitHub issues/PRs | GitHub `Search` |
| `get_github_issue` | Get full GitHub issue/PR details | GitHub `GetItem` |
| `query_github_artifact` | Find commits/PRs affecting a FHIR artifact, page, or element | GitHub `QueryByArtifact` |
| `list_github_repos` | List tracked GitHub repos | GitHub `ListRepositories` |
| `snapshot_github_issue` | Rich markdown snapshot of a GitHub issue | GitHub `GetSnapshot` |

#### Tool Implementation Pattern

```csharp
[McpServerToolType]
public static class SearchTools
{
    [McpServerTool, Description("Search across all FHIR community sources.")]
    public static async Task<string> Search(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Search query text")] string query,
        [Description("Comma-separated sources to search (jira,zulip,confluence,github). Omit for all.")] string? sources = null,
        [Description("Max results (default 20)")] int limit = 20)
    {
        var request = new UnifiedSearchRequest
        {
            Query = query,
            Limit = limit,
        };

        if (!string.IsNullOrEmpty(sources))
            request.Sources.AddRange(sources.Split(','));

        var response = await orchestrator.UnifiedSearchAsync(request);
        return FormatSearchResults(response);
    }
}

[McpServerToolType]
public static class JiraTools
{
    [McpServerTool, Description("Get full details of a specific Jira issue.")]
    public static async Task<string> GetJiraIssue(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Issue key, e.g. FHIR-43499")] string key,
        [Description("Include comments")] bool includeComments = true)
    {
        // The orchestrator proxies this to the Jira source service
        var response = await orchestrator.GetItemAsync(new GetItemRequest
        {
            Source = "jira",
            Id = key,
            IncludeContent = true,
            IncludeComments = includeComments
        });

        return FormatJiraIssue(response);
    }
}
```

### MCP Configuration

```json
{
  "mcpServers": {
    "fhir-augury": {
      "command": "dotnet",
      "args": ["run", "--project", "src/FhirAugury.Mcp"],
      "env": {
        "FHIR_AUGURY_ORCHESTRATOR": "http://localhost:5151"
      }
    }
  }
}
```

### Direct Source Access Mode

For standalone deployments (single source service, no orchestrator), the MCP
server can connect directly to a source service:

```json
{
  "mcpServers": {
    "fhir-augury-jira": {
      "command": "dotnet",
      "args": ["run", "--project", "src/FhirAugury.Mcp", "--", "--mode", "direct", "--source", "jira"],
      "env": {
        "FHIR_AUGURY_JIRA_GRPC": "http://localhost:5161"
      }
    }
  }
}
```

In direct mode, only the tools for the connected source are available, plus
single-source search and retrieval. Cross-source tools are disabled.

---

## CLI

### Architecture

The CLI (`FhirAugury.Cli`) communicates with services via gRPC or HTTP. It
no longer accesses the database directly (except for a legacy `--direct` mode
for backward compatibility during migration).

### Command Structure

```
fhir-augury
├── search                    # Unified search (via orchestrator)
│   ├── -q, --query           # Search query text
│   ├── -s, --sources         # Filter sources (csv)
│   ├── -n, --limit           # Max results
│   └── -f, --format          # Output: table | json | markdown
│
├── related                   # Find related items (via orchestrator)
│   ├── --source              # Source type of seed item
│   ├── --id                  # Seed item identifier
│   └── -n, --limit           # Max results
│
├── get                       # Get full item details
│   ├── --source              # Source type
│   ├── --id                  # Item identifier
│   └── -f, --format          # Output format
│
├── snapshot                  # Rich markdown snapshot
│   ├── --source              # Source type
│   ├── --id                  # Item identifier
│   └── --include-xref        # Include cross-source references
│
├── xref                      # Cross-reference queries
│   ├── --source              # Source type
│   ├── --id                  # Item identifier
│   └── --direction           # outgoing | incoming | both
│
├── ingest                    # Ingestion control
│   ├── trigger               # Trigger sync
│   │   ├── --source          # Source to sync (or "all")
│   │   └── --type            # full | incremental
│   ├── status                # View ingestion status
│   └── rebuild               # Rebuild database from cache
│       └── --source          # Source to rebuild
│
├── list                      # List items
│   ├── --source              # Source type
│   ├── --filter              # Source-specific filters (key=value pairs)
│   ├── -n, --limit           # Max results
│   └── --sort                # Sort field
│
├── services                  # Service management
│   ├── status                # Health of all services
│   ├── stats                 # Aggregate statistics
│   └── xref-scan             # Trigger cross-reference scan
│
└── (global options)
    ├── --orchestrator         # Orchestrator URL (default: http://localhost:5150)
    ├── --format               # Default output format
    └── --verbose              # Verbose output
```

### Usage Examples

```bash
# Unified search across all sources
fhir-augury search -q "FHIRPath normative ballot" -n 10

# Search only Jira and Zulip
fhir-augury search -q "Bundle signature" -s jira,zulip

# Find items related to a Jira issue
fhir-augury related --source jira --id FHIR-43499 --limit 20

# Get full details of a Jira issue
fhir-augury get --source jira --id FHIR-43499

# Rich snapshot with cross-references
fhir-augury snapshot --source jira --id FHIR-43499 --include-xref

# Get cross-references for an item
fhir-augury xref --source jira --id FHIR-43499 --direction both

# Trigger incremental sync for all sources
fhir-augury ingest trigger --source all

# Rebuild Jira database from cache
fhir-augury ingest rebuild --source jira

# Check service health
fhir-augury services status

# View aggregate statistics
fhir-augury services stats
```

### Output Formats

Same as v1 — table (default), JSON, and markdown formats are supported for
all commands that return data.

---

## Interface Parity Matrix

The following table confirms that every external capability is available
through all three interfaces. When adding a new capability, add a row here
and ensure all three columns are filled.

| Capability | HTTP API | MCP Tool | CLI Command |
|------------|----------|----------|-------------|
| Unified search | `GET /api/v1/search` | `search` | `fhir-augury search` |
| Find related items | `GET /api/v1/related/{source}/{id}` | `find_related` | `fhir-augury related` |
| Get item details | `GET /api/v1/items/{id}` (source) | `get_jira_issue`, `get_github_issue`, … | `fhir-augury get` |
| Rich snapshot | `GET /api/v1/items/{id}/snapshot` (source) | `snapshot_jira_issue`, `snapshot_github_issue`, … | `fhir-augury snapshot` |
| Cross-references | `GET /api/v1/xref/{source}/{id}` | `get_cross_references` | `fhir-augury xref` |
| List items | `GET /api/v1/items` (source) | `list_jira_issues`, `list_github_repos`, … | `fhir-augury list` |
| Trigger ingestion | `POST /api/v1/ingest/trigger` | `trigger_sync` | `fhir-augury ingest trigger` |
| Ingestion status | `GET /api/v1/status` (source) | `get_stats` | `fhir-augury ingest status` |
| Rebuild from cache | `POST /api/v1/rebuild` (source) | `trigger_sync` (type=rebuild) | `fhir-augury ingest rebuild` |
| Service health | `GET /api/v1/services` | `get_stats` | `fhir-augury services status` |
| Aggregate stats | `GET /api/v1/stats` | `get_stats` | `fhir-augury services stats` |
| Cross-ref scan | `POST /api/v1/xref/scan` | `trigger_sync` (type=xref-scan) | `fhir-augury services xref-scan` |
| Source-specific search | `GET /api/v1/search` (source) | `search_jira`, `search_zulip`, … | `fhir-augury search -s {source}` |
| Jira structured query | `POST /api/v1/jira/query` | `query_jira_issues` | `fhir-augury query-jira` |
| Zulip structured query | `POST /api/v1/zulip/query` | `query_zulip_messages` | `fhir-augury query-zulip` |
| GitHub artifact query | `POST /api/v1/github/artifact-query` | `query_github_artifact` | `fhir-augury query-github-artifact` |
