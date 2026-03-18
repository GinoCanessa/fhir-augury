# API Reference

HTTP API reference for the FHIR Augury background service. The service listens
on `http://localhost:5100` by default (configurable via `Api.Port`).

All API endpoints are prefixed with `/api/v1` unless otherwise noted. Responses
use JSON format.

## Health Check

### `GET /health`

Returns service health status. Not under the `/api/v1` prefix.

**Response:**

```json
{
  "status": "healthy",
  "timestamp": "2025-01-15T10:30:00Z"
}
```

---

## Search

### `GET /api/v1/search`

Unified cross-source full-text search.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `q` | `string` | Yes | — | Search query |
| `sources` | `string` | No | all | Comma-separated source filter: `jira`, `jira-comment`, `zulip`, `confluence`, `github` |
| `limit` | `int` | No | `20` | Maximum results |

**Example request:**

```
GET /api/v1/search?q=patient+resource&sources=jira,zulip&limit=10
```

**Example response:**

```json
{
  "query": "patient resource",
  "results": [
    {
      "source": "jira",
      "id": "FHIR-43499",
      "title": "Patient resource - add preferred name flag",
      "snippet": "...the <mark>Patient</mark> <mark>resource</mark> should support...",
      "score": 8.45,
      "normalizedScore": 0.92,
      "url": "https://jira.hl7.org/browse/FHIR-43499",
      "updatedAt": "2025-01-10T14:22:00Z"
    }
  ],
  "totalResults": 1
}
```

### `GET /api/v1/search/{source}`

Search within a specific source.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `q` | `string` | Yes | Search query |
| `limit` | `int` | No | Maximum results (default: 20) |
| `filter` | `string` | No | Source-specific filter |

**Example:**

```
GET /api/v1/search/jira?q=terminology&limit=5
```

---

## Ingestion

### `POST /api/v1/ingest/{source}`

Trigger an ingestion run for a source. Returns immediately with a 202 Accepted
response while ingestion runs in the background.

| Parameter | Location | Type | Default | Description |
|---|---|---|---|---|
| `source` | path | `string` | — | Source name: `jira`, `zulip`, `confluence`, `github` |
| `type` | query | `string` | — | `Full`, `Incremental`, or `OnDemand` |
| `filter` | query | `string` | — | Source-specific filter |

**Example request:**

```
POST /api/v1/ingest/jira?type=Incremental
```

**Example response (202 Accepted):**

```json
{
  "requestId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "source": "jira",
  "type": "Incremental",
  "status": "queued"
}
```

### `POST /api/v1/ingest/{source}/item`

Submit a single item for on-demand ingestion.

**Request body:**

```json
{
  "identifier": "FHIR-43499"
}
```

**Response (202 Accepted):**

```json
{
  "requestId": "...",
  "source": "jira",
  "type": "OnDemand",
  "identifier": "FHIR-43499",
  "status": "queued"
}
```

### `POST /api/v1/ingest/sync`

Trigger incremental sync for all (or specified) enabled sources.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `sources` | `string` | No | Comma-separated list of sources to sync. If omitted, syncs all enabled sources. |

**Example:**

```
POST /api/v1/ingest/sync?sources=jira,zulip
```

### `GET /api/v1/ingest/status`

Get current ingestion queue status and sync state.

**Response:**

```json
{
  "queueDepth": 0,
  "activeIngestion": null,
  "syncState": {
    "jira": {
      "lastSyncAt": "2025-01-15T09:00:00Z",
      "lastResult": "Success",
      "itemsProcessed": 150
    },
    "zulip": {
      "lastSyncAt": "2025-01-15T09:30:00Z",
      "lastResult": "Success",
      "itemsProcessed": 1200
    }
  }
}
```

### `GET /api/v1/ingest/history`

Get ingestion run history.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | all | Filter by source |
| `limit` | `int` | `20` | Maximum results |

### `GET /api/v1/ingest/schedule`

Get configured sync schedules and next run times for all sources.

### `PUT /api/v1/ingest/{source}/schedule`

Update the sync schedule for a source at runtime.

**Request body:**

```json
{
  "syncInterval": "00:30:00"
}
```

---

## Jira

### `GET /api/v1/jira/issues`

List Jira issues with optional filters and pagination.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `limit` | `int` | `50` | Results per page |
| `offset` | `int` | `0` | Pagination offset |
| `work_group` | `string` | — | Filter by work group |
| `status` | `string` | — | Filter by issue status |

**Example:**

```
GET /api/v1/jira/issues?work_group=FHIR-I&status=Open&limit=10
```

### `GET /api/v1/jira/issues/{key}`

Get a single Jira issue by key.

**Example:**

```
GET /api/v1/jira/issues/FHIR-43499
```

**Response:**

```json
{
  "key": "FHIR-43499",
  "title": "Patient resource - add preferred name flag",
  "status": "Triaged",
  "workGroup": "FHIR-I",
  "specification": "FHIR Core (FHIR)",
  "description": "...",
  "createdAt": "2024-06-15T10:00:00Z",
  "updatedAt": "2025-01-10T14:22:00Z"
}
```

### `GET /api/v1/jira/issues/{key}/comments`

Get comments on a Jira issue.

**Example:**

```
GET /api/v1/jira/issues/FHIR-43499/comments
```

---

## Zulip

### `GET /api/v1/zulip/streams`

List all indexed Zulip streams.

**Response:**

```json
{
  "streams": [
    {
      "id": 1,
      "name": "implementers",
      "description": "General implementation questions"
    }
  ]
}
```

### `GET /api/v1/zulip/messages`

Search Zulip messages.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `q` | `string` | Yes | Search query |
| `stream` | `string` | No | Filter by stream name |
| `limit` | `int` | No | Maximum results (default: 20) |

**Example:**

```
GET /api/v1/zulip/messages?q=patient+matching&stream=implementers&limit=10
```

### `GET /api/v1/zulip/thread`

Get a full Zulip topic thread with all messages.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `stream` | `string` | Yes | Stream name |
| `topic` | `string` | Yes | Topic name |

**Example:**

```
GET /api/v1/zulip/thread?stream=implementers&topic=Patient+search
```

---

## Confluence

### `GET /api/v1/confluence/pages`

List or search Confluence pages.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `limit` | `int` | `50` | Results per page |
| `offset` | `int` | `0` | Pagination offset |
| `space` | `string` | — | Filter by space key |
| `q` | `string` | — | Search query |

**Example:**

```
GET /api/v1/confluence/pages?space=FHIR&q=profiling&limit=10
```

### `GET /api/v1/confluence/pages/{id}`

Get a Confluence page with its comments.

**Example:**

```
GET /api/v1/confluence/pages/12345678
```

---

## GitHub

### `GET /api/v1/github/issues`

List or search GitHub issues and pull requests.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `limit` | `int` | `50` | Results per page |
| `offset` | `int` | `0` | Pagination offset |
| `repo` | `string` | — | Filter by repository (`owner/repo`) |
| `state` | `string` | — | Filter by state: `open`, `closed` |
| `q` | `string` | — | Search query |

**Example:**

```
GET /api/v1/github/issues?repo=HL7/fhir&state=open&limit=10
```

### `GET /api/v1/github/issues/{id}`

Get a GitHub issue or PR with its comments. The `id` is the item's unique key.

**Example:**

```
GET /api/v1/github/issues/HL7-fhir-1234
```

---

## Cross-References

### `GET /api/v1/xref/{source}/{id}`

Get cross-references and related items for a specific item. Returns explicit
links (mentions, URLs) and BM25-based similarity matches.

**Example:**

```
GET /api/v1/xref/jira/FHIR-43499
```

**Response:**

```json
{
  "source": "jira",
  "id": "FHIR-43499",
  "crossReferences": [
    {
      "targetSource": "zulip",
      "targetId": "implementers:Patient search",
      "linkType": "mention",
      "title": "Patient search discussion"
    }
  ],
  "relatedItems": [
    {
      "source": "confluence",
      "id": "12345678",
      "title": "Patient Resource Design Notes",
      "similarity": 0.85
    }
  ]
}
```

---

## Statistics

### `GET /api/v1/stats`

Get overview statistics for all sources.

**Response:**

```json
{
  "jiraIssues": 12345,
  "jiraComments": 45678,
  "zulipStreams": 142,
  "zulipMessages": 234567,
  "confluenceSpaces": 12,
  "confluencePages": 3456,
  "githubRepos": 5,
  "githubIssues": 8901,
  "githubComments": 23456,
  "databaseSizeMb": 1200.5
}
```

### `GET /api/v1/stats/{source}`

Get source-specific statistics with sync state.

**Example:**

```
GET /api/v1/stats/jira
```

---

## Error Responses

All errors use a consistent format:

```json
{
  "title": "Not Found",
  "detail": "Jira issue FHIR-99999 not found in the database."
}
```

Common HTTP status codes:

| Code | Description |
|---|---|
| `200` | Success |
| `202` | Accepted (ingestion queued) |
| `400` | Bad request (missing/invalid parameters) |
| `404` | Resource not found |
| `500` | Internal server error |
