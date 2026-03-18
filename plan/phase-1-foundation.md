# Phase 1: Foundation

**Goal:** Project scaffolding, database layer, first data source (Jira), and
end-to-end proof of the download → load → index → search pipeline.

**Status:** ✅ Complete (2026-03-18)

---

## 1.1 — Solution & Project Scaffolding

### Objective

Create all projects in the solution, configure shared properties, verify the
build system and source generator work end-to-end.

### Tasks

#### 1.1.1 Create library projects

Create each project as a `classlib` targeting `net10.0`:

| Project | Path | Purpose |
|---------|------|---------|
| `FhirAugury.Models` | `src/FhirAugury.Models/` | Shared models, enums, interfaces |
| `FhirAugury.Database` | `src/FhirAugury.Database/` | SQLite records, generated CRUD |
| `FhirAugury.Sources.Jira` | `src/FhirAugury.Sources.Jira/` | Jira API client & ingestion |
| `FhirAugury.Indexing` | `src/FhirAugury.Indexing/` | FTS5, BM25, cross-ref |

Each `.csproj` must import `../../src/common.props` via `<Import>` or
`Directory.Build.props`. Verify `LangVersion`, `TargetFrameworks`, `Nullable`,
and `ImplicitUsings` are inherited.

#### 1.1.2 Create executable projects

| Project | Path | Type |
|---------|------|------|
| `FhirAugury.Cli` | `src/FhirAugury.Cli/` | Console app (`exe`) |

For Phase 1, only the CLI is needed. Service and MCP come later.

#### 1.1.3 Create test projects

| Project | Path |
|---------|------|
| `FhirAugury.Database.Tests` | `tests/FhirAugury.Database.Tests/` |
| `FhirAugury.Sources.Tests` | `tests/FhirAugury.Sources.Tests/` |

Use xUnit. Add references to `xunit`, `xunit.runner.visualstudio`,
`Microsoft.NET.Test.Sdk`.

#### 1.1.4 Update solution file

Update `fhir-augury.slnx` to include all new projects. Organize into
solution folders: `src/`, `tests/`, `Solution Items/`.

#### 1.1.5 Configure Directory.Build.props

Create `src/Directory.Build.props` that imports `common.props` so all
projects under `src/` automatically inherit shared settings.

Consider a `tests/Directory.Build.props` for shared test configuration.

#### 1.1.6 Verify build

Run `dotnet build fhir-augury.slnx` and confirm all projects compile
with zero warnings.

### Acceptance Criteria

- [x] All projects compile with `dotnet build`
- [x] `dotnet test` runs (empty test suite, but the infrastructure works)
- [x] Source generator reference is configured in `FhirAugury.Database`
- [x] Solution file lists all projects

---

## 1.2 — Shared Models & Interfaces

### Objective

Define the core abstractions that all sources and consumers depend on.

### Files to Create in `FhirAugury.Models/`

#### 1.2.1 `IDataSource.cs`

```csharp
public interface IDataSource
{
    string SourceName { get; }
    Task<IngestionResult> DownloadAllAsync(IngestionOptions options, CancellationToken ct);
    Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions options, CancellationToken ct);
    Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions options, CancellationToken ct);
}
```

#### 1.2.2 `IngestionResult.cs`

Record that captures the outcome of an ingestion run:
- `int ItemsProcessed`
- `int ItemsNew`
- `int ItemsUpdated`
- `int ItemsFailed`
- `IReadOnlyList<IngestionError> Errors`
- `DateTimeOffset StartedAt`
- `DateTimeOffset CompletedAt`
- `IReadOnlyList<IngestedItem> NewAndUpdatedItems` (for cross-ref linking)

#### 1.2.3 `IngestionOptions.cs`

Configuration record for ingestion runs:
- `string DatabasePath`
- `string? Filter`
- `bool Verbose`
- `CancellationToken CancellationToken`

#### 1.2.4 `IngestedItem.cs`

Represents a single item processed during ingestion, used to pass to
cross-reference linker:
- `string SourceType`
- `string SourceId`
- `string Title`
- `IReadOnlyList<string> SearchableTextFields`

#### 1.2.5 `IngestionType.cs`

Enum: `Full`, `Incremental`, `OnDemand`

#### 1.2.6 `SearchResult.cs`

Unified search result record:
- `string Source`
- `string Id`
- `string Title`
- `string? Snippet`
- `double Score`
- `double? NormalizedScore`
- `string? Url`
- `DateTimeOffset? UpdatedAt`

### Acceptance Criteria

- [x] All interfaces and models compile
- [x] No dependencies on concrete implementations
- [x] XML doc comments on all public members

---

## 1.3 — Database Layer (Core + Jira)

### Objective

Define SQLite table records with `cslightdbgen.sqlitegen` attributes,
verify generated CRUD compiles, and implement database initialization.

### Files to Create in `FhirAugury.Database/`

#### 1.3.1 NuGet references

Add to `.csproj`:
```xml
<PackageReference Include="cslightdbgen.sqlitegen"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
```

#### 1.3.2 `Records/SyncStateRecord.cs`

Ingestion sync state table — tracks last sync time, cursor, schedule,
status per source/sub-source. Fields as specified in proposal §04.

#### 1.3.3 `Records/IngestionLogRecord.cs`

Ingestion run log table — records every ingestion run with timing,
counts, and errors. Indexed by `(SourceName, StartedAt)`.

#### 1.3.4 `Records/JiraIssueRecord.cs`

Jira issue table with all fields from proposal §04:
- Core: Id, Key, ProjectKey, Title, Description, Summary, Type, Priority,
  Status, Resolution, Assignee, Reporter, CreatedAt, UpdatedAt, ResolvedAt
- Custom: WorkGroup, Specification, RaisedInVersion, SelectedBallot,
  RelatedArtifacts, RelatedIssues, DuplicateOf, AppliedVersions,
  ChangeType, Impact, Vote, Labels, CommentCount
- Indexes: Key (unique), (ProjectKey, Key), Status, WorkGroup,
  Specification, UpdatedAt

#### 1.3.5 `Records/JiraCommentRecord.cs`

Jira comment table: Id, IssueId (FK), IssueKey, Author, CreatedAt, Body.
Indexes: IssueKey, CreatedAt.

#### 1.3.6 `DatabaseService.cs`

Singleton service that manages the SQLite connection:
- Constructor takes `dbPath` and optional `readOnly` flag
- `OpenConnection()` — returns a new `SqliteConnection`
- `InitializeDatabase()` — creates all tables, FTS5 virtual tables,
  triggers, and indexes if they don't exist
- Uses WAL journal mode for concurrent readers
- Handles read-only mode for MCP server

#### 1.3.7 `FtsSetup.cs`

Static helper to create FTS5 virtual tables and content-sync triggers.
Phase 1 implements Jira FTS5 only:

- `jira_issues_fts` — indexes: Key, Title, Description, Summary,
  ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts
- `jira_comments_fts` — indexes: IssueKey, Author, Body
- INSERT/UPDATE/DELETE triggers on both content tables

### Acceptance Criteria

- [x] `dotnet build` succeeds — source generator produces CRUD code
- [x] Unit test creates in-memory SQLite DB, calls `InitializeDatabase()`,
      inserts/selects/updates/deletes `JiraIssueRecord`
- [x] FTS5 tables are created and triggers fire on insert
- [x] FTS5 search returns matching rows

---

## 1.4 — Jira Source Implementation

### Objective

Implement the Jira data source supporting both JSON REST API and XML bulk
export, with custom field mapping for HL7 Jira.

### Files to Create in `FhirAugury.Sources.Jira/`

#### 1.4.1 NuGet references

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`

#### 1.4.2 `JiraSource.cs`

Implements `IDataSource`:

- `SourceName` → `"jira"`
- `DownloadAllAsync` — full download using REST `GET /rest/api/2/search`
  with JQL pagination, or XML bulk export. Iterates through all issues,
  maps custom fields, upserts into `jira_issues` and `jira_comments`.
- `DownloadIncrementalAsync` — JQL: `updated >= '{since}' ORDER BY updated ASC`,
  with pagination. Upserts changed issues and their comments.
- `IngestItemAsync` — fetches single issue by key via
  `GET /rest/api/2/issue/{key}` with all fields expanded.

#### 1.4.3 `JiraAuthHandler.cs`

Handles authentication modes:
- **Cookie mode:** sets `Cookie` header with user-provided session cookies
- **API token mode:** HTTP Basic Auth with email + API token
- Configurable via `JiraSourceOptions`

#### 1.4.4 `JiraSourceOptions.cs`

Configuration record:
- `string BaseUrl` (default: `https://jira.hl7.org`)
- `JiraAuthMode AuthMode` (enum: `Cookie`, `ApiToken`)
- `string? Cookie`
- `string? ApiToken`
- `string? Email`
- `string DefaultJql` (default: `project = "FHIR Specification Feedback"`)
- `int PageSize` (default: 100)

#### 1.4.5 `JiraFieldMapper.cs`

Maps Jira custom field IDs to semantic names. Contains the mapping table
from proposal §03:
- `customfield_11302` → Specification
- `customfield_11400` → WorkGroup
- `customfield_11808` → RaisedInVersion
- etc.

Method: `JiraIssueRecord MapIssue(JsonElement issueJson)` — extracts all
fields from a Jira REST API JSON response and returns a populated record.

#### 1.4.6 `JiraXmlParser.cs`

Parses the Jira XML bulk export format (from `sr/jira.issueviews` endpoint).
Port from `temp/JiraFhirUtils`. Returns `IEnumerable<JiraIssueRecord>`.

#### 1.4.7 `JiraCommentParser.cs`

Extracts comments from both JSON and XML issue representations.
Returns `IEnumerable<JiraCommentRecord>`.

### Acceptance Criteria

- [x] Can parse sample Jira JSON response into `JiraIssueRecord`
- [x] Custom field mapping correctly extracts all 16+ custom fields
- [x] XML parser handles the HL7 Jira export format
- [x] `DownloadAllAsync` paginates correctly (test with mock HTTP)
- [x] `DownloadIncrementalAsync` uses `since` date in JQL
- [x] `IngestItemAsync` fetches and stores a single issue

---

## 1.5 — FTS5 Indexing (Jira)

### Objective

Wire up FTS5 full-text search for Jira issues and comments.

### Files to Create/Update in `FhirAugury.Indexing/`

#### 1.5.1 NuGet references

Project references: `FhirAugury.Models`, `FhirAugury.Database`

#### 1.5.2 `TextSanitizer.cs`

Shared text cleaning utilities:
- `StripHtml(string html)` — removes HTML tags, decodes entities
- `StripMarkdown(string md)` — removes markdown syntax (optional)
- `NormalizeUnicode(string text)` — NFC normalization
- `ExtractPlainText(string content, ContentFormat format)` — dispatcher

#### 1.5.3 `FtsSearchService.cs`

Search methods for FTS5 tables:
- `SearchJiraIssues(connection, query, filters, limit)` → `List<SearchResult>`
- `SearchJiraComments(connection, query, limit)` → `List<SearchResult>`

Uses FTS5 `MATCH` queries with `rank` column for scoring.
Returns `SearchResult` records with snippet extraction via `snippet()`.

### Acceptance Criteria

- [x] Inserting a `JiraIssueRecord` automatically populates FTS5 via triggers
- [x] FTS5 search for a keyword returns matching issues with scores
- [x] HTML content is stripped before indexing (via `ContentPlain` field)
- [x] Snippet extraction works for search result display

---

## 1.6 — CLI (Jira Subset)

### Objective

Build the CLI application with commands for Jira operations, proving the
end-to-end pipeline.

### Files to Create in `FhirAugury.Cli/`

#### 1.6.1 NuGet references

```xml
<PackageReference Include="System.CommandLine" Version="2.0.*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`,
`FhirAugury.Sources.Jira`, `FhirAugury.Indexing`

#### 1.6.2 `Program.cs`

Root command setup with global options:
- `--db` — database file path (default: `fhir-augury.db`)
- `--verbose` — verbose output
- `--json` — force JSON output format
- `--config` — config file path

#### 1.6.3 `Commands/DownloadCommand.cs`

`fhir-augury download --source jira [auth options] [--filter JQL]`

- Creates database if it doesn't exist
- Calls `JiraSource.DownloadAllAsync()`
- Shows progress (issue count, elapsed time)
- Reports final statistics

#### 1.6.4 `Commands/SyncCommand.cs`

`fhir-augury sync --source jira [--since date]`

- Reads `sync_state` for last sync time
- Calls `JiraSource.DownloadIncrementalAsync(since)`
- Updates `sync_state` on completion

#### 1.6.5 `Commands/IngestCommand.cs`

`fhir-augury ingest --source jira --id FHIR-43499`

- Calls `JiraSource.IngestItemAsync(identifier)`
- Displays the ingested item

#### 1.6.6 `Commands/IndexCommand.cs`

`fhir-augury index build-fts --db path`
`fhir-augury index rebuild-all --db path`

- Rebuilds FTS5 tables (drops and recreates)
- Shows progress

#### 1.6.7 `Commands/SearchCommand.cs`

`fhir-augury search -q "FHIRPath normative" [-s jira] [-n 20] [-f table|json|markdown]`

- Calls `FtsSearchService.SearchJiraIssues()`
- Formats output in requested format (table, JSON, markdown)

#### 1.6.8 `Commands/GetCommand.cs`

`fhir-augury get --source jira --id FHIR-43499 [--format table|json|markdown]`

- Looks up item by key from database
- Displays full details

#### 1.6.9 `Commands/SnapshotCommand.cs`

`fhir-augury snapshot --source jira --id FHIR-43499`

- Renders a rich, detailed view of a Jira issue
- Includes: metadata, description, comments, links
- Markdown-formatted output

#### 1.6.10 `Commands/StatsCommand.cs`

`fhir-augury stats [--source jira]`

- Shows database statistics: total issues, comments, last sync, DB size

#### 1.6.11 `OutputFormatters/TableFormatter.cs`

Renders search results and item details as aligned ASCII tables.

#### 1.6.12 `OutputFormatters/JsonFormatter.cs`

Renders output as formatted JSON.

#### 1.6.13 `OutputFormatters/MarkdownFormatter.cs`

Renders output as markdown.

### Acceptance Criteria

- [x] `fhir-augury download --source jira` downloads issues (or errors with auth)
- [x] `fhir-augury search -q "test"` returns FTS5 results in table format
- [x] `fhir-augury get --source jira --id FHIR-12345` shows full issue
- [x] `fhir-augury snapshot --source jira --id FHIR-12345` renders rich view
- [x] `fhir-augury stats` shows database counts
- [x] `--json` flag works on all output commands
- [x] `--help` works on all commands

---

## 1.7 — Tests

### Objective

Unit tests for database CRUD, Jira parsing, and FTS5 search.

### Test Files

#### `tests/FhirAugury.Database.Tests/`

- `DatabaseInitializationTests.cs` — verify table creation, FTS5 setup
- `JiraIssueRecordTests.cs` — CRUD operations on `jira_issues`
- `JiraCommentRecordTests.cs` — CRUD operations on `jira_comments`
- `SyncStateRecordTests.cs` — CRUD operations on `sync_state`
- `Fts5JiraTests.cs` — FTS5 search, trigger-based indexing, snippet extraction

#### `tests/FhirAugury.Sources.Tests/`

- `JiraFieldMapperTests.cs` — custom field mapping from sample JSON
- `JiraXmlParserTests.cs` — XML parsing with sample export data
- `TextSanitizerTests.cs` — HTML/markdown stripping, Unicode normalization

### Test Data

Create `tests/TestData/` with:
- `sample-jira-issue.json` — representative Jira REST API response
- `sample-jira-export.xml` — representative Jira XML export snippet

### Acceptance Criteria

- [x] All tests pass with `dotnet test`
- [x] Database tests use in-memory SQLite (`:memory:`)
- [x] Source tests use static sample data, no network calls
