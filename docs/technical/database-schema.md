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
| **Discussion publication** | Embedded generated SQLite | Discussion renderer schema-v2 projection; not durable service state |

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

### Live run lineage and snapshot publication proof

Run purpose and repair lineage are additive live-state fields:

| Table | Column | Type | Description |
|-------|--------|------|-------------|
| `authoring_runs` | `Purpose` | TEXT NOT NULL | `authoring` by default; also `initial-revalidation`, `grouping-maintenance`, `publication-refresh`, `publication-reconciliation`, or `canonical-epoch-recovery` |
| `authoring_runs` | `SourceRunId` | TEXT? | Earlier run selected as maintenance lineage; required for publication refresh, reconciliation, and canonical-epoch recovery |
| `authoring_review_snapshots` | `PublicationProofJson` | TEXT? | Canonical serialized publication proof retained with snapshot lifecycle state and recovery |

Startup adds the missing columns without replacing the live table or any
immutable snapshot. Existing rows initially receive the `authoring` default;
the migration then conservatively marks only recognizable database-only
maintenance rows as `grouping-maintenance` and known revalidation lineage as
`initial-revalidation`. Ambiguous historical rows remain `authoring`. New
`publication-refresh` and `publication-reconciliation` rows are
snapshot-producing, have a distinct run ID, and point `SourceRunId` at a
completed Preparer run whose snapshot record is ready.

New protected enrichment uses the existing `authoring_runs.RequestJson` column
for a versioned maintenance envelope: recipe `publication-enrichment`, version
1, a safe corpus comparison, and private frozen input fingerprints. The
request, maintenance items, run, and fence are persisted atomically before
scheduling. Only database null means the legacy Jira-only recipe; malformed
or unknown non-null metadata is never interpreted as legacy. This adds no
public Preparer snapshot column and does not change publication-proof v1.

The immutable JSON snapshot descriptor exposes the optional
`publicationProof` object:

| Field | Meaning |
|-------|---------|
| `contractVersion` | Canonical Preparer publication-proof contract version |
| `purpose` | Exact value `publication-refresh`, `publication-reconciliation`, or `canonical-epoch-recovery` |
| `sourceRunId` | Refresh/reconciliation: operator-selected completed source run; recovery: terminal abandoned reconciliation |
| `sourceName` | Exact source value `jira` |
| `sourceLastSuccessfulRefreshAt` | Refresh: frozen canonical UTC upstream-success watermark; reconciliation: proof capture coordinate; recovery: source abandonment time |
| `sourceContentRevision` | Refresh/reconciliation: stable Jira content generation; recovery: authoring epoch, not a Jira freshness claim |
| `publicDisplayNamePolicyVersion` | Refresh: current public people-policy version; reconciliation/recovery: `0` for the dedicated purpose-specific proof |
| `corpusFingerprint` | Refresh/recovery: complete accepted receipt/item/source-revision membership; reconciliation: carried-plus-staged overlay corpus |
| `groupingFingerprint` | Refresh/recovery: complete canonical grouping output; reconciliation v2: complete grouping-impact fingerprint |
| `capturedAt` | Canonical UTC time after all proof-bearing stages completed |

`PublicationProofJson` lets snapshot promotion/reconciliation restore the same
descriptor proof after interruption. It is deliberately not added to the
public snapshot's `authoring_snapshot_provenance` table. Likewise, the live
`Purpose` and `SourceRunId` columns are not added to the schema-v3
`authoring_runs` snapshot projection. The descriptor is the publication-proof
boundary, so schema v3 remains an additive people-policy revision of the v2
snapshot catalog rather than a copy of every live maintenance table.

The descriptor's `contractVersion` is the independent generic
`PreparedTicketPublicationContract` version (currently 1), including for a
reconciliation publication. Private reconciliation comparison/proof JSON is
contract v2; promotion does not copy that private version number into the
generic descriptor contract.

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
v1, v2, and v3 catalogs. The current Preparer host requires v3. V1 and v2
remain usable for freshness and source data available in their catalogs, but
their unversioned people values are always treated as unavailable. Planner and
Tickets for Applying remain on the separate Planner snapshot v1 catalog.

### Preparer publication-reconciliation live state

Changed-ticket publication reconciliation is private Preparer state. None of
the tables in this section is copied into the public schema-v3 snapshot. The
public boundary is the sanitized snapshot plus its descriptor proof.

#### Reconciliation identity and frozen comparison

`prepared_ticket_publication_reconciliations` has one row per
`purpose = 'publication-reconciliation'` run:

| Column | Type | Description |
|--------|------|-------------|
| `RowId` | INTEGER PK | Generated row identifier |
| `RunId` | TEXT UNIQUE | Owning reconciliation run |
| `SourceRunId` | TEXT | Completed baseline run selected by the operator |
| `SourceSnapshotId` | TEXT | Verified immutable baseline snapshot |
| `SourceSnapshotSha256` | TEXT | Baseline database digest |
| `StableJiraGeneration` | TEXT | One generation shared by the complete Jira observation |
| `CorpusFingerprint` | TEXT | Frozen accepted baseline corpus |
| `ComparisonJson` | TEXT | Contract-v2 complete per-ticket decisions and coordinates |
| `PromotionState` | TEXT | `staged`, `snapshot-publish-pending`, `ready`, `cancelled`, or `canonical-unpublished` |
| `CapturedAt` | TEXT | UTC comparison capture time |
| `CancelledAt` | TEXT? | Immutable audited pre-promotion cancellation time |
| `CancellationReason` | TEXT? | Required operator reason for `cancelled` |
| `AbandonedAt` | TEXT? | Audited explicit abandonment time |
| `AbandonmentReason` | TEXT? | Required operator reason for `canonical-unpublished` |

`prepared_ticket_publication_reconciliation_items` is the relational index of
that comparison. Its primary key is `(RunId, TicketKey)`; ticket keys use
case-insensitive collation. Each row stores `Disposition` (`carry-forward` or
`re-author`), baseline/current source revisions, baseline receipt ID, run-item
ID, contributing run ID, authored fingerprint, grouping fingerprint, and the
selected `ItemKind` and `ExpectedSourceRevision`. The latter two columns are
nullable only so existing contract-v1 rows remain readable; every v2 row
requires them. Carry-forward rows retain the accepted baseline item values,
while re-authored rows retain the accepted reconciliation item values.
Admission writes these rows, the reconciliation record, mixed
`authoring_run_items`, both fences, and the initial journal through one
caller-owned immediate transaction after repeating the Jira observation.

#### Revised-ticket staging and overlay

Only `re-author` decisions receive staging rows:

| Table | Key | Durable contents |
|-------|-----|------------------|
| `prepared_ticket_publication_staged_graphs` | `(RunId, TicketKey)` | Run item/operation, frozen source revision, receipt content fingerprint, complete authored payload JSON, and `StagedAt` |
| `prepared_ticket_publication_staged_hydration` | `(RunId, TicketKey)` | Complete hydration JSON, hydration fingerprint, and `StagedAt` |
| `prepared_ticket_publication_staged_receipts` | `(RunId, TicketKey)` plus unique `ReceiptId` | Receipt/run-item/operation coordinates, authored receipt fingerprint, and `PersistedAt` |

Receipt acceptance and all three staged representations commit with the
authoring item transition on the same SQLite connection. Canonical ticket and
hydration rows are not changed at acceptance. Overlay reads resolve
carry-forward decisions from their frozen canonical receipts and changed
decisions from matching complete staging tuples; duplicate, missing, or
fingerprint-divergent children are rejected. The overlay fingerprint is the
SHA-256 of canonical JSON produced by
`PreparedTicketPublicationContract.ComputeCorpusFingerprint` over exactly
`TicketKey`, `ReceiptId`, `RunItemId`, `ContributingRunId`, `ItemKind`, and
`ExpectedSourceRevision`, sorted by the shared contract. The same value flows
through grouping stages, the candidate descriptor, the private proof, and the
promoted public proof.

Promotion readiness is stricter than the generic run completion predicate.
Every `re-author` decision must join to an `authoring_run_items` row in exact
`complete` state and matching graph, hydration, staged receipt, immutable
receipt, run-item, operation, source revision, and authored fingerprint.
`superseded` never counts as reconciliation-complete, even though ordinary
authoring finalization can treat it as terminal.

#### Grouping impact and unaffected fingerprints

| Table | Key | Durable contents |
|-------|-----|------------------|
| `prepared_ticket_publication_grouping_impacts` | `(RunId, PartitionKey)` | `ImpactJson`, completion bit, and update time for each old/new affected partition |
| `prepared_ticket_publication_staged_grouping` | `(RunId, PartitionKey)` | Complete replacement JSON plus overlay corpus, semantic output, protected-row fingerprints, and staging time |
| `prepared_ticket_publication_grouping_stage_receipts` | `(RunId, PartitionKey)` | Exact stage/lease/partition/input coordinates, semantic output fingerprint, topic/group/member counts, and persistence time |
| `prepared_ticket_publication_unaffected_fingerprints` | `RunId` | JSON containing impacted partition keys and independent authored-row, receipt-coordinate, grouping-row, and combined fingerprints |

The impacted key is the normalized
`WorkGroupClean + U+001F + Specification + U+001F + Type` coordinate. A
partition move therefore records both old and new partitions. Staged grouping
is replacement data, not a merge; empty output is represented by a complete
payload with no topics.

Finalization also creates one `authoring_run_stages` row named
`publication-reconciliation-grouping` for every impacted partition. Its
`InputFingerprint` is the candidate overlay corpus fingerprint, not the
baseline comparison fingerprint. A stage-context write must hold the exact
in-progress lease and must carry the same partition, overlay fingerprint,
revised keys, and complete overlay membership. The replacement, completed
impact, and receipt commit together while canonical topic/group/member rows
remain unchanged. A reclaimed lease may replay only identical staged content;
the durable receipt moves to the new lease. Completed stages are skipped only
when their matching receipt exists. Closure and unaffected-row validation run
after all such stages complete.

The unaffected authored-row component includes every protected canonical row
outside the revised ticket set except the reconciliation run's own
`authoring_runs` row. That row's expected lifecycle fields change from running
through finalizing/completed and are workflow state, not authored output.
Unchanged canonical ticket rows are not exempted. Receipt coordinates and
unaffected topic/group/member values and ordering are separately fingerprinted
and remain exact.

#### Candidate, proof, fences, and promotion journal

| Table | Key | Durable contents |
|-------|-----|------------------|
| `prepared_ticket_publication_snapshot_descriptors` | `RunId` | Immutable candidate descriptor JSON containing reserved snapshot/provenance coordinates plus the exact post-provenance SHA-256, size, table counts, and persistence time |
| `prepared_ticket_publication_reconciliation_proofs` | `RunId` | Contract-v2 proof JSON and capture time |
| `prepared_ticket_publication_reconciliation_fences` | `RunId` | Reconciliation lease ID and acquisition time, paired with the processor-wide `authoring_mutation_fences` row |
| `prepared_ticket_publication_reconciliation_journal` | `RunId` UNIQUE | State, typed staged reservation/candidate or pending promotion descriptor JSON, last recovery attempt, stable failure code/detail, and update time |

The proof JSON binds source run/snapshot, stable Jira generation,
accepted/carried/re-authored counts, overlay corpus fingerprint,
grouping-impact fingerprint, and capture time. The candidate descriptor binds
snapshot ID, processor/run, temporary and final paths, schema, sequence,
authoring epoch, item/receipt counts, creation time, checksum, size, public
table counts, and the same overlay/grouping evidence. Before materialization,
the Preparer writes those identity coordinates as a typed reservation in the
staged journal while `PromotionState = 'staged'`. After sanitization it writes
one matching
`authoring_snapshot_provenance` row, checkpoints and validates SQLite, then
hashes the resulting bytes. Candidate persistence writes the exact
post-provenance descriptor and proof, replacing only its matching reservation
in the staged journal. A restarted finalizer may reuse that already
authenticated candidate but cannot replace it with different bytes.

The journal starts at `staged`. The database-first promotion transaction
revalidates the candidate's exact post-provenance digest and size, every
descriptor coordinate, the durable reservation, workspace, and fingerprints.
It applies every revised graph and complete impacted grouping replacement,
creates the `authoring_review_snapshots` row from the already reserved
identity, and stores a promotion descriptor while moving both state rows to
`snapshot-publish-pending`. It does not generate a new snapshot ID or sequence
after hashing, and it does not claim to atomically move a filesystem file.

Pending recovery uses the journal plus the snapshot row:

- absent final + valid temporary: validate the exact journaled
  post-provenance bytes read-only, move without overwrite, then validate the
  same digest at the final path;
- matching final: validate its digest, size, provenance, coordinates, and
  counts against both descriptors and the snapshot record, then reuse it;
- conflicting/corrupt final, missing/corrupt temporary when no final is
  usable, checksum/provenance/count divergence, contradictory candidate or
  promotion descriptors, or a missing/inconsistent snapshot row: persist
  `promotion-recovery-failure` and retain the fence;
- creating/promoted/ready snapshot rows: advance only the missing transitions;
- ready snapshot with incomplete run, or complete run with pending journal:
  finish the same run/journal/fence transitions without replaying canonical
  replacement;
- missing staging after canonical commit: rely only on authenticated
  journal/descriptor/record/file evidence; never reconstruct the overlay;
- cancellation of pending recovery execution: retain pending state and fences with
  `promotion-recovery-failure`;
- competing attempts: serialize per run and accept a race winner only when
  the immutable final bytes match the same journaled evidence.

Successful recovery sets snapshot, run, reconciliation, and journal ready,
then removes graph/hydration/receipt impact workspace and the candidate
descriptor in the same transaction that releases the reconciliation fence.
Proof, comparison, item decisions, unaffected fingerprint, and journal remain
auditable. Recovery never opens candidate/final SQLite files for writing,
never creates or replaces provenance, and marks the snapshot promoted with
the previously journaled digest rather than a checksum discovered during
recovery. Startup and the explicit retry endpoint use the same
pending-journal protocol.

Contract-v1 comparison and proof rows remain deserializable for these audit
views, including null v2-only item coordinates. They are execution-inert:
grouping dispatch, candidate materialization, pending recovery, and promotion
require contract v2 and refuse rather than reinterpret a v1 corpus.

Dedicated operator cancellation is valid only while both state rows are
`staged` and before a trusted row exists in
`prepared_ticket_publication_snapshot_descriptors`, a snapshot row exists, or
canonical authored state points at the reconciliation. A single immediate
transaction writes `PromotionState = 'cancelled'`, `CancelledAt`, and
`CancellationReason`; updates or recreates the journal as terminal
`cancelled`; closes active attempts and incomplete stages; marks all run
items superseded while retaining accepted receipts; releases
`prepared_ticket_publication_reconciliation_fences` and the matching
`authoring_mutation_fences` row; and finally marks `authoring_runs`
`superseded`.

That transaction deletes only disposable unpromoted rows from staged
graph/hydration/receipt, grouping impact/replacement/stage receipt,
unaffected-fingerprint, and reconciliation-proof tables. It retains the
reconciliation row and `ComparisonJson`, relational item decisions, generic
run/item/attempt/stage/receipt history, and cancellation journal. A repeated
cancel returns the first timestamp and reason without rewriting them.
Workspace writers and stage leases require the still-staged state and both
fences, preventing a retry race from recreating rows after cancellation.

Explicit abandonment is valid only from `snapshot-publish-pending`. It writes
`canonical-unpublished`, `AbandonedAt`, and `AbandonmentReason`, records the
canonical-unpublished detail and completion time on `authoring_runs`, changes
that generic row from `finalizing` or `error` to terminal `abandoned`, and
releases both fences without rolling back canonical rows or changing the prior
ready snapshot. `abandoned` is non-recoverable, is excluded from active-run
capacity and scheduler/finalization queries, and leaves the reconciliation
row, journal, reason, and time as the canonical-unpublished audit.

Snapshot production is defined by the durable row, not a purpose allowlist:
every `authoring_runs.DatabaseOnly = 0` row is snapshot-producing. While any
reconciliation remains `canonical-unpublished` without a resolution matching
its source run ID and authoring epoch, the sole bypass is
`Purpose = 'canonical-epoch-recovery'`; database-only rows remain allowed.
`prevent_snapshot_after_unpublished_canonical` guards run insertion.
Companion triggers guard mutation-fence insertion, snapshot-row insertion,
snapshot promotion/readiness, run finalization/completion, and reconciliation
promotion. Each raises the exact SQLite sentinel
`canonical-unpublished-restriction`, which service code translates to the
same typed HTTP `409`. These triggers are last-ditch integrity checks behind
the shared guard at admission, queued fence acquisition, candidate creation,
and finalization/promotion. A pre-existing `error` or `finalizing` row already
audited as canonical-unpublished is migrated to `abandoned` at schema
initialization. The restriction remains durable until a separately explicit
operation verifies a snapshot for that canonical epoch.

### Preparer canonical-epoch recovery live state

These three additive private tables are created without changing any existing
publication or the schema-v3 public snapshot catalog:

| Table | Unique coordinates | Durable content |
|---|---|---|
| `prepared_ticket_canonical_epoch_recoveries` | `RunId`, `SourceRunId` | Append-only source abandonment/current epoch recipe; corpus, complete grouping, and recipe fingerprints; capture time |
| `prepared_ticket_canonical_epoch_recovery_journal` | `RunId` | Mutable state, typed reservation or trusted candidate `SnapshotDescriptorJson`, last attempt, failure code/detail, update time |
| `prepared_ticket_canonical_epoch_recovery_resolutions` | `RunId`, `SourceRunId`, `SnapshotId` | Append-only successful link, authoring epoch, snapshot SHA-256, corpus/grouping fingerprints, dedicated proof JSON, resolution time |

Admission is one fenced immediate transaction on one connection. The new
`authoring_runs` row has `DatabaseOnly = 0`, purpose
`canonical-epoch-recovery`, and `SourceRunId` pointing at the abandoned
reconciliation. Every selected item is `complete` with its existing
`AcceptedReceiptId`; a run-specific `recovery:<runId>:<itemKind>` kind keeps
maintenance selections distinct from authored revision coordinates. There
are no new authoring operations, receipts, or grouping stages.

`RecipeJson` (also retained as run `RequestJson`) freezes the full current
receipt coordinate set, complete grouping partitions/fingerprints, source
abandonment time/reason, epoch, and capture time. Its
`CanonicalRowsFingerprint` additionally protects exact public canonical row
values, including hydration and grouping IDs/order, against drift between
admission and final CAS. The public corpus fingerprint remains the canonical
six-field receipt-coordinate serialization. Grouping uses the shared full
partition-output serialization, not reconciliation's impact-only digest.

The journal starts at `materialization-pending`. Its reservation freezes the
snapshot ID, sequence, processor/run, epoch, schema, item/receipt counts,
temporary/final paths, and creation time before provenance is written.
After sanitization, provenance, checkpointing, integrity/count validation,
and hashing, one transaction stores the trusted candidate descriptor and a
`creating` snapshot row and changes the journal to `snapshot-publish-pending`.
The descriptor's digest is of the exact post-provenance bytes.

The final file can exist while the snapshot row is still `creating`.
Only exact final-file verification plus one shared-SQLite compare-and-swap
may insert a resolution, mark the snapshot/journal ready, complete the
recovery run, and delete its processor fence. Transaction-aware
`AuthoringRunStore.MarkSnapshotReadyAsync` and `CompleteRunAsync` use the
caller's connection/transaction; no independently opened write connection
or file move participates. A rollback leaves all these transitions pending.
Startup/retry authenticate the same reserved/journaled coordinates, not a
new snapshot or run.

Eligibility queries and integrity triggers anti-join the resolution table
on abandoned `RunId` and `AuthoringEpoch`. Thus resolution releases only
that restriction without ever modifying the source's `abandoned` status,
`canonical-unpublished` promotion/journal state, abandonment timestamp, or
reason. The recovery recipe and resolution are never updated or deleted by
recovery APIs. On conflict the journal retains failure detail and the
unresolved run/fence; successful resolution is the sole release boundary.

### Preparer publication-refresh live state

Metadata-only refresh adds two Preparer-owned live tables and one nullable
grouping-receipt field. They support recovery and proof construction; none is
copied into the public schema-v3 snapshot.

The existing `prepared_ticket_partition_receipts` table gains nullable
`OutputFingerprint TEXT`. New grouping writes bind their semantic
topics/groups/members output to that receipt. A null value identifies a legacy
receipt whose output must instead be bound by the certification table below.

#### `prepared_ticket_publication_refresh_receipts`

One row is written atomically with a successful publication metadata update.
The unique live key is `(RunId, StageId)`.

| Column | Type | Description |
|--------|------|-------------|
| `RowId` | INTEGER PK | Generated row identifier |
| `RunId` | TEXT | Owning `publication-refresh` run |
| `StageId` | TEXT | Owning `publication-enrichment-v1` stage, or a persisted legacy `publication-metadata` stage |
| `InputFingerprint` | TEXT | New recipe binds original snapshot identity, original/current corpus, protected values/references, and retained grouping; legacy source-run/corpus hashing is unchanged |
| `CorpusFingerprint` | TEXT | Canonical current accepted receipt-backed corpus |
| `SourceLastSuccessfulRefreshAt` | TEXT | Maximum proven project watermark in the coherent read |
| `SourceContentRevision` | INTEGER | One stable Jira content generation shared by every ticket |
| `PublicDisplayNamePolicyVersion` | INTEGER | Policy version proved by every fetched ticket |
| `AppliedAt` | TEXT | UTC metadata transaction time |

The same transaction allowlists updates to parent
`prepared_ticket_hydration` publication fields, the matching self row in
`prepared_jira_hydration`, normalized
`prepared_ticket_in_person_requesters`, and the refresh run's
`authoring_run_input_provenance` row. The versioned enrichment recipe also
updates the explicit self-ticket Jira `UpdatedAt` and context/outcome columns
of hydration for already accepted Zulip associations. Missing Zulip hydration
may be inserted only for an existing accepted reference. Authored payloads,
accepted relationships/IDs/justifications, receipts, contributing runs' frozen
input provenance, unrelated hydration, topics, groups, and members are
protected. Every accepted association has a typed outcome; the existing
`HydrationReason` stores the `zulip-reference-v1:` backing/latest-outcome
envelope, without a new snapshot column. Failed lookups retain safe
source-backed context as last-known or unbacked URLs as explicitly unverified.
A retry validates frozen protection and reuses the matching committed receipt
without another Jira or Zulip fetch.

#### `prepared_ticket_partition_certifications`

A refresh never reruns grouping. Each `grouping-certification` stage validates
the current partition membership and output against its latest retained source
receipt. New `prepared_ticket_partition_receipts` rows carry nullable
`OutputFingerprint`; when it is present, it must match the current semantic
grouping output. A legacy null fingerprint is bound by a certification row
whose unique live key is `(RunId, PartitionKey)`:

| Column | Type | Description |
|--------|------|-------------|
| `RowId` | INTEGER PK | Generated row identifier |
| `RunId` | TEXT | Refresh run performing certification |
| `StageId` | TEXT | Refresh `grouping-certification` stage |
| `PartitionKey` | TEXT | Certified grouping partition |
| `InputFingerprint` | TEXT | Current accepted partition membership |
| `SourceRunId` | TEXT | Run owning the retained grouping receipt |
| `SourceStageId` | TEXT | Stage owning the retained grouping receipt |
| `SourceInputFingerprint` | TEXT | Input fingerprint on that source receipt |
| `OutputFingerprint` | TEXT | Canonical fingerprint of current topics/groups/members |
| `CertifiedAt` | TEXT | UTC certification time |

The descriptor-level grouping fingerprint is calculated from every
stage-verified partition/output pair, whether a current receipt proves it
directly or a legacy receipt needs a certification row. The source receipts
remain intact and attributable to their original runs.

### Discussion renderer schema v3

After it validates a Preparer v1, v2, or v3 pair, the publisher creates a new
filtered SQLite database from scratch. Renderer version 3 is independent of
the Preparer snapshot version and contains only these browser-facing tables:

| Table | Key columns and purpose |
|-------|-------------------------|
| `site_metadata` | One row keyed by `RendererSchemaVersion = 3`; stores `BaseTitle`, `SiteName`, nullable upstream Jira refresh, canonical `ReadinessJson` and `CorpusSummaryJson`, and resolved filters |
| `facet_dimensions` | Publication-owned catalog of `Dimension`, hash `Route`, label, order, and list visibility |
| `tickets` | One row per case-insensitive ticket key with display metadata, nullable canonical UTC `JiraUpdatedAt` from its own self-Jira row, summaries, proposals, rationale, and request/resolution content |
| `ticket_people` | `(TicketKey, Role, OrderInRole)` for `reporter`, `assignee`, and `in-person-requester`, with `Availability` and structured `UnavailableReason`; only exact-current-policy, context-free-safe v3 display names are non-null |
| `ticket_facets` | `(TicketKey, Dimension, ValueKey)` plus display/sort values, normalized across `project`, `wg`, `type`, `artifact`, `page`, `impact`, and `spec`, including the `__unknown__` machine key |
| `summary_sources` | `(TicketKey, SummaryKind, SourceKey)` for unique `linked-jira`, `related-jira`, and `related-zulip` adjacent links, including safe canonical URLs where available |
| `related_items` | `(TicketKey, Kind, ItemKey, LinkTypeKey)` for broader related context, labels, safe URLs, detail, justification, and hydration state |
| `topics` | Authored discussion topic metadata and stable render hints |
| `topic_groups` | Ordered groups within a topic |
| `topic_members` | Ordered grouped/ungrouped ticket membership and display columns |

`site-manifest.json` reports these renderer table counts, renderer schema
version, stable base `title`, `displayTitle`, optional upstream Jira refresh,
structured `discussionReadiness`, and `discussionCorpus`. Readiness carries `isReady`, evidence
(`ordinary-snapshot` or `publication-refresh`), optional Jira content revision
and public people-policy version, and ordered reason objects. Reason codes are
`legacy-snapshot-schema`, `missing-ordinary-provenance`,
`invalid-refresh-proof`, and `missing-people-policy-proof`.

Preparer v1 and v2 remain readable but always produce degraded readiness and
cannot populate trusted people. An unavailable Reporter or Assignee row carries
the readiness reason. `No public display name available` describes an
exact-current-policy row without a publishable name; it does not establish
that a role is absent or a ticket is unassigned. The publisher never migrates the
input pair in place. The browser queries the renderer database only; processor
ledger and persistence tables are not shipped as its contract. No live Jira,
Orchestrator, or processor connection is needed after publication.

For schema-v2 or schema-v3 input, renderer construction requires exactly one
accepted authoring coordinate for every retained ticket. Zero or multiple
matches are structural validation errors and abort publication before this
database is created. Missing or incomplete source provenance degrades
readiness, independently of the title date. A schema-v3 descriptor carrying
`publicationProof` instead qualifies freshness only when its source lineage,
current-policy evidence, corpus fingerprint, and grouping fingerprint all
match the retained snapshot; a mismatch produces `invalid-refresh-proof`.
Schema-v1 and schema-v2 people are never copied into
`ticket_people`; schema-v3 values are independently checked for the current
marker and public value safety during projection and renderer validation.

`CorpusSummaryJson` matches the manifest's `discussionCorpus` and injected
presentation. It records exported ticket/project counts, valid self-Jira date
count and maximum, `dateCoverage` (`empty`, `none`, `partial`, `complete`),
ticket counts with public Reporter/Assignee/requester names, and per-kind
related-link counts (resolved safe, unresolved with retained safe URL, and no
usable URL). The validator independently recomputes these facts from renderer
rows and retains exact immutable-source projection validation.

Only complete dates in a nonempty generation-filtered export supply the title
suffix, for example `Tickets for Discussion - Sept 15, 2026`. The date is the
maximum self-ticket `UpdatedAt`, normalized to UTC, never an upstream
watermark, linked-ticket date, receipt timestamp, or publication clock.
Partial coverage keeps its known maximum as a fact but has no title date;
malformed non-null dates abort publication. Browser filters do not change the
frozen title or corpus facts. Existing renderer-v2 sites retain their original
assets/database and remain usable; there is no in-place migration.

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
