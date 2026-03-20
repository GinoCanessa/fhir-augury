# FHIR Augury v2 Proposal — Review

> **Status:** All decisions below have been applied to the proposal documents
> (01–08). This document is retained as a record of the review process and
> decisions made.

Cross-document review of the v2 proposal (01–08) for internal consistency,
completeness, and open design questions.

---

## Inconsistencies

### 1. gRPC Service Naming (03 vs 05)

The service names in proto definitions differ between the conceptual design
(03-source-services) and the formal contracts (05-api-contracts):

| Source | In 03 | In 05 |
|--------|-------|-------|
| Jira | `JiraService` | `JiraService` ✓ |
| Zulip | `ZulipService` | `ZulipSourceService` ✗ |
| Confluence | `ConfluenceService` | `ConfluenceSourceService` ✗ |
| GitHub | `GitHubSourceService` | `GitHubSourceService` ✓ |

**Decision needed:** Standardize on `{Source}Service` or `{Source}SourceService`.

**Decision:** Standardize on `{Source}Service`.

### 2. Request/Response Type Names (03 vs 05)

The same RPCs use different request and return types across documents.
Systematic pattern: 03 uses shorter, non-prefixed names; 05 prefixes with the
source name. Both approaches are reasonable, but they need to agree.

**Examples (Jira):**

| RPC | 03 Request Type | 05 Request Type | 03 Return | 05 Return |
|-----|----------------|-----------------|-----------|-----------|
| `GetIssueComments` | `GetCommentsRequest` | `JiraGetCommentsRequest` | `stream JiraComment` | `stream Comment` |
| `GetIssueLinks` | `GetLinksRequest` | `JiraGetLinksRequest` | `IssueLinksResponse` | `JiraIssueLinksResponse` |
| `ListByWorkGroup` | `WorkGroupRequest` | `JiraWorkGroupRequest` | `stream JiraIssueSummary` | `stream ItemSummary` |
| `ListBySpecification` | `SpecificationRequest` | `JiraSpecificationRequest` | `stream JiraIssueSummary` | `stream ItemSummary` |
| `QueryIssues` | `JiraQueryRequest` | `JiraQueryRequest` ✓ | `stream JiraIssueSummary` | `stream ItemSummary` |

This same pattern repeats for Zulip (`GetThreadRequest` vs `ZulipGetThreadRequest`,
`stream ZulipMessageSummary` vs `stream ItemSummary`) and Confluence
(`GetCommentsRequest` vs `ConfluenceGetCommentsRequest`,
`stream ConfluencePageSummary` vs `stream ItemSummary`).

**Decision needed:** Should source-specific RPCs return source-specific types
(e.g., `JiraIssueSummary`) or generic common types (e.g., `ItemSummary`)?
Source-specific types carry richer data; common types simplify the
orchestrator. A possible middle ground: source-specific types that embed
`ItemSummary` plus extra fields.

**Decision:** Each of the specific types derive from the common `ItemSummary`, so
the source-specific RPCs should return source-specific types (e.g., `JiraIssueSummary`).

### 3. RPCs Defined in 03 but Missing from 05

Several RPCs described in the source service designs (03) are absent from the
formal proto definitions (05):

**Jira:**
- `ListSpecArtifacts(ListSpecArtifactsRequest) returns (stream SpecArtifactEntry)`
- `GetIssueNumbers(GetIssueNumbersRequest) returns (JiraIssueNumbersResponse)`
- `GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse)`

**Zulip:**
- `GetThreadSnapshot(SnapshotRequest) returns (SnapshotResponse)`

**Confluence:**
- `GetPageSnapshot(SnapshotRequest) returns (SnapshotResponse)`

**GitHub:**
- `GetPullRequestForCommit(GetPRForCommitRequest) returns (GitHubPullRequest)`
- `GetCommitsForPullRequest(GetCommitsForPRRequest) returns (stream GitHubCommit)`
- `SearchCommits(SearchRequest) returns (SearchResponse)`
- `GetJiraReferences(GetJiraRefsRequest) returns (stream GitHubJiraRef)`
- `GetIssueSnapshot(SnapshotRequest) returns (SnapshotResponse)`

These are referenced elsewhere (e.g., `ListSpecArtifacts` is used by the
GitHub service for repo discovery, `GetIssueNumbers` is used for Jira
reference validation). They need to be added to 05.

**Decision:** Add the missing definitions to 05.

### 4. Snapshot RPC: Common vs Source-Specific

The snapshot capability is defined inconsistently:

- **05** defines `GetSnapshot` as a common RPC in `SourceService`
  (source_service.proto).
- **03** defines source-specific snapshot RPCs with different names:
  `GetIssueSnapshot` (Jira, GitHub), `GetThreadSnapshot` (Zulip),
  `GetPageSnapshot` (Confluence).
- **05's** source-specific proto files don't include any snapshot RPCs.

**Decision needed:** Keep snapshot in the common `SourceService` contract
(cleaner), or define source-specific snapshot RPCs (allows different
request parameters per source)? If common, remove the source-specific
variants from 03. If source-specific, remove `GetSnapshot` from 05's
`source_service.proto`.

**Decision:** There should be a generic `GetSnapshot` that describes
how to request the specific content. E.g., `Jira/{ticket identifier}` vs.
`Confluence/{page id}` vs. `GitHub/{org}/{repo}/{file path..}`. The
individual services should define specific calls that the orchestrator
can map to. The individual services can include additional parameters
in their calls, and callers can use those APIs directly if necessary.

### 5. `GetContent` Listed as Common Capability but Not in Proto

The common capabilities table in 03 lists `GetContent` — "Retrieve the full
content/body of an item (for rendering or LLM consumption)." However, 05's
`source_service.proto` has no `GetContent` RPC. Instead, `GetItem` has an
`include_content` boolean flag.

**Resolution:** Either add `GetContent` to the proto or remove it from the
capabilities table and note that content is retrieved via `GetItem` with
`include_content = true`.

**Decision:** `GetContent` should be added to the contracts and mirror
the process used for `GetSnapshot`, as described above.

### 6. `StreamSearchableText` Missing from 03's Capabilities Table

05's `source_service.proto` defines `StreamSearchableText` as a common
capability. 04 describes the orchestrator using it for cross-reference
scanning. But 03's "Capabilities Every Source Service Must Provide" table
omits it.

**Resolution:** Add `StreamSearchableText` to 03's capabilities table.

**Decision:** Yes, add this.

### 7. Orchestrator Missing Proxy RPCs (04/05 vs 07)

The MCP code example in 07 shows calling
`orchestrator.GetItemAsync(new GetItemRequest { Source = "jira", ... })`,
implying the orchestrator proxies `GetItem` calls to source services.
However, the orchestrator proto in 05 defines only:

- `UnifiedSearch`
- `FindRelated`
- `GetCrossReferences`
- `TriggerSync`
- `GetServicesStatus`
- `TriggerXRefScan`

There is no `GetItem`, `GetSnapshot`, or source-specific query proxy RPCs
in the orchestrator contract — yet the HTTP API table in 05 lists
`POST /api/v1/jira/query`, `POST /api/v1/zulip/query`, and
`POST /api/v1/github/artifact-query` on the orchestrator.

**Decision needed:** Should the orchestrator proxy all source-service RPCs
(acting as a single gateway), or should MCP/CLI connect directly to source
services for source-specific operations? This is a fundamental routing
decision that affects the entire client architecture. Options:

1. **Gateway model:** Orchestrator proxies everything. Simpler for clients
   (single endpoint), but orchestrator proto needs many more RPCs.
2. **Direct access model:** MCP/CLI connect to both orchestrator (for
   cross-source ops) and individual source services (for source-specific ops).
   More client config, but orchestrator stays focused.
3. **Hybrid:** Orchestrator proxies common operations (`GetItem`,
   `GetSnapshot`) but clients connect directly for source-specific queries.

**Decision:** Option 3, the hybrid approach.

### 8. MCP Parity Matrix Issues (07)

Several rows in the Interface Parity Matrix conflate distinct operations:

| Capability | MCP Tool | Actual RPC | Issue |
|------------|----------|------------|-------|
| Rebuild from cache | `trigger_sync` (type=rebuild) | `RebuildFromCache` | `TriggerSync` only supports "incremental" and "full" types — rebuild is a separate RPC |
| Cross-ref scan | `trigger_sync` (type=xref-scan) | `TriggerXRefScan` | Same — xref-scan is a separate orchestrator RPC, not a sync type |
| Service health | `get_stats` | `GetServicesStatus` | Health and stats are separate endpoints in 04/05 |

The MCP tool `trigger_sync` is overloaded to cover three distinct operations
that have separate RPCs. Either add dedicated MCP tools or add a `type`
discriminator to the `TriggerSyncRequest` proto.

**Decision:** Add a `type` discriminator.

### 9. CLI Command Tree Incomplete (07)

The CLI command structure in 07 shows generic commands (`search`, `related`,
`get`, `snapshot`, `xref`, `ingest`, `list`, `services`) but the parity
matrix references source-specific commands not in the tree:

- `fhir-augury query-jira` (Jira structured query)
- `fhir-augury query-zulip` (Zulip structured query)
- `fhir-augury query-github-artifact` (GitHub artifact query)

These should be added to the command tree, or the parity matrix should
reference a generic `query` subcommand with `--source` flags.

**Decision:** Add the commands to the tree.

### 10. `github_repos.has_issues` Missing from Proto (03 vs 05)

03 describes using the `has_issues` flag from `github_repos` for `#NNN`
reference disambiguation. But 05's `GitHubRepo` message only has:
`full_name`, `description`, `issue_count`, `pr_count`, `url` — no
`has_issues` field.

**Resolution:** Add `has_issues` to the `GitHubRepo` proto message. This is
an internal detail (used during indexing), but it should be exposed for
transparency and debugging.

**Decision:** Agreed, add `has_issues`.

### 11. Jira Cache Layout: `_workgroups.xml` (03 vs 06)

03's Jira cache format (line 231) lists `_workgroups.xml` under
`jira-spec-artifacts/xml/`, but 06's Jira cache layout doesn't include it.

**Resolution:** Add `_workgroups.xml` to 06's cache layout.

**Decision:** Agreed, add `_workgroups.xml`.

### 12. GitHub Commit Changed-Files Data Not Modeled (03)

03 describes `QueryByArtifact` finding commits whose changed-file lists
intersect with mapped file paths. This requires knowing which files each
commit changed, but there is no `github_commit_files` table in the data
model (03), no `changed_files` field in the `GitHubCommit` proto message
(05), and no description of how this data is ingested or cached.

**Resolution:** Define a `github_commit_files` table (or equivalent) and
describe how changed-file data is obtained (Git Trees API? Commit detail
API?) and cached.

**Decision:** Yes, add the necessary table. The repo is cloned locally, 
so data can be retrieved via the appropriate commands. The cloned
repo serves as the cache, document the process for discovery and indexing.

---

## Open Design Questions

### Q1: GitHub Authentication

03 specifies auth strategies for Jira (cookie), Zulip (credential file),
and Confluence (basic), but the GitHub service configuration has no auth
settings. GitHub APIs require authentication for reasonable rate limits
(unauthenticated: 60 req/hr; authenticated: 5,000 req/hr) and private repo
access.

**Needs:** Auth configuration (PAT, GitHub App, OAuth) in the GitHub service
config schema.

**Decision:** We are not writing to github and all repositories we are using
are public. There should be optional support for GitHub tokens for when
an authenticated user is desired.

### Q2: Post-Ingestion Notification Mechanism

04 states the orchestrator triggers a cross-reference scan "after a source
service completes an ingestion run (notified via gRPC streaming or polling)."
Which mechanism is intended?

Options:
1. **Polling** — Orchestrator periodically calls `GetIngestionStatus` on each
   source.
2. **Server-streaming** — Source service streams status updates to the
   orchestrator via a long-lived gRPC stream.
3. **Callback** — Source service calls an orchestrator RPC when ingestion
   completes.

None of these are defined in the proto contracts.

**Decision:** The individual services should callback into the orchestrator
when an ingestion operation completes. The orchestrator should periodically
poll the status while the ingestion task is running (configurable, default
to every 30 seconds) to ensure the task has not errored.

### Q3: Error Handling During Fan-Out Search

When the orchestrator fans out a unified search to all source services
(04), what happens if one source service is down or times out?

Options:
1. Return partial results from healthy services with a warning.
2. Fail the entire search.
3. Return cached/stale results for the failed source.

This is not specified anywhere.

**Decision:** Option 1, return partial results with a note.

### Q4: In-Process Deployment Mechanics

02 shows an in-process hosting snippet where all services register in a
single `WebApplication`. But each service needs its own gRPC server on
separate ports. How does this work in practice?

Options:
1. In-process channels (no network) — services call each other directly.
2. Multiple Kestrel endpoints on different ports in one process.
3. A single gRPC server that routes to all services by service name.

The in-process model is listed as a deployment option for development, so
this needs to be specified enough to implement.

**Decision:** Option 2 - multiple endpoints in the same process.

### Q5: Database Schema Versioning

Each source service owns its own SQLite database. How are schema versions
tracked? When a service updates its schema, how does it know whether to
rebuild from cache vs. migrate in place?

Rebuild-from-cache is the stated strategy for schema changes, but there's no
mechanism described for detecting that a schema change has occurred (e.g., a
version table, schema hash comparison, or migration framework).

**Decision:** The structure is very stable once deployed and the data
is relatively cheap to re-index (as long as the contents are cached locally).
If there are schema changes, the SQLite database file will be deleted as 
part of deployment.

### Q6: Rate Limiting for Source APIs

03 does not detail rate limiting strategies for any source API. Zulip and
GitHub have documented rate limits. For GitHub in `all` repo mode, ingesting
dozens of repositories will require careful rate limit management.

**Needs:** Rate limiting strategy per source — at minimum for GitHub
(respecting `X-RateLimit-*` headers, backoff, token rotation) and Zulip.

**Decision:** Add rate-limiting configuration for each service, with appropriate
default values.

### Q7: GitHub MCP Tool Coverage

07's MCP tools table includes GitHub tools for search, get issue, artifact
query, list repos, and snapshot — but omits tools for several GitHub-specific
RPCs defined in 03:

- `GetPullRequestForCommit` — find the PR that introduced a commit
- `GetCommitsForPullRequest` — list commits in a PR
- `SearchCommits` — full-text search across commit messages
- `GetJiraReferences` — find Jira references in GitHub content

Are these intentionally omitted from MCP (and thus not available to LLM
agents), or should they be added?

**Decision:** Add the calls.

### Q8: Confluence HTML-to-Plain-Text Conversion

Confluence pages use a proprietary storage format. 05's `ConfluencePage`
proto has `body_plain` and `body_storage` fields. 03 mentions
"storage-format parsing" as a Confluence-specific feature.

**Needs:** Where does the conversion happen — at ingestion time (stored in
`body_plain`) or at query time? What library/approach is used? This affects
FTS5 indexing quality since the plain-text extraction needs to handle
Confluence macros, tables, and embedded content.

**Decision:** The Confluence format is a derivative of HTML. Cached data
should persist in that format and be converted during ingestion. This
ensures that the ingestion process can be updated without re-downloading
from Confluence.

### Q9: Zulip HTML vs Plain Text

05's `ZulipMessage` has both `content` (plain text) and `content_html`
(original HTML). Which field is indexed in FTS5? Is the plain text
derived from HTML at ingestion time, or does the Zulip API provide both?

**Decision:** HTML content will be ingested, but needs to be processed in
a way similar to Jira content. 

### Q10: Orchestrator `GetItem` Proxying

Related to inconsistency #7 — if the orchestrator does proxy `GetItem`, the
response for a Jira issue vs. a Zulip message vs. a GitHub issue will have
very different shapes. The current generic `ItemResponse` in
`source_service.proto` uses `map<string, string> metadata` as a catch-all.

Is this sufficient, or should the orchestrator return source-specific
types? If generic, how do MCP tools format rich source-specific data from
a flat key-value map?

**Decision:** The hybrid approach was selected as resolution for inconsistency #7.
The generic calls will return a generic view of the content, and the individual
services are available for tailored responses.