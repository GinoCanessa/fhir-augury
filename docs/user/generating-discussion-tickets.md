# Generating Discussion Tickets

This guide produces a static **Tickets for Discussion** review site from a
processor-owned Preparer run. The Preparer owns ticket selection, authoring
workers, result receipts, hydration, topic grouping, and snapshot creation.
The Dev UI and `ticket-site` publish only from the immutable, verified snapshot
pair downloaded after the run completes.

## What you'll produce

- A completed Preparer authoring run with one durable receipt per accepted
  ticket.
- With the Dev UI, a workflow-bound pair under
  `cache\devui-authoring-snapshots\prepare\<runId>\` and a self-contained site
  under
  `cache\devui-review-sites\prepare\<runId>\discussion\`.
- With the headless CLI flow, a pair under
  `cache\authoring-snapshots\preparer\<runId>\` and the selected
  `ticket-site` output root.

Each durable pair contains the descriptor, database, and
`verified-pair.json`. Each published discussion sub-site contains
`site-manifest.json`. The checked-in Preparer default emits public snapshot
schema v3; the current host requires that version. The publisher also accepts
legacy Preparer snapshot schema v1 and v2 pairs. Older pairs remain publishable
but produce degraded readiness, cannot expose trusted people, and are never
edited in place. The live processor database is not a site input. Preparer
snapshot schema v3 and the publication-owned Discussion renderer schema v3 are
separate contracts.

## Reading the generated site

The default visible title is, for example,
`Tickets for Discussion - Sept 15, 2026`. Its date is the maximum **self-ticket
Jira `UpdatedAt`** among the tickets actually exported by the publication's
generation filters. In the snapshot this is
`prepared_jira_hydration.UpdatedAt` where `TicketKey` equals `JiraKey`.
Related-ticket dates, upstream refresh watermarks, authoring completion,
snapshot creation, and publication time are not substitutes. The maximum is
normalized to UTC, with fixed English labels
`Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sept, Oct, Nov, Dec` and an unpadded
day. There is no `Built` prefix.

The suffix requires a nonempty export with a valid date for every exported
ticket. `discussionCorpus.dateCoverage` is `empty`, `none`, `partial`, or
`complete`; partial coverage retains its known `maxJiraUpdatedAt` as a fact
but does not add a title date. Malformed non-null dates fail validation rather
than silently becoming missing dates. For schema-v2/v3 input, zero or multiple
accepted authoring coordinates for a retained ticket remain structural
validation errors. Complete dates can qualify the suffix even when provenance
readiness is degraded, including on a readable legacy snapshot.

An explicit `ticket-site --title` remains the stable base title. The date
suffix and then the generation-filter suffix form `displayTitle`. Browser
filters do not change this frozen title. Tickets for Applying remains
unchanged on the Planner snapshot/renderer v1 path.

Every newly generated Discussion manifest and renderer database also carries
structured readiness:

- **Publication readiness is verified.** `discussionReadiness.isReady` is
  true and `evidence` is `ordinary-snapshot` or `publication-refresh`.
- **Publication readiness is degraded.** One or more reason codes explain the
  boundary: `legacy-snapshot-schema`, `missing-ordinary-provenance`,
  `invalid-refresh-proof`, or `missing-people-policy-proof`.
- **Publication readiness is not recorded.** The existing
  `site-manifest.json` predates structured readiness. Treat Jira freshness and
  trusted people as unverified.

Schema v1 and v2 always have degraded readiness because they predate the
schema-v3 people-policy contract. They can still have complete self-ticket
dates, but cannot expose trusted people and are never upgraded in place.
`jiraSourceLastSuccessfulRefreshAt` remains upstream provenance, not the title
date.

Readiness and coverage are separate. Renderer-v3 manifests and browser
databases record `discussionCorpus`: ticket and exported-project counts,
valid self-date count and maximum, tickets with a public Reporter, Assignee,
or in-person requester, and per-kind related-link counts. Link counts
distinguish resolved safe links, unresolved rows retaining a safe URL, and
rows without a usable URL. These are measured exported-corpus facts, not a
promise that every name or link is available. Older manifests without this
summary have **unknown coverage**, not zero coverage.

Ticket pages use only public, snapshot-resident context:

- Reporter and Assignee use only schema-v3 display names carrying the exact
  current public-policy marker. When that proof is unavailable, the page shows
  the structured readiness reason instead of a legacy value.
  `No public display name available` means no publishable value was supplied
  under the current policy; it does not establish that the role is absent or
  that a ticket is unassigned.
  In-person requesters appear only when at least one current-policy,
  context-free-safe v3 value exists. Schema-v1 and schema-v2 sites
  intentionally show no public people because those snapshots cannot prove
  that the account-identifier policy ran.
- Facets are cumulative and preserve the current overview, crosscut, or list
  route. Removing one chip keeps the other filters. Active and
  generation-fixed dimensions are hidden from facet choices; Project is
  omitted for a single-project **export**, not merely a filtered subset.
  List search/sort and back/forward state survive filter changes, and an
  empty result retains recovery controls.
- Guided proposal headings explain the choices without changing authored
  proposal bodies, justifications, recommendations, or their copy-for-AI
  contents.
- Safe HTTP(S) repository, Jira, and Zulip URLs are actionable
  external links. In-corpus Jira navigation uses the site's hash route and
  preserves filters; external source links open safely in another tab.
  Numeric Zulip references resolve to indexed message context and a `/near/`
  URL, not a guessed topic link. Linked/related Jira and Zulip summaries share
  source links with the related-item context. Repair of missing GitHub-item
  URLs or lookup failures is outside this enrichment operation; repository
  references use their separate snapshot-resident URLs.
- A failed lookup retains previously source-backed safe context as
  **last-known**, with the lookup failure still visible. An old zero-message
  thread URL is **unverified**, even if it once carried a `resolved` flag.
  Sidebar and summary-source diagnostics distinguish these cases. Optional
  detail diagnostics remain visible even when the link itself is resolved.
- The browser reads a publication-owned renderer-schema-v3 database projected
  from the verified snapshot. It never reads processor tables and makes no
  live Jira, Orchestrator, or Preparer request, so the complete output remains
  usable through `file://` or ordinary static hosting. Discussion HTML embeds
  the trusted SQL.js WebAssembly bytes used for direct-file initialization and
  retains the standalone WebAssembly asset for hosted compatibility.

## Prerequisites

- For starting a preparation run, `source-jira` (`:5160`) and the
  Orchestrator (`:5150`) are healthy.
- `processor-jira-fhir-preparer` (`:5171`) is started, healthy, and reports
  processing as running, not paused or unobserved. Under Aspire it uses
  explicit start.
- For the UI flow, start the explicit `devui` resource (`:5210`) in Aspire.
- For the headless flow, `fhir-augury-cli` is installed, or use
  `dotnet run --project src\FhirAugury.Cli --` in place of the executable.

Before starting a publication refresh, verify the Preparer's **effective**
configuration, not only the checked-in default. Inspect its gitignored
`appsettings.local.json` and process/Aspire environment for
`Processing:SnapshotSchemaVersion`, including the exact environment variable
`FHIR_AUGURY_PREPARER_Processing__SnapshotSchemaVersion`. Correct a stale
local v1/v2 override to `3` without adding or committing an override, restart
the Preparer, and confirm its log says
`Effective Preparer snapshot schema version is 3`. The current host rejects
every other value at startup; legacy v1/v2 support is reader compatibility,
not a producer setting.

Planner may remain offline. Planned tickets, Planner run history, and Planner
snapshots are not prerequisites for preparation or Discussion publication.
Opening the workspace does not require existing preparation history either;
publication still requires a completed, snapshot-producing Preparer run and
its verified snapshot pair.

```powershell
fhir-augury-cli --json '{"command":"services","action":"status"}'
curl http://localhost:5171/health
```

Aspire remains responsible for resource start/stop, logs, and traces. The Dev
UI reports readiness evidence and directs you back to Aspire for resource
problems; it does not control the AppHost.

## Dev UI flow

The workspace renders both **Prepare tickets** and **Plan tickets** cards,
including **Start a run** and **Open by run ID**, immediately. Once the page
becomes interactive, preparation history, planning history, and readiness load
independently. Preparation results appear when their own read completes, even
while Planner is pending or unavailable. A readiness or history failure has
its own diagnostic rather than hiding the other observations or navigation.

1. Open `http://localhost:5210`, choose **Prepare tickets**, and select
   **Start a run**.
2. Choose the processor's configured selection or paste explicit Jira keys.
   The UI normalizes explicit keys and requires every token to be valid.
   Snapshot-and-site output is the default; database-only output is an
   advanced opt-out with no later site action.
3. Review Preparer, Orchestrator, and Jira readiness, then submit once. A
   structured `409` shows each authoritative conflicting run as an **Open
   run** link instead of encouraging another start.
4. Monitor `/operations/prepare/<runId>`. The page reads the processor-owned
   run immediately and polls sequentially while `state.isTerminal` is false.
   This includes run status `error`, which is recoverable and non-terminal;
   there is no generic terminal `failed` run state.
5. Let automatic retry run by default. **Retry now** appears only when
   `allowedActions.canRetryNow` is true. Supersession appears only when
   `allowedActions.canSupersede` is true and requires confirmation plus a
   non-empty reason; the processor permits it only for an eligible current
   error without an accepted receipt.
6. After a snapshot-producing run reaches `completed`, review full versus
   partial success and every superseded ticket, then choose **Generate review
   site**. Open the exact run site at
   `/review-sites/prepare/<runId>/discussion/`.

Each page visit independently requests each processor's operator-visible
active/recovering and recent terminal runs, up to 20 by default and
prioritizing non-terminal work. Ordinary runs and purpose-marked maintenance
runs, including `publication-refresh`, are visible; initial-revalidation and
legacy unmarked rows are intentionally absent. `truncated:true` means older
history exists. **Open by run ID** always uses authoritative detail and
remains available while history or readiness is pending or unsuccessful,
including for a known run outside that bounded list.

Before the first successful history read, counts are unknown. Only a current
successful read can show **No active runs** or **No recent completed runs**;
a successful zero-length list is genuinely empty history. **Run history is
unavailable** indicates a transient HTTP, transport, or timeout problem.
**Run history read failed** identifies other errors, such as authentication,
authorization, routing/configuration, or an invalid response. Neither failure
means there are no runs, and an HTTP error alone does not prove an Aspire
resource is stopped.

Refreshing retains the last successful history, counts, and links, labeled
**Stale history** with the original last-successful-read time. They remain
visible while refreshing or after an unsuccessful refresh; a successful
refresh replaces them. If that previous list was empty, the card says
**Previously observed empty history; current run counts are unknown**, not
that the current history is empty.

**Refresh history** affects only that workflow's history and is disabled only
while its own read is in flight. The header's **Refresh and recheck** schedules
idle histories and an explicit readiness refresh without waiting for Planner
history to finish. Requests coalesce per observation: another request for an
already-running history or readiness read uses that attempt, without queuing a
second one. Typed run IDs remain in their cards across these updates.

On `/operations/prepare/new`, both the review summary and submission require
a current successful readiness read with no required-service blockers, a valid
selection, no submission already in progress, and no outstanding unknown-start
review. Readiness loading or rechecking, an unavailable/failed read, and a
successful report containing blockers all prevent starting. Missing, disabled,
unobserved, or unhealthy required services, or paused Preparer processing,
remain blockers. A previous ready result is stale while a recheck is in
progress or after it fails; it never supplies a current **Ready to start**
badge or permission to submit. Resolve genuine required service problems in
Aspire and recheck.

**Recheck readiness** on the start form and the readiness part of **Refresh and
recheck** use the Orchestrator's existing all-services refresh. When scheduled,
that refresh may still wait on optional service probes, including an offline
Planner. This does not make Planner or planned data a Discussion prerequisite
and does not block Home's independent history and navigation.

If transport fails after a start, retry, or supersede may have reached the
processor, the UI labels the outcome unknown and never replays the mutation.
It reconciles with read-only list/detail requests; an ambiguous start requires
explicit review before another submission. A `409` after an item action also
refreshes the run because server-advertised actions are hints and the processor
rechecks eligibility atomically.

An empty or unavailable history is not publication input. **Generate review
site** checks authoritative completion of a snapshot-producing Preparer run and
requires a verified pair bound to that workflow and run. Database-only runs or
missing, invalid, or mismatched pairs cannot be published. The Dev UI reuses an
existing verified pair or downloads one into its private run directory, then
invokes the shared publisher in-process. Processor success and site-publication
success are separate. A publication failure retains the pair, leaves receipts
and run state unchanged, and retries publication only.
Pairs and run sites under ignored `cache\` are retained indefinitely in this
release; remove old workflow/run directories manually only while the Dev UI is
stopped. Generated JavaScript is served from the Dev UI origin, which is a
trusted-local boundary, and the snapshot cache itself is never web-served.

## Populate source people only after an explicit preview

Source-person population is a separate, explicitly authorized prerequisite
when qualifying names are missing. Generating a site or refreshing Preparer
publication data does **not** perform Jira backfill. Use the Orchestrator
gateway (`:5150`) or the Dev UI API catalog's **Public People Maintenance**
entries; there is no dedicated preview/apply CLI action documented here.

Preview is read-only and defaults to cache-only evidence:

```http
POST /api/v1/jira/public-people/preview
Content-Type: application/json

{"keys":["FHIR-10028","FHIR-29212"],"evidenceMode":"cache-only"}
```

Review `code`, each ticket/role reason, `canApply`, `affectedTickets`,
`changes`, and `requiresSharedUserImpactAcknowledgement`. The source binds
exact identities, ticket revisions, before-images, trusted observations, and
the complete shared-user impact, including nonselected tickets. It never
matches or rebinds people by display name. Limits are 2,000 requested keys,
10,000 affected ticket coordinates, eight outstanding previews, and 15 minutes
after preview completion. Oversized impact is refused, not truncated.

Legacy raw caches without source-written acquisition receipts have **unknown,
untrusted origin**. Their presence is not permission to populate names.
Missing identity/name evidence, conflicting observations, missing fields,
malformed values, and rejected names are surfaced explicitly. There is no
guarantee that every missing name can be recovered. Only a deliberate
`"evidenceMode":"upstream"` opts into upstream reads using existing Jira source
authentication; it does not ingest issues, and a different upstream revision
can still make the preview ineligible.

Apply only the issued token. Set `acknowledgeSharedUserImpact` to `true` only
after expressly accepting every disclosed nonselected-ticket effect when
required:

```http
POST /api/v1/jira/public-people/apply
Content-Type: application/json

{"previewToken":"<previewToken>","acknowledgeSharedUserImpact":false}
```

The source holds its pipeline gate and rechecks the preview in one SQLite
immediate transaction. People, people lookups, and the local content-generation
increment commit together; existing identity bindings, non-people data,
mutation markers, and ingestion/upstream watermarks are preserved. Caller
names, identities, URLs, paths, and unknown request fields are not accepted as
replacement evidence. Stale, expired, consumed, or restart-lost previews need a
new preview. A lost apply response is an unknown outcome: inspect and obtain
fresh evidence, never blindly replay the mutation.

## Choose the correct publication replacement workflow

Generating a site again from an existing verified pair reproduces that pair;
it cannot incorporate a newer Jira revision. The Preparer has two separate
replacement operations:

| Operator intent | Dev UI action | CLI action | What may change |
|-----------------|---------------|------------|-----------------|
| Incorporate one or more newer accepted Jira revisions | **Reconcile changed tickets and refresh snapshot** | `reconcile-publication` | Re-authors every changed baseline ticket, replaces the complete affected grouping closure, and publishes a new immutable snapshot |
| Repair current publication metadata when accepted Jira revisions have not changed | **Advanced: repair publication metadata only** → **Repair publication metadata and snapshot** | `refresh-publication` | Refreshes allowlisted Jira/Zulip publication metadata and certifies existing grouping; never re-authors or regroups |

Do not use metadata repair as a shortcut for changed tickets. It deliberately
refuses revision drift. Do not start reconciliation merely to update names,
links, or freshness metadata: reconciliation is the changed-authored-output
workflow and has a different recovery boundary.

## Reconcile changed tickets and refresh the snapshot

Start from the completed, snapshot-producing Preparer run whose published
corpus is the baseline. Keep its verified descriptor/database pair and
run-scoped site: reconciliation always creates a new run, snapshot ID,
sequence, files, and site. It never edits or removes the baseline pair.

1. Open `/operations/prepare/<sourceRunId>` and choose **Reconcile changed
   tickets and refresh snapshot**.
2. Admission verifies the source run and its immutable snapshot, then compares
   **every accepted ticket in that snapshot** with Jira through the
   Orchestrator. The pass does not stop at the first newer revision. Every
   ticket is observed from one stable Jira content generation and is recorded
   as either `carry-forward` or `re-author`, with both baseline and current
   revisions. Admission repeats the complete observation while holding the
   Preparer mutation fence and persists the frozen comparison and mixed run in
   one transaction. An invalid baseline, incomplete Jira read, mixed
   generation, or revision race refuses admission rather than freezing a
   partial changed set.
3. On the new `purpose:"publication-reconciliation"` run, review the stable
   Jira generation, accepted/carried/re-authored counts, and every item
   disposition. Carry-forward items keep their exact accepted receipt
   coordinates and do not launch an author. Each `re-author` item must produce
   a complete authored graph and hydration batch for its frozen current
   revision, with matching accepted receipt/run-item/operation coordinates.
   Generic item supersession is never completion for this purpose and is not
   offered. Those results are durable run-scoped staging only; the canonical
   graph still serves the prior publication at this point.
4. Grouping derives the closure from each revised ticket's old and proposed
   partition membership. It replaces every affected shared
   topic/group/container in full, including both sides of a partition move.
   Unchanged ticket authored content is fixed input to that computation; it is
   not re-authored. Canonical authored rows and receipt coordinates for
   unaffected tickets, plus topic/group/member values and ordering outside the
   closure, must retain their exact fingerprints.
5. Finalization rechecks every frozen revised Jira revision, requires all
   staged graphs and grouping replacements, and materializes and hashes a
   sanitized temporary snapshot from the carried-plus-staged overlay. Only
   then does promotion begin.
6. After the run reaches `completed` with promotion state `ready`, choose
   **Generate review site** for the reconciliation run. Download and render
   that run's pair in a new run-scoped directory. Its publication proof has
   purpose `publication-reconciliation` and binds the baseline snapshot,
   stable Jira generation, carried/re-authored counts, overlay corpus, and
   grouping impact. The old pair and site remain independently verifiable.

Status can report later `invalidatedTicketKeys` with
`failureCode:"revision-invalidation"` if a frozen changed ticket moves again.
Do not submit output for the newer revision into the frozen run; retain the
baseline publication, inspect the complete invalidation list, and choose
**Cancel staged reconciliation…** with an audited reason. Start a new
comparison only after cancellation has terminally superseded the generic run
and released its fences.

### Database-first promotion and recovery

Promotion is a resumable protocol across SQLite and the filesystem, **not** a
claim that the two resources commit atomically:

1. A verified temporary snapshot and all staged replacements exist while the
   reconciliation is `staged`; canonical output is still unchanged.
2. One immediate SQLite transaction revalidates protected fingerprints,
   replaces revised canonical graphs and the complete affected grouping
   closure, advances accepted receipt state, creates the snapshot record, and
   journals `snapshot-publish-pending`. The mutation fence remains held.
3. Recovery publishes the already verified temporary file to its new,
   immutable final path without overwriting an existing file, validates its
   size/checksum/provenance, advances the snapshot record through promoted to
   ready, completes the run, marks the reconciliation `ready`, releases the
   fence, and removes staging only after those transitions succeed.

The observable promotion states are:

| State | Meaning and operator action |
|-------|-----------------------------|
| `staged` | Canonical output has not been promoted. Staging remains restartable. Before trusted candidate evidence exists, **Cancel staged reconciliation…** / `cancel-reconciliation` is the only terminal operator exit. The explicit snapshot-publication retry and post-promotion abandonment are not applicable. |
| `snapshot-publish-pending` | The database transaction committed, so canonical output is already the replacement, but the immutable snapshot/run transitions are incomplete. All competing Preparer mutations remain blocked with `recovery-in-progress`. Startup recovery retries automatically; **Retry snapshot publication** / `retry-reconciliation` invokes the same idempotent recovery explicitly. |
| `ready` | The final file and snapshot record are verified, the run is complete, staging is cleaned, and the fence is released. Repeating recovery does not reapply canonical replacement. |
| `cancelled` | Pre-promotion cancellation is terminal. Canonical output and the prior publication are unchanged; disposable staging is removed, both fences are released, and the generic run is `superseded`. |
| `canonical-unpublished` | An operator explicitly abandoned a pending promotion with an audited reason. Canonical replacement remains in the database, no replacement publication is claimed, and the prior verified pair is unchanged. The generic run is terminal, non-recoverable `abandoned`, not `error` and not success. |

Recovery evaluates each durable boundary rather than assuming which operation
ran last:

- With no final file, a matching temporary candidate is given the pending
  snapshot provenance, validated, and moved. A missing/corrupt temporary file
  when no valid final exists, or a checksum/provenance mismatch, reports
  `promotion-recovery-failure` and keeps the journal and fence.
- A matching final file is reused whether the interruption occurred just
  after the move or later. A conflicting or corrupt final file is never
  overwritten; recovery stops with `promotion-recovery-failure` and retains
  the fence for investigation.
- A missing or conflicting snapshot database record is a recovery failure.
  A creating record is advanced after file validation; a promoted record is
  made ready; an already-ready record is reused.
- Missing staging before database promotion is `staging-mismatch` or
  `grouping-impact-mismatch`. After the database-first transaction commits,
  recovery uses the durable journal and snapshot evidence and never applies
  staging a second time.
- A restart after the snapshot is ready but before run completion completes
  the same run. A restart after run completion but before the journal/fence
  transition finishes marks the same reconciliation ready and cleans up.

Use **Cancel staged reconciliation…** only while state is `staged` and before
trusted candidate evidence exists. The required non-blank reason and time are
retained with the frozen comparison. One transaction ends active attempts and
incomplete stages, removes only disposable unpromoted workspace, releases the
processor and reconciliation fences, and reports the generic run as terminal
`superseded`. It does not call generic item/run supersede APIs and does not
change canonical output. Repeating cancellation is idempotent. The action is
hidden after `snapshot-publish-pending`, `canonical-unpublished`, or `ready`;
direct attempts then return `cancellation-not-allowed`.

Use **Abandon without publication…** only while
`snapshot-publish-pending`, after retry and evidence repair cannot recover the
snapshot. The non-blank reason and time are persisted. Abandonment does not
copy the old snapshot back, undo canonical rows, delete the temporary
evidence, or make the new epoch publishable. It changes the generic run only
from `finalizing` or recoverable `error` to `abandoned`, releases both fences
and the single active-run slot, and leaves the audit plus prior verified pair
intact.

The restricted/allowed workflow matrix is:

| Durable intent while `canonical-unpublished` is unresolved | Outcome |
|---|---|
| Ordinary authoring with `databaseOnly:false` | HTTP `409 canonical-unpublished-restriction` |
| Initial revalidation, metadata refresh, or another reconciliation | Same typed `409` |
| Snapshot run queued before abandonment | Terminally refused before mutation-fence acquisition |
| Candidate creation or finalization/promotion for any non-database-only run | Refused with the same restriction |
| Ordinary `databaseOnly:true` work | Allowed, including at active capacity one after abandonment |
| `canonical-epoch-recovery` | Sole reserved snapshot-producing bypass |

The Preparer enforces this at admission, queued fence acquisition, candidate
creation, and immediately before finalization/promotion; SQLite triggers are
last-ditch race guards. Conflict details preserve the abandoned run and any
linked recovery run as operator navigation coordinates. The restriction
remains until the separate explicit recovery creates and verifies a snapshot
for that canonical epoch; neither abandonment nor an ordinary retry counts as
publication.

## Repair publication metadata only

Metadata repair is a Preparer-owned metadata-only lifecycle followed by
ordinary pair download and local site generation. It does not edit the legacy
pair or site, does not replay authoring or grouping, and refuses any accepted
ticket whose Jira revision differs from its accepted revision.

**Generate/Regenerate review site** and the advanced **Repair publication
metadata and snapshot** action are independent for an eligible completed
Prepare source run with an existing publication, even if it is already
proof-ready.
An unpublished completed run offers generation only. Readiness does not
guarantee complete names or links.
Generation uses the verified pair already associated with that run; refresh
requests a different linked run and pair. Planner, database-only, superseded,
active, `publication-refresh`, and `publication-reconciliation` source runs
remain ineligible in this UI. In the current UI, metadata refresh is the
advanced **Repair publication metadata and snapshot** action.

1. Open the completed source run at `/operations/prepare/<sourceRunId>` and
   retain its original descriptor, database, and complete site. Record their
   identities/digests and review readiness and coverage separately.
2. Complete the effective-configuration preflight and, only if separately
   authorized and needed, the source people preview/apply above. Then expand
   **Advanced: repair publication metadata only** and choose **Repair
   publication metadata and snapshot** once.
3. The Preparer creates a different run with
   `purpose:"publication-refresh"`, `sourceRunId:"<sourceRunId>"`, and
   `databaseOnly:false`; the UI navigates to
   `/operations/prepare/<refreshRunId>`. Admission first verifies the source
   run's immutable snapshot and requires its actual protected content,
   accepted receipts, and grouping IDs/order/membership to remain unchanged.
   It then freezes the entire current accepted corpus atomically with the
   new recipe and mutation fence.
4. Follow that run until `state.isTerminal` is true. It covers the Preparer's
   frozen receipt-backed corpus. Additional current output is permitted only
   without changing the original protected output and is disclosed through
   public run status `corpusComparison` (source snapshot, source exported
   count, current accepted count, and additional count); clients do not read
   the live database. Items retain accepted receipts and create no authoring
   attempts. The `publication-enrichment` v1 recipe's
   `publication-enrichment-v1` stage fetches current Jira publication metadata
   and accepted Zulip references through typed Orchestrator HTTP. Jira
   revisions must still match and all Jira reads must share one stable content
   generation. An allowlisted transaction applies the metadata and per-reference
   outcomes, and existing grouping is certified without dispatching it.
5. When the refresh run reaches `completed`, choose **Generate review site**
   on that run. The Dev UI downloads its verified pair to
   `cache\devui-authoring-snapshots\prepare\<refreshRunId>\` and publishes the
   new site at
   `/review-sites/prepare/<refreshRunId>/discussion/`. Snapshot download and
   site publication remain separate from processor completion.

Null or blank workgroup attribution in a valid accepted partition with no
stored topics is not corruption and does not need to be backfilled to read
its protection evidence. When the workgroup coordinate is missing,
`unattributed` is an internal comparison projection, not a Jira assignment or
a persisted replacement. Publication refresh retains the accepted tickets and
their original SQL NULL/blank values; it does not infer attribution or regroup
them. Changing a stored NULL to empty text, whitespace, or literal
`unattributed` is still protected-value drift, even if the comparison display
would look the same.

This does not guarantee completion for every nullable-data shape. An
attributed clean workgroup coordinate with a missing display still has
separate canonical grouping-certification requirements. Source-run and
snapshot eligibility, stable accepted Jira revisions and source generation,
and all exact protected-output checks continue to apply.

The new schema-v3 descriptor retains publication-proof contract v1:
`publicationProof` binds the source
run, stable Jira generation and freshness, current people policy, accepted
corpus, and retained grouping output. A matching renderer reports
`discussionReadiness.evidence:"publication-refresh"` and verified readiness.
The old verified pair and run-scoped site remain usable unless an operator
separately removes them.

If transport fails after the refresh POST may have reached the Preparer, the UI
shows **Publication refresh outcome unknown.** It never repeats the POST. It
performs one bounded read-only run-list reconciliation and displays only runs
created since submission whose `purpose` is `publication-refresh` and whose
`sourceRunId` matches. Even one candidate must be inspected before continuing;
zero or multiple candidates and `truncated:true` also require review. The
refresh button remains disabled until **I inspected the refresh result; allow
another refresh** is chosen. That acknowledgement permits a later operator
decision; it does not assert that the first POST failed.

A transient metadata or snapshot failure leaves the linked run in recoverable
`error`; keep polling and let processor recovery reuse its durable state. A
crash after the metadata transaction reuses the apply receipt without another
source fetch, and interrupted snapshot promotion resumes the same proof and
snapshot coordinate. Missing/replaced original content or changed grouping
causes refusal, including before a run is admitted. If an accepted Jira
revision changes, the source generation conflicts, or frozen protection
drifts during execution, the new run is terminally refused/superseded without
a usable new snapshot. Retain the original publication and inspect the
conflict. Do not delete accepted output, re-author, regroup, reset a database,
or force replacement of the old site as a fallback repair.

The new recipe is persisted before scheduling, even before the first stage
exists. Recovery of an explicitly persisted legacy null maintenance request
uses the old Jira-only recipe; malformed or unknown non-null metadata is
refused, not interpreted as legacy. Before a binary downgrade, quiesce or
reconcile all new-recipe runs. Committed source people and processor
publication metadata survive a code revert: do not reset them or lower source
generations. Any correction requires a separately reviewed owning-service
operation while retaining accepted output and immutable artifacts.

## Headless equivalent

Invoke the [`orchestrate-prep`](../../.github/skills/orchestrate-prep/SKILL.md)
skill. With no ticket list it requests the processor's configured scheduled
selection; with ticket keys it requests an explicit frozen run. The skill
polls processor state while the shared scheduler applies bounded automatic
retry, downloads the trusted snapshot pair, and publishes the site. Immediate
retry or early item supersession occurs only when explicitly requested.

Set `databaseOnly` only when the caller explicitly wants structured processor
state without a snapshot or site.

The skill and commands below exercise the same processor-owned run controls,
verified-pair boundary, and publisher as the UI; they remain the supported
headless/manual path.

### Manual commands

#### 1. Start the authoring run

Configured candidate selection:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
```

Explicit tickets:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":["FHIR-100","FHIR-101"],"databaseOnly":false}'
```

`no-candidates` is a successful no-op. Otherwise retain `runId`,
`authoringEpoch`, and every item ID.

The shipped Preparer admits one active authoring run at a time. A run in
`queued`, `running`, `finalizing`, or recoverable `error` state consumes that
capacity. At the processor/Orchestrator HTTP surface, a second start returns
`409` with
`error:"active-run-capacity-reached"` plus `conflictingRunIds` (and the
legacy-compatible `runId` when there is one conflict), identifying the existing
run instead of silently queuing another. Revision and revalidation conflicts
use the same structured coordinates. The Dev UI turns those coordinates into
links; the CLI keeps its top-level error envelope and adds machine-readable
`data.relatedRunIds`. Start the next run only after the current run reaches
`completed`, `completed-database-only`, `superseded`, or terminal
`abandoned`.

The processor stores the normalized start request (the effective ticket keys
and `databaseOnly` value) with the frozen run. After a service restart it
continues that same run ID and request; operators should poll the existing run
rather than submit a replacement start.

#### 2. Poll authoritative state

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
```

Continue while `state.isTerminal` is false: `queued`, `running`,
`finalizing`, and recoverable `error`. An authored item is successful only when
it is `complete` and has an `acceptedReceiptId`. For an `error`, inspect
`currentError`, `attemptsRemaining`, `nextAutomaticRetryAt`, and
`allowedActions`; the processor retries automatically after the configured
minimum delay. Do not issue manual retries by default. Use the command below
only while `allowedActions.canRetryNow` is true. It may bypass the delay, but
cannot increase the total attempt budget:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
```

Only while `allowedActions.canSupersede` is true, and after an operator has
determined that the current non-receipt-backed error is non-actionable, may it
be closed with an explicit reason:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"duplicate request"}'
```

Publication-reconciliation items always report
`allowedActions.canSupersede:false`. The generic endpoint returns
`reconciliation-cancel-required`; use the run-level staged cancellation
workflow instead.

Normal success is `completed`. `completed-database-only` is valid only for a
run that was explicitly started with `databaseOnly:true`. Either status is
lifecycle success even when item-level `superseded` outcomes make the result
partial. Continue snapshot and site publication from accepted results, and
surface every superseded item and reason.

Outer start, retry, supersede, publication-refresh, reconciliation start,
retry, cancellation, and abandonment mutations are not replayed after an
ambiguous transport failure. Reconcile through run/list or
reconciliation-status reads before deciding whether to act again; do not
treat a missing response as permission to resubmit.

Topic grouping is part of fenced finalization. Do not run a separate direct
database grouping pass. The grouping maintenance endpoint exists only for an
intentional later refresh and still runs as a processor-owned fenced operation.

#### 3. Download the canonical snapshot

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

The CLI verifies service/workflow binding, run and snapshot coordinates, safe
filenames, sizes, and SHA-256 digests before promoting the pair directory. It
writes `verified-pair.json` last; the manifest binds `Preparer`, the run and
snapshot IDs, descriptor/database filenames, size, and both digests.

#### 4. Publish the site

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out "cache\jira-ticket-sites\<runId>"
```

Open `cache\jira-ticket-sites\<runId>\index.html` and choose the discussion entry.
Use a distinct output root for a linked refresh run; do not force the original
site's replacement. Its label includes the frozen maximum Jira self-ticket
date when exported date coverage is complete. The
discussion sub-site records the stable base title, display title, optional
source refresh, structured `discussionReadiness` and `discussionCorpus`, source
snapshot identity, and renderer schema version in
`discussion\site-manifest.json`.

### Headless changed-ticket reconciliation

Retain the baseline pair/site, then start the distinct changed-ticket
operation. This action uses `sourceRunId`, not `runId`:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"reconcile-publication","sourceRunId":"<sourceRunId>"}'
```

The result contains the new reconciliation `run`, complete frozen
`comparison`, and `counts`. Save `run.runId` as `<reconciliationRunId>`.
Inspect the complete lifecycle—including per-ticket dispositions, grouping
impacts, invalidations, promotion journal, fence, and proof—with:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"reconciliation-status","runId":"<reconciliationRunId>"}'
```

Continue normal item authoring while required, but use this status rather than
the generic run status to decide publication recovery. When promotion is
`snapshot-publish-pending`, an explicit idempotent retry is:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry-reconciliation","runId":"<reconciliationRunId>"}'
```

If authoritative Jira drift invalidates the frozen work while state is still
`staged`, cancel before starting a replacement comparison:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"cancel-reconciliation","runId":"<reconciliationRunId>","reason":"frozen Jira revision changed"}'
```

The result returns terminal status, `cancelledAt`, and the persisted `reason`.
Do not use this action once promotion is pending or ready, and never substitute
generic item supersession.

If recovery cannot be repaired and the operator accepts an unpublished
canonical epoch, provide an auditable reason:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"abandon-reconciliation","runId":"<reconciliationRunId>","reason":"<reviewed reason>"}'
```

That last command is accepted only after database promotion. It is
irreversible through this lifecycle, does not restore the baseline, and
activates the `canonical-unpublished-restriction` described above. Prefer
retry. Once reconciliation reports `ready` and the run is `completed`, use
the ordinary `snapshot` action with `<reconciliationRunId>`, then publish that
new pair with `ticket-site`.

The CLI keeps stable reconciliation failures as the top-level error code
instead of collapsing them to `HTTP_ERROR`: `invalid-baseline`,
`unstable-jira-generation`, `revision-invalidation`, `staging-mismatch`,
`grouping-impact-mismatch`, `recovery-in-progress`,
`promotion-recovery-failure`, `cancellation-not-allowed`, and
`canonical-unpublished-restriction`. Human-readable detail is diagnostic;
branch automation on the stable code and inspect returned run coordinates.

### Headless metadata-only publication repair

After retaining the original pair/site, reviewing coverage independently of
readiness, and completing the schema-v3 configuration and preservation
preflights, start the linked refresh. Any source people backfill is a separate
authorized preview/apply, not an effect of this command:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"refresh-publication","runId":"<sourceRunId>"}'
```

`runId` is the completed snapshot-producing **source** run for this action.
The successful response contains the new run under `run` and its receipt-backed
maintenance items under `items`. Save `run.runId` as `<refreshRunId>`, poll it
with the existing `status` action, download its snapshot with the existing
`snapshot` action, and invoke `ticket-site` with that new verified pair. Do not
pass `<sourceRunId>` to those post-refresh steps. Review the returned run's
`corpusComparison` before accepting a publication with additional tickets.

The refresh mutation is sent once. If its response is lost, the CLI does not
replay it; it performs one
`GET /api/v1/processing-services/Preparer/authoring/runs?limit=20` and returns
an `outcome:"outcome-unknown"` result with `sourceRunId`,
`reconciliation` (`succeeded` or `failed`), matching `candidates`,
`listTruncated`, `message`, and nullable `error`. The candidates are limited to
runs created after submission with `purpose:"publication-refresh"` and the
matching `sourceRunId`. Select and inspect a single candidate manually; with
zero or multiple candidates, a truncated list, or failed reconciliation,
review Preparer history before deciding whether another mutation is safe.

`ticket-site` is not part of the online repair. It neither starts nor checks a
refresh and never calls the Preparer, Orchestrator, or Jira. It only validates
and renders the verified pair supplied after processor completion.

## Failure boundaries

- Unpersisted authoring failures are retried automatically after
  `AuthoringRetryDelay` until the total `AuthoringMaxAttempts` budget is
  exhausted; exhaustion produces an item-level `superseded` outcome and does
  not supersede the run.
- Once a receipt is accepted, later hydration, grouping, snapshot, or site
  failure does not invalidate it, trigger re-authoring, or permit item
  supersession.
- A snapshot failure is retried by processor finalization; do not re-author
  accepted items.
- A site publication failure is client-side. Keep the snapshot pair and rerun
  only `ticket-site`; never restart authoring to repair publication.
- A publication-refresh metadata transaction updates only publication fields
  and accepted Zulip enrichment outcomes and writes a durable apply receipt.
  It does not modify authored content, accepted receipts, historical input
  provenance, or grouping output.
- A publication reconciliation first stages all revised graphs and complete
  affected grouping replacements. Before the database promotion commit, a
  failure leaves canonical output and the old publication unchanged. After
  that commit, `snapshot-publish-pending` is a durable, fenced recovery state:
  retry snapshot publication rather than re-authoring or starting another
  mutation.
- Pending reconciliation recovery accepts only matching temporary/final file,
  snapshot-record, checksum, and provenance evidence. Missing or conflicting
  evidence produces `promotion-recovery-failure`; it never overwrites a
  conflicting final file or reapplies canonical replacement.
- `canonical-unpublished` is reachable only by explicit reason-bearing
  abandonment after canonical promotion. It makes the generic run terminal
  `abandoned`, releases the fence and active capacity, leaves the database
  replacement in place, preserves the old pair, and rejects all later
  snapshot-producing workflows with
  `canonical-unpublished-restriction` until explicit canonical-epoch recovery.
- Original protected-output drift refuses admission; an execution-time
  protection, source-revision, or source-generation conflict supersedes the
  refresh run and releases the fence. Retain the original publication, inspect
  the refusal, and do not override it or substitute authoring/grouping.
- The generic **Processor state changed** conflict heading is not proof of
  contention or a concurrent run. Inspect the returned error code and detail
  rather than treating every conflict as a race. Valid missing workgroup
  attribution does not waive another refusal; keep the original verified
  pair usable and do not delete accepted output, re-author, or regroup to
  bypass the check.
- Existing pre-cutover rows remain `legacy-unverified` until the processor's
  initial revalidation run accepts real receipts. Ordinary runs and the first
  canonical snapshot remain blocked until that gate clears.
- On startup after an upgrade, a stalled legacy `error` with an open attempt is
  reconciled, retried when due if budget remains, or terminalized at the item
  level after its final failure. Normal finalization releases the fence before
  the oldest queued run starts.
- Scheduled discovery does not automatically reselect an exhausted unchanged
  Jira revision. A newer revision is eligible normally; deliberately retrying
  the unchanged revision requires an explicit new run.

Typed Zulip diagnostics are available separately at gateway
`GET /api/v1/zulip/references/resolve?reference=...`. Resolution is indexed-only
and returns a per-reference outcome, including explicit ambiguity/unindexed
failures, with canonical nullable UTC timestamps. Neither the transport fix
nor synthetic acceptance evidence proves that all historical JSON failures,
missing names, or a particular live publication have been repaired. Live
repair and acceptance remain separately authorized operations.

## Reference

- [Processors runbook](../technical/processors.md)
- [`ticket-site` reference](../../tools/ticket-site/README.md)
- [Configuration reference](../configuration.md#processing-services)
- [Generating Application Tickets](generating-application-tickets.md)
- [Generating Ballot Notes](generating-ballot-notes.md)
