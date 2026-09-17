# CLI Reference

The FHIR Augury CLI (`fhir-augury`) connects to the orchestrator service via
HTTP to search, browse, and manage FHIR community data across all source
services. All input and output uses JSON.

## Usage

```bash
fhir-augury --json '{"command":"<name>", ...}' [--pretty] [--output <file>]
fhir-augury --input <file> [--pretty] [--output <file>]
fhir-augury --help [command] [--pretty] [--output <file>]
fhir-augury --json @file.json [--pretty] [--output <file>]
fhir-augury --json @- [--pretty] [--output <file>]
```

## Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `--json <string>` | Yes (unless `--input` or `--help`) | JSON string containing the command request. Use `@file.json` to read from a file, `@-` to read from stdin. Mutually exclusive with `--input`. |
| `--input <file>` | Yes (unless `--json` or `--help`) | Path to a JSON file containing the command request. Mutually exclusive with `--json`. |
| `--output <file>` | No | Write JSON output to the specified file instead of stdout. |
| `--pretty` | No | Pretty-print JSON output. Default is compact single-line JSON. |
| `--help [command]` | No | Output JSON describing available commands and their schemas. With a command name, returns only that command's schemas. |

## Environment Variables

| Variable | Description |
|----------|-------------|
| `FHIR_AUGURY_ORCHESTRATOR` | Orchestrator HTTP endpoint (default: `http://localhost:5150`) |

The orchestrator address can also be set per-request via the `orchestrator`
field in the JSON input.

---

## Input JSON Format

Every request is a JSON object with a required `command` field:

```jsonc
{
  "command": "<command-name>",    // required
  "orchestrator": "http://...",  // optional override
  "verbose": false               // optional; include timing in metadata
}
```

## Output JSON Format

All responses use a consistent envelope:

```jsonc
// Success
{
  "success": true,
  "command": "search",
  "data": { /* command-specific */ },
  "metadata": { "elapsedMs": 142, "orchestrator": "http://localhost:5150", "version": "1.2.0" },
  "warnings": []
}

// Error
{
  "success": false,
  "command": "search",
  "error": { "code": "CONNECTION_FAILED", "message": "...", "details": "..." },
  "metadata": { "orchestrator": "http://localhost:5150", "version": "1.2.0" }
}
```

---

## Commands

### `search` — Unified search

```jsonc
{
  "command": "search",
  "query": "patient matching algorithm",   // required
  "sources": ["jira", "zulip"],            // optional (default: all)
  "limit": 20                              // optional (default: 20)
}
```

### `get` — Get full item details

```jsonc
{
  "command": "get",
  "source": "jira",              // required
  "id": "FHIR-43499",           // required
  "includeComments": true,       // optional (default: true)
  "includeContent": false,       // optional (default: false)
  "includeSnapshot": false       // optional (default: false)
}
```

### `refers-to` — Outgoing cross-references

Returns items that the specified item refers to (outgoing links).

```jsonc
{
  "command": "refers-to",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `referred-by` — Incoming cross-references

Returns items that refer to the specified item (incoming links).

```jsonc
{
  "command": "referred-by",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `cross-referenced` — All cross-references

Returns both outgoing and incoming cross-references for the specified item.

```jsonc
{
  "command": "cross-referenced",
  "value": "FHIR-43499",        // required
  "sourceType": "jira",         // optional (filter by source type)
  "limit": 50                   // optional
}
```

### `list` — List items from a source

```jsonc
{
  "command": "list",
  "source": "jira",              // required
  "limit": 20,                   // optional (default: 20)
  "sortBy": "updated_at",       // optional (default: "updated_at")
  "sortOrder": "desc",          // optional (default: "desc")
  "filters": { "status": "Open" } // optional
}
```

### `query-jira` — Structured Jira query

```jsonc
{
  "command": "query-jira",
  "query": "R5 breaking change",       // optional
  "statuses": ["Open", "Reopened"],    // optional
  "workGroups": ["FHIR Infrastructure"], // optional
  "specifications": ["FHIR Core"],     // optional
  "types": ["Bug", "New Feature"],     // optional
  "priorities": ["Critical", "Major"], // optional
  "labels": ["connectathon"],          // optional
  "assignees": ["jsmith"],             // optional
  "sortBy": "updated_at",             // optional (default: "updated_at")
  "sortOrder": "desc",                // optional (default: "desc")
  "limit": 20,                        // optional (default: 20)
  "updatedAfter": "2025-01-01T00:00:00Z" // optional (ISO 8601)
}
```

### `query-zulip` — Structured Zulip query

```jsonc
{
  "command": "query-zulip",
  "query": "US Core",                // optional
  "streams": ["implementers"],       // optional
  "topic": "US Core",               // optional (exact match)
  "topicKeyword": "core",           // optional (partial match)
  "senders": ["john@example.com"],  // optional
  "sortBy": "timestamp",            // optional (default: "timestamp")
  "sortOrder": "desc",              // optional (default: "desc")
  "limit": 20,                      // optional (default: 20)
  "after": "2025-06-01T00:00:00Z", // optional (ISO 8601)
  "before": "2025-12-31T00:00:00Z" // optional (ISO 8601)
}
```

### `ingest` — Ingestion management

```jsonc
// Trigger sync
{ "command": "ingest", "action": "trigger", "sources": ["jira"], "type": "incremental" }

// Restrict a Jira sync to a single project
{ "command": "ingest", "action": "trigger", "sources": ["jira"], "jiraProject": "FHIR" }

// Check status
{ "command": "ingest", "action": "status" }

// Rebuild from cache (formerly "rebuild"; no alias)
{ "command": "ingest", "action": "reingest", "sources": ["jira"] }

// Rebuild indexes (formerly "index"; no alias)
{ "command": "ingest", "action": "reindex", "sources": ["jira"], "indexType": "bm25" }
```

**Actions:** `trigger`, `status`, `reingest`, `reindex`

> **Breaking change.** The `ingest` action names are `reingest` and
> `reindex` (formerly `rebuild` and `index`). There is no
> backwards-compatibility alias — callers using the old names will get
> `Unknown ingest action`. The wire-level orchestrator routes
> (`POST /api/v1/rebuild`, `POST /api/v1/rebuild-index`) keep their
> historical names; only the CLI / MCP surface uses the newer names.

**Optional fields:**

| Field | Type | Applies to | Description |
|-------|------|------------|-------------|
| `sources` | string[] | `trigger`, `reingest`, `reindex` | Comma-separated source names. Omit for all enabled sources. |
| `type` | string | `trigger` | `incremental` (default), `full`, or `rebuild`. |
| `jiraProject` | string | `trigger`, `reingest` | Restrict the run to a single Jira project key. Forwarded only to the Jira leg of the fan-out; ignored by other sources. Surfaced over HTTP as `?jira-project=`. |
| `indexType` | string | `reindex` | `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents`. |

**Index types:** `all`, `bm25`, `fts`, `cross-refs`, `lookup-tables`, `commits`, `artifact-map`, `page-links`, `file-contents`

### `services` — Service management

```jsonc
// Service health
{ "command": "services", "action": "status" }

// Aggregate statistics
{ "command": "services", "action": "stats" }
```

### Source-scoped commands

The CLI now ships per-source command families that mirror the typed
orchestrator proxies (`/api/v1/{name}/...`) and the MCP tool families
(see [MCP Tools](mcp-tools.md)). Every command takes a single JSON
object on stdin and emits a single JSON envelope on stdout. Use
`--help <command>` for the full per-command schema.

| Command | Hits | Purpose |
|---------|------|---------|
| `jira-items` | `/api/v1/jira/items[/{key}/...]` | List / get Jira items, related, snapshot, content, comments, links |
| `jira-dimension` | `/api/v1/jira/{labels,statuses,users,inpersons}` | Dimension lookups (replaces `list-jira-dimension`-style ad-hoc calls) |
| `jira-workgroup` | `/api/v1/jira/work-groups[/{code}/issues]` | Work-group enumeration and per-work-group issue lists |
| `jira-project` | `/api/v1/jira/projects[/{key}]` | List, get, and update Jira project metadata |
| `jira-local-processing` | `/api/v1/jira/local-processing/...` | Local processing queue (tickets, random-ticket, set-processed, clear-all-processed) |
| `jira-specs` | `/api/v1/github/jira-specs/...` | Jira-spec ↔ GitHub-artifact resolution |
| `zulip-items` | `/api/v1/zulip/items[/{id}/...]` | Zulip item shape (with `comments` / `links` returning `[]` shape stubs) |
| `zulip-messages` | `/api/v1/zulip/messages[...]` | Single message, by-user lists, paged listings |
| `zulip-streams` | `/api/v1/zulip/streams[/{id}]`, `.../streams/topics?streamName=` | Stream catalog and per-stream topic enumeration (topics keyed by `streamName` query param) |
| `zulip-threads` | `/api/v1/zulip/threads?streamName=&topic=[&limit=]`, `.../threads/snapshot?streamName=&topic=` | Topic-thread retrieval (stream/topic carried as query params, so `/` is safe) |
| `confluence-pages` | `/api/v1/confluence/pages[/{id}/...]` | Pages, related, snapshot, content, comments, children, ancestors, linked, by-label |
| `confluence-items` | `/api/v1/confluence/items[/{id}/...]` | Confluence-side item shape |
| `github-items` | `/api/v1/github/items/{action}/{**key}` | Action-first item layout (catch-all key carries `owner/name#123`) |
| `github-repos` | `/api/v1/github/repos[/{owner}/{name}/tags...]` | Repo catalog and per-repo tag/file lookups |

The `--jira-project <key>` option is also accepted by the renamed
`reingest` verb on the `ingest` command (see above).

### Run-backed authoring

Three typed command families control processor-owned authoring runs:

| Command | Processor | Workflow guide |
|---------|-----------|----------------|
| `prepared-ticket-authoring` | Preparer | [Generating Discussion Tickets](generating-discussion-tickets.md) |
| `planned-ticket-authoring` | Planner | [Generating Application Tickets](generating-application-tickets.md) |
| `ballot-note-authoring` | BallotNotes | [Generating Ballot Notes](generating-ballot-notes.md) |

All three families register six shared actions. The Preparer additionally
registers six publication-maintenance actions, for twelve exact action
values:

| Action | Required coordinates and fields | Purpose and boundaries |
|--------|---------------------------------|------------------------|
| `start` | Prepared/planned: optional `ticketKeys`. BallotNotes: `hydrationExecutionId` and optional `noteIds`. All: optional `databaseOnly`. | Freeze a run over the selected items. Omitted or empty Jira `ticketKeys` use processor discovery. Ballot-note selection stays within the named hydration execution. `databaseOnly: true` is the explicit mode that completes without producing a review snapshot. The Preparer returns HTTP `409` with `active-run-capacity-reached` while another Preparer run is live. |
| `status` | `runId` | Inspect the frozen run and its items, including durable receipt evidence, retry timing, remaining attempts, and superseded outcomes. |
| `retry` | `runId`, `itemId` | Request an immediate retry only when the latest status advertises `allowedActions.canRetryNow`. This may bypass the automatic delay but cannot expand the processor's total attempt budget. |
| `supersede` | `runId`, `itemId`, non-blank `reason` | Explicitly mark one current error terminal and non-authored only when status advertises `allowedActions.canSupersede`. Never infer the reason or use this for a receipt-backed or publication-reconciliation item. Reconciliation returns `reconciliation-cancel-required`. |
| `submit` | Prepared/planned: `payload` and `observedSourceRevision`. BallotNotes: `prose` and `observedSourceRevision`. | Worker callback only. It is valid inside a processor-launched worker with the complete `FHIR_AUGURY_AUTHORING_*` callback environment; outer operators and automation must not manufacture callback context or tokens. |
| `snapshot` | `runId`, `snapshotPath`; optional `descriptorPath` | Download and verify the immutable snapshot and trusted descriptor. The descriptor's filename and the returned `snapshotPath` / `descriptorPath` pair are authoritative. |
| `refresh-publication` (Preparer only) | `runId` | Start one metadata-only repair from the completed snapshot-producing source run named by `runId`. Returns a new run with `purpose:"publication-refresh"` and `sourceRunId` equal to the request coordinate. It reuses accepted receipts, does not replay authoring or grouping, and produces a separate snapshot. |
| `reconcile-publication` (Preparer only) | `sourceRunId` | Start changed-ticket reconciliation from a completed immutable publication baseline. Discovers every revised accepted ticket in one stable Jira generation, carries unchanged receipt coordinates, stages revised graphs and affected grouping, and produces a new immutable replacement. |
| `reconciliation-status` (Preparer only) | `runId` | Read the reconciliation run, frozen per-ticket comparison/dispositions, counts, grouping impacts, later invalidations, promotion/journal state, mutation fence, failure detail, and publication proof. |
| `retry-reconciliation` (Preparer only) | `runId` | Explicitly resume a database-promoted `snapshot-publish-pending` publication. Recovery is idempotent and does not reapply canonical replacement or overwrite a conflicting final file. |
| `cancel-reconciliation` (Preparer only) | `runId`, non-blank `reason` | Terminally cancel only a `staged` reconciliation before trusted candidate or canonical promotion. It retains the frozen comparison and cancellation audit, deletes disposable workspace, releases both fences, and reports the generic run as `superseded`. |
| `abandon-reconciliation` (Preparer only) | `runId`, non-blank `reason` | After database promotion only, audit explicit abandonment as terminal `canonical-unpublished`. It does not roll back canonical data or create a publication and it restricts later snapshot-producing runs. |

The exact Preparer action set is `start`, `status`, `retry`, `supersede`,
`submit`, `snapshot`, `refresh-publication`, `reconcile-publication`,
`reconciliation-status`, `retry-reconciliation`, `cancel-reconciliation`,
and `abandon-reconciliation`. Planner and BallotNotes do not accept any of
the six publication-maintenance actions.

Start selectors are not interchangeable:

| Commands | Selector | Meaning |
|----------|----------|---------|
| `prepared-ticket-authoring`, `planned-ticket-authoring` | `ticketKeys` | Optional Jira ticket keys. Omit or pass an empty array to let the processor discover the run's items. |
| `ballot-note-authoring` | `hydrationExecutionId` | Required hydration execution that bounds the ballot-note run. |
| `ballot-note-authoring` | `noteIds` | Optional subset of notes from that hydration execution; omission keeps the run scoped to all eligible notes in it. |

Authoring responses use several identifiers for distinct purposes:

- `runId` is the frozen run coordinate used by `status`, `retry`,
  `supersede`, `snapshot`, `reconciliation-status`,
  `retry-reconciliation`, `cancel-reconciliation`, and
  `abandon-reconciliation`. For Preparer
  `refresh-publication`, it instead selects the completed source run; the
  response supplies a different run ID for subsequent status and snapshot
  actions.
- `sourceRunId` is used only by `reconcile-publication` to select the
  completed source publication baseline. The start result supplies a new
  reconciliation `run.runId`; use that new ID for all later reconciliation,
  snapshot, and publication operations.
- `itemId` identifies one item inside a run and is the outer retry/supersede
  coordinate.
- `operationId` correlates a processor-launched worker operation and its
  receipt; it is not an outer CLI selector.
- `acceptedReceiptId` is durable evidence already persisted for an item. It is
  returned by status, not supplied to select an action.
- The descriptor's `snapshotId` is the immutable snapshot identity. The CLI
  still selects the download by `runId` and returns the verified local
  snapshot and descriptor paths.

Minimal outer-control examples:

```jsonc
// Start a Preparer run using processor discovery
{ "command": "prepared-ticket-authoring", "action": "start", "ticketKeys": [], "databaseOnly": false }

// Inspect the run and item receipts
{ "command": "prepared-ticket-authoring", "action": "status", "runId": "<runId>" }

// Retry one eligible current error item
{ "command": "prepared-ticket-authoring", "action": "retry", "runId": "<runId>", "itemId": "<itemId>" }

// Explicitly close one known non-actionable current error
{ "command": "prepared-ticket-authoring", "action": "supersede", "runId": "<runId>", "itemId": "<itemId>", "reason": "duplicate request" }

// Download the verified snapshot pair
{ "command": "prepared-ticket-authoring", "action": "snapshot", "runId": "<runId>", "snapshotPath": "cache\\authoring-snapshots\\preparer\\<runId>\\" }

// Start metadata-only publication repair from a completed source run
{ "command": "prepared-ticket-authoring", "action": "refresh-publication", "runId": "<sourceRunId>" }

// Start changed-ticket reconciliation from a completed source run
{ "command": "prepared-ticket-authoring", "action": "reconcile-publication", "sourceRunId": "<sourceRunId>" }

// Inspect or explicitly retry a pending snapshot publication
{ "command": "prepared-ticket-authoring", "action": "reconciliation-status", "runId": "<reconciliationRunId>" }
{ "command": "prepared-ticket-authoring", "action": "retry-reconciliation", "runId": "<reconciliationRunId>" }

// Cancel staged work before trusted/canonical promotion
{ "command": "prepared-ticket-authoring", "action": "cancel-reconciliation", "runId": "<reconciliationRunId>", "reason": "<reviewed reason>" }

// Explicitly accept a canonical database epoch with no replacement publication
{ "command": "prepared-ticket-authoring", "action": "abandon-reconciliation", "runId": "<reconciliationRunId>", "reason": "<reviewed reason>" }
```

Automatic retry is processor-owned. Outer automation should normally keep
polling instead of issuing `retry`; use immediate retry only as an explicit
operator choice. Never replay `start`, `retry`, `supersede`,
`refresh-publication`, `reconcile-publication`, `retry-reconciliation`,
`cancel-reconciliation`, or `abandon-reconciliation` after an ambiguous
transport failure.

Run status `error` is recoverable and non-terminal. Continue polling whenever
`state.isTerminal` is false (`queued`, `running`, `finalizing`, or `error`).
`completed`, `completed-database-only`, `superseded`, and `abandoned` are
terminal. `abandoned` is non-recoverable and represents a
canonical-unpublished reconciliation, not success; there is no generic
terminal `failed` state. Item status appends
`currentError`, `supersessionReason`, and processor-computed `allowedActions`
while retaining legacy fields.

The Preparer has an active-run capacity of one. `queued`, `running`,
`finalizing`, and recoverable `error` runs are live; only a terminal
`completed`, `completed-database-only`, `superseded`, or `abandoned` run
releases capacity. An abandoned run is not retried, finalized, or scheduled,
so database-only work can still be admitted.
Its normalized start request is stored with the run so a restart resumes the
same run rather than reconstructing a second one.

For Preparer and Planner only, the Orchestrator exposes a bounded operator-run
list at
`GET /api/v1/processing-services/{name}/authoring/runs?limit=N`, with active
and recovering runs first, terminal history newest first, and
`truncated:true` when older rows were omitted. Purpose-marked maintenance runs,
including `publication-refresh` and `publication-reconciliation`, are also
visible even without an ordinary start request so clients can reconcile by
`purpose` and `sourceRunId`.
Initial-revalidation and legacy unmarked rows remain omitted. There is no
`list` action in any authoring command family; use the Dev UI operations
overview for guided history, or the HTTP route for a headless list. Exact CLI
`status` remains the escape hatch when a run ID is known.

Preparer and Planner create/retry/supersede conflicts may include
`conflictingRunIds` and the legacy-compatible single `runId` as authoritative
recovery coordinates. BallotNotes retains its existing six-action CLI surface:
its processor does not implement the bounded collection `GET`, and its conflict
bodies do not guarantee those coordinate fields.

In status responses, `failedItems` remains the aggregate,
`retryableErrorItems` counts errors still under automatic retry, and
`supersededItems` counts terminal non-authored items. Item errors may include
`attemptsRemaining` and `nextAutomaticRetryAt`. Exact run status `completed` or
`completed-database-only` remains lifecycle success even when superseded items
make the result partial; continue snapshot/site publication from accepted
results while surfacing each superseded item and reason.

Run status also exposes optional additive `purpose` and `sourceRunId`.
A successful `refresh-publication` response uses the ordinary typed shape:

```jsonc
{
  "run": {
    "runId": "<refreshRunId>",
    "status": "running",
    "databaseOnly": false,
    "purpose": "publication-refresh",
    "sourceRunId": "<sourceRunId>"
  },
  "items": [
    {
      "runId": "<refreshRunId>",
      "status": "complete",
      "acceptedReceiptId": "<retainedReceiptId>",
      "attemptCount": 0
    }
  ]
}
```

Admission requires `runId` to identify a completed, non-database-only Preparer
run with a ready snapshot descriptor. That source run supplies lineage only:
the new refresh run snapshots the current accepted receipt-backed corpus,
which may differ from the source run's original membership.

Follow `<refreshRunId>` with `status`. After it reaches `completed`, use that
same new ID with `snapshot`, then publish the returned verified pair
separately. `refresh-publication` uses only `runId`; omit `databaseOnly`,
ticket selection, callback payload, and unrelated fields because they do not
alter this operation. If accepted Jira revisions change or the metadata read
spans more than one Jira content revision, the new run becomes terminal
`superseded` and has no snapshot. Retain the old publication and use the
separate `reconcile-publication` workflow after reviewing the mismatch.

Transport or response-body loss returns a successful CLI envelope whose
command data is an explicit unknown-outcome result, rather than replaying the
POST:

```jsonc
{
  "outcome": "outcome-unknown",
  "sourceRunId": "<sourceRunId>",
  "reconciliation": "succeeded",
  "candidates": [
    {
      "runId": "<candidateRefreshRunId>",
      "purpose": "publication-refresh",
      "sourceRunId": "<sourceRunId>"
    }
  ],
  "listTruncated": false,
  "message": "The refresh POST was not replayed. Select the single matching candidate run before continuing with status, snapshot download, and site generation.",
  "error": null
}
```

The CLI performs exactly one bounded read-only list with `limit=20`, filters to
runs created since submission with matching purpose and source lineage, and
sets `reconciliation` to `failed` with an empty candidate list and non-null
`error` if that read fails. Zero, one, or multiple candidates require operator
review; a single candidate is not selected automatically. `listTruncated:true`
means the bounded result cannot prove that all candidates were seen.

Changed-ticket reconciliation is intentionally not an alias for
`refresh-publication`. A successful `reconcile-publication` result has the
following top-level data shape (fields are abbreviated here, not optional in
the actual typed comparison):

```jsonc
{
  "run": {
    "runId": "<reconciliationRunId>",
    "purpose": "publication-reconciliation",
    "sourceRunId": "<sourceRunId>",
    "databaseOnly": false
  },
  "items": [ /* carried complete items and changed authoring items */ ],
  "comparison": {
    "sourceRunId": "<sourceRunId>",
    "sourceSnapshotId": "<snapshotId>",
    "stableJiraGeneration": "1234",
    "items": [
      {
        "ticketKey": "FHIR-123",
        "disposition": "re-author",
        "baselineSourceRevision": "...",
        "currentSourceRevision": "...",
        "baselineReceiptId": "..."
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

Admission reads the complete accepted baseline and reports all revision
decisions from one stable Jira generation. It freezes carry-forward receipt
coordinates and changed-ticket revisions before authoring begins. Revised
graphs remain staged, unchanged authored content is fixed grouping input, and
the old publication remains intact. `reconciliation-status` adds:

- `groupingImpacts`, including old/new partitions and whether each complete
  replacement is staged;
- `invalidatedTicketKeys` and `failureCode:"revision-invalidation"` when a
  frozen changed revision moves again;
- `promotion.state`, `journalState`, `mutationFenceHeld`,
  `lastRecoveryAttemptAt`, and recovery failure/audit fields;
- `publicationProof` only for the verified immutable replacement.

Promotion states are `staged`, `snapshot-publish-pending`, `ready`,
terminal `cancelled`, and terminal `canonical-unpublished`. Database promotion occurs before immutable
file publication, so `snapshot-publish-pending` means canonical rows have
already changed and all competing mutations remain fenced. Startup recovery
and `retry-reconciliation` validate and reuse matching temporary/final file
and snapshot-record evidence, advance an already promoted or ready record,
and finish an already-ready run without a second canonical apply. Missing,
corrupt, checksum-divergent, or conflicting evidence returns
`promotion-recovery-failure`; a conflicting final file is never overwritten.

`cancel-reconciliation` is accepted only while state remains `staged`, before
trusted candidate evidence or canonical replacement exists, and requires the
non-blank `reason` returned with `cancelledAt`. A cancelled run projects to
generic terminal `superseded`, releases capacity and both fences, preserves
the frozen comparison/audit, and never treats an item-level `superseded`
result as staged output. Repeating the action returns the original audit.
After pending, canonical-unpublished, or ready, the stable conflict is
`cancellation-not-allowed`.

`abandon-reconciliation` is accepted only in that pending post-database state
and requires the exact non-blank `reason` returned with `abandonedAt`. It
leaves canonical data promoted, produces no replacement snapshot, preserves
the previous verified pair, releases the fence and active capacity, and
reports generic run status `abandoned`. Thereafter every durable
`databaseOnly:false` run is treated as snapshot-producing and is rejected at
admission, queued fence acquisition, candidate creation, and
finalization/promotion with
`canonical-unpublished-restriction`; database-only ordinary authoring may
continue. A snapshot run queued before abandonment is terminally refused
before it can acquire the fence. `canonical-epoch-recovery` is the sole
snapshot-producing bypass, reserved for the separate explicit recovery that
verifies this canonical epoch.

Reconciliation HTTP failures retain their stable processor code in the CLI's
ordinary top-level error envelope:

```jsonc
{
  "success": false,
  "command": "prepared-ticket-authoring",
  "error": {
    "code": "canonical-unpublished-restriction",
    "message": "A canonical epoch remains unpublished.",
    "details": "..."
  },
  "data": {
    "relatedRunIds": [ "<abandonedRunId>", "<recoveryRunId>" ]
  }
}
```

The complete code set is `invalid-baseline`, `unstable-jira-generation`,
`revision-invalidation`, `staging-mismatch`, `grouping-impact-mismatch`,
`recovery-in-progress`, `promotion-recovery-failure`, and
`canonical-unpublished-restriction`. Do not branch on human-readable detail.
For the canonical restriction, `data.relatedRunIds` preserves the abandoned
and linked recovery coordinates from the HTTP conflict.
If a reconciliation mutation loses its response, do not replay it; inspect
the bounded run list for matching `purpose:"publication-reconciliation"` and
`sourceRunId`, or use `reconciliation-status` when the run ID is known.

Do not copy `submit` into an outer control script. For exhaustive request and
response shapes, run `fhir-augury --help <command>` or export them with
[`save-schemas`](#save-schemas--save-schemas-to-disk). Workflow and
finalization details remain in the three guides above and the
[processor runbook](../technical/processors.md).

For a directory-valued `snapshotPath`, the CLI creates or reuses one durable
workflow-bound pair directory containing the descriptor, database, and
`verified-pair.json`. The ready manifest records service, run, snapshot,
filenames, size, and descriptor/database SHA-256 digests. It is written last
and reverified before the pair is returned. Legacy loose-file output remains a
compatibility adapter, not durable publisher state.

The Dev UI's Prepare/Plan flows use the same outer-control client and verified
pair/publisher boundaries. They add a guided readiness view, processor-owned
active/recent discovery, structured conflict links, unknown-outcome
reconciliation, and run-scoped site URLs; the CLI and orchestration skills
remain the supported headless equivalent. Neither surface exposes worker
callback tokens. Site publication failure never authorizes replaying
authoring.

### `version` — Show version

```jsonc
{ "command": "version" }
```

### `show-schemas` — Output JSON schemas

Returns the full set of JSON schemas describing the CLI's input and output
contracts.

```jsonc
{ "command": "show-schemas" }
```

### `save-schemas` — Save schemas to disk

Exports schema files to a directory.

```jsonc
{ "command": "save-schemas", "outputDirectory": "./schemas" }
```

---

## Examples

```bash
# Inline JSON search
fhir-augury --json '{"command":"search","query":"patient matching","limit":5}'

# Pretty-printed output
fhir-augury --json '{"command":"search","query":"patient matching"}' --pretty

# Read from file
fhir-augury --input request.json

# Write output to file
fhir-augury --json '{"command":"search","query":"patient matching"}' --output results.json

# File-to-file pipeline
fhir-augury --input request.json --output results.json --pretty

# Read from file via @-prefix
fhir-augury --json @request.json

# Read from stdin
echo '{"command":"version"}' | fhir-augury --json @-

# Get help for all commands (JSON)
fhir-augury --help --pretty

# Get help for a specific command
fhir-augury --help search --pretty
```
