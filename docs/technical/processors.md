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

## Operations workspace and Aspire boundary

The Dev UI root (`http://localhost:5210`) is a task-oriented workspace for the
Preparer and Planner only:

| Workflow | UI routes | Required observations | Review site |
|-|-|-|-|
| Prepare | `/operations/prepare/new`, `/operations/prepare/{runId}` | Orchestrator, Preparer, Jira | `/review-sites/prepare/{runId}/discussion/` |
| Plan | `/operations/plan/new`, `/operations/plan/{runId}` | Orchestrator, Planner, Jira, GitHub | `/review-sites/plan/{runId}/applying/` |

It does not control BallotNotes or the Applier, mutate repositories, push
commits, create pull requests, or perform other GitHub writes. It also does not
start/stop Aspire resources or embed logs and traces. Resolve resource
lifecycle in the Aspire dashboard and use the UI's readiness recheck only to
request a fresh Orchestrator health sweep. A service that has not been
successfully observed is reported as **not observed**, not assumed stopped.

The CLI and `orchestrate-prep` / `orchestrate-plan` skills remain supported
headless equivalents. Both UI and headless paths are clients of
processor-owned state; neither is a scheduler, worker callback, or completion
authority.

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

Run status also carries additive lineage fields. `purpose` is one of
`authoring`, `initial-revalidation`, `grouping-maintenance`, or
`publication-refresh`; `sourceRunId` is populated when a maintenance run is
linked to an earlier run. A publication repair therefore has its own `runId`,
`purpose:"publication-refresh"`, and the selected snapshot-producing run in
`sourceRunId`.

The processor appends `state` to each operator run:

| Run status | `state.isTerminal` | `state.isRecoverable` | Polling |
|-|-|-|-|
| `queued`, `running`, `finalizing` | `false` | `false` | Continue |
| `error` | `false` | `true` | Continue; automatic recovery remains authoritative |
| `completed`, `completed-database-only`, `superseded` | `true` | `false` | Stop |

There is no generic terminal `failed` run state. `state.nextAutomaticRecoveryAt`
is populated for a valid run-level automatic recovery time when known.

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

### Discovery, actions, and conflicts

Preparer and Planner expose operator history directly at
`GET /api/v1/processing/authoring/runs?limit=N` and through the Orchestrator at
`GET /api/v1/processing-services/{Preparer|Planner}/authoring/runs?limit=N`.
The default is 20 and the accepted range is 1 through 100. The store first
selects at most `limit + 1` candidate run IDs, then aggregates items only for
that bounded set. Non-terminal `queued`, `running`, `finalizing`, and `error`
runs sort before terminal runs; each group is newest first.

Ordinary Jira authoring runs with a durable normalized start request are
listed, as are purpose-marked maintenance runs. In particular, a
`publication-refresh` remains visible even though it has no ordinary
`RequestJson`, allowing clients to reconcile an ambiguous POST by `purpose`
and `sourceRunId`. Purpose-marked `grouping-maintenance` runs are visible too.
Initial revalidation and legacy unmarked authoring rows remain available by
exact ID but are intentionally absent. The response contains `runs` and
`truncated`; use exact detail as the escape hatch for a known older run.

Detail appends these item fields while preserving legacy `error`:

- `currentError` only for a current error;
- `supersessionReason` only for a superseded item;
- `allowedActions.canRetryNow` only for an error in the currently fenced run
  with either an accepted receipt to resume or remaining authoring attempts;
- `allowedActions.canSupersede` only for an error in the currently fenced run
  without an accepted receipt.

Automatic retry remains the default. UI or headless automation must use these
capabilities to decide what to present, but the mutation endpoint is the final
authority: retry and supersede recheck status, fence, receipt, and attempt
budget in an immediate transaction and can return `409` after a status read.
Supersession always requires a non-empty operator reason.

Create/retry/supersede conflict JSON uses `AuthoringConflictResponse`. It keeps
the existing `error` and optional `detail`, adds `conflictingRunIds`, and also
sets the legacy-compatible `runId` when one related run is known. Capacity,
revision-collision, and initial-revalidation conflicts therefore give clients
authoritative navigation coordinates without prose parsing.

Jira discovery also freezes source provenance independently of the per-ticket
revision used for stale-work detection. Every paged read must report a stable,
unchanged Jira content revision; the client restarts the whole pagination
sequence up to three times on a mutation or mismatch, then suppresses
provenance if it still cannot prove one generation. Each cached candidate
stores only its own project's successful-refresh watermark and the common
content revision, overwriting old values with null when a later read cannot
prove them.

Run creation writes one `authoring_run_input_provenance` row for source `jira`
inside the same transaction as the run and items. Its
`LatestSuccessfulRefreshAt` is the maximum candidate watermark only when every
selected ticket has one. Its `ContentRevision` is retained only when all
selected tickets share the same stable revision. `CapturedAt` records run
creation and is not source freshness. Exact replay keeps the already-frozen
row; new and replacement runs derive a new row. Migrated legacy Jira runs are
seeded with null coordinates rather than current source state.

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

The public snapshot schema is processor-specific. Preparer now writes
discussion snapshot **v3**. Its immutable v2 base extends v1 with
contributing-run input provenance, parent Jira hydration provenance, Assignee
fields, and normalized in-person requester rows; v3 adds nullable public
display-name policy markers to parent hydration, related/self Jira hydration,
and requester rows. Its sanitizer keeps provenance for every run contributing
a retained receipt-backed ticket and retains people only with the exact
current policy marker and a context-free-safe value. The ticket publisher
accepts exact Preparer v1, v2, and v3 catalogs, but always treats v1/v2 people
as unavailable. Planner continues to write applying snapshot **v1**, and the
Tickets for Applying publication path accepts only that v1 contract.

Discussion publication does not expose either processor schema directly to the
browser. It validates the verified Preparer pair and projects a fresh
**Discussion renderer schema v2** database containing only presentation
metadata and readiness, the facet catalog, tickets, people availability,
normalized facets, summary sources, related context, and topic/group
membership. Preparer snapshot schema v3 and Discussion renderer schema v2 are
separate versioned contracts; neither version implies the other. Applying
continues through its existing Planner-v1 path. Both generated sites are
static artifacts with no live source, Orchestrator, or processor dependency.

Every new Discussion manifest carries `discussionReadiness`. Its `evidence`
is `ordinary-snapshot` or `publication-refresh`, and its reason codes can
include `legacy-snapshot-schema`, `missing-ordinary-provenance`,
`invalid-refresh-proof`, and `missing-people-policy-proof`. Preparer v1 and v2
pairs remain readable but always produce degraded readiness and never expose
trusted people; they are not rewritten. A v2 pair can still provide a
qualified `Built` date when its ordinary freshness provenance is complete.
Only v3 can carry the current public-display-name policy proof. In renderer
v2, a missing legacy proof displays its explicit unavailability reason, while
`Not provided` means a policy-qualified v3 role is legitimately empty.

For a v2 or v3 discussion corpus, every retained ticket must first resolve to
exactly one accepted authoring coordinate. Zero or multiple matches are
invalid snapshot structure and abort publication. With that structural
requirement satisfied, the visible `Built` date is the latest successful
upstream Jira refresh across the run and stable parent hydration freshness
coordinates for every retained ticket. Missing or null run provenance,
missing/null/partially bound parent project/revision/watermark coordinates, or
v1 input leave the title unsuffixed. Authoring completion, snapshot creation,
and site generation are never fallbacks.

Item-level supersession does not supersede the run. When every item is either
`complete` or `superseded`, normal fenced finalization still runs—even if every
item is superseded. A final run status of `completed` or
`completed-database-only` is lifecycle success; any included `superseded`
items make it an explicit partial-authoring result. Snapshot and site
publication may continue from accepted results, but every superseded item and
reason must remain visible.

`FhirAugury.Processing.Client` downloads the descriptor and streamed bytes into
a sibling staging directory, validates service/workflow/run/snapshot binding,
safe filenames, size, and SHA-256, then writes `verified-pair.json` last. The
ready manifest records `serviceName`, `runId`, `snapshotId`,
`formatVersion`, `descriptorFileName`, `databaseFileName`, `sizeBytes`,
`descriptorSha256`, and `databaseSha256`. Promotion/recovery is locked and
directory-scoped, so a partial download is never a ready pair. Preparer and
Planner require this explicit service binding because both descriptors use
processor kind `jira-fhir`.

`FhirAugury.Publishing.Tickets` accepts only a durable verified pair and checks
the public snapshot schema before rendering. The Dev UI invokes it in-process;
`ticket-site` is a thin CLI adapter over the same publisher. Each sub-site
writes the exact filename `site-manifest.json`. Discussion manifests retain a
stable base `title` and add display title, optional Jira refresh, renderer
schema, and structured readiness fields; Dev UI reconstruction validates the
stable title while preserving those additions. Neither publisher accepts a
live processor database.

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
- **Ambiguous outer mutation:** start, immediate retry, supersede, and
  publication refresh are single-attempt. If transport is lost after the
  server may have received the request, report **outcome unknown**, never
  replay, and reconcile only through list/detail reads. Repeating the mutation
  requires explicit operator review first.

### Metadata-only Discussion publication refresh

A publication refresh repairs publication evidence without replaying authored
work. Its source coordinate must be a completed, non-database-only Preparer run
with a ready snapshot. The source run establishes operator lineage; it does
not freeze the old snapshot's membership as the repair corpus. At admission,
the Preparer enumerates the **current accepted receipt-backed corpus**, creates
a new snapshot-producing run with `purpose:"publication-refresh"` and
`sourceRunId` set to the selected run, reuses each accepted receipt on a
completed maintenance item, acquires the mutation fence, and wakes the
scheduler. The start request does not fetch Jira publication metadata; that
work runs under the durable finalization stage. No authoring attempt or worker
callback is created.

Fenced finalization performs these steps:

1. Re-read the current accepted corpus and require it to match the refresh
   run's receipt, item, expected-revision, and input-fingerprint coordinates.
2. In the `publication-metadata` stage, read each current ticket through the
   Orchestrator. Every response must be stable, match that ticket's accepted
   Jira revision, carry the current public-display-name policy, and share one
   Jira content revision across the corpus.
3. In one allowlisted transaction, update only parent/self publication
   metadata and requester rows, write the refresh run's Jira input provenance,
   and insert a durable metadata apply receipt. Historical contributing runs'
   `authoring_run_input_provenance` rows remain frozen.
4. In `grouping-certification` stages, verify current grouping output against
   retained source grouping receipts. Receipts with an existing output
   fingerprint are reused directly; legacy receipts are bound to the current
   output by a separate durable certification. Grouping is never dispatched or
   recomputed.
5. Emit a new Preparer schema-v3 snapshot and descriptor. Its
   `publicationProof` binds publication contract version, purpose, source run,
   Jira freshness/content revision, people-policy version, corpus fingerprint,
   grouping fingerprint, and capture time. The new snapshot has its own
   `runId`, `snapshotId`, and monotonic sequence.

Authored payloads, accepted receipts, historical input provenance, topic
grouping, and source grouping receipts are not rewritten by this flow. After
the refresh run reaches `completed`, download that run's verified pair and
publish its Discussion site as a separate client-side operation. The prior
pair and site remain usable until the replacement publication is independently
accepted.

Stage and snapshot recovery stay on the same refresh run. If the process stops
after the metadata transaction but before stage completion, retry consumes the
durable apply receipt and does not fetch or apply source metadata again. If
snapshot promotion is interrupted, snapshot reconciliation resumes or reuses
the same immutable proof and ready snapshot coordinate rather than creating a
different repair generation. Other transient stage failures leave the run in
recoverable `error` for scheduler-owned retry.

A missing ticket, changed accepted Jira revision, or mixed Jira content
generation is intentionally different: the refresh run becomes terminal
`superseded`, records that ordinary re-authoring is required, releases the
mutation fence, writes no metadata apply receipt for the rejected generation,
and produces no snapshot. There is no override and no partial metadata apply;
start an ordinary Preparer authoring run against the changed source instead.

The refresh POST is another non-replayed outer mutation. On transport or
response-body loss, Dev UI and CLI clients issue one bounded read-only run-list
request and filter runs created since submission by
`purpose:"publication-refresh"` and matching `sourceRunId`. Zero, one, or
multiple candidates all require operator review; a truncated list is reported.
The Dev UI retains an outcome-unknown review gate and disables another refresh
until the operator acknowledges the candidates. The CLI returns
`outcome:"outcome-unknown"`, `reconciliation`, `candidates`,
`listTruncated`, `message`, and nullable `error`. Neither client repeats the
POST.

Processor completion and site-publication completion are separate axes.
Accepted receipts and a terminal processor result remain valid when local site
generation fails. The Dev UI retains the verified pair and retries only
publication.

Under Aspire, the UI stores pairs and sites in deterministic ignored roots:

```text
cache\devui-authoring-snapshots\{prepare|plan}\{runId}\
cache\devui-review-sites\{prepare|plan}\{runId}\{discussion|applying}\
```

Only the review root is served, at `/review-sites`; snapshot pairs remain
private. The same-origin site mapping is a trusted-local boundary. No automatic
retention policy exists in the first release: stop the Dev UI before manually
removing old workflow/run directories.

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

For the guided equivalent, open `/operations/prepare/new`; active/recovering
and recent runs are listed at `/`, with **Open by run ID** for exact lookup.

Manual CLI control:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"refresh-publication","runId":"<sourceRunId>"}'
```

`refresh-publication` uses `runId` as the completed source-run coordinate. On
success, follow the returned new run ID with `status`, then use that new ID for
`snapshot` and site publication. Do not use a refresh run as the source of
another refresh.

Publish a downloaded verified pair:

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --force
```

The current Preparer host **requires** snapshot schema v3; v1 and v2 are
publisher/test compatibility inputs, not selectable producer modes. Before an
authoring rollout or publication refresh, inspect the Preparer's ignored
`appsettings.local.json` and process/Aspire environment for an old
`Processing:SnapshotSchemaVersion` override, including
`FHIR_AUGURY_PREPARER_Processing__SnapshotSchemaVersion`. Remove or correct
the stale ignored/environment value so the effective value is `3`; do not add
or commit a local override as part of the repair. Start the service and confirm
its log reports `Effective Preparer snapshot schema version is 3`. Startup
rejects any other effective value.

The shared publisher remains able to publish older v1 and v2 pairs for
compatible source and freshness data, but their readiness is degraded and
people are unavailable. Only v3 carries the explicit policy proof required for
public people. The omitted `ticket-site` title for this input is
`Tickets for Discussion`.

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

For the guided equivalent, open `/operations/plan/new`; the workflow ends at
the applying review site and does not expose Applier or GitHub-write controls.

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

Planner and Tickets for Applying intentionally remain on snapshot schema v1.
The omitted `ticket-site` title for Planner input remains the existing
`Ticket Site`; Discussion freshness qualification does not apply.

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
| Preparer | `data\processor.jira.fhir.preparer.db` | Verified pair -> shared publisher -> discussion site |
| Planner | `data\processor.jira.fhir.planner.db` | Verified pair -> shared publisher -> applying site |
| Applier | worktrees, local commits, `data\processor.jira.fhir.applier.db` | On-demand upstream push |
| BallotNotes | `cache\ballot-notes.db` | Verified snapshot -> `notes-site` |

The database paths above are operator backup/configuration locations, not
client integration surfaces. The Orchestrator proxies run control over HTTP;
the Dev UI and CLI are outer clients, and static sites consume only verified
pairs. The Dev UI's run-scoped local destinations are documented above;
headless callers retain their explicit CLI/tool output paths.

Markdown remains supported as authored content inside structured database
fields and explicit site copy/export features. It is not a processor input,
completion ledger, database import format, or fallback when snapshot
publication fails.
