# API Reference

FHIR Augury v2 uses a microservices architecture. The **primary API is gRPC**,
used by the CLI and MCP tools to communicate with the orchestrator. Each service
also exposes a lightweight HTTP API for health checks and basic operations.

## Architecture

| Service | HTTP Port | gRPC Port | Description |
|---------|-----------|-----------|-------------|
| Orchestrator | 5150 | 5151 | Central hub — routes queries to sources |
| Jira | 5160 | 5161 | Indexes jira.hl7.org |
| Zulip | 5170 | 5171 | Indexes chat.fhir.org |
| Confluence | 5180 | 5181 | Indexes confluence.hl7.org |
| GitHub | 5190 | 5191 | Indexes HL7 GitHub repos |

> **Note:** The CLI and MCP server connect to the orchestrator's gRPC port
> (5151). The HTTP endpoints documented here are primarily for health checks,
> diagnostics, and lightweight integrations. For full functionality, use the
> CLI or MCP tools.

---

## gRPC API

The gRPC service definitions are in the [`protos/`](../../protos/) directory.
The orchestrator exposes gRPC services for:

- **Search** — unified full-text search across all sources
- **Get** — retrieve full item details from any source
- **Related** — find related items using keyword similarity and cross-references
- **Snapshot** — generate Markdown snapshots of items
- **CrossReference** — query cross-reference links between items
- **Ingestion** — trigger syncs, check status, rebuild indexes
- **Services** — health checks, statistics, cross-reference scanning
- **Query** — structured queries for Jira and Zulip with typed filters
- **List** — list items with filtering and sorting

The CLI (`FhirAugury.Cli`) is the recommended way to interact with the gRPC API.
See the [CLI Reference](cli-reference.md) for all available commands.

---

## Orchestrator HTTP API

**Base URL:** `http://localhost:5150`

### Health Check

#### `GET /health`

Returns orchestrator health status.

**Response:**

```json
{
  "status": "healthy",
  "service": "orchestrator",
  "version": "2.0.0"
}
```

### Search

#### `POST /api/search`

Unified search across all sources.

**Request Body:**

```json
{
  "query": "patient matching algorithm",
  "sources": ["jira", "zulip"],
  "limit": 20
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `query` | string | Yes | Search query text |
| `sources` | string[] | No | Filter to specific sources (omit for all) |
| `limit` | int | No | Maximum results (default: 20) |

**Response:** Array of search results with source, ID, title, snippet, and
relevance score.

### Related Items

#### `POST /api/related`

Find items related to a given item.

**Request Body:**

```json
{
  "source": "jira",
  "id": "FHIR-43499",
  "targetSources": ["zulip", "confluence"],
  "limit": 10
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `source` | string | Yes | Source of the reference item |
| `id` | string | Yes | Item identifier |
| `targetSources` | string[] | No | Sources to search for related items |
| `limit` | int | No | Maximum results (default: 20) |

### Cross-References

#### `GET /api/xref/{source}/{id}`

Get cross-references for an item.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `direction` | string | `outgoing` | `outgoing`, `incoming`, or `both` |

**Example:** `GET /api/xref/jira/FHIR-43499?direction=both`

### Sync

#### `POST /api/sync`

Trigger an ingestion sync.

**Request Body:**

```json
{
  "sources": ["jira"],
  "type": "incremental"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `sources` | string[] | No | Sources to sync (omit for all) |
| `type` | string | No | `full` or `incremental` (default: `incremental`) |

### Status

#### `GET /api/status`

Get health and status of all services.

**Response:** Status of orchestrator and each registered source service.

---

## Source Service HTTP APIs

Each source service exposes the same base endpoints on its own HTTP port.

### Health Check

#### `GET /health`

Returns source service health status.

**Example** (Jira on port 5160):

```bash
curl http://localhost:5160/health
```

**Response:**

```json
{
  "status": "healthy",
  "service": "jira",
  "version": "2.0.0"
}
```

### Statistics

#### `GET /api/stats`

Returns source-specific statistics (item counts, last sync time, index status).

**Example:**

```bash
curl http://localhost:5160/api/stats
```

### Ingestion

#### `POST /api/ingest`

Triggers an ingestion for this source service.

**Example:**

```bash
curl -X POST http://localhost:5160/api/ingest
```

---

## Service Ports Reference

| Service | Health Check URL | Purpose |
|---------|-----------------|---------|
| Orchestrator | `http://localhost:5150/health` | Central coordination |
| Jira | `http://localhost:5160/health` | Jira issue indexing |
| Zulip | `http://localhost:5170/health` | Zulip message indexing |
| Confluence | `http://localhost:5180/health` | Confluence page indexing |
| GitHub | `http://localhost:5190/health` | GitHub issue/PR indexing |

---

## Health Check Format

All services return health checks in the same format:

```json
{
  "status": "healthy",
  "service": "<service-name>",
  "version": "2.0.0"
}
```

The `status` field will be `"healthy"` when the service is operating normally.

---

## Error Responses

HTTP errors use a consistent format:

```json
{
  "title": "Bad Request",
  "detail": "Query parameter 'query' is required"
}
```

Common HTTP status codes:

| Code | Meaning |
|------|---------|
| `200` | Success |
| `202` | Accepted (ingestion triggered) |
| `400` | Bad request (missing/invalid parameters) |
| `404` | Item not found |
| `503` | Service unavailable |
