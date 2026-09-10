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
`site-manifest.json`. The live processor database is not a site input.

## Prerequisites

- `source-jira` (`:5160`) and the Orchestrator (`:5150`) are healthy.
- `processor-jira-fhir-preparer` (`:5171`) is started. Under Aspire it uses
  explicit start.
- For the UI flow, start the explicit `devui` resource (`:5210`) in Aspire.
- For the headless flow, `fhir-augury-cli` is installed, or use
  `dotnet run --project src\FhirAugury.Cli --` in place of the executable.

```powershell
fhir-augury-cli --json '{"command":"services","action":"status"}'
curl http://localhost:5171/health
```

Aspire remains responsible for resource start/stop, logs, and traces. The Dev
UI reports readiness evidence and directs you back to Aspire for resource
problems; it does not control the AppHost.

## Dev UI flow

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
6. After `completed`, review full versus partial success and every superseded
   ticket, then choose **Generate review site**. Open the exact run site at
   `/review-sites/prepare/<runId>/discussion/`.

The overview obtains ordinary active/recovering and recent terminal runs from
the processor on every load, returning 20 by default and prioritizing
non-terminal work. Maintenance, initial-revalidation, and legacy unmarked rows
are intentionally absent; `truncated:true` means older history exists. **Open
by run ID** always uses authoritative detail and remains available for a known
run outside that bounded list.

If transport fails after a start, retry, or supersede may have reached the
processor, the UI labels the outcome unknown and never replays the mutation.
It reconciles with read-only list/detail requests; an ambiguous start requires
explicit review before another submission. A `409` after an item action also
refreshes the run because server-advertised actions are hints and the processor
rechecks eligibility atomically.

The Dev UI reuses an existing verified pair or downloads one into its private
run directory, then invokes the shared publisher in-process. Processor success
and site-publication success are separate. A publication failure retains the
pair, leaves receipts and run state unchanged, and retries publication only.
Pairs and run sites under ignored `cache\` are retained indefinitely in this
release; remove old workflow/run directories manually only while the Dev UI is
stopped. Generated JavaScript is served from the Dev UI origin, which is a
trusted-local boundary, and the snapshot cache itself is never web-served.

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
links; the CLI keeps its existing error envelope, so use the read-only
authoring-list endpoint when headless reconciliation needs coordinates. Start
the next run only after the current run reaches `completed`,
`completed-database-only`, or `superseded`.

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

Normal success is `completed`. `completed-database-only` is valid only for a
run that was explicitly started with `databaseOnly:true`. Either status is
lifecycle success even when item-level `superseded` outcomes make the result
partial. Continue snapshot and site publication from accepted results, and
surface every superseded item and reason.

Outer start, retry, and supersede calls are not replayed after an ambiguous
transport failure. Reconcile through run/list reads before deciding whether to
act again; do not treat a missing response as permission to resubmit.

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
  --out cache\jira-ticket-site `
  --title "Tickets for Discussion" `
  --force
```

Open `cache\jira-ticket-site\index.html` and choose **Tickets for Discussion**.
The discussion sub-site records its immutable inputs in
`discussion\site-manifest.json`.

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

## Reference

- [Processors runbook](../technical/processors.md)
- [`ticket-site` reference](../../tools/ticket-site/README.md)
- [Configuration reference](../configuration.md#processing-services)
- [Generating Application Tickets](generating-application-tickets.md)
- [Generating Ballot Notes](generating-ballot-notes.md)
