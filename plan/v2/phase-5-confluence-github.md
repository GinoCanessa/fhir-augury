# Phase 5: Confluence & GitHub Source Services

**Goal:** Complete four-source coverage. Both services follow the patterns
established in Phases 2–3. The GitHub service has the most complex new
functionality (repo cloning, commit file tracking, Jira reference extraction,
FHIR artifact mapping).

**Proposal references:**
[03-source-services](../../proposal/v2/03-source-services.md) (Confluence + GitHub sections),
[05-api-contracts](../../proposal/v2/05-api-contracts.md) (`confluence.proto`, `github.proto`)

**Depends on:** Phase 4

---

## 5.1 — Confluence Source Service

### 5.1.1 — Create `FhirAugury.Source.Confluence` project

```
FhirAugury.Source.Confluence/
├── Api/
│   ├── ConfluenceGrpcService.cs
│   └── ConfluenceHttpApi.cs
├── Ingestion/
│   ├── ConfluenceIngestionPipeline.cs
│   ├── ConfluenceSource.cs           # API client (adapted from v1)
│   ├── ConfluenceAuthHandler.cs      # Cookie/Basic auth (from v1)
│   ├── ConfluenceContentParser.cs    # Storage-format → plain text (from v1)
│   └── ConfluenceLinkExtractor.cs    # Extract internal page links
├── Cache/
│   └── ConfluenceCacheLayout.cs      # Per-space, per-page caching
├── Database/
│   ├── ConfluenceDatabase.cs
│   └── Records/
│       ├── ConfluenceSpaceRecord.cs
│       ├── ConfluencePageRecord.cs
│       ├── ConfluenceCommentRecord.cs
│       ├── ConfluencePageLinkRecord.cs
│       └── ConfluenceSyncStateRecord.cs
├── Indexing/
│   └── ConfluenceIndexer.cs
├── Workers/
│   └── ScheduledIngestionWorker.cs
├── Configuration/
│   └── ConfluenceServiceOptions.cs
├── Program.cs
└── appsettings.json
```

### 5.1.2 — Configuration schema

```json
{
  "Confluence": {
    "BaseUrl": "https://confluence.hl7.org",
    "AuthMode": "basic",
    "Spaces": ["FHIR", "FHIRI", "SOA"],
    "CachePath": "./cache/confluence",
    "DatabasePath": "./data/confluence.db",
    "SyncSchedule": "1.00:00:00",
    "Ports": { "Http": 5180, "Grpc": 5181 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 5,
      "BackoffBaseSeconds": 2,
      "MaxRetries": 3
    }
  }
}
```

### 5.1.3 — Database schema

**Tables:**

| Table | Purpose |
|-------|---------|
| `confluence_spaces` | Space metadata |
| `confluence_pages` | Page content with hierarchy (parent_id) |
| `confluence_comments` | Page comments |
| `confluence_page_links` | Internal page-to-page links |
| `confluence_pages_fts` | FTS5 on pages (body_plain, title, labels) |
| `index_keywords` | BM25 keyword scores |
| `sync_state` | Per-space ingestion state |

The `ConfluencePageRecord` stores both `body_storage` (original Confluence
storage format) and `body_plain` (converted plain text for FTS5 indexing).

### 5.1.4 — Content processing

Confluence pages use a proprietary storage format (HTML derivative with
Confluence-specific macros). During ingestion:

1. Preserve original storage-format content in `body_storage` field and cache
2. Convert to plain text for `body_plain` field (using
   `ConfluenceContentParser` from v1)
3. Index `body_plain` in FTS5

This two-step approach means the conversion pipeline can be updated without
re-downloading content from Confluence.

### 5.1.5 — Internal page link extraction

Parse Confluence storage-format content to extract internal links between
pages (Confluence macros, page links, etc.). Store in
`confluence_page_links` table for hierarchy navigation and `GetLinkedPages`
queries.

### 5.1.6 — Page hierarchy navigation

Support parent/child relationships:

- `GetPageChildren` — direct children of a page
- `GetPageAncestors` — breadcrumb path to root
- Navigate page tree within a space

### 5.1.7 — Implement `ConfluenceService` gRPC RPCs

| RPC | Implementation |
|-----|---------------|
| `GetPageComments` | Stream comments for a page |
| `GetPageChildren` | Direct children of a page |
| `GetPageAncestors` | Breadcrumb path to root |
| `ListSpaces` | Stream all spaces |
| `GetLinkedPages` | Pages linked from/to a given page |
| `GetPagesByLabel` | Filter pages by label |
| `GetPageSnapshot` | Render page as rich markdown |

### 5.1.8 — Cache layout

```
cache/confluence/
├── _meta.json
├── spaces/
│   ├── FHIR.json         # Space metadata
│   ├── FHIR/             # Pages in this space
│   │   ├── {pageId}.json # One file per page
│   │   └── ...
│   ├── FHIRI.json
│   └── FHIRI/
│       └── ...
```

Per-page caching — pages are large, individually addressable, and updated
independently. Organized under parent space.

### 5.1.9 — Tests

- Content processing (storage format → plain text)
- Page hierarchy navigation
- Internal link extraction
- Space/page caching
- gRPC endpoint tests

---

## 5.2 — GitHub Source Service

The GitHub service is the most complex source, with several capabilities
not present in v1.

### 5.2.1 — Create `FhirAugury.Source.GitHub` project

```
FhirAugury.Source.GitHub/
├── Api/
│   ├── GitHubGrpcService.cs
│   └── GitHubHttpApi.cs
├── Ingestion/
│   ├── GitHubIngestionPipeline.cs
│   ├── GitHubSource.cs             # API client (adapted from v1)
│   ├── GitHubIssueMapper.cs        # JSON→record mapping (from v1)
│   ├── GitHubRateLimiter.cs        # Rate limit header handling (from v1)
│   ├── GitHubRepoCloner.cs         # Local git clone management (new)
│   ├── GitHubCommitFileExtractor.cs # git log --name-status (new)
│   └── JiraRefExtractor.cs         # Jira reference extraction (new)
├── Cache/
│   └── GitHubCacheLayout.cs
├── Database/
│   ├── GitHubDatabase.cs
│   └── Records/
│       ├── GitHubRepoRecord.cs       # includes has_issues flag
│       ├── GitHubIssueRecord.cs      # issues + PRs (is_pull_request flag)
│       ├── GitHubCommentRecord.cs
│       ├── GitHubCommitRecord.cs
│       ├── GitHubCommitFileRecord.cs # Changed files per commit (new)
│       ├── GitHubCommitPrLinkRecord.cs # Commit ↔ PR mapping (new)
│       ├── GitHubJiraRefRecord.cs    # Jira refs from GitHub content (new)
│       ├── GitHubSpecFileMapRecord.cs # Artifact→file mapping (new)
│       └── GitHubSyncStateRecord.cs
├── Indexing/
│   ├── GitHubIndexer.cs
│   ├── ArtifactFileMapper.cs        # FHIR artifact file mapping (new)
│   └── JiraRefResolver.cs           # #NNN disambiguation (new)
├── Workers/
│   └── ScheduledIngestionWorker.cs
├── Configuration/
│   └── GitHubServiceOptions.cs
├── Program.cs
└── appsettings.json
```

### 5.2.2 — Configuration schema

```json
{
  "GitHub": {
    "RepoMode": "core",
    "Repositories": ["HL7/fhir"],
    "AdditionalRepositories": [],
    "ManualLinks": [],
    "Auth": {
      "Token": null,
      "TokenEnvVar": "GITHUB_TOKEN"
    },
    "CachePath": "./cache/github",
    "DatabasePath": "./data/github.db",
    "SyncSchedule": "02:00:00",
    "Ports": { "Http": 5190, "Grpc": 5191 },
    "RateLimiting": {
      "MaxRequestsPerSecond": 10,
      "BackoffBaseSeconds": 5,
      "MaxRetries": 5,
      "RespectRateLimitHeaders": true
    }
  }
}
```

Three repository modes:
- `core` — only `HL7/fhir`
- `explicit` — listed repositories only
- `all` — discover from JIRA-Spec-Artifacts via Jira service

### 5.2.3 — Database schema

**Tables:**

| Table | Purpose |
|-------|---------|
| `github_repos` | Repository metadata (including `has_issues` flag) |
| `github_issues` | Issues and PRs (`is_pull_request` flag) |
| `github_comments` | Issue/PR comments |
| `github_commits` | Commit metadata (SHA, message, author, date) |
| `github_commit_files` | Changed files per commit (path, change type) — **new** |
| `github_commit_pr_links` | Commit ↔ PR bidirectional mapping — **new** |
| `github_jira_refs` | Jira references from GitHub content — **new** |
| `github_spec_file_map` | FHIR artifact → repo file path mapping — **new** |
| `github_issues_fts` | FTS5 on issue/PR title and body |
| `github_comments_fts` | FTS5 on comment content |
| `github_commits_fts` | FTS5 on commit messages — **new** |
| `index_keywords` | BM25 keyword scores |
| `sync_state` | Per-repo ingestion state |

### 5.2.4 — Repository cloning

**New capability.** The GitHub service clones tracked repositories locally
under the cache directory:

```
cache/github/repos/HL7_fhir/clone/   # Local git clone
```

- `git clone` on first run
- `git fetch` + `git merge` for incremental updates
- The clone serves as cache for repo content and source for commit file data

### 5.2.5 — Commit file extraction

**New capability.** After cloning/updating a repo:

1. Run `git log --name-status` (or equivalent) against the local clone
2. Extract which files each commit modified
3. Populate `github_commit_files` table with (commit SHA, file path,
   change type)
4. This enables artifact-scoped queries without per-commit API calls

### 5.2.6 — Commit ↔ PR linking

**New capability.** Build bidirectional mapping between commits and PRs:

- Merge commit metadata (from PR merge events)
- Squash-merge SHAs
- PR timeline events
- `#NNN` references in commit messages (when GitHub Issues is enabled)

Stored in `github_commit_pr_links` table. Enables:
- "Find the PR that introduced this commit"
- "Find all commits in this PR"

### 5.2.7 — Jira reference extraction

**New capability.** Scan GitHub content for Jira issue references:

**Patterns:**
- `FHIR-{N}`, `JF-{N}`, `J#{N}`, `GF-{N}` (explicit Jira prefixes)
- Bare `#NNN` (disambiguation based on `has_issues` flag — see 5.2.8)

**Validation:** Before each indexing pass, fetch the current set of valid
Jira issue numbers from the Jira service via `GetIssueNumbers` gRPC call.
Validate extracted references against this list to filter false positives.

Store validated references in `github_jira_refs` table.

### 5.2.8 — `#NNN` reference disambiguation

**New capability.** Bare `#NNN` references are ambiguous — they could refer
to GitHub issues/PRs or Jira tickets. Resolution depends on the repository's
`has_issues` flag:

| `has_issues` | Strategy |
|:---:|---|
| **off** | Treat all `#NNN` as Jira references. Validate against Jira issue list. |
| **on** | Check against GitHub issue/PR numbers first. If no match, check Jira list. |

### 5.2.9 — FHIR artifact file mapping

**New capability (Phase 1 — spec-artifacts based).**

Build `github_spec_file_map` table by reconciling JIRA-Spec-Artifacts data
(from Jira service) against the repository's file tree:

1. Fetch repo file tree via Git Trees API (single API call per repo)
2. Match artifact keys to directories under `source/` (core repo) or
   `input/` (IG repos)
3. Match artifact IDs to specific files
   (e.g., `structuredefinition-Patient.xml`)
4. Match page URLs to `.html` files or directories
5. Store mappings in `github_spec_file_map`

Resolution strategy is configurable per repository (core FHIR repo vs.
IG Publisher convention).

### 5.2.10 — Implement `GitHubService` gRPC RPCs

| RPC | Implementation |
|-----|---------------|
| `GetIssueComments` | Stream comments for an issue/PR |
| `GetPullRequestDetails` | Full PR details (additions, deletions, merge state) |
| `GetRelatedCommits` | Commits related to an issue (via #NNN refs, Jira refs) |
| `GetPullRequestForCommit` | Find the PR that introduced a commit |
| `GetCommitsForPullRequest` | List all commits in a PR |
| `SearchCommits` | FTS5 search across commit messages |
| `GetJiraReferences` | Stream Jira references found in GitHub content |
| `QueryByArtifact` | Filter commits by FHIR artifact file paths (see 5.2.11) |
| `ListRepositories` | Stream tracked repos |
| `ListByLabel` | Filter issues/PRs by label |
| `ListByMilestone` | Group issues/PRs by milestone |
| `GetIssueSnapshot` | Rich markdown snapshot |

### 5.2.11 — Implement `QueryByArtifact`

Uses `github_spec_file_map` + `github_commit_files` to find commits whose
changed-file lists intersect with mapped file paths for a given FHIR
artifact, page, or element:

1. Resolve the query parameter (artifact_key, artifact_id, page_key, or
   element_path) to a set of file paths via `github_spec_file_map`
2. JOIN with `github_commit_files` to find matching commits
3. Optionally JOIN with `github_commit_pr_links` to include associated PRs
4. Apply date filters and limit

### 5.2.12 — Rate limiting

Respect GitHub's `X-RateLimit-*` response headers:
- `X-RateLimit-Remaining` — auto-back-off when low
- `X-RateLimit-Reset` — wait until reset time
- Configurable via `RespectRateLimitHeaders` setting

Optional authentication via PAT (5,000 req/hr vs. 60 req/hr unauthenticated).

### 5.2.13 — Tests

- Issue/PR mapping
- Commit file extraction from git log
- Jira reference extraction and validation
- `#NNN` disambiguation logic
- Artifact file mapping resolution
- `QueryByArtifact` query building
- Rate limit header handling
- gRPC endpoint tests

---

## 5.3 — Orchestrator Updates

### 5.3.1 — Add Confluence and GitHub gRPC clients

Register gRPC clients for Confluence (5181) and GitHub (5191) services.
Update configuration to include all four sources.

### 5.3.2 — Extend cross-reference patterns

Add Confluence URL and GitHub URL/reference patterns to the cross-reference
scanner. The full set of patterns is now:

- Jira keys: `\bFHIR-\d+\b`
- Jira URLs: `https?://jira\.hl7\.org/browse/(FHIR-\d+)`
- Zulip URLs: `https?://chat\.fhir\.org/#narrow/stream/(\d+)`
- Confluence URLs: `https?://confluence\.hl7\.org/.*/(\d+)`
- GitHub URLs: `https?://github\.com/HL7/[^/]+/(?:issues|pull)/(\d+)`
- GitHub short refs: `HL7/[a-zA-Z0-9_-]+#\d+`

### 5.3.3 — Update unified search

Add Confluence and GitHub to fan-out search. Update freshness weights:
- Confluence: 0.1 (specification pages remain relevant regardless of age)
- GitHub: 1.0 (moderate temporal relevance)

### 5.3.4 — Update orchestrator HTTP API

Add Confluence and GitHub proxied endpoints:
- `POST /api/v1/github/artifact-query`

---

## 5.4 — MCP & CLI Updates

### 5.4.1 — Add Confluence MCP tools

| Tool | Source / RPC |
|------|-------------|
| `search_confluence` | Confluence `Search` |
| `get_confluence_page` | Orchestrator `GetItem` (proxied) |
| `list_confluence_spaces` | Confluence `ListSpaces` |
| `snapshot_confluence_page` | Orchestrator `GetSnapshot` (proxied) |

### 5.4.2 — Add GitHub MCP tools

| Tool | Source / RPC |
|------|-------------|
| `search_github` | GitHub `Search` |
| `get_github_issue` | Orchestrator `GetItem` (proxied) |
| `query_github_artifact` | GitHub `QueryByArtifact` |
| `list_github_repos` | GitHub `ListRepositories` |
| `snapshot_github_issue` | Orchestrator `GetSnapshot` (proxied) |
| `get_pr_for_commit` | GitHub `GetPullRequestForCommit` |
| `get_commits_for_pr` | GitHub `GetCommitsForPullRequest` |
| `search_commits` | GitHub `SearchCommits` |
| `get_jira_references` | GitHub `GetJiraReferences` |

### 5.4.3 — Add CLI commands

Add `query-github-artifact` command to the CLI command tree. Update
existing commands to include Confluence and GitHub in source lists.

### 5.4.4 — Interface parity validation

Verify all four sources are available through all three interfaces
(HTTP, MCP, CLI).

---

## Phase 5 Verification

- [ ] All four source services start independently on their respective ports
- [ ] Confluence handles page hierarchy navigation correctly
- [ ] Confluence storage-format content converts cleanly to plain text
- [ ] GitHub clones repositories and extracts commit file data
- [ ] GitHub correctly extracts and validates Jira references
- [ ] GitHub `#NNN` disambiguation works based on `has_issues` flag
- [ ] GitHub `QueryByArtifact` returns commits for FHIR artifacts
- [ ] Orchestrator links all four sources via cross-references
- [ ] Unified search spans all four sources with correct freshness decay
- [ ] MCP tools and CLI commands cover all four sources
- [ ] Interface parity maintained across HTTP, MCP, and CLI
- [ ] All tests pass
