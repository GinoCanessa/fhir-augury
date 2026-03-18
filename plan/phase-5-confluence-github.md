# Phase 5: Confluence & GitHub Sources

**Goal:** Complete the four-source coverage by adding Confluence and GitHub,
extending cross-referencing, and updating all interfaces.

**Depends on:** Phase 4 (Service Layer)

---

## 5.1 — Confluence Database Tables

### Files to Create in `FhirAugury.Database/`

#### 5.1.1 `Records/ConfluenceSpaceRecord.cs`

Fields: Key (PK), Name, Description, Url, LastFetchedAt.

#### 5.1.2 `Records/ConfluencePageRecord.cs`

Fields: Id, SpaceKey, Title, ParentId, BodyStorage, BodyPlain, Labels,
VersionNumber, LastModifiedBy, LastModifiedAt, Url.

Indexes: SpaceKey, ParentId, LastModifiedAt.

#### 5.1.3 `Records/ConfluenceCommentRecord.cs`

Fields: Id, PageId (FK), Author, CreatedAt, Body.
Index: PageId.

#### 5.1.4 Update `FtsSetup.cs`

Add FTS5 table and triggers:
- `confluence_pages_fts` — indexes: Title, BodyPlain, Labels
- Content-synced triggers on `confluence_pages`

#### 5.1.5 Update `DatabaseService.InitializeDatabase()`

Add Confluence table creation.

### Acceptance Criteria

- [ ] All Confluence CRUD operations work
- [ ] FTS5 triggers fire correctly

---

## 5.2 — Confluence Source Implementation

### Files to Create in `FhirAugury.Sources.Confluence/`

#### 5.2.1 NuGet references

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`

#### 5.2.2 `ConfluenceSource.cs`

Implements `IDataSource`:

- `SourceName` → `"confluence"`
- `DownloadAllAsync`:
  1. Fetch configured spaces via `GET /rest/api/space`
  2. For each space, enumerate pages via `GET /rest/api/space/{key}/content`
  3. For each page, fetch full content: `GET /rest/api/content/{id}?expand=body.storage,version,ancestors`
  4. Fetch comments: `GET /rest/api/content/{id}/child/comment`
  5. Strip Confluence storage XML → `BodyPlain`
  6. Upsert spaces, pages, comments
- `DownloadIncrementalAsync`:
  1. Use CQL: `lastModified >= '{since}' AND space in ({spaces})`
  2. Fetch updated pages and their comments
  3. Upsert changes
- `IngestItemAsync`:
  1. Accept page ID or URL
  2. Fetch single page with content and comments
  3. Upsert

#### 5.2.3 `ConfluenceSourceOptions.cs`

- `string BaseUrl` (default: `https://confluence.hl7.org`)
- `ConfluenceAuthMode AuthMode` (enum: `Basic`, `Cookie`)
- `string? Username`
- `string? ApiToken`
- `string? Cookie`
- `List<string> Spaces` (default: `["FHIR", "FHIRI"]`)
- `int PageSize` (default: 25)

#### 5.2.4 `ConfluenceContentParser.cs`

Parses Confluence storage format (XHTML/XML):
- Strips Confluence macros (`ac:structured-macro`, `ri:attachment`, etc.)
- Converts structured content to plain text
- Preserves table content
- Handles embedded images (strips, keeps alt text)

#### 5.2.5 `ConfluenceAuthHandler.cs`

HTTP message handler for Confluence authentication:
- Basic auth: `Authorization: Basic {base64(user:token)}`
- Cookie auth: `Cookie` header passthrough

### Acceptance Criteria

- [ ] Can authenticate with Confluence API
- [ ] Full download enumerates spaces → pages → comments
- [ ] Incremental download uses CQL lastModified filter
- [ ] Confluence storage format is correctly stripped to plain text
- [ ] Page hierarchy (ancestors) is preserved

---

## 5.3 — GitHub Database Tables

### Files to Create in `FhirAugury.Database/`

#### 5.3.1 `Records/GitHubRepoRecord.cs`

Fields: Id, Owner, Name, FullName, Description, LastFetchedAt.

#### 5.3.2 `Records/GitHubIssueRecord.cs`

Fields: Id, RepoFullName, Number, IsPullRequest, Title, Body, State,
Author, Labels, Assignees, Milestone, CreatedAt, UpdatedAt, ClosedAt,
MergeState, HeadBranch, BaseBranch.

Indexes: RepoFullName, State, UpdatedAt.

#### 5.3.3 `Records/GitHubCommentRecord.cs`

Fields: Id, IssueId (FK), RepoFullName, IssueNumber, Author, CreatedAt,
Body, IsReviewComment.

Indexes: IssueId, RepoFullName.

#### 5.3.4 Update `FtsSetup.cs`

Add FTS5 tables and triggers:
- `github_issues_fts` — indexes: Title, Body, Labels
- `github_comments_fts` — indexes: Body
- Content-synced triggers on both tables

#### 5.3.5 Update `DatabaseService.InitializeDatabase()`

Add GitHub table creation.

### Acceptance Criteria

- [ ] All GitHub CRUD operations work
- [ ] FTS5 triggers fire correctly
- [ ] Issues and PRs coexist in the same table (distinguished by `IsPullRequest`)

---

## 5.4 — GitHub Source Implementation

### Files to Create in `FhirAugury.Sources.GitHub/`

#### 5.4.1 NuGet references

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.*" />
```

Project references: `FhirAugury.Models`, `FhirAugury.Database`

#### 5.4.2 `GitHubSource.cs`

Implements `IDataSource`:

- `SourceName` → `"github"`
- `DownloadAllAsync`:
  1. For each configured repository
  2. Fetch repo metadata via `GET /repos/{owner}/{repo}`
  3. Paginate issues (includes PRs) via `GET /repos/{owner}/{repo}/issues?state=all`
  4. For each issue, fetch comments via `GET /repos/{owner}/{repo}/issues/{n}/comments`
  5. For PRs, also fetch review comments via `GET /repos/{owner}/{repo}/pulls/{n}/reviews`
  6. Rate-limit aware: check `X-RateLimit-Remaining`, sleep if approaching limit
  7. Upsert repos, issues, comments
- `DownloadIncrementalAsync`:
  1. Use `since` parameter on list endpoints: `GET /repos/{owner}/{repo}/issues?since={since}&state=all`
  2. Fetch comments for updated issues
  3. Upsert changes
- `IngestItemAsync`:
  1. Parse identifier as `owner/repo#number`
  2. Fetch single issue + comments
  3. Upsert

#### 5.4.3 `GitHubSourceOptions.cs`

- `string? PersonalAccessToken`
- `List<string> Repositories` (default: `["HL7/fhir", "HL7/fhir-ig-publisher"]`)
- `int PageSize` (default: 100)
- `int RateLimitBuffer` (default: 100, pause when remaining drops below this)

#### 5.4.4 `GitHubRateLimiter.cs`

Rate-limit handler:
- Reads `X-RateLimit-Remaining` and `X-RateLimit-Reset` headers
- Pauses requests when approaching the limit
- Logs rate-limit status

#### 5.4.5 `GitHubIssueMapper.cs`

Maps GitHub API JSON to `GitHubIssueRecord`:
- Detects PRs via `pull_request` field presence
- Extracts labels as comma-separated string
- Extracts assignees as comma-separated string
- Parses dates from ISO 8601 format

### Acceptance Criteria

- [ ] Can authenticate with GitHub API using PAT
- [ ] Full download paginates through all issues and PRs
- [ ] Rate limiting prevents 403 errors
- [ ] Incremental download uses `since` parameter
- [ ] PRs are stored with `IsPullRequest = true` and PR-specific fields

---

## 5.5 — Update Cross-Reference Linker

### Files to Update

#### 5.5.1 Update `CrossRefLinker.cs`

Add new patterns:
- Confluence page URLs: `https://confluence.hl7.org/.../{pageId}`
- GitHub issue/PR URLs: `https://github.com/{owner}/{repo}/issues/{number}`,
  `https://github.com/{owner}/{repo}/pull/{number}`
- GitHub `#number` references within GitHub issue/comment text

Update `RebuildAllLinksAsync` to scan all four source tables.

### Acceptance Criteria

- [ ] Confluence and GitHub URL patterns work
- [ ] GitHub `#number` references are linked within the same repo context
- [ ] Full cross-reference rebuild covers all four sources

---

## 5.6 — Update Unified Search

### Files to Update in `FhirAugury.Indexing/`

#### 5.6.1 Update `FtsSearchService.cs`

Add methods:
- `SearchConfluencePages(connection, query, space?, limit)`
- `SearchGitHubIssues(connection, query, repo?, state?, limit)`

Update `UnifiedSearch` to include all four sources.

#### 5.6.2 Update `Bm25Calculator.cs`

Update `BuildFullIndexAsync` to process Confluence and GitHub documents.

### Acceptance Criteria

- [ ] Unified search spans all four sources
- [ ] BM25 index includes Confluence and GitHub content
- [ ] Source-specific search filters work for new sources

---

## 5.7 — CLI & Service Extensions

### CLI Updates

#### 5.7.1 Update `Commands/DownloadCommand.cs`

Add `--source confluence` with Confluence auth options and
`--source github` with GitHub auth options.

#### 5.7.2 Update `Commands/SnapshotCommand.cs`

Add Confluence page and GitHub issue/PR snapshot rendering.

#### 5.7.3 Update all filter commands

Ensure `--source confluence` and `--source github` work throughout.

### Service Updates

#### 5.7.4 `Api/ConfluenceEndpoints.cs`

- `GET /api/v1/confluence/pages` — list/search pages
- `GET /api/v1/confluence/pages/{id}` — get page details

#### 5.7.5 `Api/GitHubEndpoints.cs`

- `GET /api/v1/github/issues` — list/search issues/PRs
- `GET /api/v1/github/issues/{id}` — get issue/PR details

#### 5.7.6 Register new sources in `Program.cs`

Add `ConfluenceSource` and `GitHubSource` to DI.

### Acceptance Criteria

- [ ] CLI supports all four sources for download, sync, search, snapshot
- [ ] Service API includes Confluence and GitHub endpoints
- [ ] `fhir-augury search` returns results from all four sources

---

## 5.8 — Tests

### New Test Files

#### `tests/FhirAugury.Database.Tests/`

- `ConfluencePageRecordTests.cs`
- `GitHubIssueRecordTests.cs`
- `Fts5ConfluenceTests.cs`
- `Fts5GitHubTests.cs`

#### `tests/FhirAugury.Sources.Tests/`

- `ConfluenceContentParserTests.cs` — storage format parsing
- `GitHubIssueMapperTests.cs` — JSON mapping, PR detection
- `GitHubRateLimiterTests.cs` — rate limit handling

#### `tests/FhirAugury.Indexing.Tests/`

- Update `CrossRefLinkerTests.cs` — add Confluence and GitHub patterns

#### `tests/FhirAugury.Integration.Tests/`

- `FourSourceSearchTests.cs` — unified search across all sources
- `ConfluenceEndpointTests.cs`
- `GitHubEndpointTests.cs`

### Test Data

- `tests/TestData/sample-confluence-page.json`
- `tests/TestData/sample-confluence-storage.xml`
- `tests/TestData/sample-github-issue.json`
- `tests/TestData/sample-github-pr.json`

### Acceptance Criteria

- [ ] All new tests pass
- [ ] All existing tests still pass
- [ ] Integration test verifies four-source unified search
