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
| Confluence | 5180 | — | Indexes confluence.hl7.org |
| GitHub | 5190 | 5191 | Indexes HL7 GitHub repos |
| MCP HTTP | 5200 | — | HTTP/SSE MCP server (`FhirAugury.McpHttp`) |

> **Note:** The CLI and MCP stdio server connect to the orchestrator's gRPC port
> (5151). The MCP HTTP server (`FhirAugury.McpHttp`) is a separate service on
> port 5200 that provides the same MCP tools via HTTP/SSE transport. The HTTP
> endpoints documented here are primarily for health checks, diagnostics, and
> lightweight integrations. For full functionality, use the CLI or MCP tools.

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
- **GetServiceEndpoints** — discover registered source service endpoints
- **Query** — structured queries for Jira and Zulip with typed filters
- **List** — list items with filtering and sorting

### Zulip-Specific gRPC RPCs

The `ZulipService` (defined in [`protos/zulip.proto`](../../protos/zulip.proto))
includes stream management RPCs:

| RPC | Request | Response | Description |
|-----|---------|----------|-------------|
| `ListStreams` | `ZulipListStreamsRequest` | stream `ZulipStream` | List all streams (includes `include_stream` field) |
| `GetStream` | `GetStreamRequest` | `ZulipStreamInfo` | Get a single stream by Zulip stream ID |
| `UpdateStream` | `UpdateStreamRequest` | `ZulipStreamInfo` | Update stream properties (`include_stream`) |

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

#### `GET /api/v1/search`

Unified search across all sources.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `q` | string | Yes | Search query text |
| `sources` | string | No | Comma-separated source filter (omit for all) |
| `limit` | int | No | Maximum results (default: 20) |

**Example:** `GET /api/v1/search?q=patient+matching&sources=jira,zulip&limit=10`

### Related Items

#### `GET /api/v1/related/{source}/{id}`

Find items related to a given item.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source` | string | Source type (jira, zulip, confluence, github) |
| `id` | string | Item identifier |

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `targetSources` | string | No | Comma-separated target sources |
| `limit` | int | No | Maximum results (default: 20) |

**Example:** `GET /api/v1/related/jira/FHIR-43499?targetSources=zulip&limit=5`

### Cross-References

#### `GET /api/v1/xref/{source}/{id}`

Get cross-references for an item.

**Query Parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `direction` | string | `both` | `outgoing`, `incoming`, or `both` |

**Example:** `GET /api/v1/xref/jira/FHIR-43499?direction=both`

### Items

#### `GET /api/v1/items/{source}/{id}`

Get full details of an item from a source service.

**Example:** `GET /api/v1/items/jira/FHIR-43499`

#### `GET /api/v1/items/{source}/{id}/snapshot`

Get a rich Markdown snapshot of an item.

**Example:** `GET /api/v1/items/jira/FHIR-43499/snapshot`

#### `GET /api/v1/items/{source}/{id}/content`

Get the full content of an item.

**Example:** `GET /api/v1/items/jira/FHIR-43499/content`

### Ingestion

#### `POST /api/v1/ingest/trigger`

Trigger an ingestion sync on source services.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | No | `full` or `incremental` (default: `incremental`) |
| `sources` | string | No | Comma-separated sources to sync (omit for all) |

**Example:** `POST /api/v1/ingest/trigger?type=incremental&sources=jira,zulip`

### Rebuild Index

#### `POST /api/v1/rebuild-index`

Rebuild specific indexes on source services.

### Services

#### `GET /api/v1/services`

Get health status of all connected services.

#### `GET /api/v1/stats`

Get aggregate statistics across all source services.

### Structured Queries

#### `POST /api/v1/jira/query`

Structured Jira issue query (proxied to Jira source).

#### `POST /api/v1/zulip/query`

Structured Zulip message query (proxied to Zulip source).

---

## MCP HTTP Server

**Base URL:** `http://localhost:5200`

The MCP HTTP server (`FhirAugury.McpHttp`) is a separate ASP.NET Core service
that exposes MCP tools via HTTP/SSE transport. It is distinct from the
orchestrator — it connects to the orchestrator and source services via gRPC as
a client. The server provides 18 MCP tools (6 unified, 6 Jira, 6 Zulip).

> **Note:** Confluence and GitHub do not yet have dedicated MCP tools.
> They are accessible via the unified Search, FindRelated, and
> GetCrossReferences tools.

### MCP Endpoint

#### `GET /mcp` (SSE) / `POST /mcp` (HTTP)

The Model Context Protocol endpoint. MCP clients (VS Code, Copilot, etc.)
connect to this endpoint to discover and invoke the 18 MCP tools.

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
