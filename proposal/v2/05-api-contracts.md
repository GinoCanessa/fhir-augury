# FHIR Augury v2 — API Contracts

## Shared gRPC Definitions

All source services implement a common `SourceService` contract defined in
`protos/source_service.proto`. Source-specific extensions are defined in
separate proto files.

---

## Common Proto: `source_service.proto`

```protobuf
syntax = "proto3";
package fhiraugury;

import "google/protobuf/timestamp.proto";

// Common contract that all source services implement.
service SourceService {
  // Full-text search within this source's data.
  rpc Search(SearchRequest) returns (SearchResponse);

  // Retrieve the complete details of a single item.
  rpc GetItem(GetItemRequest) returns (ItemResponse);

  // Paginated listing with optional filters.
  rpc ListItems(ListItemsRequest) returns (stream ItemSummary);

  // Find items within this source related to a given item.
  rpc GetRelated(GetRelatedRequest) returns (SearchResponse);

  // Retrieve the full rendered content of an item (markdown snapshot).
  rpc GetSnapshot(GetSnapshotRequest) returns (SnapshotResponse);

  // Stream searchable text for cross-reference scanning.
  // Returns items added/updated since the given timestamp.
  rpc StreamSearchableText(StreamTextRequest) returns (stream SearchableTextItem);

  // Trigger a full or incremental ingestion run.
  rpc TriggerIngestion(TriggerIngestionRequest) returns (IngestionStatusResponse);

  // Get current ingestion/sync status.
  rpc GetIngestionStatus(IngestionStatusRequest) returns (IngestionStatusResponse);

  // Rebuild the database from cached API responses.
  rpc RebuildFromCache(RebuildRequest) returns (RebuildResponse);

  // Get source statistics.
  rpc GetStats(StatsRequest) returns (StatsResponse);

  // Health check.
  rpc HealthCheck(HealthCheckRequest) returns (HealthCheckResponse);
}

// ── Request/Response Messages ────────────────────────────────────

message SearchRequest {
  string query = 1;
  int32 limit = 2;                           // Default: 20, max: 100
  int32 offset = 3;
  map<string, string> filters = 4;           // Source-specific key/value filters
}

message SearchResponse {
  string query = 1;
  int32 total_results = 2;
  repeated SearchResultItem results = 3;
}

message SearchResultItem {
  string source = 1;                         // "jira", "zulip", etc.
  string id = 2;                             // Source-specific identifier
  string title = 3;
  string snippet = 4;                        // Highlighted text snippet
  double score = 5;                          // Source-internal relevance score
  string url = 6;                            // Link to original content
  google.protobuf.Timestamp updated_at = 7;
  map<string, string> metadata = 8;          // Source-specific metadata
}

message GetItemRequest {
  string id = 1;                             // Source-specific identifier
  bool include_content = 2;                  // Whether to include full body content
  bool include_comments = 3;                 // Whether to include comments
}

message ItemResponse {
  string source = 1;
  string id = 2;
  string title = 3;
  string content = 4;                        // Full body content (plain text or HTML)
  string url = 5;
  google.protobuf.Timestamp created_at = 6;
  google.protobuf.Timestamp updated_at = 7;
  map<string, string> metadata = 8;          // All fields as key/value pairs
  repeated Comment comments = 9;
}

message Comment {
  string id = 1;
  string author = 2;
  string body = 3;
  google.protobuf.Timestamp created_at = 4;
  string url = 5;                            // Link to comment in source system
}

message ItemSummary {
  string id = 1;
  string title = 2;
  string url = 3;
  google.protobuf.Timestamp updated_at = 4;
  map<string, string> metadata = 5;
}

message ListItemsRequest {
  int32 limit = 1;
  int32 offset = 2;
  string sort_by = 3;                       // "updated_at", "created_at", "title"
  string sort_order = 4;                    // "asc", "desc"
  map<string, string> filters = 5;          // Source-specific filters
}

message GetRelatedRequest {
  string id = 1;                             // Seed item identifier
  int32 limit = 2;                           // Max related items to return
}

message GetSnapshotRequest {
  string id = 1;
  bool include_comments = 2;
  bool include_internal_refs = 3;            // Include related items within this source
}

message SnapshotResponse {
  string id = 1;
  string source = 2;
  string markdown = 3;                       // Full rendered snapshot in markdown
  string url = 4;                            // Link to original item in source system
}

message StreamTextRequest {
  google.protobuf.Timestamp since = 1;       // Only items updated after this timestamp
  string cursor = 2;                          // Continuation cursor for large datasets
}

message SearchableTextItem {
  string source = 1;
  string id = 2;
  string title = 3;
  repeated string text_fields = 4;           // Searchable text to scan for xrefs
  google.protobuf.Timestamp updated_at = 5;
}

message TriggerIngestionRequest {
  string type = 1;                           // "full", "incremental"
  string filter = 2;                         // Optional source-specific filter
}

message IngestionStatusRequest {}

message IngestionStatusResponse {
  string source = 1;
  string status = 2;                         // "idle", "running", "completed", "failed"
  google.protobuf.Timestamp last_sync_at = 3;
  int32 items_total = 4;
  int32 items_processed = 5;                 // For running ingestion: progress
  string last_error = 6;
  string sync_schedule = 7;                  // e.g. "01:00:00"
}

message RebuildRequest {
  bool clear_database = 1;                   // Drop and recreate all tables
  bool rebuild_indexes = 2;                  // Rebuild FTS5 + BM25 indexes
}

message RebuildResponse {
  bool success = 1;
  int32 items_loaded = 2;
  string error = 3;
  double elapsed_seconds = 4;
}

message StatsRequest {}

message StatsResponse {
  string source = 1;
  int32 total_items = 2;
  int32 total_comments = 3;
  int64 database_size_bytes = 4;
  int64 cache_size_bytes = 5;
  google.protobuf.Timestamp last_sync_at = 6;
  google.protobuf.Timestamp oldest_item = 7;
  google.protobuf.Timestamp newest_item = 8;
  map<string, int32> additional_counts = 9;  // Source-specific counts
}

message HealthCheckRequest {}

message HealthCheckResponse {
  string status = 1;                         // "healthy", "degraded", "unhealthy"
  string version = 2;
  double uptime_seconds = 3;
  string message = 4;
}
```

---

## Orchestrator Proto: `orchestrator.proto`

```protobuf
syntax = "proto3";
package fhiraugury;

import "google/protobuf/timestamp.proto";
import "source_service.proto";

service OrchestratorService {
  // Unified search across all source services.
  rpc UnifiedSearch(UnifiedSearchRequest) returns (SearchResponse);

  // Find items across all sources related to a given item.
  rpc FindRelated(FindRelatedRequest) returns (FindRelatedResponse);

  // Get cross-references for a specific item.
  rpc GetCrossReferences(GetXRefRequest) returns (GetXRefResponse);

  // Trigger ingestion across one or all source services.
  rpc TriggerSync(TriggerSyncRequest) returns (TriggerSyncResponse);

  // Get aggregate status of all source services.
  rpc GetServicesStatus(ServicesStatusRequest) returns (ServicesStatusResponse);

  // Force a cross-reference scan of recent items.
  rpc TriggerXRefScan(TriggerXRefScanRequest) returns (TriggerXRefScanResponse);
}

message UnifiedSearchRequest {
  string query = 1;
  repeated string sources = 2;              // Filter to specific sources; empty = all
  int32 limit = 3;
}

message FindRelatedRequest {
  string source = 1;                        // Source type of the seed item
  string id = 2;                            // Seed item identifier
  int32 limit = 3;
  repeated string target_sources = 4;       // Limit to specific target sources; empty = all
}

message FindRelatedResponse {
  string seed_source = 1;
  string seed_id = 2;
  string seed_title = 3;
  repeated RelatedItem items = 4;
}

message RelatedItem {
  string source = 1;
  string id = 2;
  string title = 3;
  string snippet = 4;
  string url = 5;
  double relevance_score = 6;
  string relationship = 7;                  // "xref", "reverse_xref", "similar", "shared_metadata"
  string context = 8;                       // How the items are related (snippet or description)
}

message GetXRefRequest {
  string source = 1;
  string id = 2;
  string direction = 3;                     // "outgoing", "incoming", "both"
}

message GetXRefResponse {
  repeated CrossReference references = 1;
}

message CrossReference {
  string source_type = 1;
  string source_id = 2;
  string target_type = 3;
  string target_id = 4;
  string link_type = 5;
  string context = 6;
  string target_title = 7;                   // Resolved title from target source service
  string target_url = 8;
}

message TriggerSyncRequest {
  repeated string sources = 1;              // Sources to sync; empty = all enabled
  string type = 2;                          // "incremental" (default) or "full"
}

message TriggerSyncResponse {
  repeated SourceSyncStatus statuses = 1;
}

message SourceSyncStatus {
  string source = 1;
  string status = 2;                        // "triggered", "already_running", "disabled", "error"
  string message = 3;
}

message ServicesStatusRequest {}

message ServicesStatusResponse {
  repeated ServiceHealth services = 1;
  int32 cross_ref_links = 2;
  google.protobuf.Timestamp last_xref_scan_at = 3;
}

message ServiceHealth {
  string name = 1;
  string status = 2;
  string grpc_address = 3;
  google.protobuf.Timestamp last_sync_at = 4;
  int32 item_count = 5;
  int64 db_size_bytes = 6;
  string last_error = 7;
}

message TriggerXRefScanRequest {
  bool full_rescan = 1;                      // Rescan all items, not just recent
}

message TriggerXRefScanResponse {
  string status = 1;
  int32 items_to_scan = 2;
}
```

---

## Source-Specific Proto Extensions

Each source service extends the common contract with source-specific RPCs and
message types. These are defined in separate proto files.

### `jira.proto` (extends SourceService)

```protobuf
service JiraService {
  rpc GetIssueComments(JiraGetCommentsRequest) returns (stream Comment);
  rpc GetIssueLinks(JiraGetLinksRequest) returns (JiraIssueLinksResponse);
  rpc ListByWorkGroup(JiraWorkGroupRequest) returns (stream ItemSummary);
  rpc ListBySpecification(JiraSpecificationRequest) returns (stream ItemSummary);
  rpc QueryIssues(JiraQueryRequest) returns (stream ItemSummary);
}

// Structured query for building work-lists.
// All fields are optional; combine for composable filtering.
// Repeated values within a field use OR; fields combine with AND.
message JiraQueryRequest {
  repeated string statuses = 1;              // e.g. ["Open", "Triaged"]
  repeated string resolutions = 2;           // e.g. ["Unresolved"]
  repeated string work_groups = 3;           // e.g. ["fhir-i", "pa"]
  repeated string specifications = 4;        // e.g. ["core", "us-core"]
  repeated string projects = 5;              // Include only these Jira projects
  repeated string exclude_projects = 6;      // Exclude these Jira projects
  repeated string types = 7;                 // e.g. ["Bug", "Enhancement"]
  repeated string priorities = 8;            // e.g. ["Critical", "Major"]
  repeated string labels = 9;               // All must be present (AND)
  repeated string assignees = 10;
  repeated string reporters = 11;
  google.protobuf.Timestamp created_after = 12;
  google.protobuf.Timestamp created_before = 13;
  google.protobuf.Timestamp updated_after = 14;
  google.protobuf.Timestamp updated_before = 15;
  string query = 16;                         // Optional FTS within filtered results
  string sort_by = 17;                       // "updated_at", "created_at", "priority", "status"
  string sort_order = 18;                    // "asc" or "desc"
  int32 limit = 19;
  int32 offset = 20;
}

message JiraIssue {
  string key = 1;                            // "FHIR-43499"
  string project_key = 2;
  string title = 3;
  string description = 4;
  string type = 5;                           // Bug, Enhancement, etc.
  string priority = 6;
  string status = 7;
  string resolution = 8;
  string resolution_description = 9;
  string assignee = 10;
  string reporter = 11;
  google.protobuf.Timestamp created_at = 12;
  google.protobuf.Timestamp updated_at = 13;
  google.protobuf.Timestamp resolved_at = 14;
  // Custom fields
  string work_group = 15;
  string specification = 16;
  string raised_in_version = 17;
  string selected_ballot = 18;
  string related_artifacts = 19;
  string change_type = 20;
  string labels = 21;
  int32 comment_count = 22;
  string url = 23;
}
```

### `zulip.proto` (extends SourceService)

```protobuf
service ZulipSourceService {
  rpc GetThread(ZulipGetThreadRequest) returns (ZulipThread);
  rpc ListStreams(ZulipListStreamsRequest) returns (stream ZulipStream);
  rpc ListTopics(ZulipListTopicsRequest) returns (stream ZulipTopic);
  rpc GetMessagesByUser(ZulipUserMessagesRequest) returns (stream ItemSummary);
  rpc QueryMessages(ZulipQueryRequest) returns (stream ItemSummary);
}

// Structured query for filtering Zulip messages.
// All fields are optional; combine for composable filtering.
// Repeated values within a field use OR; fields combine with AND.
message ZulipQueryRequest {
  repeated string stream_names = 1;          // Filter by stream name
  repeated int32 stream_ids = 2;             // Filter by stream ID (OR with stream_names)
  string topic = 3;                          // Exact topic match
  string topic_keyword = 4;                  // Topic substring/keyword match
  repeated string sender_names = 5;          // Filter by sender display name
  repeated int32 sender_ids = 6;             // Filter by sender ID (OR with sender_names)
  google.protobuf.Timestamp after = 7;       // Only messages after this date
  google.protobuf.Timestamp before = 8;      // Only messages before this date
  string query = 9;                          // Optional FTS within filtered results
  string sort_by = 10;                       // "timestamp", "stream", "topic"
  string sort_order = 11;                    // "asc" or "desc"
  int32 limit = 12;
  int32 offset = 13;
}

message ZulipThread {
  string stream_name = 1;
  string topic = 2;
  repeated ZulipMessage messages = 3;
  string url = 4;
}

message ZulipMessage {
  int32 id = 1;
  int32 stream_id = 2;
  string stream_name = 3;
  string topic = 4;
  string sender_name = 5;
  string content = 6;                       // Plain text content
  string content_html = 7;                  // Original HTML
  google.protobuf.Timestamp timestamp = 8;
  string url = 9;                           // Link to message in Zulip (near link)
}

message ZulipStream {
  int32 id = 1;
  string name = 2;
  string description = 3;
  int32 message_count = 4;
  string url = 5;                           // Link to stream in Zulip
}

message ZulipTopic {
  string stream_name = 1;
  string topic = 2;
  int32 message_count = 3;
  google.protobuf.Timestamp last_message_at = 4;
  string url = 5;                           // Link to topic in Zulip
}
```

### `confluence.proto` (extends SourceService)

```protobuf
service ConfluenceSourceService {
  rpc GetPageComments(ConfluenceGetCommentsRequest) returns (stream Comment);
  rpc GetPageChildren(ConfluenceGetChildrenRequest) returns (stream ItemSummary);
  rpc GetPageAncestors(ConfluenceGetAncestorsRequest) returns (stream ItemSummary);
  rpc ListSpaces(ConfluenceListSpacesRequest) returns (stream ConfluenceSpace);
  rpc GetLinkedPages(ConfluenceLinkedPagesRequest) returns (stream ItemSummary);
  rpc GetPagesByLabel(ConfluenceLabelRequest) returns (stream ItemSummary);
}

message ConfluencePage {
  int32 id = 1;
  string space_key = 2;
  string title = 3;
  string body_plain = 4;
  string body_storage = 5;                  // Original Confluence storage format
  string labels = 6;
  int32 version_number = 7;
  string last_modified_by = 8;
  google.protobuf.Timestamp last_modified_at = 9;
  string url = 10;
  int32 parent_id = 11;
}

message ConfluenceSpace {
  string key = 1;
  string name = 2;
  string description = 3;
  string url = 4;
  int32 page_count = 5;
}
```

### `github.proto` (extends SourceService)

```protobuf
service GitHubSourceService {
  rpc GetIssueComments(GitHubGetCommentsRequest) returns (stream Comment);
  rpc GetPullRequestDetails(GitHubGetPRRequest) returns (GitHubPullRequest);
  rpc GetRelatedCommits(GitHubGetCommitsRequest) returns (stream GitHubCommit);
  rpc ListRepositories(GitHubListReposRequest) returns (stream GitHubRepo);
  rpc ListByLabel(GitHubLabelRequest) returns (stream ItemSummary);
  rpc ListByMilestone(GitHubMilestoneRequest) returns (stream ItemSummary);
  rpc QueryByArtifact(GitHubArtifactQueryRequest) returns (stream GitHubCommit);
}

// Query commits/PRs scoped to a FHIR artifact, page, or element.
// Provide exactly one of artifact_key, artifact_id, page_key, or element_path.
message GitHubArtifactQueryRequest {
  string repo = 1;                           // e.g. "HL7/fhir" (required)
  string artifact_key = 2;                   // e.g. "Patient", "Bundle" (broad match)
  string artifact_id = 3;                    // e.g. "StructureDefinition/Patient" (specific file)
  string page_key = 4;                       // e.g. "subscriptions", "workflow"
  string element_path = 5;                   // e.g. "Patient.name", "Attachment.url"
  bool include_prs = 6;                      // Also return associated PRs
  google.protobuf.Timestamp after = 7;
  google.protobuf.Timestamp before = 8;
  int32 limit = 9;
}

message GitHubIssue {
  int32 id = 1;
  string repo_full_name = 2;               // "HL7/fhir"
  int32 number = 3;
  bool is_pull_request = 4;
  string title = 5;
  string body = 6;
  string state = 7;
  string author = 8;
  string labels = 9;
  string assignees = 10;
  string milestone = 11;
  google.protobuf.Timestamp created_at = 12;
  google.protobuf.Timestamp updated_at = 13;
  google.protobuf.Timestamp closed_at = 14;
  string url = 15;
  // PR-specific
  string merge_state = 16;
  string head_branch = 17;
  string base_branch = 18;
}

message GitHubPullRequest {
  GitHubIssue issue = 1;
  int32 additions = 2;
  int32 deletions = 3;
  int32 changed_files = 4;
  bool merged = 5;
  string merge_commit_sha = 6;
}

message GitHubCommit {
  string sha = 1;
  string message = 2;
  string author = 3;
  google.protobuf.Timestamp date = 4;
  string url = 5;
}

message GitHubRepo {
  string full_name = 1;
  string description = 2;
  int32 issue_count = 3;
  int32 pr_count = 4;
  string url = 5;                           // Link to repository on GitHub
}
```

---

## HTTP API (Orchestrator)

The orchestrator exposes an HTTP/JSON API that mirrors the gRPC contract.
Per the *Triple Interface* principle (see [01-overview](01-overview.md)),
every capability below is also available through the MCP server and CLI —
the HTTP API is one of three equivalent external interfaces.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/v1/search?q={query}&sources={csv}&limit={n}` | Unified search |
| `GET` | `/api/v1/related/{source}/{id}?limit={n}` | Find related items |
| `GET` | `/api/v1/xref/{source}/{id}?direction={dir}` | Cross-references |
| `POST` | `/api/v1/ingest/trigger` | Trigger sync (body: sources, type) |
| `GET` | `/api/v1/services` | Service health/status |
| `POST` | `/api/v1/xref/scan` | Trigger cross-reference scan |
| `GET` | `/api/v1/stats` | Aggregate statistics |
| `POST` | `/api/v1/jira/query` | Jira structured query (body: `JiraQueryRequest` filters) |
| `POST` | `/api/v1/zulip/query` | Zulip structured query (body: `ZulipQueryRequest` filters) |
| `POST` | `/api/v1/github/artifact-query` | GitHub artifact/page/element query (body: `GitHubArtifactQueryRequest`) |

### Source Service HTTP APIs (Standalone Use)

Each source service also exposes a lightweight HTTP API for direct access:

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/v1/search?q={query}&limit={n}` | Search within this source |
| `GET` | `/api/v1/items/{id}` | Get full item details |
| `GET` | `/api/v1/items?limit={n}&offset={n}` | List items |
| `GET` | `/api/v1/items/{id}/related` | Related items within this source |
| `GET` | `/api/v1/items/{id}/snapshot` | Rendered markdown snapshot |
| `POST` | `/api/v1/ingest` | Trigger ingestion |
| `GET` | `/api/v1/status` | Ingestion status |
| `POST` | `/api/v1/rebuild` | Rebuild from cache |
| `GET` | `/api/v1/stats` | Source statistics |
