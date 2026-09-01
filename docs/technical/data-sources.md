# Data Sources

This document describes the v2 microservices source architecture, each source
service's implementation details, and guidance for adding new data sources.

## Architecture Overview

In v2, each data source is an **independent microservice** with its own HTTP
API, SQLite database, FTS5 indexes, file-system cache, ingestion pipeline,
and scheduled sync worker. The Orchestrator (`:5150`) coordinates all
source services, aggregating search results and managing cross-references.

```
Orchestrator (:5150 HTTP)
├── Source.Jira       (:5160 HTTP)
├── Source.Zulip      (:5170 HTTP)
├── Source.Confluence  (:5180 HTTP)
├── Source.GitHub     (:5190 HTTP)
└── Source.Fhir       (:5195 HTTP, read-only spec reference)
```

The first four sources ingest and index an external upstream (Jira, Zulip,
Confluence, GitHub). `Source.Fhir` is a fifth, **read-only** source: it serves
FHIR specification reference data (StructureDefinitions and other canonical
resources) into unified search but has no external ingestion pipeline or sync
worker. The processors (`:5171`–`:5174`) are covered in the
[Processors Runbook](processors.md), not here.

## Common HTTP API Contract

Every source service implements a common set of HTTP API endpoints defined by
shared contract classes in `FhirAugury.Common/Api/`. This provides a uniform
contract for the Orchestrator:

| Endpoint | Description |
|----------|-------------|
| `Search` | FTS5 full-text search within the source, returns scored results with snippets |
| `GetItem` | Retrieve a single item by ID |
| `ListItems` | List items with optional filters and pagination |
| `GetRelated` | Find related items within the source |
| `GetSnapshot` | Point-in-time snapshot of an item |
| `GetContent` | Retrieve full content for an item |
| `StreamSearchableText` | Streams all searchable text for cross-reference scanning |
| `TriggerIngestion` | Trigger a full or incremental ingestion run |
| `GetIngestionStatus` | Get current ingestion status |
| `GetStats` | Return service statistics (item counts, DB size, last sync) |
| `RebuildFromCache` | Rebuild the database from the file-system cache |
| `GetItemCrossReferences` | Get cross-references for a specific item |
| `NotifyPeerIngestionComplete` | Notify this source that a peer completed ingestion (triggers xref re-scan). The orchestrator side of this contract is `POST /api/v1/notify-ingestion`; both halves carry the `ingestion-notifications` OpenAPI tag. See [`docs/user/api-reference.md`](../user/api-reference.md#internal-notification). |
| `RebuildIndex` | Rebuild specific indexes (BM25, FTS, cross-refs, lookup tables, etc.) |
| `HealthCheck` | Liveness/readiness probe |

## Source list-filter convention

All Source services use the [null-as-default, empty-as-explicit-all convention](../source-filter-conventions.md) for list-shaped string filters and ingestion selection lists. Jira query/local-processing filters, Zulip `StreamNames`/`SenderNames`, content-search `sources`, GitHub repository/file-content lists, and Confluence `Spaces` all follow this rule.

## Source-Specific HTTP API Endpoints

Each source also exposes source-specific HTTP API endpoints for domain queries:

### Jira Endpoints

| Endpoint | Description |
|-----|-------------|
| `GetIssueComments` | Comments for a specific issue |
| `GetIssueLinks` | Cross-reference links for an issue |
| `ListByWorkGroup` | Issues filtered by HL7 work group |
| `ListBySpecification` | Issues filtered by specification |
| `QueryIssues` | Arbitrary JQL-like query |
| `GetIssueNumbers` | Bulk issue number lookup |
| `GetIssueSnapshot` | Detailed issue snapshot |

### Zulip Endpoints

| Endpoint | Description |
|-----|-------------|
| `GetThread` | Full message thread for a topic |
| `ListStreams` | Available streams |
| `GetStream` | Get a single stream by ID |
| `UpdateStream` | Update stream properties (e.g., IncludeStream flag) |
| `ListTopics` | Topics within a stream |
| `GetMessagesByUser` | Messages filtered by sender |
| `QueryMessages` | Arbitrary message query |
| `GetThreadSnapshot` | Thread snapshot with context |

### Confluence Endpoints

| Endpoint | Description |
|-----|-------------|
| `GetPageComments` | Comments on a page |
| `GetPageChildren` | Child pages in the hierarchy |
| `GetPageAncestors` | Ancestor pages up to root |
| `ListSpaces` | Available spaces |
| `GetLinkedPages` | Pages linked from a given page |
| `GetPagesByLabel` | Pages filtered by label |
| `GetPageSnapshot` | Full page snapshot |

### GitHub Endpoints

| Endpoint | Description |
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
Source Service (HTTP API)
├── Database            — Source-specific SourceDatabase subclass (own SQLite file)
├── Ingestion Pipeline  — Fetches from upstream API, upserts into DB
├── FTS5 Index          — Content-synced FTS5 virtual tables with auto-triggers
├── BM25 Index          — Pre-computed keyword index (index_keywords, index_corpus, index_doc_stats)
├── Sync State          — sync_state + ingestion_log tables for scheduling
├── Cache               — FileSystemResponseCache for raw API responses
├── HTTP API Controllers — Common + source-specific endpoint implementations
└── Scheduled Worker    — Background worker for periodic sync
```

### Shared Infrastructure (FhirAugury.Common)

The `FhirAugury.Common` shared library provides shared API contract classes and
reusable infrastructure:

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
  `WriteOnly`), `CacheFileNaming` (flat `YYYYMMDD-YYYYMMDD-NNN.ext` range naming;
  single-day files use `start == end`). Legacy `DayOf_`/`_WeekOf_` files are
  rewritten in-place by `CacheFileNameMigrator` on first ingestion per process.
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
- **`Api/`** — Shared HTTP API contracts: `SearchContracts`, `ItemContracts`,
  `CrossReferenceContracts`, `IngestionContracts`, `ServiceContracts`,
  `ContentFormats`.
- **`HttpRetryHelper`** — Exponential backoff ±20% jitter, max 30s delay,
  respects `Retry-After` headers. Fails immediately on 401/403.

## Source Service Details

### Jira (`Source.Jira` — `:5160`)

| Property | Value |
|----------|-------|
| **Default target** | `https://jira.hl7.org` |
| **Auth methods** | Session cookie or API token (HTTP Basic) |
| **Data types** | Issues + comments |
| **Database** | `jira.db` |
| **API** | HTTP API controllers |
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

### Zulip (`Source.Zulip` — `:5170`)

| Property | Value |
|----------|-------|
| **Default target** | `https://chat.fhir.org` |
| **Auth methods** | HTTP Basic (email + API key), `.zuliprc` file |
| **Data types** | Streams + messages |
| **Database** | `zulip.db` |
| **API** | HTTP API controllers |
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
also be toggled per-stream via the `UpdateStream` HTTP API endpoint.

**Data model:**

- `ZulipStreamRecord` — Stream ID, name, description, web-public flag, baseline value (0–10, default 5, used as a search score multiplier)
- `ZulipMessageRecord` — Message ID, stream, topic, sender, plain text content,
  timestamp, reactions

HTML content is stripped to plain text via `TextSanitizer.StripHtml`.

**Database tables:** `zulip_streams` (ZulipStreamId unique),
`zulip_messages` (ZulipMessageId unique, StreamId FK), `zulip_messages_fts`
(FTS5), `zulip_thread_tickets` (thread→Jira key aggregations with reference counts),
`index_keywords`, `index_corpus`, `index_doc_stats`, `sync_state`,
`ingestion_log`.

**Jira ticket indexing:** After each ingestion (full, incremental, or cache
rebuild), the `ZulipTicketIndexer` scans all messages for Jira ticket references
(e.g., `FHIR-43499`, Jira URLs) and populates the thread-ticket link table.
thread-ticket link tables. A standalone re-index can be triggered on startup via
the `ReindexTicketsOnStartup` configuration option without requiring a full
cache rebuild.

**Incremental sync:** Cursor-based using `sync_state` — stores the last synced
message ID per stream. Sets `anchor = lastId + 1` and fetches forward.

**Pagination:** Anchor-based (`anchor`, `num_before=0`, `num_after=batchSize`).
Continues until `found_newest` is true.

---

### Confluence (`Source.Confluence` — `:5180`)

| Property | Value |
|----------|-------|
| **Default target** | `https://confluence.hl7.org` |
| **Auth methods** | Session cookie or HTTP Basic (username + API token) |
| **Data types** | Spaces + pages + comments + attachments (metadata and bytes) |
| **Database** | `confluence.db` |
| **API** | HTTP API controllers |
| **Page size** | 25 (fill); 200 (`SweepPageSize`, body-less sweep) |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

- **Cookie mode** (default): Session cookie in the `cookie` header
- **Basic mode**: HTTP Basic with `username:token`

HL7's Confluence answers the read surface **anonymously**, so a credential is
about *coverage* (restricted content) rather than access. It also sits behind an
AWS WAF that rejects any non-browser-shaped `User-Agent` with `405`. See
[Confluence API notes](confluence-api-notes.md).

**Data model:**

- `ConfluenceSpaceRecord` — Space key, name, description, URL
- `ConfluencePageRecord` — Page ID, space key, title, status
  (`current` / `archived`), parent ID, body (storage format + plain text),
  labels, version, URL
- `ConfluenceCommentRecord` — Author, date, body as plain text (PageId FK)
- `ConfluenceAttachmentRecord` — File name, media type, size, version,
  download URL, and the cache key of the downloaded bytes (PageId FK)

Body content is converted from Confluence storage format (XHTML) to plain text
by `ConfluenceContentParser`, which handles macros, images, and attachments.

**Database tables:** `confluence_spaces` (Key unique), `confluence_pages`
(ConfluenceId unique, SpaceKey, Status), `confluence_comments` (PageId FK),
`confluence_attachments` (ConfluenceAttachmentId unique, PageId FK),
`confluence_pages_fts` (FTS5), `index_keywords`, `index_corpus`,
`index_doc_stats`, `sync_state`, `ingestion_log`.

**Incremental sync: manifest reconciliation.** There is no watermark and no
full/incremental split. Acquisition splits into a cheap, body-less **sweep**
that enumerates everything that *should* exist in a space into a per-space
manifest stored in the cache, and an expensive **fill** that fetches only what
the manifest says is missing or stale. Completeness is therefore a pure function
of `(manifest, cache files)`, computable offline at any moment — see
`GET /api/v1/cache/reconcile-report`.

A manifest is written only when that space's sweep ran to exhaustion, so a
partially failed run can never advance a watermark past pages it missed. Items
the manifest no longer names are **tombstoned** by moving their cache file under
`_vanished/`, never deleted, because absence can also mean "not visible to the
credential this run used". Replay is manifest-driven and is the only path that
writes the database.

**Sweep and fill:** each space is swept as three body-less streams —
`GET /rest/api/content?spaceKey=…&type=page&expand=version` for pages, and CQL
`type = comment` / `type = attachment` searches with
`expand=version,container[,metadata]` for the rest. All three paginate through
`_links.next` to exhaustion. The fill is one
`GET /rest/api/content/{id}?expand=<profile>` per item, so every unit is
independently retryable.

**Default spaces:** every non-archived global space on the instance (~140).
Configuration may still restrict the set; `Spaces = []` tracks nothing.

**Attachments:** metadata is always swept, cached, replayed and indexed. Bytes
are downloaded unless a blob exceeds `AttachmentMaxBytes` (default 100 MB), in
which case the row still records the size and download URL with a null cache
key and the space reports `complete_with_skips`.

---

### GitHub (`Source.GitHub` — `:5190`)

| Property | Value |
|----------|-------|
| **Default target** | `https://api.github.com` |
| **Auth methods** | Bearer token (Personal Access Token) |
| **Data types** | Repositories + issues/PRs + comments |
| **Database** | `github.db` |
| **API** | HTTP API controllers |
| **Page size** | 100 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

Bearer token via PAT. Without a token, requests are unauthenticated (60 req/hr
vs 5,000 with a token). The service includes rate limiting that monitors
`X-RateLimit-Remaining` and `X-RateLimit-Reset` headers.

**Data Provider:**

The GitHub source supports two data provider implementations selected via the
`Provider` setting:

- **`rest`** (code default) — Uses the GitHub REST API directly
- **`gh-cli`** (appsettings default) — Uses the `gh` CLI tool, which handles
  authentication automatically and supports GitHub Enterprise

The `gh-cli` provider is configured via the `GhCli` section with settings for
executable path, query limits, hostname, process timeout, process concurrency,
and the history-backfill controls (see
[History backfill](#history-backfill) below).

**Data model:**

- `GitHubRepoRecord` — Full name, owner, name, description, default branch
- `GitHubIssueRecord` — UniqueKey (`owner/repo#number`), number, isPullRequest
  flag, title, body, state, author, labels, assignees, milestone, merge state
- `GitHubCommentRecord` — Author, date, body, IsReviewComment flag (IssueId FK),
  plus a stable GitHub-native identity (`ExternalId` + `CommentKind` ∈
  `issue`/`review`/`review_comment`) used to dedup across re-ingestion

The GitHub Issues API returns both issues and PRs; the mapper detects PRs via
the `pull_request` field. The items surface treats `pr` as a first-class
*content type* derived from `IsPullRequest`: `GET /api/v1/items?pullRequest=true`
returns only PRs, `?pullRequest=false` only non-PR issues, and omitting the
parameter returns both. Every items list/get/snapshot/content response carries a
`content_type` of `pr` or `issue` accordingly. The `pullRequest` filter is
mirrored across the orchestrator proxy, CLI (`github-items list`), MCP
(`ListGitHubItems`), and the DevUI API catalog.

During PR ingestion the source also populates `github_commit_pr_links` (the
commit→PR mapping, written from the combined `gh pr view` detail fetch below with
replace-on-resync semantics so force-pushes don't leave stale links) and ingests
inline (line-anchored) PR review-thread comments via
`gh api repos/{repo}/pulls/{n}/comments`, so the full PR conversation flows
through the xref/BM25 pipeline. Commit responses (`MapCommitToJson`) expose a
`prs` array of applying PRs plus a deterministic `primaryPr` (merged →
base-branch-is-repo-default → lowest PR number).

Per-PR detail (comments, reviews, commits, `baseRefName`, `mergedAt`) is fetched
in a **single** `gh pr view --json comments,reviews,commits,baseRefName,mergedAt`
call. Each section is applied under its own try/catch, so a malformed section or
a database failure in one cannot discard the other two; a failure lands the PR in
the backfill's pending-retry set (below) for repair on the next pass.

### History backfill

The moving incremental window (`updated:>=`) can never reach a repo's older
history, so each repo gets a one-time full-history backfill. It is resumable and
self-repairing, and its state lives in **two** `sync_state` sub-source namespaces:

| Sub-source | Purpose | `Status` values |
|------------|---------|-----------------|
| `backfill:<repo>` | **Terminal** completion marker. Written only when the repo's corpus is genuinely complete. | `success` |
| `backfill-progress:<repo>` | **In-flight** resume state. Cursor JSON in `LastCursor`. | `partial`, `repair_required` |

Only a `backfill:<repo>` row at `Status = "success"` gates a repo out of the
needs-backfill set. Keeping partial progress under a *separate* prefix is a
rollback-safety choice: an older binary matches only on `backfill:` and treats
any such row as complete, so a partial row written there would silently skip the
backfill forever. After a revert, progress rows are simply ignored and the
backfill re-runs from scratch — the pre-change behavior — so no manual database
cleanup is required.

`GitHubSyncStateReader.GetMostRecentOperational` allowlists
`["incremental", "full", "rebuild"]`, so neither prefix can corrupt the
incremental window or surface as the reported "last sync".

**Watermark and pending retries.** Each phase (issues, then PRs) is materialized
and sorted **descending** by item number, so a single watermark describes
everything already done: every item numbered at or above `IssuesCompletedAbove` /
`PrsCompletedAbove` has been fully processed *except* those listed in
`PendingRetry`. The watermark advances only past items whose detail work returned
cleanly, is committed in a `finally` so an interrupted item is never counted as
done, and only ever descends — so re-walking an already-completed prefix cannot
regress it. Items whose detail fetch failed go into `PendingRetry` and are
re-attempted on the next pass; the set is capped at 1,000 entries, and on
overflow the watermark freezes rather than skipping unrecorded work.

A phase is marked complete only when it enumerated to exhaustion, was not
cancelled, **and** returned fewer items than `GhCli.BackfillLimit`. A count equal
to the limit means the list was truncated and older items are unreachable, so the
phase is left incomplete and a warning is logged.

**Stall valve.** A permanently unfetchable item (a deleted or transferred PR)
would otherwise keep `PendingRetry` non-empty forever. When a pass ends with both
phases exhausted and the retry set no smaller than it started,
`StalledRepairPasses` increments; at `GhCli.BackfillMaxRepairPasses` (default 3)
the repo is marked complete anyway and a warning names the abandoned numbers.

**Cancellation.** Stopping the service mid-backfill is a clean-but-incomplete
outcome, not a failure: the run aborts within one item, logs a single
informational line, checkpoints its progress, and does **not** fall through into
the incremental pass. The next launch resumes below the watermark. Periodic
checkpointing every `GhCli.BackfillCheckpointInterval` items (default 250) bounds
what a hard kill can lose.

**Forcing a re-backfill.** To discard all state for a repo and start over:

```http
POST /api/v1/ingest?type=backfill&repo=<owner>/<name>&force=true
```

`repo` is validated against the configured repository list (it flows into `gh`
argument construction, so free-form values are rejected with `400`). Without
`force`, an explicit repo run *resumes* from the existing cursor rather than
re-fetching. The same parameters are accepted by the fire-and-forget
`POST /api/v1/ingest/trigger`.

**FHIR artifact indexing:**

The GitHub source also clones tracked repositories and parses their file
contents to extract FHIR artifacts using the `FhirAugury.Parsing.Fhir` and
`FhirAugury.Parsing.Fsh` libraries:

- `GitHubStructureDefinitionRecord` — Indexed StructureDefinitions with url,
  kind, derivation, artifact class, elements, work group, maturity level
- `GitHubSdElementRecord` — Differential elements for each StructureDefinition
- `GitHubCanonicalArtifactRecord` — CodeSystem, ValueSet, ConceptMap,
  SearchParameter, etc. with url, version, status, and format (xml/json/fsh)
- `GitHubFileContentRecord` — Indexed file contents from cloned repositories
- `GitHubFileTagRecord` — File tags with weighted categories for search boosting
- `GitHubSpecFileMapRecord` — Mapping between FHIR artifacts and file paths

**Repository categories:** Repositories are organized by category, each with a
specialized ingestion strategy:

| Category | Strategy | Default Repos |
|----------|----------|--------------|
| FhirCore | `FhirCoreStrategy` | `HL7/fhir` |
| Utg | `UtgStrategy` | `HL7/UTG` |
| FhirExtensionsPack | `FhirExtensionsPackStrategy` | `HL7/fhir-extensions` |
| Incubator | `IncubatorStrategy` | (configurable) |
| Ig | `IgStrategy` | (configurable) |

**Database tables:** `github_repos` (FullName unique, DefaultBranch),
`github_issues` (UniqueKey unique, IsPullRequest, RepoFullName), `github_comments`
(IssueId FK, IsReviewComment, ExternalId/CommentKind with a unique index on
`(RepoFullName, CommentKind, ExternalId)` for dedup), `github_commits` (Sha,
RepoFullName, Message, Body, Author, etc.),
`github_commit_files` (CommitSha, FilePath, ChangeType), `github_commit_pr_links`
(CommitSha, PrNumber, RepoFullName — populated on PR ingestion with a unique
index on `(CommitSha, PrNumber, RepoFullName)`), `github_spec_file_map` (RepoFullName, ArtifactKey,
FilePath, MapType), `github_structure_definitions` (Url, Name, Kind, ArtifactClass,
Elements via github_sd_elements), `github_canonical_artifacts` (ResourceType, Url, Name,
Format), `github_file_contents` (RepoFullName, FilePath, ContentText),
`github_file_tags` (RepoFullName, FilePath, TagCategory, Weight),
`github_issues_fts` (FTS5), `github_comments_fts` (FTS5),
`github_commits_fts` (FTS5), `github_file_contents_fts` (FTS5),
`github_structure_definitions_fts` (FTS5), `github_canonical_artifacts_fts` (FTS5),
`index_keywords`, `index_corpus`, `index_doc_stats`,
`sync_state`, `ingestion_log`.

**Incremental sync:** Uses GitHub's `since` query parameter.

**Pagination:** Page-based. Continues while returned array length ≥ PageSize.

**Default repositories:** `HL7/fhir` (FhirCore), `HL7/UTG` (Utg),
`HL7/fhir-extensions` (FhirExtensionsPack), plus configurable Incubator and IG
repositories.

**Cross-reference tables:** Each source database also maintains xref tables for
references found in its content pointing to other sources (e.g., `xref_jira`,
`xref_zulip`, `xref_github`, `xref_confluence`, `xref_fhir_element`). These are
shared record types defined in `FhirAugury.Common.Database.Records` and populated
by shared extractors in `FhirAugury.Common.Indexing`.

For the GitHub source, `xref_jira` rows come from `JiraTicketExtractor`, which
matches prefixed/hashed keys (`FHIR-N`, `J#N`, `UP-N`, `UPSM-N`, …) and Jira
URLs in all content, plus — for commit/issue/comment **prose only** — a
repo-scoped *bare-integer* pass: a standalone number (e.g. `54873`) resolves to
`PROJECT-N` when the repository's category (or a per-repo override) pins a
project key and the number falls within that key's configured numeric range.
File contents are never bare-matched (incidental integers), and a number already
named by a prefixed key is never re-guessed. See the GitHub `BareNumber*` /
`JiraNumberRanges` / `RepoOverrides` settings in
[`docs/configuration.md`](../configuration.md#configuration-options).

---

## Adding a New Data Source

To add a new data source in the v2 architecture, follow these steps:

### 1. Create the Source Service Project

Create a new project `Source.NewSource` as an independent microservice. The
project should reference `FhirAugury.Common` for shared infrastructure.

### 2. Define API Contracts

In `FhirAugury.Common/Api/`, add any new contract classes needed for the source.
The common contracts (`SearchContracts`, `ItemContracts`, etc.) should be
reusable. Add source-specific request/response types if domain-specific
endpoints are needed.

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

### 5. Implement the HTTP API Controllers

Implement HTTP API controllers for both common operations and source-specific
endpoints:

- `Search`: FTS5 MATCH query with BM25 scoring and snippet extraction
- `TriggerIngestion`: Full and incremental ingestion support
- Source-specific endpoints for domain queries

### 6. Register in Docker and Orchestrator

- Add the service to `docker-compose.yml` with appropriate HTTP port
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
| **Ports** | 5160 | 5170 | 5180 | 5190 |
| **Auth methods** | Cookie or Basic | Basic, `.zuliprc` | Cookie or Basic | Bearer (PAT) |
| **Incremental strategy** | JQL time filter | Cursor-based (msg ID) | Manifest reconciliation | `since` param |
| **Pagination** | Offset | Anchor | `_links.next` to exhaustion | Page number |
| **Rate limiting** | Retry only | Retry only | Retry only | Dedicated limiter |
| **Cache support** | ✅ | ✅ | ✅ | ✅ |
| **Default page/batch** | 100 | 1000 | 25 | 100 |
| **HTTP timeout** | 5 min | 10 min | 5 min | 5 min |
| **FTS5 tables** | issues + comments | messages | pages | issues + comments + commits + file contents + structure definitions + canonical artifacts |
| **Own database** | `jira.db` | `zulip.db` | `confluence.db` | `github.db` |
