# API Reference

The FHIR Augury background service exposes an HTTP API for searching, browsing,
ingestion control, and administration. All data endpoints are under `/api/v1`.

**Base URL:** `http://localhost:5100` (configurable via `Api.Port`)

---

## Health Check

### `GET /health`

Returns service health status.

**Response:**

```json
{
  "Status": "Healthy",
  "Timestamp": "2025-03-15T10:30:00Z"
}
```

---

## Search

### `GET /api/v1/search/`

Unified full-text search across all indexed sources.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `q` | string | Yes | | Search query |
| `sources` | string | No | all | Comma-separated source filter: `jira`, `zulip`, `confluence`, `github` |
| `limit` | int | No | `20` | Maximum results |

**Response:** Array of search results with source, ID, title, snippet, score.

### `GET /api/v1/search/{source}`

Search within a specific source.

**Path Parameters:**

| Parameter | Values |
|-----------|--------|
| `source` | `jira`, `jira-comment`, `zulip`, `confluence`, `github` |

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `q` | string | Yes | Search query |
| `limit` | int | No | Maximum results (default: 20) |
| `filter` | string | No | Source-specific filter (status, stream, space, repo) |

---

## Jira

### `GET /api/v1/jira/issues`

List Jira issues with optional filters.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `limit` | int | `50` | Page size |
| `offset` | int | `0` | Pagination offset |
| `work_group` | string | | Filter by work group |
| `status` | string | | Filter by status |

**Response:**

```json
{
  "Total": 48000,
  "Offset": 0,
  "Limit": 50,
  "Items": [
    {
      "Key": "FHIR-43499",
      "Title": "Example issue title",
      "Status": "Open",
      "Priority": "Major",
      "WorkGroup": "FHIR Infrastructure",
      "Specification": "FHIR Core",
      "UpdatedAt": "2025-03-01T12:00:00Z",
      "CommentCount": 5
    }
  ]
}
```

### `GET /api/v1/jira/issues/{key}`

Get full details of a Jira issue.

### `GET /api/v1/jira/issues/{key}/comments`

Get comments on a Jira issue.

---

## Zulip

### `GET /api/v1/zulip/streams`

List all indexed Zulip streams.

**Response:** Array of streams with ID, name, description, web-public flag,
message count, and last fetch time.

### `GET /api/v1/zulip/messages`

Search Zulip messages.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `q` | string | Yes | Search query |
| `stream` | string | No | Filter to a specific stream |
| `limit` | int | No | Maximum results (default: 20) |

### `GET /api/v1/zulip/thread`

Get a full Zulip topic thread.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `stream` | string | Yes | Stream name |
| `topic` | string | Yes | Topic name |

**Response:**

```json
{
  "Stream": "implementers",
  "Topic": "US Core questions",
  "MessageCount": 42,
  "Messages": [
    {
      "ZulipMessageId": 123456,
      "SenderName": "John Doe",
      "ContentPlain": "Message text...",
      "Timestamp": "2025-03-01T12:00:00Z",
      "Reactions": "..."
    }
  ]
}
```

---

## Confluence

### `GET /api/v1/confluence/pages`

List or search Confluence pages.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `limit` | int | `50` | Page size |
| `offset` | int | `0` | Pagination offset |
| `space` | string | | Filter by space key |
| `q` | string | | Full-text search query (triggers FTS instead of listing) |

### `GET /api/v1/confluence/pages/{id}`

Get a Confluence page with its comments.

**Response:**

```json
{
  "Page": { "..." },
  "Comments": [ "..." ]
}
```

---

## GitHub

### `GET /api/v1/github/issues`

List or search GitHub issues and pull requests.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `limit` | int | `50` | Page size |
| `offset` | int | `0` | Pagination offset |
| `repo` | string | | Filter by repository (e.g., `HL7/fhir`) |
| `state` | string | | Filter by state (`open`, `closed`) |
| `q` | string | | Full-text search query (triggers FTS) |

### `GET /api/v1/github/issues/{id}`

Get a GitHub issue or PR with its comments.

---

## Cross-References

### `GET /api/v1/xref/{source}/{id}`

Get cross-references for an item.

**Response:**

```json
{
  "Source": "jira",
  "Id": "FHIR-43499",
  "CrossReferences": [
    {
      "SourceType": "jira",
      "SourceId": "FHIR-43499",
      "TargetType": "zulip",
      "TargetId": "implementers:FHIR-43499 discussion",
      "LinkType": "mention",
      "Context": "...discussed in FHIR-43499..."
    }
  ],
  "RelatedItems": []
}
```

---

## Statistics

### `GET /api/v1/stats/`

Get database-wide statistics.

**Response:**

```json
{
  "JiraIssues": 48000,
  "JiraComments": 120000,
  "ZulipStreams": 150,
  "ZulipMessages": 1000000,
  "ConfluenceSpaces": 2,
  "ConfluencePages": 5000,
  "GitHubRepos": 2,
  "GitHubIssues": 3000,
  "CrossRefLinks": 25000,
  "Keywords": 500000,
  "TotalItems": 1076000
}
```

### `GET /api/v1/stats/{source}`

Get source-specific statistics including sync state.

---

## Ingestion Control

### `POST /api/v1/ingest/{source}`

Trigger an ingestion for a specific source.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `type` | string | `Incremental` | `Full`, `Incremental`, or `OnDemand` |
| `filter` | string | | Source-specific filter |

**Response:** `202 Accepted`

```json
{
  "RequestId": "abc-123",
  "QueuePosition": 1,
  "Source": "jira",
  "Type": "Incremental"
}
```

### `POST /api/v1/ingest/{source}/item`

Ingest a single item by identifier.

**Body:**

```json
{
  "Identifier": "FHIR-43499"
}
```

**Response:** `202 Accepted`

### `POST /api/v1/ingest/sync`

Trigger an incremental sync for one or more sources.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `sources` | string | Comma-separated sources (omit for all enabled sources) |

**Response:** `202 Accepted` with array of request IDs.

### `GET /api/v1/ingest/status`

Get current ingestion status.

**Response:**

```json
{
  "QueueDepth": 0,
  "ActiveIngestion": null,
  "Sources": [
    {
      "SourceName": "jira",
      "Status": "Idle",
      "LastSyncAt": "2025-03-15T10:00:00Z",
      "ItemsIngested": 48000,
      "LastError": null
    }
  ]
}
```

### `GET /api/v1/ingest/history`

Get ingestion run history.

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source` | string | Filter by source |
| `limit` | int | Maximum entries |

### `GET /api/v1/ingest/schedule`

Get sync schedules for all sources.

**Response:** Array of schedule entries with source, enabled flag, interval,
and next run time.

### `PUT /api/v1/ingest/{source}/schedule`

Update the sync schedule for a source.

**Body:**

```json
{
  "SyncInterval": "00:30:00"
}
```

**Response:**

```json
{
  "Source": "jira",
  "SyncInterval": "00:30:00",
  "NextRun": "2025-03-15T11:00:00Z"
}
```

---

## Error Responses

Errors use a consistent format:

```json
{
  "Title": "Bad Request",
  "Detail": "Query parameter 'q' is required"
}
```

Common HTTP status codes:

| Code | Meaning |
|------|---------|
| `200` | Success |
| `202` | Accepted (ingestion queued) |
| `400` | Bad request (missing/invalid parameters) |
| `404` | Item not found |
