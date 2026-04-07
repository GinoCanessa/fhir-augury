# API Reference

FHIR Augury v2 uses a microservices architecture with HTTP/REST APIs for all
communication. The CLI and MCP tools connect to the orchestrator via HTTP. Each
service exposes an HTTP API for health checks, search, and management operations.

## Architecture

| Service | Port | Description |
|---------|------|-------------|
| Orchestrator | 5150 | Central hub — routes queries to sources |
| Jira | 5160 | Indexes jira.hl7.org |
| Zulip | 5170 | Indexes chat.fhir.org |
| Confluence | 5180 | Indexes confluence.hl7.org |
| GitHub | 5190 | Indexes HL7 GitHub repos |
| MCP HTTP | 5200 | HTTP/SSE MCP server (`FhirAugury.McpHttp`) |

> **Note:** The MCP HTTP server (`FhirAugury.McpHttp`) is a separate service on
> port 5200 that provides the same MCP tools via HTTP/SSE transport. The CLI
> (`FhirAugury.Cli`) is the recommended way to interact with the API. See the
> [CLI Reference](cli-reference.md) for all available commands.

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

### Services

#### `GET /api/v1/services`

Get health status of all connected services with index information.

#### `GET /api/v1/endpoints`

List configured source service addresses.

#### `GET /api/v1/stats`

Get aggregated item counts and database sizes across all source services.

### Content Search

#### `GET /api/v1/content/search`

Unified multi-value content search across all sources.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `values[]` | string | Yes | Search values (repeatable) |
| `sources[]` | string | No | Source filter (repeatable, omit for all) |
| `limit` | int | No | Maximum results (default: 20) |

**Example:** `GET /api/v1/content/search?values[]=patient+matching&sources[]=jira&sources[]=zulip&limit=10`

### Cross-References

#### `GET /api/v1/content/refers-to`

Find outgoing cross-references (what a specific item refers to).

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `value` | string | Yes | Item identifier |
| `sourceType` | string | No | Filter by source type |
| `limit` | int | No | Maximum results (default: 50) |

**Example:** `GET /api/v1/content/refers-to?value=FHIR-43499&limit=10`

#### `GET /api/v1/content/referred-by`

Find incoming cross-references (what refers to a specific item).

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `value` | string | Yes | Item identifier |
| `sourceType` | string | No | Filter by source type |
| `limit` | int | No | Maximum results (default: 50) |

**Example:** `GET /api/v1/content/referred-by?value=FHIR-43499&sourceType=zulip`

#### `GET /api/v1/content/cross-referenced`

Find all cross-references for an item (both incoming and outgoing).

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `value` | string | Yes | Item identifier |
| `sourceType` | string | No | Filter by source type |
| `limit` | int | No | Maximum results (default: 50) |

**Example:** `GET /api/v1/content/cross-referenced?value=FHIR-43499`

### Items

#### `GET /api/v1/content/item/{source}/{*id}`

Get full details of a content item from any source, with optional content body,
comments, and markdown snapshot.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source` | string | Source type (jira, zulip, confluence, github) |
| `*id` | string | Item identifier (catch-all path segment) |

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `includeContent` | bool | No | Include the full content body |
| `includeComments` | bool | No | Include item comments |
| `includeSnapshot` | bool | No | Include a markdown snapshot |

**Example:** `GET /api/v1/content/item/jira/FHIR-43499?includeComments=true&includeSnapshot=true`

### Ingestion

#### `POST /api/v1/ingest/trigger`

Trigger an ingestion sync on source services.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | No | `full`, `incremental`, or `rebuild` (default: `incremental`) |
| `sources` | string | No | Comma-separated sources to sync (omit for all) |

**Example:** `POST /api/v1/ingest/trigger?type=incremental&sources=jira,zulip`

### Rebuild Index

#### `POST /api/v1/rebuild-index`

Rebuild specific indexes on source services.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | No | Index type: `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links` (default: `all`) |
| `sources` | string | No | Comma-separated sources to rebuild (omit for all) |

### Internal Notification

#### `POST /api/v1/notify-ingestion`

Internal peer notification endpoint. Used by source services to notify the
orchestrator of ingestion events.

**Request Body:** `PeerIngestionNotification` object.

### Source Proxy

#### `POST /api/v1/jira/query`

Proxy structured Jira issue query to the Jira source service.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `q` | string | No | Text query |
| `limit` | int | No | Maximum results |

#### `POST /api/v1/zulip/query`

Proxy structured Zulip message query to the Zulip source service.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `q` | string | No | Text query |
| `limit` | int | No | Maximum results |

---

## MCP HTTP Server

**Base URL:** `http://localhost:5200`

The MCP HTTP server (`FhirAugury.McpHttp`) is a separate ASP.NET Core service
that exposes MCP tools via HTTP/SSE transport. It is distinct from the
orchestrator — it connects to the orchestrator and source services via HTTP as
a client. The server provides 15 MCP tools across 4 categories (Unified,
Content, Jira, Zulip).

### MCP Endpoint

#### `GET /mcp` (SSE) / `POST /mcp` (HTTP)

The Model Context Protocol endpoint. MCP clients (VS Code, Copilot, etc.)
connect to this endpoint to discover and invoke the 15 MCP tools.

**Example client configuration:**

```json
{
  "mcpServers": {
    "fhir-augury": {
      "url": "http://localhost:5200/mcp"
    }
  }
}
```

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

#### `GET /api/v1/stats`

Returns source-specific statistics (item counts, last sync time, index status).

**Example:**

```bash
curl http://localhost:5160/api/v1/stats
```

### Status

#### `GET /api/v1/status`

Returns source service health status.

### Ingestion

#### `POST /api/v1/ingest`

Triggers an ingestion for this source service.

**Example:**

```bash
curl -X POST http://localhost:5160/api/v1/ingest
```

### Rebuild

#### `POST /api/v1/rebuild`

Rebuild database from cache.

#### `POST /api/v1/rebuild-index`

Rebuild specific indexes on this source service.

---

## Zulip Service HTTP API

**Base URL:** `http://localhost:5170/api/v1`

The Zulip service exposes additional endpoints for stream management beyond the
common source service endpoints.

### List Streams

#### `GET /api/v1/streams`

List all available Zulip streams.

**Response:**

```json
{
  "total": 42,
  "streams": [
    {
      "zulipStreamId": 123,
      "name": "implementers",
      "description": "Discussion for implementers",
      "messageCount": 5000,
      "isWebPublic": true,
      "includeStream": true,
      "url": "https://chat.fhir.org/#narrow/stream/implementers"
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `zulipStreamId` | int | Zulip's stream identifier |
| `name` | string | Stream name |
| `description` | string | Stream description |
| `messageCount` | int | Number of indexed messages |
| `isWebPublic` | bool | Whether the stream is web-public on Zulip |
| `includeStream` | bool | Whether this stream is included in ingestion |
| `url` | string | Direct link to the stream on chat.fhir.org |

### Get Stream

#### `GET /api/v1/streams/{zulipStreamId}`

Get a single stream by its Zulip stream ID.

**Example:**

```bash
curl http://localhost:5170/api/v1/streams/123
```

**Response:**

```json
{
  "zulipStreamId": 123,
  "name": "implementers",
  "description": "Discussion for implementers",
  "messageCount": 5000,
  "isWebPublic": true,
  "includeStream": true,
  "url": "https://chat.fhir.org/#narrow/stream/implementers"
}
```

Returns `404` if the stream is not found.

### Update Stream

#### `PUT /api/v1/streams/{zulipStreamId}`

Update stream properties. Currently supports toggling `includeStream`, which
controls whether the stream is included during ingestion syncs.

**Example:**

```bash
curl -X PUT http://localhost:5170/api/v1/streams/123 \
  -H "Content-Type: application/json" \
  -d '{"includeStream": false}'
```

**Request Body:**

```json
{
  "includeStream": false
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `includeStream` | bool | Yes | Whether to include this stream in ingestion |

**Response:** Same shape as the GET response with updated values.

Returns `404` if the stream is not found.

---

## Service Ports Reference

| Service | Health Check URL | Purpose |
|---------|-----------------|---------|
| Orchestrator | `http://localhost:5150/health` | Central coordination |
| Jira | `http://localhost:5160/health` | Jira issue indexing |
| Zulip | `http://localhost:5170/health` | Zulip message indexing |
| Confluence | `http://localhost:5180/health` | Confluence page indexing |
| GitHub | `http://localhost:5190/health` | GitHub issue/PR indexing |
| MCP HTTP | `http://localhost:5200/health` | MCP HTTP/SSE server |

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
