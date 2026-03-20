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

All item-level responses (`Search`, `GetItem`, `ListItems`, `GetRelated`,
`GetContent`) must include a `url` field linking to the original content in
the source system (see design principle *Link to Source Material* in
[01-overview](01-overview.md)). URLs are constructed from the service's
configured `BaseUrl` and the item's identifier (e.g.,
`https://jira.hl7.org/browse/FHIR-43499`,
`https://chat.fhir.org/#narrow/stream/179166-implementers/topic/Bundle.20signatures`,
`https://confluence.hl7.org/display/FHIR/...`,
`https://github.com/HL7/fhir/issues/123`).

---

## Source: Jira Service

### Data Model

The Jira service stores issues, comments, and issue links in its own SQLite
database (`jira.db`).

**Tables:**
- `jira_issues` — Full issue records with all standard and custom fields
- `jira_comments` — Comments on issues
- `jira_issue_links` — Internal links between issues (duplicates, blocks, relates to)
- `jira_spec_artifacts` — Parsed data from the HL7/JIRA-Spec-Artifacts repository,
  mapping Jira project families and specification keys to GitHub repository URLs,
  workgroups, and published spec URLs (see *JIRA-Spec-Artifacts Integration* below)
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
  rpc QueryIssues(JiraQueryRequest) returns (stream JiraIssueSummary);
  rpc GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse);
  rpc ListSpecArtifacts(ListSpecArtifactsRequest) returns (stream SpecArtifactEntry);
  rpc GetIssueNumbers(GetIssueNumbersRequest) returns (JiraIssueNumbersResponse);
}
```

#### Structured Issue Queries (`QueryIssues`)

The `ListByWorkGroup` and `ListBySpecification` RPCs support simple
single-filter queries, but workgroups and other consumers often need to build
more complex work-lists: e.g., "all open tickets for my workgroup, excluding
the FHIR-I project, with status Triaged or Waiting for Input." The
`QueryIssues` RPC supports composable, structured queries for these use cases.

**`JiraQueryRequest` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `statuses` | `repeated string` | Filter by status (e.g., `["Open", "Triaged", "Waiting for Input"]`). Empty = all. |
| `resolutions` | `repeated string` | Filter by resolution (e.g., `["Unresolved"]`). Empty = all. |
| `work_groups` | `repeated string` | Filter by workgroup key (e.g., `["fhir-i", "pa"]`). Empty = all. |
| `specifications` | `repeated string` | Filter by specification key (e.g., `["core", "us-core"]`). Empty = all. |
| `projects` | `repeated string` | Include only these Jira projects (e.g., `["FHIR"]`). Empty = all. |
| `exclude_projects` | `repeated string` | Exclude these Jira projects. Applied after `projects`. |
| `types` | `repeated string` | Filter by issue type (e.g., `["Bug", "Enhancement"]`). Empty = all. |
| `priorities` | `repeated string` | Filter by priority (e.g., `["Critical", "Major"]`). Empty = all. |
| `labels` | `repeated string` | Filter by label. All listed labels must be present (AND). |
| `assignees` | `repeated string` | Filter by assignee username. Empty = all. |
| `reporters` | `repeated string` | Filter by reporter username. Empty = all. |
| `created_after` | `Timestamp` | Only issues created after this date. |
| `created_before` | `Timestamp` | Only issues created before this date. |
| `updated_after` | `Timestamp` | Only issues updated after this date. |
| `updated_before` | `Timestamp` | Only issues updated before this date. |
| `query` | `string` | Optional full-text search within filtered results. |
| `sort_by` | `string` | Sort field: `updated_at`, `created_at`, `priority`, `status`. |
| `sort_order` | `string` | `asc` or `desc` (default: `desc`). |
| `limit` | `int32` | Max results to return. |
| `offset` | `int32` | Pagination offset. |

All filter fields are optional and combined with AND logic. Repeated values
within a single field use OR logic (e.g., `statuses: ["Open", "Triaged"]`
matches issues with status Open **or** Triaged).

**Example work-list queries:**

```
# Open tickets for the Patient Administration workgroup
QueryIssues { work_groups: ["pa"], statuses: ["Open", "Triaged"] }

# All unresolved FHIR-core bugs, sorted by priority
QueryIssues { specifications: ["core"], types: ["Bug"],
              resolutions: ["Unresolved"], sort_by: "priority" }

# Tickets updated in the last 30 days, excluding CDA project
QueryIssues { updated_after: <30 days ago>, exclude_projects: ["CDA"] }

# Full-text search within a workgroup's open tickets
QueryIssues { work_groups: ["fhir-i"], statuses: ["Open"],
              query: "FHIRPath normative" }
```

### Cache Format

```
cache/jira/
├── _meta.json                          # Sync cursor, last update timestamps
├── DayOf_2026-03-18-000.xml            # Batch files (XML or JSON)
├── DayOf_2026-03-18-001.xml
├── DayOf_2026-03-17-000.xml
├── ...
└── jira-spec-artifacts/                # Local clone of HL7/JIRA-Spec-Artifacts
    ├── xml/
    │   ├── _families.xml               # Jira project prefixes (FHIR, CDA, V2, OTHER)
    │   ├── _workgroups.xml             # HL7 work group definitions
    │   ├── SPECS-FHIR.xml              # Specification list for FHIR family
    │   ├── FHIR-core.xml               # Spec detail: gitUrl, artifacts, pages
    │   ├── FHIR-us-core.xml
    │   └── ...
    └── ...
```

Jira data is cached in date-based batch files (same approach as v1 local
caching proposal). Each batch contains all issues created/updated in that
date window. The XML format is used for bulk exports; JSON for REST API
responses.

### JIRA-Spec-Artifacts Integration

The [`HL7/JIRA-Spec-Artifacts`](https://github.com/HL7/JIRA-Spec-Artifacts)
repository is the authoritative source for mapping between Jira projects,
specifications, and their corresponding GitHub repositories. The Jira service
maintains a local clone of this repo under `cache/jira/jira-spec-artifacts/`
and runs `git pull` during each ingestion run.

**Change detection:** The JIRA-Spec-Artifacts repo is infrequently updated
— typically only when new specifications are published or existing ones are
reconfigured. Since Jira ingestion runs much more frequently (e.g., daily),
the service must avoid re-parsing and reloading the spec-artifact data when
nothing has changed. After `git pull`, the service checks whether the local
HEAD commit SHA has changed since the last parse. If the SHA is unchanged,
the XML parsing and `jira_spec_artifacts` table reload are skipped entirely.
The last-processed SHA is stored in `sync_state`.

**What the repo provides:**

| File | Content |
|------|---------|
| `xml/_families.xml` | Jira project prefixes (`FHIR`, `CDA`, `V2`, `OTHER`) — each maps to a Jira feedback project |
| `xml/SPECS-{FAMILY}.xml` | List of all specifications in a family, with keys and names |
| `xml/{FAMILY}-{key}.xml` | Per-specification detail including `gitUrl` (GitHub repo URL), `url` (published spec URL), `defaultWorkgroup`, versions, artifacts, and pages |

**Key attribute: `gitUrl`** — Each specification XML file has a `gitUrl`
attribute on the root `<specification>` element that links to the GitHub
repository for that spec. For example:

```xml
<!-- FHIR-core.xml -->
<specification gitUrl="https://github.com/HL7/fhir" url="http://hl7.org/fhir" ... />

<!-- FHIR-us-core.xml -->
<specification gitUrl="https://github.com/HL7/US-Core" url="http://hl7.org/fhir/us/core" ... />
```

Note that GitHub repo names do not always match Jira spec keys (e.g., spec
key `us-davinci-cdex` → repo `HL7/davinci-ecdx`), which is why parsing
this data is essential rather than relying on naming conventions.

**How the Jira service uses this data:**

1. **Parsing** — During ingestion (or rebuild), the Jira service parses all
   XML files and loads the family → spec → GitHub mapping into the
   `jira_spec_artifacts` table.
2. **Specification linking** — Enriches Jira issues with their specification's
   GitHub repo URL, published URL, and workgroup assignment.
3. **Serving to other services** — Exposes the spec-artifact data via the
   `ListSpecArtifacts` gRPC method and the `GetIssueNumbers` method (which
   returns the set of valid Jira issue numbers for reference validation).
   The GitHub service uses `ListSpecArtifacts` to discover which repositories
   to ingest (see GitHub Service configuration below).

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
  rpc QueryMessages(ZulipQueryRequest) returns (stream ZulipMessageSummary);
  rpc GetThreadSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

#### Structured Message Queries (`QueryMessages`)

Similar to the Jira `QueryIssues` RPC, the Zulip service exposes a
composable structured query for filtering messages. This supports use cases
like "find all messages by a specific person in a specific stream about a
given topic keyword."

**`ZulipQueryRequest` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `stream_names` | `repeated string` | Filter by stream name (e.g., `["implementers", "terminology"]`). Empty = all. |
| `stream_ids` | `repeated int32` | Filter by stream ID. Can be combined with `stream_names` (OR). |
| `topic` | `string` | Filter by topic — exact match. |
| `topic_keyword` | `string` | Filter by topic — substring/keyword match (e.g., `"Bundle"` matches topics containing "Bundle"). |
| `sender_names` | `repeated string` | Filter by sender display name. |
| `sender_ids` | `repeated int32` | Filter by sender user ID. Can be combined with `sender_names` (OR). |
| `after` | `Timestamp` | Only messages after this date. |
| `before` | `Timestamp` | Only messages before this date. |
| `query` | `string` | Optional full-text search within filtered results. |
| `sort_by` | `string` | Sort field: `timestamp`, `stream`, `topic`. |
| `sort_order` | `string` | `asc` or `desc` (default: `desc`). |
| `limit` | `int32` | Max results to return. |
| `offset` | `int32` | Pagination offset. |

All filter fields are optional and combined with AND logic. Repeated values
within a single field use OR logic (e.g., `stream_names: ["implementers",
"terminology"]` matches messages in either stream).

**Example queries:**

```
# All messages by Grahame Grieve in the implementers stream
QueryMessages { sender_names: ["Grahame Grieve"], stream_names: ["implementers"] }

# Recent messages in any stream about FHIRPath
QueryMessages { topic_keyword: "FHIRPath", after: <30 days ago> }

# Full-text search within a specific topic thread
QueryMessages { stream_names: ["implementers"], topic: "Bundle signatures",
                query: "digital signature" }

# Messages from multiple people across all streams
QueryMessages { sender_names: ["Lloyd McKenzie", "Grahame Grieve"],
                after: <7 days ago>, sort_by: "timestamp" }
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
│   ├── FHIR/                           # Pages in this space
│   │   ├── {pageId}.json               # One file per page (full content)
│   │   └── ...
│   ├── FHIRI.json
│   └── FHIRI/
│       ├── {pageId}.json
│       └── ...
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
- `github_repos` — Repository metadata (including `has_issues` flag from the
  GitHub API, used to control `#NNN` reference disambiguation)
- `github_issues` — Issues and PRs (with `is_pull_request` flag)
- `github_comments` — Issue and PR comments
- `github_commits` — Commit metadata (SHA, message, author, date)
- `github_commit_pr_links` — Bidirectional mapping between commits and PRs
  (merge commits, squash-merge SHAs, and commits referenced in PR bodies/timelines)
- `github_jira_refs` — References from GitHub artifacts (commits, PRs, issues,
  comments) to Jira issue keys, with source location (which SHA, PR number,
  or comment contained the reference)
- `github_spec_file_map` — Mapping from FHIR artifacts, pages, and elements
  to repository file paths (see *FHIR Artifact File Mapping* below)
- `github_issues_fts` — FTS5 virtual table for issue/PR title and body
- `github_comments_fts` — FTS5 virtual table for comment content
- `github_commits_fts` — FTS5 virtual table for commit messages (enables
  full-text search across commit history)
- `index_keywords` — BM25 keyword scores
- `sync_state` — Per-repo ingestion state

### Internal Indexing

- **Issue/PR linking:** GitHub's own issue references (`#123`, `fixes #456`)
  extracted from issue bodies, PR bodies, and commit messages. See
  *`#NNN` reference disambiguation* below for how bare `#NNN` references are
  resolved.
- **Commit ↔ PR linking:** Bidirectional mapping between commits and pull
  requests stored in `github_commit_pr_links`. Populated from merge commit
  metadata, squash-merge SHAs, PR timeline events, and `#NNN` references in
  commit messages. Enables two key queries:
  - *Find the PR that introduced a commit* — look up any commit SHA to get the
    PR that merged it (or that references it).
  - *Find all commits in a PR* — given a PR number, retrieve the full list of
    commits associated with it (including the merge/squash commit).
- **FHIR/Jira issue extraction:** Commit messages, PR titles/bodies, issue
  bodies, and comments are scanned for Jira issue references using several
  known patterns: `FHIR-{N}`, `JF-{N}`, `J#{N}`, `GF-{N}`, and similar
  project-prefixed forms. Matched references are validated against the known
  set of Jira issue numbers and stored in `github_jira_refs`.

  Because a Jira ticket always exists before any commit that references it,
  the GitHub service fetches the current list of Jira issue numbers from the
  Jira service (via gRPC) **before** each indexing pass — whether full rebuild
  or incremental. This allows the extractor to validate references against
  real ticket numbers and discard false positives (e.g., a version string
  like `FHIR-5` in a non-ticket context). The issue list is lightweight
  (just numbers/keys) and is fetched once per indexing run.

  This enables queries like:
  - *Find all GitHub commits/PRs that reference FHIR-12345*
  - *For a given Jira issue, find all related GitHub activity*
- **`#NNN` reference disambiguation:** Many HL7 repositories have GitHub
  Issues disabled; contributors commonly use bare `#NNN` to reference Jira
  tickets in commit messages and PR descriptions. The GitHub service uses the
  repository's `has_issues` flag (stored in `github_repos`) to determine how
  to resolve bare `#NNN` references:

  | `has_issues` | Resolution strategy |
  |:---:|---|
  | **off** | All bare `#NNN` references are treated as Jira ticket numbers. They are validated against the known Jira issue list and, if valid, stored in `github_jira_refs`. |
  | **on** | The reference is checked against existing GitHub issue/PR numbers for that repository. If a matching GitHub issue or PR exists (e.g., `#1`, `#2`, `#100`), it is treated as a GitHub reference and stored in `github_commit_pr_links` or as an internal cross-ref. If no matching GitHub issue/PR exists (e.g., `#54000`), the number is checked against the Jira issue list and, if valid, stored in `github_jira_refs`. |

  Explicitly prefixed references (`FHIR-{N}`, `JF-{N}`, `J#{N}`, `GF-{N}`)
  are always treated as Jira references regardless of the `has_issues` setting.
- **FHIR artifact/page/element indexing:** The GitHub service consumes the
  JIRA-Spec-Artifacts data (obtained from the Jira service via
  `ListSpecArtifacts`) to understand the FHIR artifacts, pages, and their
  associated workgroups defined for each specification. This enables queries
  scoped to a specific FHIR artifact (e.g., "Patient"), page (e.g.,
  "Subscriptions Framework"), or element (e.g., "Patient.name"). See
  *FHIR Artifact File Mapping* below for details on how artifact names are
  resolved to repository file paths.
- **Commit message search:** FTS5 index on commit messages (`github_commits_fts`)
  enables full-text search across commit history — useful for finding when a
  change was introduced, tracing commit patterns, and searching by author or
  keyword.
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
  rpc GetPullRequestForCommit(GetPRForCommitRequest) returns (GitHubPullRequest);
  rpc GetCommitsForPullRequest(GetCommitsForPRRequest) returns (stream GitHubCommit);
  rpc SearchCommits(SearchRequest) returns (SearchResponse);
  rpc GetJiraReferences(GetJiraRefsRequest) returns (stream GitHubJiraRef);
  rpc QueryByArtifact(GitHubArtifactQueryRequest) returns (stream GitHubCommit);
  rpc ListRepositories(ListReposRequest) returns (stream GitHubRepo);
  rpc ListByLabel(LabelRequest) returns (stream GitHubIssueSummary);
  rpc ListByMilestone(MilestoneRequest) returns (stream GitHubIssueSummary);
  rpc GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse);
}
```

### FHIR Artifact File Mapping

The GitHub service needs to connect FHIR specification concepts — artifacts
(e.g., `Patient`, `CanonicalResource`, `Bundle`), pages (e.g., "Subscriptions
Framework", "Workflow"), and elements (e.g., `Patient.name`,
`Attachment.url`) — to actual files and directories in GitHub repositories.
This enables queries like "show me all commits that affected the Patient
resource" or "what PRs touched the Subscriptions Framework page."

#### Data Sources

**Phase 1 (current):** The GitHub service consumes JIRA-Spec-Artifacts data
from the Jira service via `ListSpecArtifacts`. Each specification's XML file
(e.g., `FHIR-core.xml`) defines:

- **Artifacts** — with `key`, `name`, and `id` (e.g.,
  `key="Patient" id="StructureDefinition/Patient" workgroup="pa"`)
- **Pages** — with `key`, `name`, and `url` (e.g.,
  `key="subscriptions" name="Subscriptions Framework" url="subscriptions"`)
- **Artifact page extensions** — URL suffixes for artifact sub-pages
  (e.g., `-definitions`, `-examples`, `-mappings`)

**Phase 2 (future enhancement):** Parse the repository content directly
(e.g., StructureDefinition XML files) to extract element-level data
(`Patient.name`, `Patient.birthDate`, etc.) and build finer-grained mappings.

#### File Path Resolution

Artifact and page names from JIRA-Spec-Artifacts do not map 1:1 to repository
file paths. The GitHub service builds the `github_spec_file_map` table by
reconciling spec-artifact names against the repository's actual file tree.

**Resolution strategy for the FHIR core repo (`HL7/fhir`):**

| Concept | Spec-Artifact Data | Repository Path Pattern | Notes |
|---------|-------------------|------------------------|-------|
| Resource (broad) | `key="Patient"` | `source/patient/` (directory) | An artifact maps to a source directory containing all related files |
| Resource (specific) | `id="StructureDefinition/Patient"` | `source/patient/structuredefinition-Patient.xml` | The StructureDefinition is the canonical definition file |
| Element | `Patient.name` (phase 2) | `source/patient/structuredefinition-Patient.xml` | Elements map to their parent resource's StructureDefinition |
| Page | `url="subscriptions"` | `source/subscriptions.html` or `source/subscriptions/` | Pages may be single files or directories |
| Data type | `key="Base64Binary"` | `source/datatypes/` | Primitive/complex types live under datatypes/ |

The resolution process:

1. **Fetch the repository file tree** — The GitHub service retrieves the
   directory listing for each tracked repo (via the Git Trees API) and caches
   it. This is a lightweight operation (single API call per repo).
2. **Match artifact keys to directories** — For each artifact key, search for
   a matching directory under `source/` (case-insensitive). E.g., artifact
   `Patient` → `source/patient/`.
3. **Match artifact IDs to specific files** — For each artifact ID (e.g.,
   `StructureDefinition/Patient`), search within the matched directory for a
   file matching the pattern `structuredefinition-{Name}.xml`.
4. **Match page URLs to files** — For each page URL, search for matching
   `.html` files or directories under `source/`.
5. **Store mappings** — Insert resolved mappings into `github_spec_file_map`
   with the artifact/page key, the repo, and the matched file/directory paths.

Unresolved mappings (where no matching file/directory is found) are logged
and stored with a null path so they can be manually overridden or resolved
in a future indexing pass.

**Note:** Different repositories have different conventions. IG repositories
(e.g., `HL7/US-Core`) typically use `input/` instead of `source/` and
follow the IG Publisher directory layout. The resolution strategy is
configurable per repository, with sensible defaults for the core FHIR repo
and the IG Publisher convention.

#### Artifact-Scoped Queries

The `QueryByArtifact` RPC uses the `github_spec_file_map` to filter commits
and PRs to those that touch files associated with a given artifact, page, or
element.

**`GitHubArtifactQueryRequest` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `repo` | `string` | Repository full name (e.g., `HL7/fhir`). Required. |
| `artifact_key` | `string` | Artifact key from spec-artifacts (e.g., `Patient`, `Bundle`). Matches the artifact's directory and all files within it. |
| `artifact_id` | `string` | Artifact formal ID (e.g., `StructureDefinition/Patient`). Matches the specific definition file. |
| `page_key` | `string` | Page key from spec-artifacts (e.g., `subscriptions`, `workflow`). |
| `element_path` | `string` | Element path (e.g., `Patient.name`, `Attachment.url`). Resolved to the parent resource's file. Phase 2: will resolve to specific lines/sections. |
| `include_prs` | `bool` | Also return associated PRs for matching commits. |
| `after` | `Timestamp` | Only results after this date. |
| `before` | `Timestamp` | Only results before this date. |
| `limit` | `int32` | Max results. |

Only one of `artifact_key`, `artifact_id`, `page_key`, or `element_path`
should be provided. The query finds all commits whose changed-file list
intersects with the mapped file paths for the given concept.

**Example queries:**

```
# All commits affecting the Patient resource (broad — entire source/patient/ directory)
QueryByArtifact { repo: "HL7/fhir", artifact_key: "Patient" }

# Commits affecting the Patient StructureDefinition specifically
QueryByArtifact { repo: "HL7/fhir", artifact_id: "StructureDefinition/Patient" }

# Commits related to the Subscriptions Framework page
QueryByArtifact { repo: "HL7/fhir", page_key: "subscriptions" }

# Commits affecting Patient.name (resolves to the Patient StructureDefinition file)
QueryByArtifact { repo: "HL7/fhir", element_path: "Patient.name" }

# Recent PRs affecting the Bundle resource
QueryByArtifact { repo: "HL7/fhir", artifact_key: "Feed", include_prs: true,
                  after: <90 days ago> }
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

The GitHub service supports three modes for selecting which repositories to
ingest, controlled by the `RepoMode` setting:

| `RepoMode` | Behavior |
|:---:|---|
| `core` *(default)* | Ingest only `HL7/fhir` (the core FHIR spec repository). |
| `explicit` | Ingest only the repositories listed in `Repositories`. |
| `all` | Discover all linked repositories from the Jira service's JIRA-Spec-Artifacts data (via `ListSpecArtifacts` gRPC call) and ingest all of them. |

In `all` mode, the GitHub service calls the Jira service's `ListSpecArtifacts`
RPC at the start of each ingestion run to get the current set of `gitUrl`
values. Any new repositories that appear in the spec-artifacts data are
automatically added to ingestion. Repositories can also be explicitly added
via `AdditionalRepositories` for repos not tracked in JIRA-Spec-Artifacts.

The `ManualLinks` setting allows manually linking a Jira project/spec to a
GitHub repository when the link is not established in JIRA-Spec-Artifacts or
needs to be overridden.

```json
{
  "GitHub": {
    "RepoMode": "core",
    "Repositories": ["HL7/fhir"],
    "AdditionalRepositories": [],
    "ManualLinks": [
      { "JiraProject": "FHIR", "JiraSpec": "custom-ig", "GitHubRepo": "HL7/custom-ig" }
    ],
    "CachePath": "./cache/github",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "Ports": { "Http": 5190, "Grpc": 5191 }
  }
}
```

**Examples:**

```jsonc
// Default: core FHIR repo only
{ "GitHub": { "RepoMode": "core" } }

// Specific repos only
{ "GitHub": { "RepoMode": "explicit", "Repositories": ["HL7/fhir", "HL7/US-Core", "HL7/fhir-ig-publisher"] } }

// All repos from JIRA-Spec-Artifacts, plus extras
{ "GitHub": { "RepoMode": "all", "AdditionalRepositories": ["HL7/fhir-ig-publisher"] } }
```
