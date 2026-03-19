# Data Sources

This document describes the source connector architecture, each connector's
implementation details, and guidance for adding new data sources.

## IDataSource Interface

Every source connector implements the `IDataSource` interface from
`FhirAugury.Models`:

```csharp
public interface IDataSource
{
    Task<IngestionResult> DownloadAllAsync(
        DatabaseService db, ISourceOptions options, CancellationToken ct);

    Task<IngestionResult> DownloadIncrementalAsync(
        DatabaseService db, ISourceOptions options, DateTimeOffset since, CancellationToken ct);

    Task<IngestionResult> IngestItemAsync(
        string id, ISourceOptions options, CancellationToken ct);
}
```

This consistent interface allows the ingestion worker, scheduler, CLI, and API
to treat all sources uniformly.

## Source Connector Architecture

Each source connector follows the same internal pattern:

```
Source (IDataSource)
├── SourceOptions      — Configuration (URLs, credentials, page sizes)
├── AuthHandler        — DelegatingHandler adding auth headers to HttpClient
├── Mapper/Parser      — Converts API responses to database record types
└── (optional) RateLimiter — Source-specific rate limiting
```

### Shared Infrastructure

- **`HttpRetryHelper`** — Retries on transient failures (HTTP 429/500/502/503/504)
  with exponential backoff + jitter. Max 3 retries. Respects `Retry-After`
  headers. Fails immediately on 401/403 with clear error messages.
- **`IResponseCache`** — Optional file-system cache for raw API responses.
  Supports `WriteThrough`, `CacheOnly`, `WriteOnly`, and `Disabled` modes.
- **`TextSanitizer`** — Strips HTML tags, Markdown syntax, normalizes Unicode
  (NFC), extracts plain text from various formats.

## Connector Details

### Jira (`FhirAugury.Sources.Jira`)

| Property | Value |
|----------|-------|
| **Default target** | `https://jira.hl7.org` |
| **Auth methods** | Session cookie or API token (HTTP Basic) |
| **Data types** | Issues + comments |
| **Page size** | 100 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

- **Cookie mode** (default): Raw session cookie sent as the `cookie` header
- **ApiToken mode**: HTTP Basic Auth with `email:token`

Auth mode is auto-selected: if both `ApiToken` and `Email` are provided, ApiToken
mode is used; otherwise Cookie mode.

**Data model:**

- `JiraIssueRecord` — Issue key, title, description, status, priority, custom
  fields
- `JiraCommentRecord` — Comment author, body, timestamps

16 HL7-specific custom fields are mapped to domain properties (e.g.,
`customfield_11302` → Specification, `customfield_11400` → WorkGroup).

**Incremental sync:** Appends `AND updated >= '{since}'` to the JQL query.

**Pagination:** Offset-based (`startAt` vs `total`).

**Special feature:** Also supports XML RSS export parsing via `JiraXmlParser`.

**Key classes:**

| Class | Responsibility |
|-------|---------------|
| `JiraSource` | Main `IDataSource` implementation |
| `JiraSourceOptions` | Configuration: BaseUrl, AuthMode, Cookie, ApiToken, Email, DefaultJql, PageSize |
| `JiraAuthHandler` | `DelegatingHandler` for cookie or Basic auth |
| `JiraFieldMapper` | Maps JSON API responses to record types, handles custom fields |
| `JiraXmlParser` | Parses Jira XML RSS export format |

---

### Zulip (`FhirAugury.Sources.Zulip`)

| Property | Value |
|----------|-------|
| **Default target** | `https://chat.fhir.org` |
| **Auth methods** | HTTP Basic (email + API key), `.zuliprc` file |
| **Data types** | Streams + messages |
| **Batch size** | 1000 |
| **HTTP timeout** | 10 minutes |
| **Cache support** | Yes |

**Authentication:**

HTTP Basic Auth with `email:apikey`. Credentials can come from:
1. Direct `Email` and `ApiKey` options
2. A `.zuliprc` file (standard Zulip bot credential format)

**Data model:**

- `ZulipStreamRecord` — Stream ID, name, description, web-public flag
- `ZulipMessageRecord` — Message ID, stream, topic, sender, plain text content,
  timestamp, reactions

HTML content is stripped to plain text via `TextSanitizer.StripHtml`. Only
web-public streams are downloaded by default (`OnlyWebPublic = true`).

**Incremental sync:** Cursor-based using `SyncStateRecord` — stores the last
synced message ID per stream. Sets `anchor = lastId + 1` and fetches forward.
This is the most sophisticated incremental mechanism of the four connectors.

**Pagination:** Anchor-based (`anchor`, `num_before=0`, `num_after=batchSize`).
Continues until `found_newest` is true.

**Key classes:**

| Class | Responsibility |
|-------|---------------|
| `ZulipSource` | Main `IDataSource` implementation |
| `ZulipSourceOptions` | Configuration: BaseUrl, CredentialFile, Email, ApiKey, BatchSize, OnlyWebPublic |
| `ZulipAuthHandler` | `DelegatingHandler` for Basic auth; parses `.zuliprc` files |
| `ZulipMessageMapper` | Maps JSON to stream/message records, strips HTML |

---

### Confluence (`FhirAugury.Sources.Confluence`)

| Property | Value |
|----------|-------|
| **Default target** | `https://confluence.hl7.org` |
| **Auth methods** | Session cookie or HTTP Basic (username + API token) |
| **Data types** | Spaces + pages + comments |
| **Page size** | 25 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | Yes |

**Authentication:**

- **Cookie mode** (default): Session cookie in the `cookie` header
- **Basic mode**: HTTP Basic with `username:token`

**Data model:**

- `ConfluenceSpaceRecord` — Space key, name, description, URL
- `ConfluencePageRecord` — Page ID, space key, title, parent ID, body
  (storage format + plain text), labels, version, URL
- `ConfluenceCommentRecord` — Author, date, body as plain text

Body content is converted from Confluence storage format (XHTML) to plain text
by `ConfluenceContentParser`, which handles macros, images, and attachments.

**Incremental sync:** Uses Confluence CQL:
`lastModified >= "{since}" AND space in ("FHIR","FHIRI") AND type = page`

**Pagination:** Offset-based. Continues while `_links.next` exists.

**Default spaces:** `["FHIR", "FHIRI"]`

**Key classes:**

| Class | Responsibility |
|-------|---------------|
| `ConfluenceSource` | Main `IDataSource` implementation |
| `ConfluenceSourceOptions` | Configuration: BaseUrl, AuthMode, Cookie, Username, ApiToken, Spaces, PageSize |
| `ConfluenceAuthHandler` | `DelegatingHandler` for cookie or Basic auth |
| `ConfluenceContentParser` | Converts Confluence storage format XHTML to plain text |

---

### GitHub (`FhirAugury.Sources.GitHub`)

| Property | Value |
|----------|-------|
| **Default target** | `https://api.github.com` |
| **Auth methods** | Bearer token (Personal Access Token) |
| **Data types** | Repositories + issues/PRs + comments |
| **Page size** | 100 |
| **HTTP timeout** | 5 minutes |
| **Cache support** | No |

**Authentication:**

Bearer token via PAT. Without a token, requests are unauthenticated (60 req/hr
vs 5,000 with a token).

**Data model:**

- `GitHubRepoRecord` — Full name, owner, name, description
- `GitHubIssueRecord` — Key (`owner/repo#number`), number, isPullRequest flag,
  title, body, state, author, labels, assignees, milestone, merge state
- `GitHubCommentRecord` — Author, date, body

The GitHub Issues API returns both issues and PRs; the mapper detects PRs via
the `pull_request` field.

**Incremental sync:** Uses GitHub's `since` query parameter.

**Rate limiting:** Dedicated `GitHubRateLimiter` (`DelegatingHandler`) monitors
`X-RateLimit-Remaining` and `X-RateLimit-Reset` headers. Pauses all requests
when remaining calls drop below `RateLimitBuffer` (default: 100).

**Pagination:** Page-based. Continues while returned array length ≥ PageSize.

**Default repositories:** `["HL7/fhir", "HL7/fhir-ig-publisher"]`

**Key classes:**

| Class | Responsibility |
|-------|---------------|
| `GitHubSource` | Main `IDataSource` implementation |
| `GitHubSourceOptions` | Configuration: PersonalAccessToken, Repositories, PageSize, RateLimitBuffer |
| `GitHubIssueMapper` | Maps JSON to repo/issue/comment records; detects PRs |
| `GitHubRateLimiter` | `DelegatingHandler` for Bearer auth + rate limit monitoring |

---

## Adding a New Data Source

To add a new data source, follow these steps:

### 1. Define the Database Records

In `FhirAugury.Database`, create record classes decorated with source-generator
attributes:

```csharp
[SqliteTable("new_source_items")]
public partial record class NewSourceItemRecord
{
    [SqliteColumn("id", isPrimaryKey: true)]
    public long Id { get; set; }

    [SqliteColumn("title")]
    public string Title { get; set; } = string.Empty;

    // Add fields as needed...

    [SqliteColumn("searchable_text", isVirtual: true)]
    public string SearchableTextField => $"{Title} {Body}";
}
```

### 2. Create the FTS5 Setup

In `FtsSetup.cs`, add a method to create the FTS5 virtual table with triggers:

```csharp
public static void CreateNewSourceFts(SqliteConnection conn) { ... }
public static void RebuildNewSourceFts(SqliteConnection conn) { ... }
```

### 3. Create the Source Connector

Create a new project `FhirAugury.Sources.NewSource` with:

- `NewSourceOptions` implementing `ISourceOptions`
- `NewSourceAuthHandler` extending `DelegatingHandler`
- `NewSourceMapper` to convert API responses to records
- `NewSource` implementing `IDataSource`

### 4. Register in the CLI and Service

- Add authentication options to `DownloadCommand`, `SyncCommand`, `IngestCommand`
- Register the source in `IngestionWorker` for the background service
- Add API endpoints in a new `NewSourceEndpoints.cs`

### 5. Add to the Indexing Pipeline

- Update `CrossRefLinker` with regex patterns for the new source's identifiers
- Update `Bm25Calculator` to include the new source's text fields
- Update `FtsSearchService` to query the new FTS table

### 6. Add MCP Tools

Add tool methods in the appropriate MCP tool classes (Search, Retrieval,
Listing, Snapshot).

## Comparison Matrix

| Feature | Jira | Zulip | Confluence | GitHub |
|---------|------|-------|------------|--------|
| **Auth methods** | Cookie or Basic | Basic, `.zuliprc` | Cookie or Basic | Bearer (PAT) |
| **Incremental strategy** | JQL time filter | Cursor-based (msg ID) | CQL time filter | `since` param |
| **Pagination** | Offset | Anchor | Offset | Page number |
| **Rate limiting** | Retry only | Retry only | Retry only | Dedicated limiter |
| **Cache support** | ✅ | ✅ | ✅ | ❌ |
| **Default page/batch** | 100 | 1000 | 25 | 100 |
| **HTTP timeout** | 5 min | 10 min | 5 min | 5 min |
