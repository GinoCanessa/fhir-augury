# Database Schema

This document describes the SQLite database schema used by FHIR Augury v2,
including the per-service database architecture, all tables, FTS5 virtual
tables, indexes, and the source-generated CRUD layer.

## Overview

In the v2 microservices architecture, each stateful service maintains its
**own SQLite database file**. There is no single shared database — data is
distributed across services, and publication creates a separate generated
browser database:

| Service | Database File | Contents |
|---------|---------------|----------|
| **Source.Jira** | `jira.db` | Issues, comments, FTS5, BM25 index, sync state |
| **Source.Zulip** | `zulip.db` | Streams, messages, FTS5, BM25 index, sync state |
| **Source.Confluence** | `confluence.db` | Spaces, pages, comments, attachments, FTS5, BM25 index, sync state |
| **Source.GitHub** | `github.db` | Repos, issues/PRs, comments, FTS5, BM25 index, sync state |
| **Orchestrator** | `orchestrator.db` | Cross-reference links, cross-ref scan state |
| **Preparer** | `processor.jira.fhir.preparer.db` | Private preparation runs, receipts, hydration, grouping, and snapshot source state |
| **Planner** | `processor.jira.fhir.planner.db` | Private planning runs, receipts, grouping, and Applier compatibility state |
| **Discussion publication** | Embedded generated SQLite | Renderer-schema-v1 projection; not durable service state |

Source databases use:

- **WAL mode** — Concurrent readers alongside a single writer
- **Source-generated CRUD** — All database operations generated at compile time
  via `cslightdbgen.sqlitegen`
- **Content-synced FTS5** — Auto-generated triggers keep FTS5 indexes in sync

## Database Initialization

Each source service extends the `SourceDatabase` abstract base class from
`FhirAugury.Common`. On startup, `SourceDatabase` configures these SQLite
PRAGMAs:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size = -64000;   -- 64 MB page cache
PRAGMA temp_store = MEMORY;
```

`SourceDatabase` provides these methods:

| Method | Description |
|--------|-------------|
| `InitializeSchema()` | Creates all tables, indexes, and FTS5 virtual tables |
| `ExecuteInBatches()` | Batch operations using savepoints for partial rollback |
| `ExecuteInTransaction()` | Full transaction wrapper |
| `CreateFts5Table()` | Creates FTS5 virtual table with auto-generated INSERT/DELETE/UPDATE triggers |
| `RebuildFts5()` | Rebuilds FTS5 index from content table |
| `GetDatabaseSizeBytes()` | Returns database file size |
| `CheckIntegrity()` | Runs SQLite integrity check |

Base table creation uses `CREATE TABLE IF NOT EXISTS`. Services also run
targeted forward-only startup migrations, such as additive `ALTER TABLE`
columns and conservative backfills, when an existing database predates a
required field.

---

## Per-Source Tables

### Common Tables (present in every source database)

#### `sync_state` — Per-source sync tracking

The core shape is shared. `LastSuccessfulSyncAt` is an additive Jira-only
column used by the provenance contract.

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceName` | TEXT | Source identifier (jira, zulip, confluence, github) |
| `SubSource` | TEXT? | Sub-source (e.g., Zulip stream name) |
| `LastSyncAt` | TEXT | Timestamp of the latest recorded sync activity; for Jira this is not the canonical upstream-success watermark |
| `LastSuccessfulSyncAt` | TEXT? | Jira-specific latest proven error-free upstream full/incremental refresh; null when unavailable |
| `LastCursor` | TEXT? | Cursor for position-based sync (e.g., Zulip message ID) |
| `ItemsIngested` | INTEGER | Total items ingested |
| `SyncSchedule` | TEXT? | Configured sync interval (TimeSpan) |
| `NextScheduledAt` | TEXT? | Next scheduled sync time |
| `Status` | TEXT? | Current sync status |
| `LastError` | TEXT? | Last error message |

Index: `(SourceName, SubSource)`

Jira keys `SubSource` as `<project>:<run-type>`. Its
`LastSuccessfulSyncAt` advances only for an error-free upstream `full` or
`incremental` run. Partial/error results and cache rebuilds retain the prior
value. Known project values are preserved across a database reset; no value is
inferred from cache timestamps or local replay completion.

#### `ingestion_log` — Ingestion run history

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceName` | TEXT | Source identifier |
| `RunType` | TEXT | Full, Incremental, or OnDemand |
| `StartedAt` | TEXT | Run start time |
| `CompletedAt` | TEXT? | Run completion time |
| `ItemsProcessed` | INTEGER | Total items processed |
| `ItemsNew` | INTEGER | New items inserted |
| `ItemsUpdated` | INTEGER | Existing items updated |
| `ErrorMessage` | TEXT? | Error details if failed |

Index: `(SourceName, StartedAt)`

#### `index_keywords` — BM25 keyword index (per-service)

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Document source type |
| `SourceId` | TEXT | Document identifier |
| `Keyword` | TEXT | Indexed keyword |
| `Count` | INTEGER | Term frequency in document |
| `KeywordType` | TEXT | Classification: word, fhir_path, fhir_operation |
| `Bm25Score` | REAL | Pre-computed BM25 score |

Indexes: `(SourceType, SourceId)`, `(Keyword, KeywordType)`

#### `index_corpus` — Corpus statistics (per-service)

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Keyword` | TEXT | Keyword |
| `KeywordType` | TEXT | Keyword classification |
| `DocumentFrequency` | INTEGER | Number of documents containing this keyword |
| `Idf` | REAL | Inverse document frequency |

Index: `(Keyword, KeywordType)`

#### `index_doc_stats` — Document statistics (per-service)

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT UNIQUE | Source type identifier |
| `TotalDocuments` | INTEGER | Total documents for this source |
| `AverageDocLength` | REAL | Average document length (tokens) |

Index: `(SourceType)`

---

### Cross-Reference Tables (present in every source database)

Each source database maintains xref tables for references found in its content
that point to items in other sources. These use shared record types from
`FhirAugury.Common.Database.Records`.

#### `xref_jira` — References to Jira issues

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source type of the containing item |
| `SourceId` | TEXT | ID of the containing item |
| `LinkType` | TEXT | Reference type (e.g., "mention") |
| `Context` | TEXT? | Surrounding text context |
| `JiraKey` | TEXT | Referenced Jira issue key |

#### `xref_zulip` — References to Zulip messages/topics

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source type of the containing item |
| `SourceId` | TEXT | ID of the containing item |
| `LinkType` | TEXT | Reference type |
| `Context` | TEXT? | Surrounding text context |
| `StreamId` | INTEGER? | Zulip stream ID |
| `StreamName` | TEXT? | Zulip stream name |
| `TopicName` | TEXT? | Zulip topic name |
| `MessageId` | INTEGER? | Zulip message ID |

#### `xref_confluence` — References to Confluence pages

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source type of the containing item |
| `SourceId` | TEXT | ID of the containing item |
| `LinkType` | TEXT | Reference type |
| `Context` | TEXT? | Surrounding text context |
| `PageId` | TEXT | Confluence page ID |

#### `xref_github` — References to GitHub issues/PRs

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source type of the containing item |
| `SourceId` | TEXT | ID of the containing item |
| `LinkType` | TEXT | Reference type |
| `Context` | TEXT? | Surrounding text context |
| `RepoFullName` | TEXT | Repository full name (e.g., HL7/fhir) |
| `IssueNumber` | INTEGER | Issue or PR number |

#### `xref_fhir_element` — References to FHIR element paths

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source type of the containing item |
| `SourceId` | TEXT | ID of the containing item |
| `LinkType` | TEXT | Reference type |
| `Context` | TEXT? | Surrounding text context |
| `ResourceType` | TEXT | FHIR resource type |
| `ElementPath` | TEXT | FHIR element path |

Not every xref table exists in every source — each source creates tables for
other sources (not itself):

| Source DB | xref Tables |
|-----------|-------------|
| Jira | `xref_zulip`, `xref_github`, `xref_confluence`, `xref_fhir_element` |
| Zulip | `xref_jira`, `xref_github`, `xref_confluence`, `xref_fhir_element` |
| Confluence | `xref_jira`, `xref_zulip`, `xref_github`, `xref_fhir_element` |
| GitHub | `xref_jira`, `xref_zulip`, `xref_confluence`, `xref_fhir_element` |

---

### Jira (`jira.db`)

#### `jira_issues`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Key` | TEXT UNIQUE | Issue key (e.g., FHIR-43499) |
| `ProjectKey` | TEXT | Project key |
| `Title` | TEXT | Issue title |
| `Description` | TEXT? | Full description |
| `DescriptionPlain` | TEXT? | Plain-text version of Description (HTML stripped) |
| `Summary` | TEXT? | Short summary |
| `Type` | TEXT | Issue type (Bug, Enhancement, etc.) |
| `Priority` | TEXT | Priority level |
| `Status` | TEXT | Current status |
| `Resolution` | TEXT? | Resolution type |
| `ResolutionDescription` | TEXT? | Resolution details (custom field) |
| `ResolutionDescriptionPlain` | TEXT? | Plain-text version of ResolutionDescription (HTML stripped) |
| `Assignee` | TEXT? | Legacy assigned-user display field |
| `AssigneeUserId` | INTEGER? | Exact `jira_users.Id` used for safe public display-name resolution |
| `Reporter` | TEXT? | Legacy reporter display field |
| `ReporterUserId` | INTEGER? | Exact `jira_users.Id` used for safe public display-name resolution |
| `CreatedAt` | TEXT | Creation timestamp |
| `UpdatedAt` | TEXT | Last update timestamp |
| `ResolvedAt` | TEXT? | Resolution timestamp |
| `WorkGroup` | TEXT? | HL7 work group (custom field) |
| `Specification` | TEXT? | Related specification (custom field) |
| `RaisedInVersion` | TEXT? | Version raised in (custom field) |
| `SelectedBallot` | TEXT? | Selected ballot (custom field) |
| `RelatedArtifacts` | TEXT? | Related artifacts (custom field) |
| `RelatedIssues` | TEXT? | Related issues (custom field) |
| `DuplicateOf` | TEXT? | Duplicate issue key (custom field) |
| `AppliedVersions` | TEXT? | Applied versions (custom field) |
| `ChangeType` | TEXT? | Change type (custom field) |
| `Impact` | TEXT? | Impact assessment (custom field) |
| `Vote` | TEXT? | Vote information (custom field) |
| `VoteMover` | TEXT? | Parsed mover from Vote field |
| `VoteSeconder` | TEXT? | Parsed seconder from Vote field |
| `VoteForCount` | INTEGER? | Parsed for-count from Vote field |
| `VoteAgainstCount` | INTEGER? | Parsed against-count from Vote field |
| `VoteAbstainCount` | INTEGER? | Parsed abstain-count from Vote field |
| `Labels` | TEXT? | Comma-separated labels |
| `CommentCount` | INTEGER | Number of comments |

Indexes: `(ProjectKey, Key)`, `(Status)`, `(WorkGroup, UpdatedAt)`,
`(Specification, UpdatedAt)`, `(UpdatedAt)`

#### `jira_comments`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `IssueId` | INTEGER FK | → `jira_issues.Id` |
| `IssueKey` | TEXT | Parent issue key |
| `Author` | TEXT | Comment author |
| `CreatedAt` | TEXT | Comment timestamp |
| `Body` | TEXT | Comment body |
| `BodyPlain` | TEXT | Plain-text version of Body (HTML stripped) |

Indexes: `(IssueKey)`, `(CreatedAt)`

#### `jira_issue_related` — Maps issues to their related issue keys (from the RelatedIssues custom field)

| Column | Type | Constraints |
|--------|------|-------------|
| `Id` | INTEGER | PRIMARY KEY |
| `IssueId` | INTEGER | NOT NULL |
| `IssueKey` | TEXT | NOT NULL, indexed |
| `RelatedIssueKey` | TEXT | NOT NULL, indexed |

#### `jira_issue_labels` — Junction table linking issues to label entries

| Column | Type | Constraints |
|--------|------|-------------|
| `Id` | INTEGER | PRIMARY KEY |
| `IssueId` | INTEGER | NOT NULL, indexed |
| `LabelId` | INTEGER | NOT NULL, indexed |

#### `jira_projects` — Project catalog and successful watermark projection

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Row identifier |
| `Key` | TEXT UNIQUE | Jira project key |
| `Enabled` | INTEGER | Whether ingestion configuration enables the project |
| `BaselineValue` | INTEGER | Project search-ranking baseline |
| `IssueCount` | INTEGER | Last observed local issue count |
| `LastSyncAt` | TEXT? | Canonical latest successful upstream refresh for the project |

Issue counts may change after local rebuild work, but `LastSyncAt` advances only
with the project watermark described above.

#### `jira_source_state` — Local content-generation fence

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Singleton ID (`1`) |
| `ContentRevision` | INTEGER | Monotonic local database generation |
| `MutationInProgress` | INTEGER | Boolean fence set while a source mutation is active |
| `UpdatedAt` | TEXT | Time the generation state last changed |

Mutations advance the revision before and after their writes. Readers capture
this row, result data, and project watermarks in one transaction. A stable
content revision is a consistency coordinate; it is deliberately independent
of the upstream-success date.

#### `jira_users` and `jira_issue_inpersons` — Public people source

`jira_users` stores `Id`, unique `Username`, `DisplayName`,
`HasAccountUsername`, and `HasExplicitDisplayName`. `HasAccountUsername`
distinguishes a true account identifier from the synthetic key used by a
display-name-only row; existing rows of unknown origin migrate conservatively
to true, and later account observations upgrade the flag monotonically. Only
rows whose explicit-display flag remains eligible under the shared
account-equality and email-token policy can enter the structured public people
contract. `jira_issue_inpersons` joins `IssueKey` to `UserId`. Reporter and
Assignee use the exact IDs on `jira_issues`, while requester results are
trimmed, case-insensitively deduplicated, and deterministically sorted. The
public projection never includes usernames, email addresses, or user IDs.

#### Index/Lookup Tables

Eight tables for navigable field values, all sharing this structure:

| Column | Type | Constraints |
|--------|------|-------------|
| `Id` | INTEGER | PRIMARY KEY |
| `Name` | TEXT | NOT NULL, UNIQUE |
| `IssueCount` | INTEGER | NOT NULL |

Table names: `jira_index_workgroups`, `jira_index_specifications`,
`jira_index_ballots`, `jira_index_labels`, `jira_index_types`,
`jira_index_priorities`, `jira_index_statuses`, `jira_index_resolutions`

> **Note:** Spec-artifact data (the parsed `JIRA-Spec-Artifacts` repository) is
> owned by the GitHub source's `jira_specs*` table family — not the Jira source.
> See the GitHub-source schema and the `api/v1/jira-specs/...` endpoints for
> that surface.

#### `jira_issue_links` — Issue-to-issue links

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceKey` | TEXT | Source issue key |
| `TargetKey` | TEXT | Target issue key |
| `LinkType` | TEXT | Link type |

#### FTS5 tables: `jira_issues_fts`, `jira_comments_fts`

---

### Zulip (`zulip.db`)

#### `zulip_streams`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `ZulipStreamId` | INTEGER UNIQUE | Zulip stream ID |
| `Name` | TEXT | Stream name |
| `Description` | TEXT? | Stream description |
| `IsWebPublic` | INTEGER | Boolean: web-public flag |
| `IncludeStream` | INTEGER | Boolean: whether stream is included in ingestion (default 1) |
| `MessageCount` | INTEGER | Total messages fetched |
| `LastFetchedAt` | TEXT | Last fetch timestamp |

Index: `(Name)`

#### `zulip_messages`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `ZulipMessageId` | INTEGER UNIQUE | Zulip message ID |
| `StreamId` | INTEGER FK | → `zulip_streams.Id` |
| `StreamName` | TEXT | Stream name (denormalized) |
| `Topic` | TEXT | Topic name |
| `SenderId` | INTEGER | Sender's Zulip user ID |
| `SenderName` | TEXT | Sender display name |
| `SenderEmail` | TEXT? | Sender email |
| `ContentHtml` | TEXT? | Original HTML content |
| `ContentPlain` | TEXT | Plain-text content |
| `Timestamp` | TEXT | Message timestamp |
| `CreatedAt` | TEXT | Record creation time |
| `Reactions` | TEXT? | JSON-encoded reactions |

Indexes: `(StreamId, Topic)`, `(SenderId)`, `(SenderName)`, `(Timestamp)`,
`(StreamName, Topic)`

#### `zulip_thread_tickets` — Aggregated Jira references per thread

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `StreamName` | TEXT | Stream name |
| `Topic` | TEXT | Topic name |
| `JiraKey` | TEXT | Referenced Jira issue key |
| `ReferenceCount` | INTEGER | Number of references in thread |
| `FirstSeenAt` | TEXT | First reference timestamp |
| `LastSeenAt` | TEXT | Most recent reference timestamp |

#### FTS5 table: `zulip_messages_fts`

---

### Confluence (`confluence.db`)

#### `confluence_spaces`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Key` | TEXT UNIQUE | Space key (e.g., FHIR) |
| `Name` | TEXT | Space name |
| `Description` | TEXT? | Space description |
| `Url` | TEXT? | Space URL |
| `LastFetchedAt` | TEXT | Last fetch timestamp |

#### `confluence_pages`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `ConfluenceId` | TEXT UNIQUE | Confluence page ID |
| `SpaceKey` | TEXT | Parent space key |
| `Title` | TEXT | Page title |
| `Status` | TEXT | `current` or `archived`, projected from the sweep manifest |
| `ParentId` | TEXT? | Parent page ID (hierarchy) |
| `BodyStorage` | TEXT? | Body in Confluence storage format |
| `BodyPlain` | TEXT? | Body as plain text |
| `Labels` | TEXT? | Comma-separated labels |
| `VersionNumber` | INTEGER | Page version |
| `LastModifiedBy` | TEXT? | Last modifier |
| `LastModifiedAt` | TEXT | Last modification timestamp |
| `Url` | TEXT? | Page URL |

Indexes: `(SpaceKey)`, `(ParentId)`, `(LastModifiedAt)`, `(Status)`

`Status` is added to a pre-existing database by an `ALTER TABLE` in
`ConfluenceDatabase.InitializeSchema`, because the generated `CreateTable` is
create-if-not-exists and would otherwise leave the column missing while its
generated index failed at startup. Adding it does **not** require rebuilding
`confluence_pages_fts`, which indexes only `BodyPlain`, `Title` and `Labels`.

#### `confluence_comments`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `PageId` | INTEGER FK | → `confluence_pages.Id` |
| `ConfluencePageId` | TEXT | Confluence page ID |
| `Author` | TEXT | Comment author |
| `CreatedAt` | TEXT | Comment timestamp |
| `Body` | TEXT | Comment body (plain text) |

Index: `(PageId)`

#### `confluence_attachments`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `PageId` | INTEGER FK | → `confluence_pages.Id` |
| `ConfluencePageId` | TEXT | Owning Confluence page ID |
| `ConfluenceAttachmentId` | TEXT UNIQUE | Confluence attachment ID |
| `FileName` | TEXT | Attachment file name |
| `MediaType` | TEXT? | Reported media type |
| `FileSizeBytes` | INTEGER? | Reported byte length; null when Confluence reports none |
| `VersionNumber` | INTEGER | Attachment version |
| `CreatedAt` | TEXT | Version timestamp |
| `DownloadUrl` | TEXT? | Absolute download URL |
| `CacheKey` | TEXT? | Cache key of the downloaded bytes; **null when the blob was skipped by `AttachmentMaxBytes` or not yet fetched** |

Indexes: `(PageId)`, `(ConfluencePageId)`

The row exists whether or not the bytes were downloaded, so an attachment
excluded by policy stays discoverable, searchable, and fetchable by hand.

#### `confluence_jira_refs` — Jira references found in Confluence pages

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `ConfluenceId` | TEXT | Confluence page ID |
| `JiraKey` | TEXT | Referenced Jira issue key |
| `Context` | TEXT? | Surrounding text context |

Indexes: `(ConfluenceId)`, `(JiraKey)`

#### `confluence_page_links` — Page-to-page links

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourcePageId` | INTEGER | Source page ID |
| `TargetPageId` | INTEGER | Target page ID |
| `LinkType` | TEXT | Link type |

#### FTS5 table: `confluence_pages_fts`

---

### GitHub (`github.db`)

#### `github_repos`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `FullName` | TEXT UNIQUE | Full name (e.g., HL7/fhir) |
| `Owner` | TEXT | Repository owner |
| `Name` | TEXT | Repository name |
| `Description` | TEXT? | Repository description |
| `LastFetchedAt` | TEXT | Last fetch timestamp |
| `Category` | TEXT | Repository category (enum string) |
| `DefaultBranch` | TEXT? | Default branch (e.g., master/main); migrated column used for deterministic primary-PR selection |

#### `github_issues`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `UniqueKey` | TEXT UNIQUE | Key (`owner/repo#number`) |
| `RepoFullName` | TEXT | Repository full name |
| `Number` | INTEGER | Issue/PR number |
| `IsPullRequest` | INTEGER | Boolean: is this a PR |
| `Title` | TEXT | Title |
| `Body` | TEXT? | Body text |
| `State` | TEXT | open or closed |
| `Author` | TEXT? | Creator |
| `Labels` | TEXT? | Comma-separated labels |
| `Assignees` | TEXT? | Comma-separated assignees |
| `Milestone` | TEXT? | Milestone name |
| `CreatedAt` | TEXT | Creation timestamp |
| `UpdatedAt` | TEXT | Last update timestamp |
| `ClosedAt` | TEXT? | Close timestamp |
| `MergeState` | TEXT? | PR merge state |
| `HeadBranch` | TEXT? | PR head branch |
| `BaseBranch` | TEXT? | PR base branch |

Indexes: `(RepoFullName, Number)`, `(State)`, `(Milestone)`, `(UpdatedAt)`

#### `github_comments`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `IssueId` | INTEGER FK | → `github_issues.Id` |
| `RepoFullName` | TEXT | Repository full name |
| `IssueNumber` | INTEGER | Issue/PR number |
| `Author` | TEXT | Comment author |
| `CreatedAt` | TEXT | Comment timestamp |
| `Body` | TEXT | Comment body |
| `IsReviewComment` | INTEGER | Boolean: is this a review comment |
| `ExternalId` | TEXT? | Stable GitHub-native comment identity (GraphQL node id for gh-CLI issue comments/reviews; numeric REST id stringified for review-thread comments). Migrated column |
| `CommentKind` | TEXT? | Comment kind discriminator: `issue`, `review`, or `review_comment`. Migrated column |

Indexes: `(IssueId)`, `(RepoFullName, IssueNumber)`; unique
`(RepoFullName, CommentKind, ExternalId)` (`ix_github_comments_external`) so
`INSERT OR IGNORE` dedupes all three comment kinds across re-ingestion (the
GetIndex() PK cannot). Inline (line-anchored) PR review-thread comments are
ingested here as `CommentKind = review_comment`.

#### `github_commits`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Sha` | TEXT UNIQUE | Commit SHA |
| `RepoFullName` | TEXT | Repository full name |
| `Message` | TEXT | Commit message (first line) |
| `Body` | TEXT? | Commit body (remaining lines) |
| `Author` | TEXT | Author name |
| `AuthorEmail` | TEXT? | Author email |
| `CommitterName` | TEXT? | Committer name |
| `CommitterEmail` | TEXT? | Committer email |
| `Date` | TEXT | Commit date |
| `Url` | TEXT? | Commit URL |
| `FilesChanged` | INTEGER | Number of files changed |
| `Insertions` | INTEGER | Lines inserted |
| `Deletions` | INTEGER | Lines deleted |
| `Refs` | TEXT? | Branch/tag refs |

Indexes: `(RepoFullName)`, `(Date)`

#### `github_commit_files` — Files changed in commits

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `CommitSha` | TEXT | Parent commit SHA |
| `FilePath` | TEXT | File path |
| `ChangeType` | TEXT | Change type (added, modified, deleted) |
| `BlobSha` | TEXT? | Post-image (new) blob SHA for this `(commit, file)`, captured from `git log --raw --no-abbrev` during ingestion. Null for deletions (all-zero sentinel) and for rows written by an older extractor before this column existed — consumers must tolerate its absence. |

Indexes: covering `(CommitSha, FilePath, BlobSha, ChangeType)` and `(FilePath)`. The
covering index lets a window reader load a commit's changed files (path, change type,
and resolved blob) index-only, without touching the row heap.

#### `github_commit_pr_links` — Commit-to-PR associations

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `CommitSha` | TEXT | Commit SHA |
| `PrNumber` | INTEGER | Pull request number |
| `RepoFullName` | TEXT | Repository full name |

Indexes: `(CommitSha)`, `(PrNumber, RepoFullName)`; unique
`(CommitSha, PrNumber, RepoFullName)` (`ix_github_commit_pr_links_natural`).
Populated during PR ingestion from `gh pr view --json commits` with
delete-then-insert (replace) semantics per PR, so a force-push that rewrites a
PR's commit set does not leave stale links and re-ingestion stays idempotent.

#### `github_spec_file_map` — Specification-to-file mappings

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `ArtifactKey` | TEXT | Specification artifact key |
| `FilePath` | TEXT | File path in repository |
| `MapType` | TEXT | Mapping type |
| `WorkGroup` | TEXT? | Canonical HL7 work-group code resolved by `WorkGroupResolutionPass` |
| `WorkGroupRaw` | TEXT? | Original (pre-resolution) work-group input; populated when it didn't resolve or resolved to a different code |

#### `github_structure_definitions` — Parsed FHIR StructureDefinitions

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `FilePath` | TEXT | Source file path |
| `Url` | TEXT | Canonical URL |
| `Name` | TEXT | SD name |
| `Title` | TEXT? | Human-readable title |
| `Status` | TEXT? | Publication status |
| `ArtifactClass` | TEXT | Classification (Profile, Extension, Resource, etc.) |
| `Kind` | TEXT | SD kind (resource, complex-type, etc.) |
| `IsAbstract` | INTEGER | Whether abstract |
| `FhirType` | TEXT? | FHIR type name |
| `BaseDefinition` | TEXT? | Base SD URL |
| `Derivation` | TEXT? | Derivation (specialization, constraint) |
| `FhirVersion` | TEXT? | FHIR version |
| `Description` | TEXT? | Description |
| `Publisher` | TEXT? | Publisher |
| `WorkGroup` | TEXT? | HL7 work group |
| `WorkGroupRaw` | TEXT? | Original (pre-resolution) work-group input preserved by `WorkGroupResolutionPass` when not canonical |
| `FhirMaturity` | TEXT? | Maturity level (FMM) |
| `StandardsStatus` | TEXT? | Standards status |
| `Category` | TEXT? | Category |
| `Contexts` | TEXT? | Extension contexts (JSON) |

#### `github_sd_elements` — StructureDefinition differential elements

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `StructureDefinitionId` | INTEGER FK | FK → `github_structure_definitions` |
| `ElementId` | TEXT? | Element ID |
| `Path` | TEXT | Element path (e.g., `Patient.name`) |
| `Name` | TEXT? | Element name |
| `Short` | TEXT? | Short description |
| `Definition` | TEXT? | Full definition |
| `MinCardinality` | INTEGER? | Minimum cardinality |
| `MaxCardinality` | TEXT? | Maximum cardinality |
| `Types` | TEXT? | Allowed types (JSON) |
| `FieldOrder` | INTEGER | Order within the SD |

#### `github_canonical_artifacts` — Parsed canonical FHIR artifacts

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `FilePath` | TEXT | Source file path |
| `ResourceType` | TEXT | Resource type (CodeSystem, ValueSet, etc.) |
| `Url` | TEXT | Canonical URL |
| `Name` | TEXT? | Artifact name |
| `Title` | TEXT? | Human-readable title |
| `Version` | TEXT? | Version |
| `Status` | TEXT? | Publication status |
| `Description` | TEXT? | Description |
| `Publisher` | TEXT? | Publisher |
| `WorkGroup` | TEXT? | Canonical HL7 work-group code resolved by `WorkGroupResolutionPass` |
| `WorkGroupRaw` | TEXT? | Original (pre-resolution) work-group input preserved when not canonical |
| `FhirMaturity` | INTEGER? | Maturity level (FMM) |
| `StandardsStatus` | TEXT? | Standards status |
| `TypeSpecificData` | TEXT? | Per-resource-type extracted JSON |
| `Format` | TEXT | Source format (xml, json, fsh) |

#### `github_repo_workgroups` — Per-repo derived default work-group attribution

Lives in its own table (rather than as a column on `github_repos`) so API-driven
repo upserts in `GitHubRestProvider` / `GitHubCliProvider` — which fully rewrite
the `github_repos` row from `MapRepo` output — cannot blank out the value
derived by `WorkGroupResolutionPass`.

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT UNIQUE | Owner/Name (e.g., `HL7/fhir`) — one row per repo |
| `WorkGroup` | TEXT? | Canonical HL7 work-group code, or `NULL` when no signal |
| `WorkGroupRaw` | TEXT? | Original input preserved when not canonical |
| `Source` | TEXT | Provenance (`config` or `majority-jira-spec`) |
| `ResolvedAt` | TEXT | When the row was last derived |

#### `hl7_workgroups` — Authoritative HL7 work-group codeset

Local copy of the `CodeSystem-hl7-work-group` resource, populated from the
support XML. Used by `WorkGroupResolutionPass` to canonicalize free-text
work-group inputs.

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Code` | TEXT UNIQUE | Canonical work-group code (e.g., `fhir-i`) |
| `Name` | TEXT | Display name |
| `Definition` | TEXT? | Definition text |
| `Retired` | INTEGER | Whether retired |
| `NameClean` | TEXT | Normalized name (case/punct-folded) for fuzzy lookups |

#### `github_file_contents` — Indexed repository file contents

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `FilePath` | TEXT | File path relative to repo root |
| `FileExtension` | TEXT | File extension |
| `ParserType` | TEXT | Parser used for extraction |
| `ContentText` | TEXT | Extracted text content |
| `ContentLength` | INTEGER | Original file size |
| `ExtractedLength` | INTEGER | Extracted text length |
| `LastCommitSha` | TEXT? | SHA of last commit touching this file |
| `LastModifiedAt` | TEXT? | Last modification timestamp |

#### `github_file_tags` — File tags for search boosting

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `RepoFullName` | TEXT | Repository full name |
| `FilePath` | TEXT | File path |
| `TagCategory` | TEXT | Tag category |
| `TagName` | TEXT | Tag name |
| `TagModifier` | TEXT? | Tag modifier |
| `Weight` | REAL | Tag weight for search scoring |

#### FTS5 tables

`github_issues_fts`, `github_comments_fts`, `github_commits_fts`,
`github_file_contents_fts`, `github_structure_definitions_fts`,
`github_canonical_artifacts_fts`

---

## Processor snapshots and ticket publication databases

Processor databases are private service state. Clients receive only sanitized,
immutable snapshot pairs, and ticket publication creates a separate embedded
database for browser use.

### Jira processor input provenance

`jira_processing_source_tickets` caches two nullable source coordinates with
each candidate:

| Column | Type | Description |
|--------|------|-------------|
| `SourceProjectLastSuccessfulRefreshAt` | TEXT? | Successful upstream watermark for that ticket's own Jira project |
| `SourceContentRevision` | INTEGER? | Stable Jira local-content revision represented by discovery |

A later unproven read overwrites these values with null rather than retaining
stale provenance.

#### `authoring_run_input_provenance` — Frozen run input

| Column | Type | Description |
|--------|------|-------------|
| `RowId` | INTEGER PK | Auto-increment |
| `RunId` | TEXT | Owning authoring run |
| `Source` | TEXT | Source name (`jira` for Jira processors) |
| `LatestSuccessfulRefreshAt` | TEXT? | Complete-corpus upstream refresh coordinate, or null |
| `ContentRevision` | INTEGER? | Common stable source generation, or null |
| `CapturedAt` | TEXT | Run creation time |

The authoring store writes the logical one-row-per-run/source provenance set in
the same immediate transaction as `authoring_runs` and
`authoring_run_items`. For Jira, the refresh is the maximum project-specific
candidate value only when every selected ticket has one, and the content
revision is retained only when every selected ticket shares it. `CapturedAt`
is run provenance, not a source-freshness fallback. Legacy Jira runs are seeded
with null source coordinates.

### Preparer public snapshot schemas v2 and v3

Preparer schema v2 extends the unchanged v1 table catalog:

| Table | v2 addition |
|-------|-------------|
| `authoring_run_input_provenance` | Provenance rows for every run contributing a retained receipt-backed ticket |
| `prepared_ticket_hydration` | `Assignee`, `SourceProject`, `SourceLastSuccessfulRefreshAt`, and `SourceContentRevision` |
| `prepared_jira_hydration` | `Assignee` for related Jira projections |
| `prepared_ticket_in_person_requesters` | New normalized `(RowId, TicketKey, DisplayName)` rows |

Schema v3 leaves that immutable v2 catalog unchanged and adds nullable
`PublicDisplayNamePolicyVersion` columns:

| Table | v3 addition |
|-------|-------------|
| `prepared_ticket_hydration` | Policy proof for the parent Reporter and Assignee values |
| `prepared_jira_hydration` | Policy proof for each self/related Jira Reporter and Assignee pair |
| `prepared_ticket_in_person_requesters` | Policy proof for each requester row |

Migrated rows receive null markers, not the current policy version. The v3
sanitizer retains people only when the row carries the exact current marker
and the value passes the context-free email safety check; untrusted parent and
Jira values become null and untrusted requester rows are removed.

The sanitizer selects one catalog by configured schema version and derives
descriptor counts from that same catalog. It removes requester rows outside
the retained ticket set and provenance outside contributing runs. The
checked-in Preparer writer emits v3; the discussion publisher accepts exact
v1, v2, and v3 catalogs. V1 and v2 remain usable for freshness and source data
available in their catalogs, but their unversioned people values are always
treated as unavailable. Planner and Tickets for Applying remain on the
separate Planner snapshot v1 catalog.

### Discussion renderer schema v1

After it validates a Preparer v1, v2, or v3 pair, the publisher creates a new
filtered SQLite database from scratch. Renderer version 1 is independent of
the Preparer snapshot version and contains only these browser-facing tables:

| Table | Key columns and purpose |
|-------|-------------------------|
| `site_metadata` | One row keyed by `RendererSchemaVersion`; stores `BaseTitle`, qualified `SiteName`, nullable Jira refresh, and resolved filters |
| `tickets` | One row per case-insensitive ticket key with display metadata, summaries, proposals, rationale, and request/resolution content |
| `ticket_people` | `(TicketKey, Role, OrderInRole)` for `reporter`, `assignee`, and `in-person-requester`; only exact-current-policy, context-free-safe v3 display names are non-null |
| `ticket_facets` | `(TicketKey, Dimension, ValueKey)` normalized across `project`, `wg`, `type`, `artifact`, `page`, `impact`, and `spec`, including the `__unknown__` machine key |
| `summary_sources` | `(TicketKey, SummaryKind, SourceKey)` for unique `linked-jira`, `related-jira`, and `related-zulip` adjacent links |
| `related_items` | `(TicketKey, Kind, ItemKey, LinkTypeKey)` for the broader related-context display |
| `topics` | Authored discussion topic metadata and stable render hints |
| `topic_groups` | Ordered groups within a topic |
| `topic_members` | Ordered grouped/ungrouped ticket membership and display columns |

`site-manifest.json` reports these renderer table counts, its schema version,
the stable base `title`, and optional `displayTitle` and Jira refresh. The
browser queries this database only; processor ledger and persistence tables
are not shipped as its contract. No live Jira, Orchestrator, or processor
connection is needed after publication.

For schema-v2 or schema-v3 input, renderer construction requires exactly one
accepted authoring coordinate for every retained ticket. Zero or multiple
matches are structural validation errors and abort publication before this
database is created. With valid structure, missing or null run provenance and
incomplete parent project/revision/watermark coordinates leave the Jira refresh
null and the display title unsuffixed. Schema-v1 and schema-v2 people are never
copied into `ticket_people`; schema-v3 values are independently checked for
the current marker and public value safety during projection and renderer
validation.

---

## Orchestrator Database (`orchestrator.db`)

The Orchestrator maintains its own database for cross-service coordination.
Cross-references are source-owned (each source stores its own xref tables), so
the orchestrator only tracks scan state for coordinating peer notifications.

#### `xref_scan_state` — Incremental cross-reference scanning state

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceName` | TEXT | Source identifier (jira, zulip, confluence, github) |
| `LastCursor` | TEXT? | Cursor for position-based scanning |
| `LastScanAt` | TEXT? | Timestamp of last scan |

---

## FTS5 Virtual Tables

Each source service creates its FTS5 virtual tables using
`SourceDatabase.CreateFts5Table()`, which auto-generates content-sync triggers
(INSERT, DELETE, UPDATE) to keep the FTS5 index in sync with the content table.

| Service | FTS5 Table | Content Table | Indexed Columns |
|---------|------------|--------------|----------------|
| Jira | `jira_issues_fts` | `jira_issues` | Title, DescriptionPlain, ResolutionDescriptionPlain |
| Jira | `jira_comments_fts` | `jira_comments` | BodyPlain |
| Zulip | `zulip_messages_fts` | `zulip_messages` | ContentPlain, Topic |
| Confluence | `confluence_pages_fts` | `confluence_pages` | Title, BodyPlain, Labels |
| GitHub | `github_issues_fts` | `github_issues` | Title, Body |
| GitHub | `github_comments_fts` | `github_comments` | Body |
| GitHub | `github_commits_fts` | `github_commits` | Message, Body |
| GitHub | `github_file_contents_fts` | `github_file_contents` | ContentText, FilePath |
| GitHub | `github_structure_definitions_fts` | `github_structure_definitions` | Name, Title, Description |
| GitHub | `github_canonical_artifacts_fts` | `github_canonical_artifacts` | Name, Title, Description, Url |

See [Indexing and Search](indexing-and-search.md) for details on how FTS5
triggers work, how queries are processed, and how the Orchestrator aggregates
results across services.

## Source-Generated CRUD

Database records use `cslightdbgen.sqlitegen` (a Roslyn source generator) to
produce all CRUD operations at compile time. Each table is defined as a
`partial record class` with `[LdgSQLiteTable]` attributes within its source
service project:

```csharp
[LdgSQLiteTable("jira_issues")]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(WorkGroup), nameof(UpdatedAt))]
public partial record class JiraIssueRecord
{
    [LdgSQLiteKey]
    public long Id { get; set; }

    [LdgSQLiteUnique]
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    // ...
}
```

The generator produces:

- `CreateTable()` / `DropTable()` — Schema management
- `Insert()` / `Update()` / `Delete()` — Single and batch operations
- `SelectSingle()` / `SelectList()` / `SelectEnumerable()` — Typed queries
- `SelectCount()` / `SelectDict()` — Aggregation and dictionary lookups
- `LoadMaxKey()` / `GetIndex()` — Thread-safe auto-increment ID generation

All methods are available as both static methods and extension methods on
`IDbConnection`.

### Type Mappings

| C# Type | SQLite Type |
|---------|-------------|
| `long`, `int` | `INTEGER` |
| `string` | `TEXT` |
| `double` | `REAL` |
| `bool` | `INTEGER` (0/1) |
| `DateTimeOffset` | `TEXT` (ISO 8601) |
| `string?` | `TEXT` (nullable) |

## Database Architecture Summary

```
Source.Jira (jira.db)
├── jira_issues, jira_comments          — Content tables
├── jira_issue_related, jira_issue_labels — Relationship tables
├── jira_index_workgroups, jira_index_specifications, jira_index_ballots,
│   jira_index_labels, jira_index_types, jira_index_priorities,
│   jira_index_statuses, jira_index_resolutions — Index/lookup tables
├── jira_issues_fts, jira_comments_fts  — FTS5 virtual tables (content-synced)
├── index_keywords, index_corpus, index_doc_stats — BM25 index
└── sync_state, ingestion_log           — Sync infrastructure

Source.Zulip (zulip.db)
├── zulip_streams, zulip_messages       — Content tables
├── zulip_thread_tickets — Jira reference tables
├── zulip_messages_fts                  — FTS5 virtual table (content-synced)
├── zulip_keywords, zulip_corpus_keywords, zulip_doc_stats — BM25 index
└── zulip_sync_state, ingestion_log     — Sync infrastructure

Source.Confluence (confluence.db)
├── confluence_spaces, confluence_pages, confluence_comments, confluence_attachments — Content tables
├── confluence_jira_refs                — Jira reference table
├── confluence_pages_fts                — FTS5 virtual table (content-synced)
├── index_keywords, index_corpus, index_doc_stats — BM25 index
└── sync_state, ingestion_log           — Sync infrastructure

Source.GitHub (github.db)
├── github_repos, github_issues, github_comments — Content tables
├── github_commits, github_commit_files, github_commit_pr_links — Commit tables
├── github_jira_refs, github_spec_file_maps — Reference/mapping tables
├── github_structure_definitions, github_sd_elements — FHIR StructureDefinition data
├── github_canonical_artifacts — Canonical FHIR artifacts (CodeSystem, ValueSet, etc.)
├── github_file_contents, github_file_tags — Repository file contents and tags
├── github_issues_fts, github_comments_fts, github_commits_fts — FTS5 virtual tables
├── github_file_contents_fts, github_structure_definitions_fts — FTS5 virtual tables
├── github_canonical_artifacts_fts — FTS5 virtual table
├── github_keywords, github_corpus_keywords, github_doc_stats — BM25 index
└── github_sync_state, ingestion_log    — Sync infrastructure

Orchestrator (orchestrator.db)
└── xref_scan_state                     — Incremental scan cursors
```

---

## Auxiliary Databases

In addition to the per-service SQLite databases, FHIR Augury supports two
optional **read-only** auxiliary databases that provide extended vocabulary and
language data to all source services. These are loaded once at startup by the
`AuxiliaryDatabase` class in `FhirAugury.Common` and cached in frozen/immutable
collections for thread-safe access.

### Auxiliary Database (stop words + lemmas)

A shared SQLite file configured via `AuxiliaryDatabasePath` in each service's
`AuxiliaryDatabase` configuration section.

#### `stop_words` — Extended stop word list

| Column | Type | Description |
|--------|------|-------------|
| `word` | TEXT NOT NULL | A stop word to exclude from indexing |

These are merged with the hardcoded defaults in `StopWords` at startup via
`StopWords.CreateMergedSet()`.

#### `lemmas` — Inflection-to-lemma mappings

| Column | Type | Description |
|--------|------|-------------|
| `Inflection` | TEXT NOT NULL | Inflected word form (e.g., "patients") |
| `Category` | TEXT | Part of speech or category (informational) |
| `Lemma` | TEXT NOT NULL | Base form (e.g., "patient") |

Used by the `Lemmatizer` class to normalize tokens during keyword extraction.
Only entries with inflection length ≥ 3 characters are loaded.

### FHIR Specification Database (element paths + operations)

A separate SQLite file configured via `FhirSpecDatabasePath` in each service's
`AuxiliaryDatabase` configuration section.

#### `elements` — FHIR element paths

| Column | Type | Description |
|--------|------|-------------|
| `Path` | TEXT | FHIR element path (e.g., `Patient.name.given`) |

Resource names are extracted from the path (the segment before the first dot)
and merged with the hardcoded defaults in `FhirVocabulary` via
`FhirVocabulary.CreateMergedResourceNames()`.

#### `operations` — FHIR operation codes

| Column | Type | Description |
|--------|------|-------------|
| `Code` | TEXT | Operation code (e.g., `validate` or `$validate`) |

Operation codes are normalized to include the `$` prefix and merged with
hardcoded defaults via `FhirVocabulary.CreateMergedOperations()`.

### Graceful Degradation

When auxiliary database paths are not configured (or the files don't exist),
the system falls back to hardcoded defaults — no auxiliary database is required
for normal operation. All SQL failures during loading are caught and logged as
warnings.

---

## Dictionary Database

The `DictionaryDatabase` (from `FhirAugury.Common`) compiles dictionary source
files into an SQLite database. Used by all services.

#### `words` — Dictionary words

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Word` | TEXT | Dictionary word |

Index: `idx_words_word` on `(Word)`

#### `typos` — Typo corrections

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Typo` | TEXT | Misspelled word |
| `Correction` | TEXT | Corrected spelling |

Indexes: `idx_typos_typo` on `(Typo)`, `idx_typos_correction` on `(Correction)`
