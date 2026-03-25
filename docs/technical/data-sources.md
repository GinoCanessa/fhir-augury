# Data Sources

This document describes the v2 microservices source architecture, each source
service's implementation details, and guidance for adding new data sources.

## Architecture Overview

In v2, each data source is an **independent microservice** with its own gRPC
server, SQLite database, FTS5 indexes, file-system cache, ingestion pipeline,
and scheduled sync worker. The Orchestrator (`:5150/5151`) coordinates all
source services, aggregating search results and managing cross-references.

```
Orchestrator (:5150 HTTP / :5151 gRPC)
├── Source.Jira       (:5160 HTTP / :5161 gRPC)
├── Source.Zulip      (:5170 HTTP / :5171 gRPC)
├── Source.Confluence  (:5180 HTTP / :5181 gRPC)
└── Source.GitHub     (:5190 HTTP / :5191 gRPC)
```

## Common gRPC Contract — `SourceService`

Every source service implements the `SourceService` gRPC service defined in
`protos/`. This provides a uniform contract for the Orchestrator:

| RPC | Description |
|-----|-------------|
| `Search` | FTS5 full-text search within the source, returns scored results with snippets |
| `GetItem` | Retrieve a single item by ID |
| `ListItems` | List items with optional filters and pagination |
| `GetRelated` | Find related items within the source |
| `GetSnapshot` | Point-in-time snapshot of an item |
| `GetContent` | Retrieve full content for an item |
| `StreamSearchableText` | Server-streaming RPC — streams all searchable text for cross-reference scanning |
| `TriggerIngestion` | Trigger a full or incremental ingestion run |
| `GetIngestionStatus` | Get current ingestion status |
| `GetStats` | Return service statistics (item counts, DB size, last sync) |
| `RebuildFromCache` | Rebuild the database from the file-system cache |
| `HealthCheck` | Liveness/readiness probe |

## Source-Specific gRPC Services

Each source also exposes a source-specific gRPC service for domain queries:

### `JiraService`

| RPC | Description |
|-----|-------------|
| `GetIssueComments` | Comments for a specific issue |
| `GetIssueLinks` | Cross-reference links for an issue |
| `ListByWorkGroup` | Issues filtered by HL7 work group |
| `ListBySpecification` | Issues filtered by specification |
| `QueryIssues` | Arbitrary JQL-like query |
| `ListSpecArtifacts` | Specification artifact listing |
| `GetIssueNumbers` | Bulk issue number lookup |
| `GetIssueSnapshot` | Detailed issue snapshot |

### `ZulipService`

| RPC | Description |
|-----|-------------|
| `GetThread` | Full message thread for a topic |
| `ListStreams` | Available streams |
| `GetStream` | Get a single stream by ID |
| `UpdateStream` | Update stream properties (e.g., IncludeStream flag) |
| `ListTopics` | Topics within a stream |
| `GetMessagesByUser` | Messages filtered by sender |
| `QueryMessages` | Arbitrary message query |
| `GetThreadSnapshot` | Thread snapshot with context |

### `ConfluenceService`

| RPC | Description |
|-----|-------------|
| `GetPageComments` | Comments on a page |
| `GetPageChildren` | Child pages in the hierarchy |
| `GetPageAncestors` | Ancestor pages up to root |
| `ListSpaces` | Available spaces |
| `GetLinkedPages` | Pages linked from a given page |
| `GetPagesByLabel` | Pages filtered by label |
| `GetPageSnapshot` | Full page snapshot |

### `GitHubService`

| RPC | Description |
|-----|-------------|
| `GetIssueComments` | Comments on an issue/PR |
| `GetPullRequestDetails` | PR-specific details (branches, merge state) |
| `GetRelatedCommits` | Commits referencing an issue |
| `GetPullRequestForCommit` | Find the PR associated with a commit |
| `GetCommitsForPullRequest` | List commits in a PR |
| `SearchCommits` | Search commit messages |
| `GetJiraReferences` | Jira keys found in issues/PRs |
| `ListRepositories` | Tracked repositories |
| `ListByLabel` | Issues/PRs by label |
| `ListByMilestone` | Issues/PRs by milestone |
| `QueryByArtifact` | Issues/PRs referencing a FHIR artifact |
| `GetIssueSnapshot` | Issue/PR snapshot |

## Per-Source Service Architecture

Each source service follows the same internal pattern:

```
Source Service (gRPC server)
├── Database            — Source-specific SourceDatabase subclass (own SQLite file)
├── Ingestion Pipeline  — Fetches from upstream API, upserts into DB
├── FTS5 Index          — Content-synced FTS5 virtual tables with auto-triggers
├── BM25 Index          — Pre-computed keyword index (index_keywords, index_corpus, index_doc_stats)
├── Sync State          — sync_state + ingestion_log tables for scheduling
├── Cache               — FileSystemResponseCache for raw API responses
├── gRPC Services       — SourceService + source-specific service implementations
└── Scheduled Worker    — Background worker for periodic sync
```

### Shared Infrastructure (FhirAugury.Common)

The `FhirAugury.Common` shared library compiles the proto definitions and
provides:

- **`Database/SourceDatabase`** — Abstract base class for per-source SQLite
  databases. Opens with WAL mode + performance pragmas. Provides
  `InitializeSchema()`, `ExecuteInBatches()` (savepoints),
  `ExecuteInTransaction()`, `CreateFts5Table()` (auto-generates content-sync
  triggers), `RebuildFts5()`, `GetDatabaseSizeBytes()`, `CheckIntegrity()`.
- **`Database/AuxiliaryDatabase`** — Read-only SQLite loader for optional
  external stop words, lemmatization data, and FHIR vocabulary. Loads data once
  at startup into frozen collections (`FrozenSet`/`FrozenDictionary`). Provides
  `StopWords`, `Lemmatizer`, `FhirResourceNames`, and `FhirOperations`
  properties. Falls back to hardcoded defaults when database files are not
  configured.
- **`Configuration/`** — Shared configuration types including
  `AuxiliaryDatabaseOptions` (paths to auxiliary/FHIR spec DBs),
  `Bm25Options` (configurable K1/B/UseLemmatization/FtsTokenizer parameters
  per service), and `DictionaryDatabaseOptions` (compiled dictionary builder).
- **`Caching/`** — `IResponseCache`, `FileSystemResponseCache` (atomic writes
  via temp + move), `CacheMode` enum (`Disabled`, `WriteThrough`, `CacheOnly`,
  `WriteOnly`), `CacheFileNaming` (`_WeekOf_`/`DayOf_` batch naming).
- **`Text/`** — `CrossRefPatterns` (regex patterns for Jira keys, Jira/Zulip/
  GitHub/Confluence URLs, GitHub short refs `HL7/repo#123`),
  `FhirVocabulary` (100+ FHIR resource names, 30+ operations; extensible via
  auxiliary DB using `CreateMergedResourceNames()`/`CreateMergedOperations()`),
  `KeywordClassifier` (word/stop_word/fhir_path/fhir_operation),
  `StopWords` (200+ English; extensible via auxiliary DB using
  `CreateMergedSet()`), `TextSanitizer` (strip HTML/Markdown, NFC
  Unicode normalization), `Tokenizer` (FHIR paths/operations first, then
  strip URLs/emails/code blocks, then words), `TokenCounter` (shared
  count-and-classify with stop-word filtering and lemmatization),
  `Lemmatizer` (inflection→lemma normalization with `Empty` singleton
  fallback).
- **`Grpc/`** — `GrpcClientExtensions`, `GrpcErrorMapper`.
- **`HttpRetryHelper`** — Exponential backoff ±20% jitter, max 30s delay,
  respects `Retry-After` headers. Fails immediately on 401/403.

## Source Service Details

### Jira (`Source.Jira` — `:5160/5161`)

| Property | Value |
|----------|-------|
| **Default target** | `https://jira.hl7.org` |
| **Auth methods** | Session cookie or API token (HTTP Basic) |
| **Data types** | Issues + comments |
| **Database** | `jira.db` |
| **gRPC services** | `SourceService`, `JiraService` |
| **Page size** | 100 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

- **Cookie mode** (default): Raw session cookie sent as the `cookie` header
- **ApiToken mode**: HTTP Basic Auth with `email:token`

Auth mode is auto-selected: if both `ApiToken` and `Email` are provided, ApiToken
mode is used; otherwise Cookie mode.

**Data model:**

- `JiraIssueRecord` — Issue key, title, description, status, priority, 32+
  fields including HL7 custom fields and parsed vote components
- `JiraCommentRecord` — Comment author, body, body plain text, timestamps (IssueKey FK)

16 HL7-specific custom fields are mapped to domain properties (e.g.,
`customfield_11302` → Specification, `customfield_11400` → WorkGroup).

**Database tables:** `jira_issues` (Key unique, 32+ columns),
`jira_comments` (IssueKey FK), `jira_issue_related` (related issue keys),
`jira_issue_labels` (issue-to-label junction), `jira_index_workgroups`,
`jira_index_specifications`, `jira_index_ballots`, `jira_index_labels`,
`jira_index_types`, `jira_index_priorities`, `jira_index_statuses`,
`jira_index_resolutions` (index/lookup tables), `jira_issues_fts` (FTS5),
`jira_comments_fts` (FTS5), `index_keywords`, `index_corpus`,
`index_doc_stats`, `sync_state`, `ingestion_log`.

**Incremental sync:** Appends `AND updated >= '{since}'` to the JQL query.

**Pagination:** Offset-based (`startAt` vs `total`).

**Special feature:** Also supports XML RSS export parsing via `JiraXmlParser`.

---

### Zulip (`Source.Zulip` — `:5170/5171`)

| Property | Value |
|----------|-------|
| **Default target** | `https://chat.fhir.org` |
| **Auth methods** | HTTP Basic (email + API key), `.zuliprc` file |
| **Data types** | Streams + messages |
| **Database** | `zulip.db` |
| **gRPC services** | `SourceService`, `ZulipService` |
| **Batch size** | 1000 |
| **HTTP timeout** | 10 minutes |
| **Cache support** | Yes |

**Authentication:**

HTTP Basic Auth with `email:apikey`. Credentials can come from:
1. Direct `Email` and `ApiKey` options
2. A `.zuliprc` file (standard Zulip bot credential format)

The `OnlyWebPublic` flag restricts ingestion to web-public streams.
The `ExcludedStreamIds` configuration option allows excluding specific streams
from ingestion. During stream sync, excluded streams have their `IncludeStream`
column set to `0` in the `zulip_streams` table; only streams with
`IncludeStream = 1` are ingested for messages. The `IncludeStream` flag can
also be toggled per-stream via the `UpdateStream` gRPC RPC.

**Data model:**

- `ZulipStreamRecord` — Stream ID, name, description, web-public flag
- `ZulipMessageRecord` — Message ID, stream, topic, sender, plain text content,
  timestamp, reactions

HTML content is stripped to plain text via `TextSanitizer.StripHtml`.

**Database tables:** `zulip_streams` (ZulipStreamId unique),
`zulip_messages` (ZulipMessageId unique, StreamId FK), `zulip_messages_fts`
(FTS5), `index_keywords`, `index_corpus`, `index_doc_stats`, `sync_state`,
`ingestion_log`.

**Incremental sync:** Cursor-based using `sync_state` — stores the last synced
message ID per stream. Sets `anchor = lastId + 1` and fetches forward.

**Pagination:** Anchor-based (`anchor`, `num_before=0`, `num_after=batchSize`).
Continues until `found_newest` is true.

---

### Confluence (`Source.Confluence` — `:5180/5181`)

| Property | Value |
|----------|-------|
| **Default target** | `https://confluence.hl7.org` |
| **Auth methods** | Session cookie or HTTP Basic (username + API token) |
| **Data types** | Spaces + pages + comments |
| **Database** | `confluence.db` |
| **gRPC services** | `SourceService`, `ConfluenceService` |
| **Page size** | 25 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

- **Cookie mode** (default): Session cookie in the `cookie` header
- **Basic mode**: HTTP Basic with `username:token`

**Data model:**

- `ConfluenceSpaceRecord` — Space key, name, description, URL
- `ConfluencePageRecord` — Page ID, space key, title, parent ID, body
  (storage format + plain text), labels, version, URL
- `ConfluenceCommentRecord` — Author, date, body as plain text (PageId FK)

Body content is converted from Confluence storage format (XHTML) to plain text
by `ConfluenceContentParser`, which handles macros, images, and attachments.

**Database tables:** `confluence_spaces` (Key unique), `confluence_pages`
(ConfluenceId unique, SpaceKey), `confluence_comments` (PageId FK),
`confluence_pages_fts` (FTS5), `index_keywords`, `index_corpus`,
`index_doc_stats`, `sync_state`, `ingestion_log`.

**Incremental sync:** Uses Confluence CQL:
`lastModified >= "{since}" AND space in ("FHIR","FHIRI","SOA") AND type = page`

**Pagination:** Offset-based. Continues while `_links.next` exists.

**Default spaces:** `["FHIR", "FHIRI", "SOA"]`

---

### GitHub (`Source.GitHub` — `:5190/5191`)

| Property | Value |
|----------|-------|
| **Default target** | `https://api.github.com` |
| **Auth methods** | Bearer token (Personal Access Token) |
| **Data types** | Repositories + issues/PRs + comments |
| **Database** | `github.db` |
| **gRPC services** | `SourceService`, `GitHubService` |
| **Page size** | 100 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

Bearer token via PAT. Without a token, requests are unauthenticated (60 req/hr
vs 5,000 with a token). The service includes rate limiting that monitors
`X-RateLimit-Remaining` and `X-RateLimit-Reset` headers.

**Data model:**

- `GitHubRepoRecord` — Full name, owner, name, description
- `GitHubIssueRecord` — UniqueKey (`owner/repo#number`), number, isPullRequest
  flag, title, body, state, author, labels, assignees, milestone, merge state
- `GitHubCommentRecord` — Author, date, body, IsReviewComment flag (IssueId FK)

The GitHub Issues API returns both issues and PRs; the mapper detects PRs via
the `pull_request` field.

**Database tables:** `github_repos` (FullName unique), `github_issues`
(UniqueKey unique, IsPullRequest, RepoFullName), `github_comments` (IssueId FK,
IsReviewComment), `github_issues_fts` (FTS5), `github_comments_fts` (FTS5),
`index_keywords`, `index_corpus`, `index_doc_stats`, `sync_state`,
`ingestion_log`.

**Incremental sync:** Uses GitHub's `since` query parameter.

**Pagination:** Page-based. Continues while returned array length ≥ PageSize.

**Default repositories:** `["HL7/fhir"]`

---

## Adding a New Data Source

To add a new data source in the v2 architecture, follow these steps:

### 1. Create the Source Service Project

Create a new project `Source.NewSource` as an independent microservice. The
project should reference `FhirAugury.Common` for shared infrastructure.

### 2. Define Proto Definitions

In `protos/`, add the gRPC service definitions:

- Implement the common `SourceService` RPCs (Search, GetItem, ListItems, etc.)
- Define a source-specific service (e.g., `NewSourceService`) with
  domain-specific RPCs

### 3. Define the Database Schema

Create record classes decorated with `cslightdbgen.sqlitegen` attributes:

```csharp
[LdgSQLiteTable("new_source_items")]
public partial record class NewSourceItemRecord
{
    [LdgSQLiteKey]
    public long Id { get; set; }

    [LdgSQLiteUnique]
    public string UniqueId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    // Add fields as needed...
}
```

Extend `SourceDatabase` to create a source-specific database class that calls
`CreateFts5Table()` for content-synced FTS5 virtual tables.

### 4. Implement the Ingestion Pipeline

Build the ingestion pipeline within the source service:

- API client with `HttpRetryHelper` for transient failure handling
- Auth handler (`DelegatingHandler`) for source-specific authentication
- Mapper/parser to convert API responses to record types
- `FileSystemResponseCache` for caching raw responses
- Scheduled worker for periodic sync using `sync_state` and `ingestion_log`

### 5. Implement the gRPC Services

Implement both the common `SourceService` and the source-specific gRPC service:

- `Search`: FTS5 MATCH query with BM25 scoring and snippet extraction
- `StreamSearchableText`: Stream all content for cross-reference scanning
- `TriggerIngestion`: Full and incremental ingestion support
- Source-specific RPCs for domain queries

### 6. Register in Docker and Orchestrator

- Add the service to `docker-compose.yml` with appropriate ports
- Register the source in the Orchestrator so it is included in fan-out search,
  cross-reference scanning, and aggregated results
- Add cross-reference patterns to `CrossRefPatterns` in `FhirAugury.Common`
  if the new source has identifiable link patterns

### 7. Add MCP Tools

Add tool methods in the appropriate MCP tool classes (Search, Retrieval,
Listing, Snapshot) to expose the new source through the MCP interface.

## Comparison Matrix

| Feature | Jira | Zulip | Confluence | GitHub |
|---------|------|-------|------------|--------|
| **Ports** | 5160/5161 | 5170/5171 | 5180/5181 | 5190/5191 |
| **Auth methods** | Cookie or Basic | Basic, `.zuliprc` | Cookie or Basic | Bearer (PAT) |
| **Incremental strategy** | JQL time filter | Cursor-based (msg ID) | CQL time filter | `since` param |
| **Pagination** | Offset | Anchor | Offset | Page number |
| **Rate limiting** | Retry only | Retry only | Retry only | Dedicated limiter |
| **Cache support** | ✅ | ✅ | ✅ | ✅ |
| **Default page/batch** | 100 | 1000 | 25 | 100 |
| **HTTP timeout** | 5 min | 10 min | 5 min | 5 min |
| **FTS5 tables** | issues + comments | messages | pages | issues + comments |
| **Own database** | `jira.db` | `zulip.db` | `confluence.db` | `github.db` |
