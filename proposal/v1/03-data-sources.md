# FHIR Augury — Data Sources

## 1. Zulip (chat.fhir.org)

### Client Library

`zulip-cs-lib` NuGet package (authored by GinoCanessa). Provides typed C#
access to the Zulip REST API.

### API Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/streams` | List all streams (filter to `is_web_public`) |
| `GET /api/v1/messages` | Fetch messages per stream with narrow filters |
| `GET /api/v1/users` | User directory (optional, for sender metadata) |

### Authentication

HTTP Basic Auth with `email:apiKey`. Credentials stored in a `zuliprc`-format
file or passed via CLI options / environment variables.

### Download Strategy

1. **Full download:** Iterate all public streams. For each stream, paginate
   through messages in ascending order using the last-seen message ID as
   anchor. Batch size: 1000 messages per request.
2. **Incremental:** For each stream, record the highest message ID seen.
   On update, fetch messages with anchor = last ID + 1. Also re-fetch
   messages updated since last sync (edited messages).
3. **On-demand:** Accept a `stream:topic` identifier and fetch that single
   topic thread.

### Data Extracted

| Field | Source |
|-------|--------|
| Message ID | `message.id` |
| Stream ID/name | `message.stream_id`, stream name from stream list |
| Topic | `message.subject` |
| Sender | `message.sender_full_name`, `sender_email`, `sender_id` |
| Content (raw) | `message.content` (HTML) |
| Content (plain) | HTML-stripped for indexing |
| Timestamp | `message.timestamp` (Unix epoch) |
| Reactions | `message.reactions` (JSON array) |

### Estimated Volume

~1M+ messages, ~100+ public streams. Database size: ~1.5–2 GB.

---

## 2. Jira (jira.hl7.org)

### API Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `GET /rest/api/2/search` | Search issues via JQL with pagination |
| `GET /rest/api/2/issue/{key}` | Single issue with all fields |
| `GET /sr/jira.issueviews:searchrequest-xml/...` | XML bulk export (alternate) |

The system supports both JSON REST API and XML export approaches. The JSON
API is preferred for incremental updates; the XML export is useful for
initial bulk loads.

### Authentication

Cookie-based session authentication (browser cookie pass-through) or HTTP
Basic Auth with API token. Cookie approach mirrors the pattern from
JiraFhirUtils. API token approach is preferred for automated use.

### JQL Queries

```
project = "FHIR Specification Feedback"
  AND updated >= '{since}' ORDER BY updated ASC
```

With optional filters for specification, work group, status, etc.

### Custom Field Mapping

HL7 Jira uses extensive custom fields. Key mappings (from reference implementations):

| Custom Field ID | Semantic Name |
|-----------------|---------------|
| `customfield_11302` | Specification |
| `customfield_11400` | Work Group |
| `customfield_11808` | Raised in Version |
| `customfield_10618` | Resolution Description |
| `customfield_11300` | Related Artifacts |
| `customfield_10902` | Selected Ballot |
| `customfield_14905` | Related Issues |
| `customfield_14909` | Duplicate Of |
| `customfield_14807` | Applied Versions |
| `customfield_14910` | Change Type |
| `customfield_10001` | Impact / Breaking Change |
| `customfield_14907` | In-Person Resolution |
| `customfield_14908` | Retro Ballot |
| `customfield_14900` | Considered Related Issues |
| `customfield_10600` | Triaged? |
| `customfield_14904` | Considered Issues |
| `customfield_10900` | Specification Link |
| `customfield_14911` | Subspec |

### Data Extracted

- Issue metadata: key, type, priority, status, resolution, assignee, reporter
- Dates: created, updated, resolved, due
- Content: summary/title, description, resolution description
- Custom fields: specification, work group, ballot, versions, etc.
- Comments: author, date, body (per comment)
- Links: issue links, related issues, duplicates

### Estimated Volume

~48k+ issues with comments. Database size: ~500 MB–1 GB.

---

## 3. Confluence (confluence.hl7.org)

### API Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `GET /rest/api/space` | List all spaces (filter to FHIR-related) |
| `GET /rest/api/space/{key}/content` | List pages in a space |
| `GET /rest/api/content/{id}?expand=body.storage,version,ancestors` | Page content |
| `GET /rest/api/content/search?cql=...` | CQL-based content search |
| `GET /rest/api/content/{id}/child/comment` | Page comments |
| `GET /rest/api/content/{id}/history` | Version history |

### Authentication

HTTP Basic Auth with username + API token (Atlassian Cloud style) or
session cookies for on-premise instances.

### Target Spaces

Primary FHIR-related Confluence spaces:

| Space Key | Description |
|-----------|-------------|
| `FHIR` | Main FHIR project space — meeting notes, governance, WG pages |
| `FHIRI` | FHIR Infrastructure working group |
| `SOA` | Security / OAuth working group |

(Configurable — users can add additional space keys.)

### Download Strategy

1. **Full download:** Enumerate spaces → enumerate pages per space → download
   each page's storage-format body, metadata, labels, and comments.
2. **Incremental:** Use CQL `lastModified >= '{since}'` to find updated pages.
3. **On-demand:** Accept a page ID or URL and fetch that single page.

### Data Extracted

| Field | Source |
|-------|--------|
| Page ID | `content.id` |
| Space key | `content.space.key` |
| Title | `content.title` |
| Body (storage) | `content.body.storage.value` (Confluence storage XML) |
| Body (plain) | HTML/XML stripped for indexing |
| Labels | `content.metadata.labels` |
| Version | `content.version.number`, `when`, `by` |
| Ancestors | `content.ancestors` (page hierarchy) |
| URL | Constructed from base URL + space + title |
| Comments | Child comments with author, date, body |

### Estimated Volume

Varies. Primary FHIR space likely has several thousand pages. Database size:
~200–500 MB.

---

## 4. GitHub (github.com/HL7)

### API / Client

GitHub REST API v3 and/or GraphQL API v4. Uses `HttpClient` with
token-based authentication. (The `Octokit` NuGet package is an option,
but raw `HttpClient` keeps dependencies minimal and aligns with the
other sources.)

### Target Repositories

Configurable. Default set:

| Repository | Content |
|------------|---------|
| `HL7/fhir` | Core FHIR specification source |
| `HL7/fhir-ig-publisher` | IG Publisher tool |
| `HL7/ig-template-base` | IG templates |
| Additional IGs | User-configurable list |

### API Endpoints Used

| Endpoint | Purpose |
|----------|---------|
| `GET /repos/{owner}/{repo}/issues` | List issues (includes PRs) |
| `GET /repos/{owner}/{repo}/issues/{n}/comments` | Issue/PR comments |
| `GET /repos/{owner}/{repo}/pulls/{n}` | PR details |
| `GET /repos/{owner}/{repo}/pulls/{n}/reviews` | PR reviews |
| `GET /repos/{owner}/{repo}/commits` | Commit log |
| `GET /search/issues?q=...` | Cross-repo search |

### Authentication

Personal Access Token (PAT) via `Authorization: Bearer {token}` header.
Stored in configuration file or environment variable.

### Download Strategy

1. **Full download:** For each configured repo, paginate through all issues,
   PRs, and their comments. Rate-limit aware (GitHub gives 5000 req/hr).
2. **Incremental:** Use `since` parameter on list endpoints to fetch items
   updated after last sync.
3. **On-demand:** Accept `owner/repo#number` identifier for a single issue/PR.

### Data Extracted

- Issues: number, title, body, state, labels, assignees, milestone, author
- PRs: same as issues plus merge status, head/base branches, review state
- Comments: author, date, body (for both issues and PR reviews)
- Commits: SHA, message, author, date (optional, for linking)

### Estimated Volume

Depends on repos configured. Core `HL7/fhir` repo has ~2k issues.
Database size: ~100–300 MB.

---

## Cross-Source Data Flow

```
  Zulip API ──┐
  Jira API  ──┤    ┌─────────────┐    ┌──────────────┐    ┌──────────┐
  Confluence ─┼───▶│  Downloaders │───▶│  Normalizers  │───▶│  SQLite  │
  GitHub API ─┘    └─────────────┘    └──────────────┘    │  + FTS5  │
                                                          └────┬─────┘
                                                               │
                                      ┌──────────────┐        │
                                      │  Cross-Ref    │◀───────┘
                                      │  Linker       │────────┐
                                      └──────────────┘        │
                                                          ┌────▼─────┐
                                                          │  xref_*  │
                                                          │  tables  │
                                                          └──────────┘
```

Each source normalizes its data into source-specific tables, then a
cross-reference pass scans text fields for identifiers from other sources
(e.g., `FHIR-12345` in Zulip messages, `#123` GitHub references in Jira
descriptions, Confluence page URLs in any source).
