# API Reference

FHIR Augury v2 uses a microservices architecture with HTTP/REST APIs for all
communication. The CLI and MCP tools connect to the orchestrator via HTTP.
Source services expose search and ingestion APIs; processor services expose
health, lifecycle, authoring, and maintenance APIs according to their role.
The Dev UI also uses the Orchestrator exclusively, including its
source-oriented API Tests tabs.

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
> `services` is the **aggregate dashboard** — it returns the latest typed
> observations for the Orchestrator, enabled sources, and enabled processors.
> Use `services` to render UI; use `status` for an automated ready/not-ready
> decision.

The legacy unversioned `GET /health` endpoint is still served by the
default Aspire health-check pipeline.

### Services

#### `GET /api/v1/services`

Get the cached typed readiness payload:

```jsonc
{
  "services": [
    {
      "name": "Planner",
      "serviceKind": "processing",
      "status": "healthy",
      "enabled": true,
      "configured": true,
      "checkedAt": "2026-09-10T12:00:00Z",
      "processingStatus": "running",
      "processingIsRunning": true,
      "processingRemainingCount": 2,
      "processingInFlightCount": 1,
      "processingErrorCount": 0,
      "requiredServices": [ "Jira", "GitHub" ]
    }
  ],
  "lastCheckedAt": "2026-09-10T12:00:00Z"
}
```

The payload always includes a local Orchestrator entry and every enabled
configured source/processor, including services not yet successfully observed.
`serviceKind` distinguishes `orchestrator`, `source`, and `processing`.
Processing observations include cached lifecycle/queue fields and
`requiredServices`. A missing observation is not proof that Aspire stopped a
resource.

#### `POST /api/v1/services/refresh`

Run a fresh bounded health sweep and return the same
`ServicesStatusResponse`. One service timeout is recorded as that service's
unavailable observation without discarding the rest of the sweep. This route
does not start or stop resources; use Aspire for resource lifecycle, logs, and
traces.

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

Jira item responses add two optional `ItemResponse` members. Other sources and
older payloads may omit them:

```jsonc
{
  "source": "jira",
  "id": "FHIR-43499",
  "title": "...",
  "provenance": {
    "source": "jira",
    "contentRevision": 1842,
    "isStable": true,
    "projectLastSuccessfulRefreshAt": {
      "FHIR": "2026-09-08T05:00:00+00:00"
    }
  },
  "people": {
    "publicDisplayNamePolicyVersion": 1,
    "reporter": "Ada Lovelace",
    "assignee": null,
    "inPersonRequesters": [
      "Grace Hopper"
    ]
  }
}
```

`contentRevision` identifies the local Jira database generation and
`isStable:false` means the read occurred while that generation was being
mutated. Project timestamps are the latest proven error-free upstream Jira
full/incremental refreshes, not cache rebuild or response times. `people`
contains only Jira-authenticated display names that pass the current public
display-name policy; it never adds username, email, or user ID fields. The
policy rejects a value equal to its proven account username
case-insensitively and any value containing an email-address-shaped token.
`publicDisplayNamePolicyVersion` is optional for wire compatibility, but a Jira
response that evaluated the complete people payload sets it to the current
version even when every name is absent. Downstream processors trust people
only when that marker exactly matches their current policy; missing, older,
unknown, and future markers fail closed. The requester list is
case-insensitively deduplicated and deterministically ordered.

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

Source-specific endpoints are reachable through typed orchestrator proxies at
`/api/v1/{name}/...`, where `{name}` is one of `jira`, `zulip`,
`confluence`, `github`, or `fhir`. Shared content and lifecycle operations may
instead use an Orchestrator-native aggregate route or typed metadata
replacement. A typed proxy preserves method, query string, body, response
status, and ETag / `Last-Modified` headers; it strips `Authorization` and
`Cookie` headers by design.

Examples:

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/v1/jira/query` | Structured Jira issue query |
| `POST` | `/api/v1/jira/ingest?jira-project=FHIR` | Trigger Jira ingest scoped to one project |
| `GET`  | `/api/v1/jira/work-groups` | List HL7 work groups |
| `POST` | `/api/v1/jira/local-processing/tickets?type=fhir` | Page local-processing candidates with source provenance |
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

The Dev UI's source tabs use an exhaustive route matrix over these gateway
surfaces. A tab may select an Orchestrator-native aggregate content route, a
typed source proxy, or an aggregate readiness/statistics replacement, but the
invocation base and inline schema document are always the Orchestrator. Source
tabs do not call the source ports directly.

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
| `GET` | `/api/v1/processing-services/{name}/authoring/runs?limit=N` | List bounded operator-visible runs (Preparer and Planner only) |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{sourceRunId}/publication-refresh` | Start a linked metadata-only Discussion publication refresh (Preparer only) |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{sourceRunId}/publication-reconciliation` | Start changed-ticket Discussion publication reconciliation (Preparer only) |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation` | Read the frozen comparison, grouping impact, invalidation, and promotion state (Preparer only) |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/retry` | Retry a pending immutable snapshot publication (Preparer only) |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/cancel` | Cancel staged reconciliation before trusted or canonical promotion, with an audited reason (Preparer only) |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/abandon` | Audit post-promotion abandonment without publication (Preparer only) |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}` | Get run and item status |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/items/{itemId}/retry` | Retry one eligible current error item |
| `POST` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/items/{itemId}/supersede` | Explicitly supersede one current non-receipt-backed error |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot` | Get the trusted snapshot descriptor |
| `GET` | `/api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |

Shipped configuration enables the names `Preparer`, `Planner`, and
`BallotNotes`. Applier is directly addressable on port 5173 but is not
registered with the Orchestrator by default. A generic proxy route does not
mean every configured service implements the underlying capability:
Preparer and Planner provide the common lifecycle plus the Jira authoring APIs,
including bounded run listing and structured conflict coordinates. BallotNotes
provides its existing authoring surface but not the Jira processors'
lifecycle/queue surface, bounded run listing, or structured conflict-coordinate
extension. Applier provides lifecycle/queue but no authoring-run API.
Receipt lookup and authenticated worker result submission are direct
processor endpoints; the Orchestrator does not proxy them.

The supersede request body is `{"reason":"<non-empty operator reason>"}`.
Publication-reconciliation items never advertise generic supersession.
Attempting that item endpoint returns `409` with exact
`error:"reconciliation-cancel-required"`, even before a receipt is accepted;
use the dedicated run-level cancellation route while promotion is still
`staged`.
The Orchestrator forwards the request and preserves the processor's response
body, content type, status code, and `Retry-After`; it does not interpret the
reason, attempt count, receipt state, or processor-owned lifecycle.

For Preparer and Planner, the run collection defaults to 20 entries and accepts
`limit` from 1 through 100. It selects at most `limit + 1` operator-visible
runs before item aggregation, prioritizes `queued`, `running`,
`finalizing`, and recoverable `error` runs, then orders terminal history newest
first. Ordinary runs require their durable request marker. Purpose-marked maintenance runs, including `grouping-maintenance`,
`publication-refresh`, `publication-reconciliation`, and the reserved
`canonical-epoch-recovery`, are also visible
without that marker so clients can reconcile by `purpose` and `sourceRunId`.
Initial revalidation and legacy unmarked authoring rows are omitted. The
response is:

```jsonc
{
  "runs": [ /* AuthoringRunStatus */ ],
  "truncated": false
}
```

Use exact-run `GET` as the escape hatch for a known omitted or older run.

Run status appends processor-owned state:

- `state.isTerminal` is `false` for `queued`, `running`, `finalizing`, and
  `error`; an `error` is recoverable and remains pollable.
- `state.isTerminal` is `true` for `completed`,
  `completed-database-only`, `superseded`, and `abandoned`. `abandoned` is a
  non-recoverable reconciliation outcome; there is no generic terminal
  `failed` status.
- `state.nextAutomaticRecoveryAt` describes the next run-level recovery time
  when known.
- `purpose` is an additive value such as `authoring`,
  `initial-revalidation`, `grouping-maintenance`,
  `publication-refresh`, or `publication-reconciliation`.
  `canonical-epoch-recovery` is reserved for the dedicated snapshot recovery
  workflow and is the only snapshot-producing purpose allowed through an
  unresolved canonical restriction.
- `sourceRunId` is additive maintenance lineage. For a
  `publication-refresh` or `publication-reconciliation`, it identifies the
  completed run selected by the operator; the maintenance run itself has a
  different `runId`.

Items retain the legacy `error` field and append `currentError`,
`supersessionReason`, and processor-computed `allowedActions`.
`allowedActions.canRetryNow` is true only for an eligible `error` in the
currently fenced run with an accepted receipt to resume or authoring attempts
remaining. `allowedActions.canSupersede` additionally requires no accepted receipt and is
always false for a publication-reconciliation item. These capabilities are
display hints: retry and supersede recheck the
fence, receipt, state, and budget atomically and may still return `409`.

For Preparer and Planner, create/retry/supersede conflicts preserve the stable
`error` and optional `detail` fields and add authoritative coordinates:

```jsonc
{
  "error": "active-run-capacity-reached",
  "detail": "...",
  "conflictingRunIds": [ "<runId>" ],
  "runId": "<runId>"
}
```

`runId` is present for legacy compatibility when there is one related run.
Capacity, revision-collision, and revalidation conflicts populate
`conflictingRunIds` when coordinates are available. Clients must not parse
human-readable detail to find a run.

An unresolved abandoned canonical epoch returns HTTP `409` with exact
`error:"canonical-unpublished-restriction"`. `conflictingRunIds` contains the
abandoned reconciliation and any linked canonical-epoch recovery run; `runId`
is populated only when there is one coordinate. The same envelope is used
whether admission rejected proactively or a last-ditch SQLite integrity
trigger won a race.

BallotNotes retains its existing conflict bodies with `error` and optional
`detail`; clients must not require `conflictingRunIds` or `runId` from that
processor.

#### Preparer Discussion publication refresh

Use the bodyless proxied mutation:

```http
POST /api/v1/processing-services/Preparer/authoring/runs/{sourceRunId}/publication-refresh
```

`sourceRunId` must name a completed, non-database-only Preparer run with a
ready snapshot. The selected run is lineage, not a request to rewrite its
snapshot or replay its item membership. The Preparer creates a new
snapshot-producing maintenance run over the current accepted receipt-backed
corpus, reuses every accepted receipt, and returns `202 Accepted`:

```jsonc
{
  "run": {
    "runId": "<refreshRunId>",
    "processorKind": "jira-fhir",
    "status": "running",
    "databaseOnly": false,
    "purpose": "publication-refresh",
    "sourceRunId": "<sourceRunId>",
    "state": {
      "isTerminal": false,
      "isRecoverable": false
    }
  },
  "items": [
    {
      "runId": "<refreshRunId>",
      "businessKey": "FHIR-123",
      "status": "complete",
      "acceptedReceiptId": "<retainedReceiptId>",
      "attemptCount": 0
    }
  ]
}
```

The response `Location` is
`/api/v1/processing-services/Preparer/authoring/runs/{refreshRunId}`.
The Orchestrator preserves the processor body, status, `Location`, and
`Retry-After`; it sends no request body and does not perform the repair itself.
The direct Preparer route is
`POST /processing/authoring/runs/{sourceRunId}/publication-refresh`, whose
direct `Location` is
`/processing/authoring/runs/{refreshRunId}`. The Orchestrator rewrites that
location to the proxied status route above.

Immediate rejection uses the typed fields `error`, optional `detail`,
`conflictingRunIds`, and the legacy-compatible single `runId` when one related
run is known:

| Status | Stable `error` | Meaning |
|--------|----------------|---------|
| `400` | `invalid-source-run` | The coordinate is not a completed snapshot-producing Preparer run or its ready descriptor is invalid |
| `404` | `source-run-not-found` | No source run exists at that coordinate |
| `409` | `authoring-not-activated`, `cutover-in-progress`, or `revalidation-required` | Run-backed authoring is not ready for maintenance |
| `409` | `mutation-fence-unavailable` | Another fenced mutation is active; related run coordinates are returned when known |
| `409` | `canonical-unpublished-restriction` | An abandoned canonical epoch has no verified replacement snapshot; related abandoned/recovery run coordinates are returned |
| `409` | `run-not-active`, `source-revision-mismatch`, or `stage-fingerprint-mismatch` | Processor state no longer permits the requested maintenance start |
| `409` | `invalid-source-snapshot` | The selected original snapshot fails identity, integrity, schema, size, or digest validation |
| `409` | `original-output-changed` or `original-grouping-changed` | Original accepted output or grouping values/IDs/order/membership no longer match the source snapshot |
| `409` | `invalid-accepted-graph`, `invalid-protection-catalog`, or `unclassified-protection-column` | The current graph cannot be safely covered by the publication-preservation contract |

After acceptance, poll the returned run through the ordinary status route.
Admission preserves the original snapshot's protected output and freezes the
entire current accepted corpus with the `publication-enrichment` v1 recipe,
items, and mutation fence in one transaction. Optional public
`run.corpusComparison` discloses its `sourceSnapshotId`,
`sourceExportedTicketCount`, `currentAcceptedTicketCount`, and
`additionalTicketCount`; the source run's item count is not a substitute.
Finalization performs allowlisted Jira and accepted-Zulip metadata updates,
existing grouping certification, and snapshot materialization; it launches no
authoring or grouping worker. Source people preview/apply is separate and is
never invoked by this operation. Transient source and stage failures can appear
as recoverable run `error` with `state.isTerminal:false`. A durable metadata apply receipt
makes recovery after the metadata commit idempotent without refetching Jira or
Zulip, and snapshot reconciliation preserves the same proof-bearing snapshot
after interrupted promotion.

A missing ticket, per-ticket revision mismatch, or mixed Jira content
generation, as well as frozen protected-state drift, terminally changes the
refresh run to `superseded`, releases its mutation fence, and prevents a usable
new snapshot. A rejected metadata batch has no partial apply or apply receipt;
a later refusal does not undo metadata that already committed. Retain the old
pair/site and inspect the conflict. Deletion, reset, ordinary re-authoring,
regrouping, and forced replacement of the old site are not repair fallbacks.
Run error text contains stable stage failure codes such as
`source-unavailable`, `ticket-not-found`, `invalid-source-response`,
`missing-source-provenance`, `unstable-source`,
`missing-project-provenance`, `people-policy-not-current`,
`source-revision-mismatch`, `source-generation-conflict`,
`frozen-protection-drift`, `invalid-publication-recipe`, and
`unsupported-publication-recipe`.

Only persisted null request metadata selects the legacy Jira-only
`publication-metadata` recipe during recovery. New runs use
`publication-enrichment-v1`; malformed or unknown non-null recipes are
refused, and legacy receipts cannot satisfy the new stage. Quiesce/reconcile
new-recipe runs before a binary downgrade. These contracts do not authorize
live repair or establish recovery of a particular historical publication.

Outer-control clients do not replay start, retry, supersede,
publication-refresh, or publication-reconciliation start/retry/cancel/abandon
mutations after transport loss because the processor may have committed the
mutation. For an unknown
refresh outcome, issue one bounded read-only
`GET /api/v1/processing-services/Preparer/authoring/runs?limit=20` and inspect
runs created since submission whose `purpose` is `publication-refresh` and
whose `sourceRunId` matches. Zero, one, or multiple candidates require
operator review, and `truncated:true` must be surfaced. Reads and snapshot
downloads may use transient retry.

#### Preparer changed-ticket publication reconciliation

This is a separate lifecycle from the metadata-only publication refresh
above. Refresh refuses changed accepted Jira revisions and never re-authors or
regroups. Reconciliation discovers the complete changed baseline set,
re-authors only that set into staging, recomputes the affected grouping
closure with unchanged tickets as fixed input, and publishes a new immutable
replacement.

Start it with the bodyless proxied mutation:

```http
POST /api/v1/processing-services/Preparer/authoring/runs/{sourceRunId}/publication-reconciliation
```

The direct Preparer route is
`POST /processing/authoring/runs/{sourceRunId}/publication-reconciliation`.
`sourceRunId` must identify a completed, non-database-only Preparer run with a
valid ready snapshot. Admission validates that immutable baseline, reads every
accepted baseline ticket through the Orchestrator from one stable Jira content
generation, and records every ticket as `carry-forward` or `re-author`.
Revision differences are collected across the complete pass rather than
stopping at the first mismatch. The observation is repeated under the
mutation fence before the comparison, run, mixed items, and staging recipe are
committed.

Success returns `202 Accepted`. The proxied `Location` is the reconciliation
status endpoint for the new run:

```jsonc
{
  "run": {
    "runId": "<reconciliationRunId>",
    "processorKind": "jira-fhir",
    "purpose": "publication-reconciliation",
    "sourceRunId": "<sourceRunId>",
    "databaseOnly": false
  },
  "items": [
    {
      "businessKey": "FHIR-123",
      "status": "pending"
    }
  ],
  "comparison": {
    "contractVersion": 2,
    "sourceRunId": "<sourceRunId>",
    "sourceSnapshotId": "<sourceSnapshotId>",
    "sourceSnapshotSha256": "<sha256>",
    "stableJiraGeneration": "1234",
    "capturedAt": "<UTC timestamp>",
    "corpusFingerprint": "<sha256>",
    "items": [
      {
        "ticketKey": "FHIR-123",
        "disposition": "re-author",
        "baselineSourceRevision": "<revision>",
        "currentSourceRevision": "<revision>",
        "baselineReceiptId": "<receiptId>",
        "baselineRunItemId": "<itemId>",
        "baselineContributingRunId": "<runId>",
        "baselineAuthoredFingerprint": "<sha256>",
        "baselineGroupingFingerprint": "<sha256>",
        "itemKind": "fhir",
        "expectedSourceRevision": "<revision>"
      }
    ]
  },
  "counts": {
    "acceptedTicketCount": 20,
    "carryForwardTicketCount": 18,
    "reAuthorTicketCount": 2,
    "invalidatedTicketCount": 0
  }
}
```

Carry-forward items are already complete at their frozen receipt coordinates.
Changed items accept complete authored graph/hydration output into run-scoped
staging only. The grouping impact includes every old/new partition and shared
topic/group/container whose text, identity, membership, or ordering can
change. Canonical rows are untouched until all changed items, complete
grouping replacements, unaffected-row/receipt/grouping fingerprints, and the
temporary candidate snapshot validate.

Read the complete state with:

```http
GET /api/v1/processing-services/Preparer/authoring/runs/{runId}/publication-reconciliation
```

The direct route is
`GET /processing/authoring/runs/{runId}/publication-reconciliation`. It
returns `200 OK` with `run`, `items`, `comparison`, `counts`,
`groupingImpacts`, `promotion`, `invalidatedTicketKeys`, nullable
`publicationProof`, and nullable `failureCode`/`failureDetail`.
`promotion` contains `state`, nullable `journalState`, `mutationFenceHeld`,
`lastRecoveryAttemptAt`, recovery failure fields, cancellation
`cancelledAt`/`cancellationReason`, and abandonment audit fields. The proof is
present only for the verified replacement and has exact purpose
`publication-reconciliation`.

Promotion uses database-first ordering. The temporary snapshot is
materialized and verified while state is `staged`. One immediate SQLite
transaction then applies revised canonical graphs, accepted receipt state,
and complete affected grouping replacements; creates the snapshot row; and
journals `snapshot-publish-pending` while retaining the fence. Filesystem
publication and the snapshot/run/journal ready transitions occur afterward.
This is a durable resumable protocol, not physical atomicity across SQLite and
the filesystem.

| Promotion/evidence state | Recovery behavior |
|--------------------------|-------------------|
| `staged` | Canonical output is unchanged. Every `re-author` item must be `complete` with matching graph, hydration, accepted receipt, run-item, operation, revision, and fingerprint coordinates. A `superseded` item never satisfies this check. Missing graph/group staging is `staging-mismatch`/`grouping-impact-mismatch`. Dedicated cancellation is available until trusted candidate persistence. |
| Pending, final absent, temporary valid | Add/verify snapshot provenance, validate the candidate, and move it once to the immutable final path. |
| Pending, final matching | Reuse and validate it; never reapply canonical replacement. This covers interruption immediately after the move. |
| Pending, final conflicting/corrupt | Stop with `promotion-recovery-failure`, never overwrite the file, and keep the fence. |
| Pending, temporary missing/corrupt with no valid final | Stop with `promotion-recovery-failure`; retain journal/fence for repair. A checksum or provenance mismatch is handled the same way. |
| Snapshot row absent/conflicting | Stop with `promotion-recovery-failure`. A creating row advances after file validation, a promoted row advances to ready, and a ready row is reused. |
| Snapshot ready, run incomplete | Complete the same run and continue the journal/fence transition. |
| Run complete, journal still pending | Mark the reconciliation `ready`, release the fence, and clean staging. |
| `ready` | Final snapshot, run, and journal agree; staging is no longer required. Recovery is idempotent. |
| `canonical-unpublished` | Explicit terminal abandonment; it is not recoverable through the pending retry route and is not a successful publication. |
| `cancelled` | Terminal pre-promotion cancellation. Canonical output and the prior publication are unchanged; both fences and disposable workspace are released. |

Startup recovery automatically scans pending journals. The explicit equivalent
is bodyless and returns `{ "status": { /* full reconciliation status */ },
"recoveryStarted": true }` on success:

```http
POST /api/v1/processing-services/Preparer/authoring/runs/{runId}/publication-reconciliation/retry
```

The direct route has the same suffix under
`/processing/authoring/runs`. While pending, all competing Preparer mutations
return `409` with `error:"recovery-in-progress"`.

Only while both reconciliation and journal remain `staged`, before trusted
candidate evidence or canonical replacement exists, may an operator cancel:

```http
POST /api/v1/processing-services/Preparer/authoring/runs/{runId}/publication-reconciliation/cancel
Content-Type: application/json

{"reason":"<non-blank audited reason>"}
```

The direct route has the same suffix under
`/processing/authoring/runs`. Success returns the full terminal `status`,
`cancelledAt`, and persisted `reason`. One transaction records the audit,
ends active attempts and incomplete stages, projects every run item and the
generic run to terminal `superseded` while retaining accepted receipts,
releases both fences, and deletes only
unpromoted disposable workspace. The frozen comparison, item decisions,
generic receipts, and cancellation journal remain. Repeating cancellation
returns the original audit. Cancellation is not offered and returns
`cancellation-not-allowed` after `snapshot-publish-pending`,
`canonical-unpublished`, or `ready`.

Only after the database transaction has committed may an operator explicitly
release the fence without a replacement publication:

```http
POST /api/v1/processing-services/Preparer/authoring/runs/{runId}/publication-reconciliation/abandon
Content-Type: application/json

{"reason":"<non-blank audited reason>"}
```

The direct route again uses `/processing/authoring/runs`. Success returns the
full `status`, `abandonedAt`, and exact `reason`. It records
`canonical-unpublished`, transitions the generic run from `finalizing` or
recoverable `error` to terminal, non-recoverable `abandoned`, leaves promoted
canonical rows in place, retains the prior immutable publication, and does
not treat abandonment as proof. The run releases capacity and both fences and
does not participate in retry, scheduling, or finalization.

Snapshot intent is durable: every run with `databaseOnly:false` is
snapshot-producing regardless of purpose. While the restriction is
unresolved, the Preparer checks that intent at admission, immediately before
a queued run acquires the mutation fence, before snapshot candidate creation,
and immediately before finalization/promotion. A snapshot run queued before
abandonment is terminally refused before fence acquisition. SQLite guards on
run/fence/snapshot/finalization writes remain the last line of defense.
Ordinary `databaseOnly:true` work remains eligible, including when active-run
capacity is one. Only the reserved `canonical-epoch-recovery` purpose may
bypass the restriction so a separately explicit recovery can create and
verify the snapshot that resolves the epoch.

Stable reconciliation failure codes are:

| `error` / `failureCode` | Meaning |
|-------------------------|---------|
| `invalid-baseline` | Missing/ineligible source run, invalid snapshot, or baseline/current protected-output mismatch |
| `unstable-jira-generation` | Any baseline ticket lacked stable source evidence, observations spanned generations, or Jira changed while admission was frozen |
| `revision-invalidation` | A frozen changed ticket moved again before completion |
| `staging-mismatch` | Changed-ticket graph/receipt staging is incomplete or divergent |
| `grouping-impact-mismatch` | The affected closure or a complete partition replacement is missing/divergent |
| `recovery-in-progress` | A pending promotion owns the mutation fence |
| `promotion-recovery-failure` | Temporary/final file, snapshot row, checksum/provenance, or transition evidence conflicts |
| `cancellation-not-allowed` | Cancellation was missing a reason or the reconciliation was no longer staged before trusted/canonical promotion |
| `canonical-unpublished-restriction` | The live canonical epoch was explicitly abandoned without a verified replacement snapshot |

Start returns `404` with `invalid-baseline` when the source coordinate is not
found and `409` for typed admission/fence conflicts. Status returns `404`
`invalid-baseline` for an unknown/non-reconciliation run. Retry, cancel, and abandon
return `409` with a typed failure when their state/evidence preconditions are
not met. The Orchestrator preserves processor status, body, `Location`, and
`Retry-After`; clients must branch on the stable code, not prose. Never replay
a lost start/retry/cancel/abandon response. Use the bounded run list filtered by
`purpose` and `sourceRunId`, or status when `runId` is known.

Snapshot bytes are streamed through the Orchestrator rather than buffered as a
complete SQLite file. Range and conditional request headers are forwarded, and
the proxy preserves `206`, `304`, content range/length/disposition,
`Accept-Ranges`, ETag, last-modified, and `Retry-After` semantics.

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

## Jira Service HTTP API

**Base URL:** `http://localhost:5160/api/v1`

The direct Jira routes below are also available through the typed Orchestrator
proxy under `http://localhost:5150/api/v1/jira`. The proxy preserves the
additive response fields.

### Jira item provenance and people

#### `GET /api/v1/items/{key}`

Returns the common `ItemResponse` plus the Jira `provenance` and `people`
objects documented under [Orchestrator Items](#items). The issue row, optional
comments, people, project watermark, and content-generation fence are read in
one SQLite transaction. Legacy string metadata remains for compatibility;
consumers needing public person data should use the structured `people` object.

### Local-processing candidates

#### `POST /api/v1/local-processing/tickets?type=fhir`

Returns one filtered page and its unpaged total with a provenance envelope
captured in the same read transaction:

```jsonc
{
  "results": [
    {
      "key": "FHIR-43499",
      "projectKey": "FHIR",
      "title": "...",
      "type": "Change Request",
      "status": "Triaged",
      "priority": "Medium",
      "workGroup": "FHIR Infrastructure",
      "specification": "FHIR Core (FHIR)",
      "url": "https://jira.hl7.org/browse/FHIR-43499",
      "updatedAt": "2026-09-07T12:00:00+00:00"
    }
  ],
  "limit": 500,
  "offset": 0,
  "total": 1,
  "provenance": {
    "source": "jira",
    "contentRevision": 1842,
    "isStable": true,
    "projectLastSuccessfulRefreshAt": {
      "FHIR": "2026-09-08T05:00:00+00:00"
    }
  }
}
```

`type` selects `fhir`, `pss`, `baldef`, or `ballot`; omitted means `fhir`.
The request body is `JiraLocalProcessingListRequest` (filters plus `limit` and
`offset`). A client consuming multiple pages must require stable matching
content revisions and project watermark values across the complete sequence.
The shipped Jira processor client retries the whole sequence up to three
passes and suppresses provenance if no stable generation can be proven.

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

Preparer and Planner expose the common direct control family under
`/api/v1/processing/authoring/runs`. Preparer additionally exposes the
unversioned publication-maintenance routes shown below:

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/v1/processing/authoring/runs` | Create a frozen run |
| `GET` | `/api/v1/processing/authoring/runs?limit=N` | List bounded operator-visible runs |
| `GET` | `/api/v1/processing/authoring/runs/{runId}` | Get run and item status |
| `POST` | `/processing/authoring/runs/{sourceRunId}/publication-refresh` | Start a linked Preparer metadata-only publication refresh (Preparer only) |
| `POST` | `/processing/authoring/runs/{sourceRunId}/publication-reconciliation` | Start Preparer changed-ticket publication reconciliation |
| `GET` | `/processing/authoring/runs/{runId}/publication-reconciliation` | Get complete reconciliation and recovery status |
| `POST` | `/processing/authoring/runs/{runId}/publication-reconciliation/retry` | Retry pending snapshot publication |
| `POST` | `/processing/authoring/runs/{runId}/publication-reconciliation/cancel` | Cancel staged reconciliation with an audited reason |
| `POST` | `/processing/authoring/runs/{runId}/publication-reconciliation/abandon` | Audit abandonment after canonical promotion |
| `POST` | `/api/v1/processing/authoring/runs/{runId}/items/{itemId}/retry` | Retry one eligible current error item |
| `POST` | `/api/v1/processing/authoring/runs/{runId}/items/{itemId}/supersede` | Supersede one current error with an explicit reason |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/operations/{operationId}/receipt` | Retrieve the durable operation receipt |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/snapshot` | Retrieve the trusted snapshot descriptor |
| `GET` | `/api/v1/processing/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |

Processor-launched workers submit authenticated results directly to the
unversioned callback
`POST /processing/authoring/runs/{runId}/items/{itemId}/result`. This callback
is not an outer operator endpoint and is not exposed through the Orchestrator.

`retry` requests an immediate retry and may bypass the configured delay, but
cannot expand the total attempt limit. `supersede` requires
`{"reason":"..."}` and is accepted only for an `error` item without an
accepted receipt in the currently fenced run. Missing or blank reasons return
400, unknown or mismatched coordinates return 404, and invalid lifecycle,
fence, attempt-limit, completed, or receipt-backed states return 409.
Reconciliation items are excluded regardless of receipt state and return
`reconciliation-cancel-required`; their only pre-promotion terminal operator
action is the dedicated reason-bearing cancellation route.

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
| `POST` | `/api/v1/ballot-notes/authoring/runs/{runId}/items/{itemId}/supersede` | Supersede one current error with an explicit reason |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/operations/{operationId}/receipt` | Retrieve the durable operation receipt |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/snapshot` | Retrieve the trusted snapshot descriptor |
| `GET` | `/api/v1/ballot-notes/authoring/runs/{runId}/snapshot/bytes` | Download immutable snapshot bytes |
| `POST` | `/api/v1/ballot-notes/authoring/runs/{runId}/items/{itemId}/{type}/{slug}/result` | Submit an authenticated worker result |

BallotNotes does not expose a collection `GET` for bounded run listing, and its
conflict responses do not guarantee the Preparer/Planner
`conflictingRunIds`/`runId` extension.

All three run-status envelopes use the same additive failure fields:
`failedItems` is the aggregate of retryable errors plus terminal
non-authored outcomes, `retryableErrorItems` counts the errors still governed
by processor-owned automatic retry, and `supersededItems` counts terminal
item-level outcomes. Error items include `attemptsRemaining` and
`nextAutomaticRetryAt` where applicable. A run that finishes as `completed` or
`completed-database-only` while containing superseded items is lifecycle
success with an explicit partial-authoring result; consumers may continue
snapshot publication from accepted results but must surface every superseded
item and reason.

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
| `409` | Processor-owned conflict or an action that lost eligibility |
| `503` | Service unavailable |
