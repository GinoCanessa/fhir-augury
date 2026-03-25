# Database Schema

This document describes the SQLite database schema used by FHIR Augury v2,
including the per-service database architecture, all tables, FTS5 virtual
tables, indexes, and the source-generated CRUD layer.

## Overview

In the v2 microservices architecture, each service maintains its **own SQLite
database file**. There is no single shared database — data is distributed
across services:

| Service | Database File | Contents |
|---------|---------------|----------|
| **Source.Jira** | `jira.db` | Issues, comments, FTS5, BM25 index, sync state |
| **Source.Zulip** | `zulip.db` | Streams, messages, FTS5, BM25 index, sync state |
| **Source.Confluence** | `confluence.db` | Spaces, pages, comments, FTS5, BM25 index, sync state |
| **Source.GitHub** | `github.db` | Repos, issues/PRs, comments, FTS5, BM25 index, sync state |
| **Orchestrator** | `orchestrator.db` | Cross-reference links, cross-ref scan state |

Each database uses:

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

All tables use `CREATE TABLE IF NOT EXISTS` — the schema is forward-only
additive with no migration system.

---

## Per-Source Tables

### Common Tables (present in every source database)

#### `sync_state` — Per-source sync tracking

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceName` | TEXT | Source identifier (jira, zulip, confluence, github) |
| `SubSource` | TEXT? | Sub-source (e.g., Zulip stream name) |
| `LastSyncAt` | TEXT | Timestamp of last successful sync |
| `LastCursor` | TEXT? | Cursor for position-based sync (e.g., Zulip message ID) |
| `ItemsIngested` | INTEGER | Total items ingested |
| `SyncSchedule` | TEXT? | Configured sync interval (TimeSpan) |
| `NextScheduledAt` | TEXT? | Next scheduled sync time |
| `Status` | TEXT? | Current sync status |
| `LastError` | TEXT? | Last error message |

Index: `(SourceName, SubSource)`

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

Indexes: `(SourceType, SourceId)`, `(Keyword)`, `(Keyword, KeywordType)`

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
| `Assignee` | TEXT? | Assigned user |
| `Reporter` | TEXT? | Reporter user |
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

Indexes: `(Key)`, `(ProjectKey, Key)`, `(Status)`, `(WorkGroup)`,
`(Specification)`, `(UpdatedAt)`

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

Indexes: `(StreamId)`, `(StreamId, Topic)`, `(SenderId)`, `(Timestamp)`,
`(StreamName, Topic)`

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
| `ParentId` | TEXT? | Parent page ID (hierarchy) |
| `BodyStorage` | TEXT? | Body in Confluence storage format |
| `BodyPlain` | TEXT? | Body as plain text |
| `Labels` | TEXT? | Comma-separated labels |
| `VersionNumber` | INTEGER | Page version |
| `LastModifiedBy` | TEXT? | Last modifier |
| `LastModifiedAt` | TEXT | Last modification timestamp |
| `Url` | TEXT? | Page URL |

Indexes: `(SpaceKey)`, `(ParentId)`, `(LastModifiedAt)`

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

Indexes: `(RepoFullName)`, `(State)`, `(UpdatedAt)`, `(RepoFullName, Number)`

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

Indexes: `(IssueId)`, `(RepoFullName)`

#### FTS5 tables: `github_issues_fts`, `github_comments_fts`

---

## Orchestrator Database (`orchestrator.db`)

The Orchestrator maintains its own database for cross-service concerns.

#### `cross_ref_links` — Cross-reference links between sources

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT | Source item type (jira, zulip, etc.) |
| `SourceId` | TEXT | Source item identifier |
| `TargetType` | TEXT | Target item type |
| `TargetId` | TEXT | Target item identifier |
| `LinkType` | TEXT | Link type (currently always "mention") |
| `Context` | TEXT? | ~100 chars of surrounding text |

Indexes: `(SourceType, SourceId)`, `(TargetType, TargetId)`, `(LinkType)`

#### `xref_scan_state` — Incremental cross-reference scanning state

Tracks cursor/timestamp-based incremental scanning so the Orchestrator only
processes new or updated content when building cross-reference links.

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
| GitHub | `github_issues_fts` | `github_issues` | Title, Body, Labels |
| GitHub | `github_comments_fts` | `github_comments` | Body |

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
[LdgSQLiteIndex(nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
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
├── zulip_messages_fts                  — FTS5 virtual table (content-synced)
├── index_keywords, index_corpus, index_doc_stats — BM25 index
└── sync_state, ingestion_log           — Sync infrastructure

Source.Confluence (confluence.db)
├── confluence_spaces, confluence_pages, confluence_comments — Content tables
├── confluence_pages_fts                — FTS5 virtual table (content-synced)
├── index_keywords, index_corpus, index_doc_stats — BM25 index
└── sync_state, ingestion_log           — Sync infrastructure

Source.GitHub (github.db)
├── github_repos, github_issues, github_comments — Content tables
├── github_issues_fts, github_comments_fts       — FTS5 virtual tables (content-synced)
├── index_keywords, index_corpus, index_doc_stats — BM25 index
└── sync_state, ingestion_log           — Sync infrastructure

Orchestrator (orchestrator.db)
├── cross_ref_links                     — Cross-source reference links
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
