# Database Schema

This document describes the SQLite database schema used by FHIR Augury,
including all tables, FTS5 virtual tables, indexes, and the source-generated
CRUD layer.

## Overview

FHIR Augury stores all data in a single SQLite database file with:

- **15 content tables** — Data from all four sources plus infrastructure
- **6 FTS5 virtual tables** — Full-text search indexes with content-sync triggers
- **WAL mode** — Concurrent readers alongside a single writer
- **Source-generated CRUD** — All database operations generated at compile time

## Database Initialization

On startup, `DatabaseService.InitializeDatabase()` sets these SQLite PRAGMAs
(write mode only):

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size = -64000;   -- 64 MB page cache
PRAGMA temp_store = MEMORY;
```

All tables use `CREATE TABLE IF NOT EXISTS` — the schema is forward-only
additive with no migration system.

## Tables

### Infrastructure

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

---

### Jira

#### `jira_issues`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Key` | TEXT UNIQUE | Issue key (e.g., FHIR-43499) |
| `ProjectKey` | TEXT | Project key |
| `Title` | TEXT | Issue title |
| `Description` | TEXT? | Full description |
| `Summary` | TEXT? | Short summary |
| `Type` | TEXT | Issue type (Bug, Enhancement, etc.) |
| `Priority` | TEXT | Priority level |
| `Status` | TEXT | Current status |
| `Resolution` | TEXT? | Resolution type |
| `ResolutionDescription` | TEXT? | Resolution details (custom field) |
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

Indexes: `(IssueKey)`, `(CreatedAt)`

---

### Zulip

#### `zulip_streams`

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `ZulipStreamId` | INTEGER UNIQUE | Zulip stream ID |
| `Name` | TEXT | Stream name |
| `Description` | TEXT? | Stream description |
| `IsWebPublic` | INTEGER | Boolean: web-public flag |
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

---

### Confluence

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

---

### GitHub

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

---

### Cross-References and BM25

#### `xref_links` — Cross-reference links

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

#### `index_keywords` — BM25 keyword index

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

#### `index_corpus` — Corpus statistics

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `Keyword` | TEXT | Keyword |
| `KeywordType` | TEXT | Keyword classification |
| `DocumentFrequency` | INTEGER | Number of documents containing this keyword |
| `Idf` | REAL | Inverse document frequency |

Index: `(Keyword, KeywordType)`

#### `index_doc_stats` — Document statistics

| Column | Type | Description |
|--------|------|-------------|
| `Id` | INTEGER PK | Auto-increment |
| `SourceType` | TEXT UNIQUE | Source type identifier |
| `TotalDocuments` | INTEGER | Total documents for this source |
| `AverageDocLength` | REAL | Average document length (tokens) |

Index: `(SourceType)`

---

## FTS5 Virtual Tables

All FTS5 tables use the content-sync pattern with automatic triggers:

| FTS5 Table | Content Table | Indexed Columns |
|------------|--------------|----------------|
| `jira_issues_fts` | `jira_issues` | Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts |
| `jira_comments_fts` | `jira_comments` | IssueKey, Author, Body |
| `zulip_messages_fts` | `zulip_messages` | StreamName, Topic, SenderName, ContentPlain |
| `confluence_pages_fts` | `confluence_pages` | Title, BodyPlain, Labels |
| `github_issues_fts` | `github_issues` | Title, Body, Labels |
| `github_comments_fts` | `github_comments` | Body |

See [Indexing and Search](indexing-and-search.md) for details on how FTS5
triggers work and how queries are processed.

## Source-Generated CRUD

Database records use `cslightdbgen.sqlitegen` (a Roslyn source generator) to
produce all CRUD operations at compile time. Each table is defined as a
`partial record class` with attributes:

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
