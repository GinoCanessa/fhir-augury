# Processors runbook

Operator reference for the Preparer, Planner, Applier, and BallotNotes
processors. Preparer, Planner, and BallotNotes use durable run-backed
authoring. Their databases are private service state; clients control runs
through HTTP or the typed CLI and publish review sites only from verified
snapshot pairs.

## Topology

| Processor | Port | Upstreams | Result |
|-----------|------|-----------|--------|
| Preparer | 5171 | Jira source, Orchestrator | Prepared discussion tickets |
| Planner | 5172 | Jira source, GitHub source, Orchestrator | Structured implementation plans |
| Applier | 5173 | Planner compatibility projection | Local repository commits, push on demand |
| BallotNotes | 5174 | GitHub source/clone, Jira source, Orchestrator | Hydrated and authored ballot notes |

All four resources use `WithExplicitStart()` under Aspire. Start the required
sources and Orchestrator first, then start the processor.

## Run-backed authoring contract

### One-way activation

Preparer, Planner, and BallotNotes ship with
`ActivateRunBackedAuthoring:true`. On the first startup against a legacy
database the service:

1. Acquires exclusive startup ownership.
2. Closes authoring and maintenance admission.
3. Creates and verifies the configured pre-cutover SQLite backup.
4. Classifies existing domain rows without real receipts as
   `legacy-unverified`.
5. Allocates the next authoring epoch and creates a fenced initial
   revalidation run.

The active epoch cannot transition back to legacy after accepting a receipt.
Ordinary runs, grouping maintenance, and the first canonical snapshot remain
blocked until initial revalidation succeeds. If source revisions change, the
processor replaces the revalidation run while retaining unchanged accepted
provenance in the logical revalidation corpus.

### Runs, items, and receipts

A run freezes its item membership and expected source/evidence revisions.
Typical run states are `queued`, `running`, `finalizing`, `completed`,
`completed-database-only`, `error`, and `superseded`.

`AuthoringRunScheduler<TItem>` is the sole run-backed lifecycle loop. It
reconciles durable errors and source revisions, acquires the mutation fence for
the oldest eligible queued run, dispatches work while processing is running,
invokes processor-specific finalization, and activates the next queued run only
after successful completion releases the prior fence. Pausing processing stops
new activation and dispatch, but it does not strand reconciliation or
finalization for the already fenced run.

Each item receives an operation ID plus a secret token. The token is supplied
only to the processor-launched worker through environment variables. Result
submission validates the operation, token, run/item coordinates, authoring
epoch, content hash, and observed source revision in the same transaction that
persists the domain result and immutable receipt.

`AuthoringMaxAttempts` is a total ceiling on unpersisted authoring attempts,
including the first claim and an abandoned pre-receipt claim.
`AuthoringRetryDelay` is the minimum interval from the failed attempt's durable
`CompletedAt` before the scheduler retries it automatically. An explicit
item-level `retry` may bypass that wait, but it cannot increase the attempt
budget. Reaching the limit atomically closes the last attempt and marks the item
`superseded`.

Only an unpersisted retry gets a new operation ID and token. Once a receipt is
accepted, a later error returns to persisted post-processing after the delay
without dispatching the author again; receipt-backed items cannot be
operator-superseded.

Status keeps `failedItems` as the aggregate of current retryable errors and
terminal non-authored outcomes. `retryableErrorItems` reports the former and
`supersededItems` reports the latter. An error item also reports
`attemptsRemaining` and `nextAutomaticRetryAt` when applicable.

### Finalization and snapshots

For Preparer and Planner, post-persistence item processing completes hydration
before the item becomes `complete`. Fenced run finalization then refreshes the
workgroup catalog, performs grouping for every affected partition, and creates
the snapshot. Grouping callbacks carry a run ID, stage ID, stage lease, and
input fingerprint. BallotNotes finalization verifies current evidence/prose
provenance. A processor-wide mutation fence excludes other authoring and
maintenance writers through snapshot completion.

Normal runs produce:

- an immutable SQLite snapshot,
- a trusted descriptor containing processor/run/epoch identity, monotonic
  sequence, schema version, file name, size, SHA-256, item/receipt counts, and
  table counts,
- a `completed` run state only after the descriptor and bytes are ready.

`databaseOnly:true` is the sole explicit snapshot/site opt-out and completes as
`completed-database-only`.

Item-level supersession does not supersede the run. When every item is either
`complete` or `superseded`, normal fenced finalization still runs—even if every
item is superseded. A final run status of `completed` or
`completed-database-only` is lifecycle success; any included `superseded`
items make it an explicit partial-authoring result. Snapshot and site
publication may continue from accepted results, but every superseded item and
reason must remain visible.

The CLI downloads and verifies the descriptor/byte pair, then promotes both
files atomically. `ticket-site` and `notes-site` reject live processor
databases.

### Failure boundaries

- **Before receipt acceptance:** the scheduler automatically retries after the
  configured minimum delay while attempts remain. An operator may request an
  immediate retry without expanding the total limit, or explicitly supersede a
  known non-actionable current error with a non-empty reason.
- **After receipt acceptance:** hydration, grouping, or snapshot failure does
  not invalidate the receipt. Post-persistence work resumes from durable state
  without re-authoring or supersession.
- **Snapshot download failure:** retry the download; do not re-author.
- **Site publication failure:** preserve the verified pair and rerun only the
  site tool. Processor state is unchanged.
- **Source revision change:** stale work is superseded or the initial
  revalidation run is atomically replaced. The gate is not cleared by stale
  work.

### Recovery and Jira rediscovery

After an upgraded processor starts, legacy `error` items with an open attempt
are reconciled from their durable timestamps and messages. Due items below the
limit return to automatic retry; a final unpersisted failure becomes an
item-level `superseded` result. The run then follows normal finalization,
releases its fence, and only afterward can the oldest queued run acquire it.

Scheduled Preparer and Planner discovery does not immediately select an
exhausted unchanged Jira source revision into a fresh batch. A genuinely newer
revision is eligible normally. Deliberately retrying the unchanged revision
requires an explicit new run, so scheduled discovery cannot invisibly reset
the attempt budget.

## Preparer (`processor-jira-fhir-preparer`, :5171)

The Preparer selects configured Jira tickets and launches `ticket-prep`
workers. Use the outer-control skill for the complete flow:

```text
/orchestrate-prep
```

Manual CLI control:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

Publish:

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --force
```

Grouping is part of finalization. To intentionally refresh all current
grouping partitions later, use the processor-owned maintenance endpoint:

```powershell
curl -X POST http://localhost:5171/api/v1/prepared-ticket-groupings/maintenance `
  -H "Content-Type: application/json" `
  -d '{}'
```

## Planner (`processor-jira-fhir-planner`, :5172)

The Planner selects configured Jira tickets and launches `ticket-plan`
workers. It also maintains the existing completion ID/timestamp projection
consumed by the Applier.

```text
/orchestrate-plan
```

Manual CLI control:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"status","runId":"<runId>"}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\planner\\<runId>\\"}'
```

Publish:

```powershell
dotnet run --project tools\ticket-site -- `
  --planner-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --force
```

Grouping maintenance:

```powershell
curl -X POST http://localhost:5172/api/v1/planned-ticket-topics/maintenance `
  -H "Content-Type: application/json" `
  -d '{}'
```

## Applier (`processor-jira-fhir-applier`, :5173)

The Applier retains its existing queue contract. It auto-discovers completed
Planner output, applies each plan in per-ticket repository worktrees, and
commits successful changes locally.

```powershell
curl -X POST http://localhost:5173/processing/start
curl -X POST http://localhost:5173/processing/stop
curl http://localhost:5173/processing/queue
```

Push successful local commits for one ticket on demand:

```powershell
curl -X POST http://localhost:5173/api/v1/applied-tickets/FHIR-12345/push
```

There is no `orchestrate-applier` skill.

## BallotNotes (`processor-github-fhir-ballotnotes`, :5174)

BallotNotes first creates an immutable hydration execution for one repository
window, then creates a separate authoring run over all or a selected subset of
that execution's completed units.

Recommended:

```text
/orchestrate-notes
```

Hydrate:

```powershell
curl -X POST http://localhost:5174/api/v1/ballot-notes/hydrate `
  -H "Content-Type: application/json" `
  -d '{"repoOwner":"HL7","repoName":"fhir","sinceSha":"<sha>"}'
curl "http://localhost:5174/api/v1/ballot-notes/hydrate/status?executionId=<executionId>"
```

Author and download:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"status","runId":"<runId>"}'
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

Publish:

```powershell
dotnet run --project tools\notes-site -- report `
  --snapshot-db "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\notes-site `
  --force
```

The processor is the only authoring writer. Per-unit worker submissions require
callback context; outer clients cannot use the retired bare prose write.
Workgroup reallocation also submits one expected-revision batch through the
processor mutation fence.

## Worker callback environment

Processor-launched Jira and BallotNotes workers receive:

- `FHIR_AUGURY_AUTHORING_WORKER=1`
- `FHIR_AUGURY_AUTHORING_RUN_ID`
- `FHIR_AUGURY_AUTHORING_ITEM_ID`
- `FHIR_AUGURY_AUTHORING_CALLBACK_URL`
- `FHIR_AUGURY_AUTHORING_OPERATION_ID`
- `FHIR_AUGURY_AUTHORING_OPERATION_TOKEN`
- `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`

BallotNotes also supplies note type and hydration execution variables.
Grouping workers receive a separate `FHIR_AUGURY_GROUPING_*` run/stage/lease
context. These values are processor-generated capabilities; do not put them in
configuration files or outer-control commands.

## Output destinations

| Processor | Durable service state | Review publication |
|-----------|-----------------------|--------------------|
| Preparer | `data\processor.jira.fhir.preparer.db` | Verified snapshot -> `ticket-site` discussion |
| Planner | `data\processor.jira.fhir.planner.db` | Verified snapshot -> `ticket-site` applying |
| Applier | worktrees, local commits, `data\processor.jira.fhir.applier.db` | On-demand upstream push |
| BallotNotes | `cache\ballot-notes.db` | Verified snapshot -> `notes-site` |

The database paths above are operator backup/configuration locations, not
client integration surfaces. The Orchestrator and CLI proxy run control over
HTTP; static sites consume only trusted snapshots.

Markdown remains supported as authored content inside structured database
fields and explicit site copy/export features. It is not a processor input,
completion ledger, database import format, or fallback when snapshot
publication fails.
