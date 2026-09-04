# API Reference

FHIR Augury v2 uses a microservices architecture with HTTP/REST APIs for all
communication. The CLI and MCP tools connect to the orchestrator via HTTP.
Source services expose search and ingestion APIs; processor services expose
health, lifecycle, authoring, and maintenance APIs according to their role.

## Architecture

| Service | Port | Description |
|---------|------|-------------|
| Orchestrator | 5150 | Central hub — routes queries to sources |
| Jira | 5160 | Indexes jira.hl7.org |
| Zulip | 5170 | Indexes chat.fhir.org |
| Confluence | 5180 | Indexes confluence.hl7.org |
| GitHub | 5190 | Indexes HL7 GitHub repos |
| FHIR | 5195 | Serves FHIR spec reference data (read-only) |
| Preparer | 5171 | Authors prepared discussion-ticket output |
| Planner | 5172 | Authors implementation plans |
| Applier | 5173 | Applies queued plans locally and pushes on demand |
| BallotNotes | 5174 | Hydrates evidence and authors ballot notes |
| MCP HTTP | 5200 | HTTP/SSE MCP server (`FhirAugury.McpHttp`) |

> **Note:** The MCP HTTP server (`FhirAugury.McpHttp`) is a separate service on
> port 5200 that provides the same MCP tools via HTTP/SSE transport. The CLI
> (`FhirAugury.Cli`) is the recommended way to interact with the API. See the
> [CLI Reference](cli-reference.md) for all available commands.

---

## Orchestrator HTTP API

**Base URL:** `http://localhost:5150`

### Health Check

#### `GET /api/v1/health`

Always returns `200 OK`. Liveness signal — the orchestrator process is
running and accepting HTTP requests. Does **not** consult source health.

**Response:**

```json
{
  "status": "healthy",
  "service": "orchestrator",
  "version": "2.0.0"
}
```

#### `GET /api/v1/status`

Readiness signal sourced from the in-process service-health registry.
Returns `200 OK` when the orchestrator considers itself ready (every
required source is healthy), or `503 Service Unavailable` when one or
more configured sources are degraded. Use this for load-balancer
readiness probes and dashboards.

> **Note on the difference vs `GET /api/v1/services`.** `health` and
> `status` are orchestrator-only signals (process liveness / readiness).
> `services` is the **aggregate dashboard** — it fans out to every source
> and returns per-source health, last-sync time, item counts, and index
> state. Use `services` to render UI; use `status` for an automated
> ready/not-ready decision.

The legacy unversioned `GET /health` endpoint is still served by the
default Aspire health-check pipeline.

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

#### `GET /api/v1/content/item/{source}/{**id}`

Get full details of a content item from any source, with optional content body,
comments, and markdown snapshot.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `source` | string | Source type (jira, zulip, confluence, github) |
| `**id` | string | Item identifier (multi-segment greedy catch-all — preserves `/` in keys such as `HL7/fhir:source/patient/...`) |

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
| `type` | string | No | `full`, `incremental`, or `rebuild` (default: `incremental`). The CLI verb for `rebuild` is `reingest`; the wire value remains `rebuild`. |
| `sources` | string | No | Comma-separated sources to sync (omit for all) |
| `jira-project` | string | No | Restrict ingestion to a single Jira project key. Forwarded only to the Jira leg of the fan-out; ignored by other sources. |

**Example:** `POST /api/v1/ingest/trigger?type=incremental&sources=jira,zulip`
**Example (single Jira project):** `POST /api/v1/ingest/trigger?sources=jira&jira-project=FHIR`

### Rebuild Index

#### `POST /api/v1/rebuild-index`

Rebuild specific indexes on source services.

**Query Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `type` | string | No | Index type: `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents` (default: `all`) |
| `sources` | string | No | Comma-separated sources to rebuild (omit for all) |

### Internal Notification

#### `POST /api/v1/notify-ingestion`

Internal peer notification endpoint. Source services call this when an
ingestion run completes; the orchestrator persists the cross-reference
scan and fans out a `POST /api/v1/{name}/notify-peer` to every other
enabled source so peers can re-scan their cross-reference indexes against
the freshly-updated source. Both halves of this protocol carry the
`ingestion-notifications` OpenAPI tag.

**Request Body:** `PeerIngestionNotification` object.

### Typed Source Proxies

Every source endpoint is reachable through a typed orchestrator proxy at
`/api/v1/{name}/...`, where `{name}` is one of `jira`, `zulip`,
`confluence`, or `github`. The proxy preserves method, query string,
body, response status, and ETag / `Last-Modified` headers; it strips
`Authorization` and `Cookie` headers by design.

Examples:

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/v1/jira/query` | Structured Jira issue query |
| `POST` | `/api/v1/jira/ingest?jira-project=FHIR` | Trigger Jira ingest scoped to one project |
| `GET`  | `/api/v1/jira/work-groups` | List HL7 work groups |
| `POST` | `/api/v1/zulip/query` | Structured Zulip query |
| `GET`  | `/api/v1/zulip/streams` | List Zulip streams |
| `GET`  | `/api/v1/zulip/items/{id}/comments` | Always returns `[]` (shape-parity stub) |
| `GET`  | `/api/v1/zulip/items/{id}/links` | Always returns `[]` (shape-parity stub) |
| `GET`  | `/api/v1/confluence/pages/{pageId}` | Get a Confluence page |
| `GET`  | `/api/v1/confluence/ingestion-block` | Is Confluence ingestion blocked by an edge captcha challenge? |
| `POST` | `/api/v1/confluence/ingestion-block/clear?clearedBy=you` | Clear the block after solving the challenge |
| `GET`  | `/api/v1/github/repos` | List indexed GitHub repositories |
| `GET`  | `/api/v1/github/items/snapshot/{**key}` | GitHub action-first item snapshot (catch-all key preserves `owner/name#123`) |

The full set of typed proxy routes is enumerated in
[Source Endpoint Reference](../technical/source-endpoint-reference.md)
and surfaced in the merged orchestrator OpenAPI document.

### Processing-service proxies

The Orchestrator exposes configured processors under
`/api/v1/processing-services`:

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/v1/processing-services` | List enabled processing services and their configured metadata and cached health |
| `GET` | `/api/v1/processing-services/{name}/health` | Proxy the named service's health |
| `GET` | `/api/v1/processing-services/{name}/status` | Proxy processing status |
| `GET` | `/api/v1/processing-services/{name}/queue` | Proxy queue statistics |
| `POST` | `/api/v1/processing-services/{name}/start` | Start queue processing |
| `POST` | `/api/v1/processing-services/{name}/stop` | Stop queue processing |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs` | Create an authoring run |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}` | Get run and item status |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/items/{itemId}/retry` | Retry one eligible failed item |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot` | Get the trusted snapshot descriptor |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |

Shipped configuration enables the names `Preparer`, `Planner`, and
`BallotNotes`. Applier is directly addressable on port 5173 but is not
registered with the Orchestrator by default. A generic proxy route does not
mean every configured service implements the underlying capability:
Preparer and Planner provide the common lifecycle plus authoring APIs,
BallotNotes provides authoring but not the Jira processors' lifecycle/queue
surface, and Applier provides lifecycle/queue but no authoring-run API.
Receipt lookup and authenticated worker result submission are direct
processor endpoints; the Orchestrator does not proxy them.

> **Note.** There is no generic reverse proxy at
> `/api/v1/source/{name}/...`; per-source operations are exposed through
> the typed proxies. The orchestrator self-metadata routes
> (`/api/v1/source/orchestrator/...`) are preserved by design.

---

## MCP HTTP Server

**Base URL:** `http://localhost:5200`

The MCP HTTP server (`FhirAugury.McpHttp`) is a separate ASP.NET Core service
that exposes MCP tools via HTTP/SSE transport. It is distinct from the
orchestrator — it connects to the orchestrator and source services via HTTP as
a client. The server provides 16 MCP tools across 4 categories (Unified,
Content, Jira, Zulip).

### MCP Endpoint

#### `GET /mcp` (SSE) / `POST /mcp` (HTTP)

The Model Context Protocol endpoint. MCP clients (VS Code, Copilot, etc.)
connect to this endpoint to discover and invoke the 16 MCP tools.

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

### Status / Health

Each source service publishes both an unversioned `GET /health` (Aspire
default) and the versioned trio:

#### `GET /api/v1/health`

Always `200`; process liveness only.

#### `GET /api/v1/status`

Returns source service health status (readiness — `200` when ready,
`503` when degraded).

#### `GET /api/v1/stats`

Source-specific statistics (item counts, last sync time, index status).

### Ingestion

#### `POST /api/v1/ingest`

Synchronous ingestion. Jira additionally accepts `?project=KEY` to scope
to a single project; the typed orchestrator proxy renames this consumer-
facing parameter to `?jira-project=KEY` (see
[Typed Source Proxies](#typed-source-proxies)).

#### `POST /api/v1/ingest/trigger`

Asynchronous ingestion (queues a run and returns immediately).

### Reingest / Reindex

#### `POST /api/v1/rebuild`

Rebuild database from cache (no upstream re-fetch). The CLI surface uses
the `reingest` verb for this operation; the wire path remains `rebuild`.

#### `POST /api/v1/rebuild-index`

Rebuild specific indexes on this source service. The CLI surface uses
the `reindex` verb for this operation; the wire path remains
`rebuild-index`.

### Internal Peer Notification

#### `POST /api/v1/notify-peer`

Receives `PeerIngestionNotification` from the orchestrator after another
source's ingestion run completes; triggers a cross-reference re-scan
against the newly updated peer. Tagged `ingestion-notifications`.

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

## Processor Service HTTP APIs

Processors own their databases and expose HTTP control surfaces; clients must
not open those databases directly. Full request and response schemas are
available through [OpenAPI](openapi.md). Operational sequencing and failure
recovery remain in the [processor runbook](../technical/processors.md), and
service registration is described in
[Processing Services configuration](../configuration.md#processing-services).

### Jira processor lifecycle

Preparer (`http://localhost:5171`), Planner (`http://localhost:5172`), and
Applier (`http://localhost:5173`) expose the versioned
Processing.Common surface:

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/api/v1/health` | Processor and database health |
| `GET` | `/api/v1/status` | Lifecycle state and processing counters |
| `GET` | `/api/v1/processing/queue` | Queue statistics |
| `POST` | `/api/v1/processing/start` | Admit and process queued work |
| `POST` | `/api/v1/processing/stop` | Stop admitting queued work |

### Preparer and Planner authoring runs

Preparer and Planner expose the same direct control family under
`/api/v1/processing/authoring/runs`:

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/v1/processing/authoring/runs` | Create a frozen run |
| `GET` | `/api/v1/processing/authoring/runs/{runId}` | Get run and item status |
| `POST` | `/api/v1/processing/authoring/runs/{runId}/items/{itemId}/retry` | Retry one eligible failed item |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/operations/{operationId}/receipt` | Retrieve the durable operation receipt |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/snapshot` | Retrieve the trusted snapshot descriptor |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |

Processor-launched workers submit authenticated results directly to the
unversioned callback
`POST /processing/authoring/runs/{runId}/items/{itemId}/result`. This callback
is not an outer operator endpoint and is not exposed through the Orchestrator.

### BallotNotes APIs

BallotNotes (`http://localhost:5174`) has a distinct surface: default
`GET /health`, hydration under `/api/v1/ballot-notes/hydrate`, authoring runs
under `/api/v1/ballot-notes/authoring/runs`, and maintenance under
`/api/v1/ballot-notes/maintenance`. Do not assume the Jira processors'
versioned status, queue, start, or stop routes apply to it.

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/v1/ballot-notes/authoring/runs` | Create a run scoped to a hydration execution |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}` | Get run and item status |
| `POST` | `/api/v1/ballot-notes/authoring/runs/{runId}/items/{itemId}/retry` | Retry one eligible failed item |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/operations/{operationId}/receipt` | Retrieve the durable operation receipt |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/snapshot` | Retrieve the trusted snapshot descriptor |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |
| `POST` | `/api/v1/ballot-notes/authoring/runs/{runId}/items/{itemId}/{type}/{slug}/result` | Submit an authenticated worker result |

### Applier API

Applier consumes queued plan output through the common lifecycle/queue
surface. It is intentionally outside the run-backed authoring route tables.
After local application, push one ticket's configured repositories on demand:

#### `POST /api/v1/applied-tickets/{ticketKey}/push`

---

## Service Ports Reference

| Service | Health Check URL | Purpose |
|---------|-----------------|---------|
| Orchestrator | `http://localhost:5150/health` | Central coordination |
| Jira | `http://localhost:5160/health` | Jira issue indexing |
| Zulip | `http://localhost:5170/health` | Zulip message indexing |
| Confluence | `http://localhost:5180/health` | Confluence page indexing |
| GitHub | `http://localhost:5190/health` | GitHub issue/PR indexing |
| Preparer | `http://localhost:5171/health` | Prepared discussion-ticket authoring |
| Planner | `http://localhost:5172/health` | Implementation-plan authoring |
| Applier | `http://localhost:5173/health` | Queue-driven local application and on-demand push |
| BallotNotes | `http://localhost:5174/health` | Hydration and ballot-note authoring |
| MCP HTTP | `http://localhost:5200/health` | MCP HTTP/SSE server |

---

## Health Check Format

Core source services return health checks in this common format:

```json
{
  "status": "healthy",
  "service": "<service-name>",
  "version": "2.0.0"
}
```

The `status` field will be `"healthy"` when the service is operating normally.
Processor health payloads reflect their distinct lifecycle and database state;
use each processor's [OpenAPI document](openapi.md) for its exact shape.

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
