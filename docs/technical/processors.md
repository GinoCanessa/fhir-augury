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
`authoring`, `initial-revalidation`, `grouping-maintenance`,
`publication-refresh`, or `publication-reconciliation`; `sourceRunId` is
populated when a maintenance run is linked to an earlier run. Each publication
maintenance operation therefore has its own `runId`, distinct purpose, and the
selected snapshot-producing run in `sourceRunId`.

New publication-enrichment runs also expose optional `corpusComparison` in
public run status: `sourceSnapshotId`, `sourceExportedTicketCount`,
`currentAcceptedTicketCount`, and `additionalTicketCount`. It is the durable
admission comparison, not a client-side live database query. Legacy runs may
omit it; omission is not a measured zero.

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
`publication-refresh` remains visible whether it has legacy null request
metadata or the new versioned maintenance recipe in `RequestJson`, allowing
clients to reconcile an ambiguous POST by `purpose` and `sourceRunId`.
Purpose-marked `publication-reconciliation` and `grouping-maintenance` runs
are visible too.
Initial revalidation and legacy unmarked authoring rows remain available by
exact ID but are intentionally absent. The response contains `runs` and
`truncated`; use exact detail as the escape hatch for a known older run.

Detail appends these item fields while preserving legacy `error`:

- `currentError` only for a current error;
- `supersessionReason` only for a superseded item;
- `allowedActions.canRetryNow` only for an error in the currently fenced run
  with either an accepted receipt to resume or remaining authoring attempts;
- `allowedActions.canSupersede` only for an error in the currently fenced run
  without an accepted receipt, and never for a
  `publication-reconciliation` item.

Automatic retry remains the default. UI or headless automation must use these
capabilities to decide what to present, but the mutation endpoint is the final
authority: retry and supersede recheck status, fence, receipt, and attempt
budget in an immediate transaction and can return `409` after a status read.
Supersession always requires a non-empty operator reason. The server rejects
every reconciliation-item supersede with
`reconciliation-cancel-required`, including an unaccepted error; only the
dedicated pre-promotion run cancellation may terminate that work.

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
**Discussion renderer schema v3** database containing only presentation
metadata, readiness and corpus coverage, the facet catalog, tickets, people
availability, normalized facets, summary sources, related context, and
topic/group membership. Preparer snapshot schema v3 and Discussion renderer
schema v3 are separate versioned contracts; neither version implies the other. Applying
continues through its existing Planner-v1 path. Both generated sites are
static artifacts with no live source, Orchestrator, or processor dependency.

Every new Discussion manifest carries `discussionReadiness`. Its `evidence`
is `ordinary-snapshot` or `publication-refresh`, and its reason codes can
include `legacy-snapshot-schema`, `missing-ordinary-provenance`,
`invalid-refresh-proof`, and `missing-people-policy-proof`. Preparer v1 and v2
pairs remain readable but always produce degraded readiness and never expose
trusted people; they are not rewritten. Only v3 can carry the current
public-display-name policy proof. A missing legacy proof displays its explicit
unavailability reason. `No public display name available` for a current-policy
value means no publishable name was supplied, not that the person/role is
absent or the ticket is unassigned.

For a v2 or v3 discussion corpus, every retained ticket must first resolve to
exactly one accepted authoring coordinate. Zero or multiple matches are
invalid snapshot structure and abort publication. The title date is a separate
exported-corpus fact: the maximum self-ticket
`prepared_jira_hydration.UpdatedAt` (`TicketKey = JiraKey`) after generation
filters, normalized to UTC. A nonempty export with complete valid dates uses
`Tickets for Discussion - Sept 15, 2026`, with fixed
`Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sept, Oct, Nov, Dec` labels and an
unpadded day, never `Built`. Missing/partial dates omit the suffix;
malformed non-null values fail validation. Related-ticket dates, provenance
watermarks, authoring/snapshot clocks, and publication time are never
fallbacks. Complete dates can qualify even on degraded or legacy input.
`JiraSourceLastSuccessfulRefreshAt` remains independent upstream provenance.

Renderer-v3 manifests require `discussionCorpus`, matching the browser's
`site_metadata.CorpusSummaryJson`. It records `ticketCount`,
`exportedProjectCount`, `validJiraUpdatedAtCount`, `maxJiraUpdatedAt`,
`dateCoverage` (`empty`, `none`, `partial`, `complete`), and ticket counts with
public Reporter, Assignee, and requester names. A partial maximum remains a
reported fact without a title suffix. Each `linksByKind` entry records
`kind`, `totalRows`, `resolvedSafeLinks`, `unresolvedWithRetainedSafeLinks`,
and `withoutUsableUrl`. A safe retained URL does not itself prove source
backing. Legacy manifests can omit this summary and have unknown coverage;
Applying does not acquire this Discussion-only contract.

Generation filters fix the exported corpus and display-title suffix;
interactive filters do not alter the frozen title. Discussion's classic
`state.js` is a mandatory versioned asset, not an optional module. Facets
preserve overview/crosscut/list routes, accumulate without clearing other
chips, and preserve search/sort/history. Active/fixed dimensions and a
single-project export's Project dimension are hidden. Guided proposal labels
leave authored bodies unchanged. Safe external links and filter-preserving
in-corpus Jira links share explicit failure/last-known diagnostics across
related-item and summary-source surfaces.

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
schema, readiness, and corpus coverage. Dev UI reconstruction validates the
committed manifest/presentation and recomputes
`TicketSiteManifest.ComputeBuildIdentity()`;
it does not rewrite legacy artifacts into the new contract. Neither publisher
accepts a live processor database or performs an in-place renderer migration.

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
- **Ambiguous outer mutation:** start, immediate retry, supersede, publication
  refresh, reconciliation start/retry/cancel, and reconciliation abandonment are
  single-attempt. If transport is lost after the server may have received the
  request, report **outcome unknown**, never replay, and reconcile only through
  list/detail/status reads. Repeating the mutation requires explicit operator
  review first.

### Changed-ticket Discussion publication reconciliation

`publication-reconciliation` is the processor-owned replacement strategy when
one or more accepted Jira revisions differ from a completed Discussion
publication. It is deliberately separate from `publication-refresh`:
metadata-only refresh certifies that accepted revisions and grouping are
unchanged and refuses drift; reconciliation freezes the drift, authors the
complete changed set, recomputes its grouping consequences, and creates a new
immutable publication. Neither strategy mutates the selected source snapshot
or an existing site.

#### Admission and frozen recipe

The operator supplies a completed, snapshot-producing Preparer
`sourceRunId`. The planner uses
`PreparedTicketPublicationBaselineReader` to verify the ready immutable pair
and uses the protection reader to require the baseline's accepted authored
output, receipt coordinates, and grouping to remain a valid subset of current
canonical state. It then fetches **every accepted baseline ticket** through
the typed Orchestrator Jira boundary. Discovery completes the whole pass:
newer revisions become decisions rather than a first-mismatch exception, and
source failures are accumulated. Every successful observation must have
stable source provenance and the same Jira content generation.

The resulting reconciliation contract-v2 comparison records baseline
snapshot identity and digest, stable Jira generation, corpus fingerprint, and
for every ticket: baseline/current source revision, `carry-forward` or
`re-author`, baseline receipt/run-item/contributing-run coordinates, authored
graph fingerprint, baseline grouping fingerprint, and the selected `ItemKind`
and `ExpectedSourceRevision`. A carried decision freezes those two values
from the accepted baseline item. A re-authored decision freezes them from the
reconciliation item that will accept the replacement receipt. While holding
an immediate Preparer transaction and the mutation fence, admission
revalidates the baseline and repeats the complete Jira observation. Any
generation/revision race aborts with `unstable-jira-generation`. Only then
are the comparison,
`purpose:"publication-reconciliation"` run, mixed carried/pending item set,
reconciliation fence, and source lineage committed together.

Carry-forward items remain complete and retain their accepted receipt
coordinates. A changed item is accepted only for its frozen current revision
and operation token. Receipt acceptance, complete payload/hydration staging,
and the item transition share the caller-owned SQLite transaction. The result
does not write canonical authored or hydration tables. A later Jira change is
reported as `revision-invalidation` and blocks finalization.

Local Preparer Jira-store lag is not authoritative drift: generic stale-item
reconciliation leaves every publication-reconciliation item untouched, so an
ordinary pre-receipt source mismatch remains an eligible retry while the
processor cache catches up. Full-corpus Orchestrator observation is the
authoritative invalidation guard. Before promotion, every `re-author` decision
must have a generic item in exact `complete` state plus matching staged graph,
hydration, receipt, run-item, operation, source-revision, and fingerprint
coordinates. Generic `superseded` never satisfies reconciliation readiness.

#### Overlay and grouping closure

`PreparedTicketCorpusView` resolves each carry-forward ticket from the exact
frozen canonical receipt and each changed ticket from one complete staged
graph/hydration/receipt tuple. Missing, duplicated, or fingerprint-divergent
children make the overlay invalid; reads never silently combine old and new
children. Every resolved ticket is projected to the shared
`PreparedTicketPublicationCorpusItem(TicketKey, ReceiptId, RunItemId,
ContributingRunId, ItemKind, ExpectedSourceRevision)` coordinate. The
Preparer and publisher both use
`PreparedTicketPublicationContract.ComputeCorpusFingerprint`; no
reconciliation-specific text hash or authored-content field substitutes for
that canonical membership coordinate.

The grouping delta compares every changed ticket's old baseline membership
with its staged self-Jira partition. Its impact closure includes both sides of
a partition move and every shared topic/group/container whose identity, text,
membership, or order can change. Each impacted partition is staged as a
complete replacement, including an empty replacement when the overlay leaves
no grouped container there. Unchanged ticket authored content is fixed input
to the replacement; the workflow does not re-author unchanged tickets or
regroup unrelated partitions.

Production finalization persists one
`publication-reconciliation-grouping` authoring stage per exact impacted
partition. Every stage uses the candidate
`OverlayCorpusFingerprint` as its input fingerprint and carries the revised
keys plus the complete overlay membership. The configured `topic-groupings`
worker receives those run/stage/lease coordinates through its reconciliation
adapter. Its clustering-signals and hydration reads use the run-scoped
`/{workGroupClean}/{specification}/{type}` routes, which resolve only
`PreparedTicketCorpusView`; the worker must not fall back to the live
workgroup-only projections.

An exact reconciliation-context grouping `PUT` writes
`prepared_ticket_publication_staged_grouping` and a matching durable stage
receipt. It never calls the ordinary canonical grouping writer. Ordinary
grouping-maintenance stages retain their existing read/write contract and
continue to replace canonical grouping under the mutation fence. A
zero-member impacted partition is completed directly as `topics: []`, without
requiring a worker process. If a worker exits without the exact staged
receipt, the stage fails and a later finalization attempt resumes that
pending/error stage rather than re-running completed partitions.

Before candidate materialization, the Preparer freezes and rechecks three
independent unaffected components:

- protected canonical authored rows for tickets outside the changed set;
- exact accepted receipt/item/source-revision coordinates for those tickets;
- topic/group/member rows and ordering outside the impacted partition set.

The reconciliation run's own `authoring_runs` row is intentionally excluded
from the authored-row component because its `running` → `finalizing` →
`completed` lifecycle is workflow state, not unaffected authored output. No
canonical ticket row is excluded by that rule, and receipt and grouping
fingerprints remain separate and exact. Any other drift fails with
`staging-mismatch` or `grouping-impact-mismatch`.

Only after every durable grouping stage and matching receipt is complete does
finalization check closure completeness and unaffected canonical rows. It
then rechecks frozen Jira revisions and durably reserves snapshot coordinates
in the `staged` journal. The reservation fixes the snapshot ID, processor/run,
monotonic sequence, authoring epoch, schema, item/receipt counts, temporary
and final paths, and creation time before any candidate digest exists. The
Preparer builds the schema-v3 temporary snapshot from the
carried-plus-staged overlay, sanitizes it, writes exactly one
`authoring_snapshot_provenance` row with those reserved coordinates and actual
public table counts, checkpoints SQLite, and validates integrity and counts.
Only those final self-contained bytes are sized and SHA-256 hashed. The
candidate descriptor contains both the reserved coordinates and that
post-provenance digest. Its proof binds purpose
`publication-reconciliation`, source run/snapshot, stable Jira generation,
accepted/carried/re-authored counts, overlay corpus fingerprint,
grouping-impact fingerprint, and capture time.

The private comparison and reconciliation proof use reconciliation contract
version 2. Version-1 comparison/proof JSON remains deserializable for status
and audit, but cannot dispatch grouping, materialize a candidate, recover a
pending publication, or promote. Missing version-1 item coordinates are never
inferred. This version is independent of
`PreparedTicketPublicationContract.CurrentVersion`: the promoted public
snapshot descriptor continues to use generic publication-proof contract v1
while retaining the canonical v2 reconciliation corpus fingerprint.

#### Database-first promotion journal

Promotion is intentionally database-first and resumable. It does **not** claim
physical atomicity between SQLite and the filesystem.

1. In `staged`, the complete workspace, durable snapshot-coordinate
   reservation, and post-provenance authenticated temporary candidate exist,
   while canonical rows and the prior immutable publication are unchanged.
2. One immediate transaction on one connection revalidates the run/fences and
   staged/unaffected fingerprints and the exact candidate digest, size,
   provenance, counts, descriptor, and reserved coordinates. It then
   replaces each revised canonical graph and hydration batch, advances its
   receipt-backed authoring state, replaces every impacted grouping partition,
   creates the snapshot record from the reserved coordinates,
   writes the durable promotion descriptor using the same reserved identity,
   and moves both reconciliation and journal to
   `snapshot-publish-pending`.
3. After commit, recovery opens candidate/final databases read-only, moves an
   exact candidate once to the immutable final path without overwrite, and
   revalidates the journaled post-provenance digest and every reserved
   coordinate. It advances the same snapshot record from creating/promoted to
   ready, completes the same run, then atomically marks the
   journal/reconciliation `ready`, removes staging, and releases the
   reconciliation fence.

The pending journal is the cross-resource handoff. While it exists, every
competing Preparer mutation returns `recovery-in-progress`. Startup invokes
recovery for every pending run; the status-specific retry endpoint invokes the
same operation. Canonical replacement is never applied twice and an existing
final file is never overwritten.

Recovery handles each evidence combination explicitly:

| Evidence at restart | Required action |
|---------------------|-----------------|
| State `staged`; graph/group staging missing or divergent | Refuse before canonical promotion with `staging-mismatch` or `grouping-impact-mismatch`; preserve prior publication. |
| Pending; final absent; temporary candidate matches the post-provenance journal, candidate descriptor, and snapshot record | Validate read-only, move without overwrite, validate the same bytes at the final path, and continue. |
| Pending; final absent; temporary missing, corrupt, wrong-sized, or checksum-divergent | Record `promotion-recovery-failure`, retain journal/workspace/fence, and stop. |
| Pending; final present and matching the journaled digest and all coordinates | Reuse it and continue, regardless of whether the crash occurred immediately after the file move. |
| Pending; final present but conflicting, corrupt, or accompanied by contradictory descriptor/record evidence | Record `promotion-recovery-failure`, do not overwrite it, and retain the fence. |
| Pending snapshot record absent or inconsistent in identity, paths, sequence/epoch/schema, counts, digest, size, or creation time | Record `promotion-recovery-failure`; filesystem evidence alone is not adopted. |
| Snapshot record creating | Validate final bytes and mark it promoted, then ready. |
| Snapshot record promoted | Mark the same record ready after validation. |
| Snapshot record already ready; run incomplete | Reuse its descriptor and complete the same run. |
| Run complete; journal still pending | Mark reconciliation/journal ready, release the fence, and clean workspace. |
| Staging missing after the database promotion commit | Do not reconstruct or replay the overlay; recover from the exact journal, descriptor, snapshot record, and authenticated file. |
| Recovery is cancelled while pending | Record `promotion-recovery-failure` with the journal still pending and retain the fence. |
| Recovery attempts compete | Serialize attempts per run; a filesystem-race winner is accepted only after exact final-byte validation, and later attempts return the same ready descriptor. |
| Reconciliation `ready` | No canonical replay; the verified replacement is terminal success and cleanup is idempotent. |
| Reconciliation `cancelled` | No recovery or canonical replay; the frozen comparison and cancellation audit are terminal while both fences and disposable workspace remain released. |

Missing staging after the database promotion commit is not a reason to replay
the overlay: pending recovery is driven by the journal, snapshot record, and
verified file evidence. Conversely, no pending journal means recovery cannot
infer that canonical promotion occurred merely from an unclaimed temporary
file. Recovery never creates, replaces, or repairs
`authoring_snapshot_provenance`, and it never adopts a checksum calculated
from unjournaled bytes; every promoted checksum is the one frozen before
canonical promotion.

#### Audited pre-promotion cancellation

The dedicated cancellation transition is valid only from `staged`, with a
non-blank reason, before trusted candidate evidence, an
`authoring_review_snapshots` row, or canonical replacement exists. One
immediate transaction records `cancelled`, time, and reason; ends active
attempts and incomplete stages; marks all generic run items superseded while
retaining accepted receipts;
removes graph/hydration/receipt, grouping, unaffected-fingerprint, and proof
workspace; releases both the reconciliation and processor mutation fences;
and finally projects `authoring_runs` to terminal `superseded`. It does not
invoke generic item/run supersede APIs.

The comparison, item decisions, accepted receipts, run history, and cancelled
journal remain audit evidence. Repeating cancellation returns that original
audit without rewriting it. Concurrent item retry, stage work, finalization,
and cancellation serialize on SQLite: a cancellation winner makes later
workspace writes/fence checks fail; a trusted-candidate or promotion winner
makes cancellation fail with `cancellation-not-allowed`. Cancellation is not
available from `snapshot-publish-pending`, `canonical-unpublished`, or
`ready`.

#### Audited unpublished canonical state

An operator may abandon only `snapshot-publish-pending`, only with a non-blank
reason, and only through the explicit endpoint. The transaction records
`canonical-unpublished`, abandonment time/reason, the run error/completion
time, and releases both fences. It does not compensate canonical data, publish
or delete the candidate, alter the prior verified pair, or count as successful
publication.

The database trigger then rejects every new `authoring_runs` row with
`DatabaseOnly = 0`, returning `canonical-unpublished-restriction`. Therefore
metadata refresh, another reconciliation, and ordinary snapshot-producing
authoring remain blocked. Ordinary `databaseOnly:true` authoring can continue.
The state must remain visible until a separately explicit recovery operation
creates and verifies a snapshot for that canonical epoch; the pending retry
endpoint and abandonment itself do not clear it.

Stable lifecycle failures are `invalid-baseline`,
`unstable-jira-generation`, `revision-invalidation`, `staging-mismatch`,
`grouping-impact-mismatch`, `recovery-in-progress`,
`promotion-recovery-failure`, `cancellation-not-allowed`, and
`canonical-unpublished-restriction`. Status exposes counts, decisions,
grouping impacts, invalidated keys, promotion/journal/fence state, recovery
failure details, audit fields, and the nullable replacement proof so callers
do not infer processor state from private tables.

### Metadata-only Discussion publication refresh

A publication refresh enriches publication metadata without replaying authored
work or recomputing grouping and refuses changed accepted Jira revisions. Its
source coordinate must be a completed, non-database-only Preparer run with a
ready snapshot. Retain that run's original descriptor/database pair and
complete site, including their digests, as the immutable fallback.

Admission makes two comparisons. First it verifies the source run's own
immutable snapshot and requires its actual accepted content, receipt
coordinates, grouping IDs, values, ordering, and membership to remain present
unchanged. It then freezes the **entire current accepted receipt-backed
corpus**, including historical protected provenance. Additional current
tickets/disjoint grouping partitions are allowed only without altering
original protected output, and are disclosed through public run status
`corpusComparison`; the source run's item count alone is not the baseline's
exported-corpus count.

The new `purpose:"publication-refresh"` run, `sourceRunId`, completed
receipt-backed maintenance items, mutation fence, and versioned recipe are
created atomically. `authoring_runs.RequestJson` records recipe
`publication-enrichment`, version `1`, with original/frozen protection
coordinates before the scheduler is woken, including if no stage rows yet
exist. New `StartAsync` calls always use this protected recipe; null is not a
caller-selectable legacy mode. The start does not fetch publication metadata,
create authoring attempts, or dispatch workers.

Source people population remains a separate explicit prerequisite, not part
of refresh or site generation. Use gateway
`POST /api/v1/jira/public-people/preview` and `/apply` with trusted
same-revision evidence and complete shared-impact acknowledgement. Cache-only
is the default; legacy raw caches without source acquisition receipts are
untrusted and upstream authentication use requires deliberate opt-in. A
current policy marker does not guarantee any name is available. See the
[source maintenance contract](data-sources.md#deliberate-public-people-previewapply)
for bounds, atomicity, refusal, and lost-response handling.

Fenced finalization performs these steps:

1. Re-read the frozen current graph and require exact receipt, item,
   expected-revision, protected-value, and grouping coordinates. Counts and
   legacy content hashes alone are insufficient.
2. In `publication-enrichment-v1`, use the real typed Orchestrator Jira item
   API and `GET /api/v1/zulip/references/resolve?reference=...` for accepted
   Zulip associations only. Every Jira response must be stable, match the
   accepted revision, carry the current people policy, and share one Jira
   content generation. The explicit self-ticket Jira update time is
   publication metadata, not a replacement receipt revision.
3. `ApplyPublicationEnrichmentAsync` rechecks the full stage lease, fence,
   recipe, graph, and exact outcome coverage in one allowlisted transaction.
   It updates parent/self publication metadata, requester rows, and accepted
   Zulip hydration only; writes the new run's Jira provenance and durable
   apply receipt; and verifies protection again. Authored analysis,
   proposals, reference justifications, unrelated hydration, accepted
   receipts, historical contributing-run provenance, and grouping are not
   rewritten. Every accepted Zulip reference has an outcome: a failed lookup
   can retain a safe, previously source-backed link/context as last-known,
   but cannot certify it as resolved. A legacy zero-message URL remains
   unverified. Optional timestamp diagnostics can accompany a resolved link.
4. In `grouping-certification` stages, verify current grouping output against
   retained source grouping receipts. Receipts with an existing output
   fingerprint are reused directly; legacy receipts are bound to the current
   output by a separate durable certification. Grouping is never dispatched or
   recomputed.
5. Recheck protection on the actual SQLite backup and emit a new Preparer
   schema-v3 snapshot and descriptor. Public publication-proof contract v1
   remains unchanged: `publicationProof` binds purpose, source run,
   Jira freshness/content revision, people-policy version, corpus fingerprint,
   grouping fingerprint, and capture time. The new snapshot has its own
   `runId`, `snapshotId`, and monotonic sequence.

Authored payloads, accepted receipts, historical input provenance, topic
grouping, and source grouping receipts are not rewritten by this flow. After
the refresh run reaches `completed`, download that run's verified pair and
publish its Discussion site in a different run-scoped directory as a separate
client-side operation. Keep the prior pair/site intact and usable. The Dev UI
offers generation/regeneration independently from refresh even when readiness
is verified; neither action silently invokes the other or backfills sources.

Stage and snapshot recovery stay on the same refresh run. If the process stops
after the metadata transaction but before stage completion, retry consumes the
durable apply receipt and does not fetch or apply source metadata again. If
snapshot promotion is interrupted, snapshot reconciliation resumes or reuses
the same immutable proof and ready snapshot coordinate rather than creating a
different repair generation. Other transient stage failures leave the run in
recoverable `error` for scheduler-owned retry.

Missing/replaced original output or changed grouping refuses admission.
Execution-time protected-graph drift, a missing ticket, changed accepted Jira
revision, or mixed Jira generation terminally supersedes/refuses the refresh,
releases the fence, and prevents a usable new snapshot. A rejected metadata
batch has no partial apply or apply receipt. A later protection failure after
an already committed batch does not undo that commit. Retain the original
publication and inspect the conflict; there is no deletion, reset,
re-authoring, regrouping, or forced old-site replacement fallback.

Recovery discriminates recipes only from persisted metadata. An explicitly
persisted null legacy maintenance request resumes the old Jira-only
`publication-metadata` recipe. A malformed or unknown non-null recipe is
refused, never treated as legacy; an old stage receipt cannot satisfy
`publication-enrichment-v1`. Receipt-first recovery of a committed new stage
does not refetch Jira or Zulip. Before deploying an older binary, stop new
enrichment admission and quiesce/reconcile new-recipe runs: database
readability alone does not establish safe in-flight downgrade.

Committed source people and processor publication metadata persist after a
code revert. Do not reset them, restore broad SQL backups, lower source
generations, or replay expensive authoring/grouping. A metadata correction
requires a separately reviewed conditional operation by its owning service,
while retaining all accepted output and prior artifacts. No Preparer public
schema migration needs reversal.

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
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"reconcile-publication","sourceRunId":"<sourceRunId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"reconciliation-status","runId":"<reconciliationRunId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry-reconciliation","runId":"<reconciliationRunId>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"cancel-reconciliation","runId":"<reconciliationRunId>","reason":"<reviewed reason>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"abandon-reconciliation","runId":"<reconciliationRunId>","reason":"<reviewed reason>"}'
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"refresh-publication","runId":"<sourceRunId>"}'
```

Use `reconcile-publication` when accepted Jira revisions changed; it selects
the baseline with `sourceRunId`. Use `refresh-publication` only for
metadata-only repair with unchanged revisions; it selects the completed
source run in `runId`. Both return a different new run ID. Follow a
reconciliation with `reconciliation-status`; use reason-bearing cancellation
only while it remains staged, and retry/abandon only at the documented
post-promotion boundary. Follow a refresh with ordinary `status`. After
successful completion, use the
new run ID for `snapshot` and site publication. Do not use either maintenance
run as the source of another maintenance operation.

Publish a downloaded verified pair:

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out "cache\jira-ticket-sites\<runId>"
```

Use the new refresh run's pair and output root, never `--force` against the
original publication as a repair. The offline publisher neither reads sources
nor starts/checks enrichment. Synthetic tests and browser walkthroughs are not
authorization or proof of live-corpus repair; that remains a separate operator
operation with preservation/revision evidence gates.

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
