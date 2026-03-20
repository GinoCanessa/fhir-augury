# FHIR Augury v2 — Source Services

## Common Design

All four source services (Jira, Zulip, Confluence, GitHub) share the same
architectural pattern. Each is a self-contained ASP.NET service that owns its
data end-to-end: ingestion, caching, storage, indexing, and serving.

### Internal Architecture

```
┌──────────────────────────────────────────────────────────┐
│                     Source Service                        │
│                                                          │
│  ┌──────────────────┐    ┌─────────────────────────┐     │
│  │   gRPC / HTTP    │    │   Background Workers    │     │
│  │    Endpoints     │    │                         │     │
│  │                  │    │  • Scheduled Ingestion   │     │
│  │  • Search        │    │  • Cache-to-DB Loader    │     │
│  │  • GetItem       │    │  • Index Rebuilder       │     │
│  │  • ListItems     │    │                         │     │
│  │  • GetRelated    │    └────────────┬────────────┘     │
│  │  • TriggerIngest │                 │                  │
│  │  • GetStatus     │                 │                  │
│  └────────┬─────────┘                 │                  │
│           │                           │                  │
│           ▼                           ▼                  │
│  ┌────────────────────────────────────────────┐          │
│  │              Core Logic                     │          │
│  │                                             │          │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────┐  │          │
│  │  │ Ingester │  │ Indexer  │  │ Querier  │  │          │
│  │  │          │  │          │  │          │  │          │
│  │  │ Download │  │ FTS5     │  │ Search   │  │          │
│  │  │ Parse    │  │ BM25     │  │ Retrieve │  │          │
│  │  │ Normalize│  │ Internal │  │ Navigate │  │          │
│  │  │ Store    │  │  x-refs  │  │ Filter   │  │          │
│  │  └────┬─────┘  └────┬─────┘  └────┬─────┘  │          │
│  │       │             │             │        │          │
│  └───────┼─────────────┼─────────────┼────────┘          │
│          ▼             ▼             ▼                   │
│  ┌────────────────┐  ┌───────────────────┐               │
│  │  Cache Layer   │  │  SQLite Database  │               │
│  │  (filesystem)  │  │  (source-specific │               │
│  │                │  │   schema + FTS5)  │               │
│  └────────────────┘  └───────────────────┘               │
└──────────────────────────────────────────────────────────┘
```

### Service Host Pattern

Each source service uses the same hosting pattern:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Source-specific configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddEnvironmentVariables("FHIR_AUGURY_JIRA_");

// Core services
builder.Services.AddSingleton<SourceDatabase>();       // SQLite connection management
builder.Services.AddSingleton<ResponseCache>();        // File-system cache
builder.Services.AddSingleton<IngestionPipeline>();    // Download → cache → normalize → store
builder.Services.AddSingleton<InternalIndexer>();      // FTS5 + BM25 + internal refs

// Background workers
builder.Services.AddHostedService<ScheduledIngestionWorker>();

// gRPC + HTTP
builder.Services.AddGrpc();

var app = builder.Build();

// gRPC endpoints
app.MapGrpcService<JiraSourceGrpcService>();

// HTTP endpoints (for standalone use / debugging)
app.MapJiraHttpApi();

app.Run();
```

### Capabilities Every Source Service Must Provide

Each source service implements a common contract (defined in `source_service.proto`)
plus source-specific extensions:

| Capability | Description |
|-----------|-------------|
| **Search** | Full-text search within this source's data. Returns ranked results with snippets. |
| **GetItem** | Retrieve complete details of a single item by its identifier. |
| **ListItems** | Paginated listing with filters (status, date range, labels, etc.). |
| **GetRelated** | Find items within this source that are related to a given item (internal cross-refs, BM25 similarity). |
| **GetContent** | Retrieve the full content/body of an item (for rendering or LLM consumption). |
| **TriggerIngestion** | Start a full or incremental ingestion run. |
| **GetIngestionStatus** | Report current ingestion state (running, last sync time, items count). |
| **RebuildFromCache** | Rebuild the database entirely from cached API responses. |
| **GetStats** | Report statistics (total items, index size, cache size, etc.). |

---

## Source: Jira Service

### Data Model

The Jira service stores issues, comments, and issue links in its own SQLite
database (`jira.db`).

**Tables:**
- `jira_issues` — Full issue records with all standard and custom fields
- `jira_comments` — Comments on issues
- `jira_issue_links` — Internal links between issues (duplicates, blocks, relates to)
- `jira_issues_fts` — FTS5 virtual table for full-text search
- `jira_comments_fts` — FTS5 virtual table for comment search
- `index_keywords` — BM25 keyword scores for similarity queries
- `sync_state` — Ingestion state tracking

### Internal Indexing

The Jira service maintains internal cross-references:
- **Issue links:** `duplicates`, `blocks`, `is blocked by`, `relates to` — parsed
  from Jira's link data and from custom fields (`relatedIssues`, `duplicateOf`,
  `consideredRelatedIssues`)
- **Specification grouping:** Issues that share the same `specification` or
  `workGroup` custom field values
- **BM25 similarity:** Pre-computed keyword scores for "find similar issues"
  within the Jira corpus

### Source-Specific gRPC Extensions

```protobuf
service JiraService {
  // Common SourceService methods (inherited via composition)
  rpc Search(SearchRequest) returns (SearchResponse);
  rpc GetItem(GetItemRequest) returns (JiraIssue);
  rpc ListItems(ListItemsRequest) returns (stream JiraIssueSummary);

  // Jira-specific
  rpc GetIssueComments(GetCommentsRequest) returns (stream JiraComment);
  rpc GetIssueLinks(GetLinksRequest) returns (IssueLinksResponse);
  rpc ListByWorkGroup(WorkGroupRequest) returns (stream JiraIssueSummary);
  rpc ListBySpecification(SpecificationRequest) returns (stream JiraIssueSummary);
  rpc GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

### Cache Format

```
cache/jira/
├── _meta.json                          # Sync cursor, last update timestamps
├── DayOf_2026-03-18-000.xml            # Batch files (XML or JSON)
├── DayOf_2026-03-18-001.xml
├── DayOf_2026-03-17-000.xml
└── ...
```

Jira data is cached in date-based batch files (same approach as v1 local
caching proposal). Each batch contains all issues created/updated in that
date window. The XML format is used for bulk exports; JSON for REST API
responses.

### Configuration

```json
{
  "Jira": {
    "BaseUrl": "https://jira.hl7.org",
    "AuthMode": "cookie",
    "CachePath": "./cache/jira",
    "DatabasePath": "./data/jira.db",
    "SyncSchedule": "01:00:00",
    "DefaultProject": "FHIR",
    "Ports": { "Http": 5160, "Grpc": 5161 }
  }
}
```

---

## Source: Zulip Service

### Data Model

**Tables:**
- `zulip_streams` — Stream metadata (name, description, message count)
- `zulip_messages` — Individual messages with content, sender, topic
- `zulip_messages_fts` — FTS5 virtual table
- `index_keywords` — BM25 keyword scores
- `sync_state` — Per-stream ingestion state

### Internal Indexing

- **Topic threading:** Messages within the same stream+topic form a thread.
  The service can return entire threads and navigate between topics.
- **Stream browsing:** List topics within a stream, ordered by most recent
  activity or message count.
- **BM25 similarity:** Find messages/threads similar to a given message.
- **Sender indexing:** Find all messages by a specific sender.

### Source-Specific gRPC Extensions

```protobuf
service ZulipService {
  rpc Search(SearchRequest) returns (SearchResponse);
  rpc GetItem(GetItemRequest) returns (ZulipMessage);
  rpc ListItems(ListItemsRequest) returns (stream ZulipMessageSummary);

  // Zulip-specific
  rpc GetThread(GetThreadRequest) returns (ZulipThread);
  rpc ListStreams(ListStreamsRequest) returns (stream ZulipStream);
  rpc ListTopics(ListTopicsRequest) returns (stream ZulipTopic);
  rpc GetMessagesByUser(UserMessagesRequest) returns (stream ZulipMessageSummary);
  rpc GetThreadSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

### Cache Format

```
cache/zulip/
├── _meta_s270.json                     # Per-stream sync cursor
├── _meta_s412.json
├── s270/                               # Stream ID directory
│   ├── _WeekOf_2024-08-05-000.json     # Weekly batch (initial download)
│   ├── DayOf_2026-03-18-000.json       # Daily batch (incremental)
│   └── ...
├── s412/
│   └── ...
```

### Configuration

```json
{
  "Zulip": {
    "BaseUrl": "https://chat.fhir.org",
    "CredentialFile": "~/.zuliprc",
    "CachePath": "./cache/zulip",
    "DatabasePath": "./data/zulip.db",
    "SyncSchedule": "04:00:00",
    "Ports": { "Http": 5170, "Grpc": 5171 }
  }
}
```

---

## Source: Confluence Service

### Data Model

**Tables:**
- `confluence_spaces` — Space metadata
- `confluence_pages` — Page content with hierarchy (parent/child relationships)
- `confluence_comments` — Page comments
- `confluence_page_links` — Internal page-to-page links (extracted from page content)
- `confluence_pages_fts` — FTS5 virtual table
- `index_keywords` — BM25 keyword scores
- `sync_state` — Per-space ingestion state

### Internal Indexing

- **Page hierarchy:** Navigate parent/child relationships. Find all pages under
  a given parent. Breadcrumb generation.
- **Internal links:** Links between Confluence pages extracted from
  storage-format content (Confluence macros, page links, etc.)
- **Label browsing:** Find pages by label, list all labels in a space.
- **BM25 similarity:** Find pages similar to a given page.

### Source-Specific gRPC Extensions

```protobuf
service ConfluenceService {
  rpc Search(SearchRequest) returns (SearchResponse);
  rpc GetItem(GetItemRequest) returns (ConfluencePage);
  rpc ListItems(ListItemsRequest) returns (stream ConfluencePageSummary);

  // Confluence-specific
  rpc GetPageComments(GetCommentsRequest) returns (stream ConfluenceComment);
  rpc GetPageChildren(GetChildrenRequest) returns (stream ConfluencePageSummary);
  rpc GetPageAncestors(GetAncestorsRequest) returns (stream ConfluencePageSummary);
  rpc ListSpaces(ListSpacesRequest) returns (stream ConfluenceSpace);
  rpc GetLinkedPages(GetLinkedPagesRequest) returns (stream ConfluencePageSummary);
  rpc GetPagesByLabel(LabelRequest) returns (stream ConfluencePageSummary);
  rpc GetPageSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

### Cache Format

```
cache/confluence/
├── _meta.json                          # Global sync cursor
├── spaces/
│   ├── FHIR.json                       # Space metadata
│   └── FHIRI.json
├── pages/
│   ├── {page-id}.json                  # One file per page (full content)
│   └── ...
```

### Configuration

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "basic",
    "Spaces": ["FHIR", "FHIRI", "SOA"],
    "CachePath": "./cache/confluence",
    "DatabasePath": "./data/confluence.db",
    "SyncSchedule": "1.00:00:00",
    "Ports": { "Http": 5180, "Grpc": 5181 }
  }
}
```

---

## Source: GitHub Service

### Data Model

**Tables:**
- `github_repos` — Repository metadata
- `github_issues` — Issues and PRs (with `is_pull_request` flag)
- `github_comments` — Issue and PR comments
- `github_commits` — Commit metadata (SHA, message, author, date)
- `github_issues_fts` — FTS5 virtual table
- `github_comments_fts` — FTS5 virtual table
- `index_keywords` — BM25 keyword scores
- `sync_state` — Per-repo ingestion state

### Internal Indexing

- **Issue/PR linking:** GitHub's own issue references (`#123`, `fixes #456`)
  extracted from issue bodies, PR bodies, and commit messages.
- **Commit-to-issue linking:** Commits that reference issues via conventional
  commit patterns or `#NNN` references.
- **Label browsing:** Find issues/PRs by label.
- **Milestone grouping:** Issues/PRs grouped by milestone.
- **BM25 similarity:** Find issues/PRs similar to a given one.

### Source-Specific gRPC Extensions

```protobuf
service GitHubSourceService {
  rpc Search(SearchRequest) returns (SearchResponse);
  rpc GetItem(GetItemRequest) returns (GitHubIssue);
  rpc ListItems(ListItemsRequest) returns (stream GitHubIssueSummary);

  // GitHub-specific
  rpc GetIssueComments(GetCommentsRequest) returns (stream GitHubComment);
  rpc GetPullRequestDetails(GetPRRequest) returns (GitHubPullRequest);
  rpc GetRelatedCommits(GetCommitsRequest) returns (stream GitHubCommit);
  rpc ListRepositories(ListReposRequest) returns (stream GitHubRepo);
  rpc ListByLabel(LabelRequest) returns (stream GitHubIssueSummary);
  rpc ListByMilestone(MilestoneRequest) returns (stream GitHubIssueSummary);
  rpc GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

### Cache Format

```
cache/github/
├── _meta.json                          # Global sync cursor
├── repos/
│   ├── HL7_fhir/                       # Owner_Repo (slash replaced with underscore)
│   │   ├── issues/
│   │   │   ├── {number}.json           # One file per issue/PR
│   │   │   └── ...
│   │   ├── comments/
│   │   │   ├── {issue-number}.json     # All comments for an issue
│   │   │   └── ...
│   │   └── commits/
│   │       ├── page-001.json           # Paginated commit lists
│   │       └── ...
│   └── HL7_fhir-ig-publisher/
│       └── ...
```

### Configuration

```json
{
  "GitHub": {
    "Repositories": ["HL7/fhir", "HL7/fhir-ig-publisher"],
    "CachePath": "./cache/github",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "Ports": { "Http": 5190, "Grpc": 5191 }
  }
}
```
