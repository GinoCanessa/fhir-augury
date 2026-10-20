# Generating Application Tickets

This guide produces a static **Tickets for Applying** site from a
processor-owned Planner run, then describes the separate Applier flow. The
Planner owns worker dispatch, accepted receipts, hydration, topic grouping,
and snapshot creation. The Dev UI and `ticket-site` build the site only from
the downloaded immutable, verified snapshot pair.

## What you'll produce

- A completed Planner run and accepted plan receipts.
- With the Dev UI, a workflow-bound pair under
  `cache\devui-authoring-snapshots\plan\<runId>\` and a self-contained site
  under `cache\devui-review-sites\plan\<runId>\applying\`.
- With the headless CLI flow, a pair under
  `cache\authoring-snapshots\planner\<runId>\` and the selected
  `ticket-site` output root.
- Optionally, local commits produced by the Applier and pushed on demand.

Each durable pair contains the descriptor, database, and
`verified-pair.json`; each applying sub-site contains `site-manifest.json`.

## Prerequisites

- `source-jira` (`:5160`), `source-github` (`:5190`), and the Orchestrator
  (`:5150`) are healthy.
- `processor-jira-fhir-planner` (`:5172`) is started.
- For the UI flow, start the explicit `devui` resource (`:5210`) in Aspire.
- Start `processor-jira-fhir-applier` (`:5173`) only when you are ready to
  consume completed plans.
- For the headless flow, `fhir-augury-cli` is installed, or use the local CLI
  project.

Aspire remains responsible for resource start/stop, logs, and traces. The Dev
UI reports readiness evidence and controls Planner runs only.

## Dev UI flow

1. Open `http://localhost:5210`, choose **Plan tickets**, and select **Start a
   run**.
2. Choose configured selection or paste explicit Jira keys. Snapshot-and-site
   output is the default; database-only output is an advanced opt-out.
3. Review Planner, Orchestrator, Jira, and GitHub readiness, then submit once.
   A structured `409` presents every authoritative conflicting run as an
   **Open run** link.
4. Monitor `/operations/plan/<runId>`. Polling continues while
   `state.isTerminal` is false, including recoverable run status `error`.
   Automatic retry is the default. **Retry now** and supersession are shown
   only when `allowedActions.canRetryNow` or `allowedActions.canSupersede`
   respectively is true; supersession also requires confirmation and a
   non-empty reason.
5. After `completed`, inspect full or partial success and every superseded
   item, generate the site, and open
   `/review-sites/plan/<runId>/applying/`.

The overview reads the processor-owned bounded list on each load: active and
recovering ordinary runs first, then recent terminal runs, 20 by default.
Maintenance, initial-revalidation, and legacy unmarked rows do not appear.
When the response is truncated or a run is older, **Open by run ID** calls the
authoritative detail endpoint directly.

Mutating starts and item actions are never replayed after an ambiguous
transport failure. The UI reports the outcome unknown and performs only
read-only list/detail reconciliation; an ambiguous start must be reviewed
before another submission. Item actions are atomically rechecked by the
processor, so a `409` triggers a complete run refresh.

The Dev UI retains the verified pair if publication fails and retries only the
publisher. Processor success, accepted receipts, and site-publication success
remain separate. Ignored run directories under
`cache\devui-authoring-snapshots\` and `cache\devui-review-sites\` have no
automatic retention cleanup; remove old workflow/run directories manually
only while the Dev UI is stopped. Review sites are served from the Dev UI
origin for trusted-local use, while snapshot pairs remain private.

The operations workspace stops at the Planner review site. It does not control
the Applier, mutate repositories, push commits, create pull requests, or
perform other GitHub writes.

## Headless equivalent

Invoke the [`orchestrate-plan`](../../.github/skills/orchestrate-plan/SKILL.md)
skill. It requests a frozen Planner run, polls authoritative state while the
processor applies its finite retry policy, downloads the verified snapshot
pair, and publishes the applying site. It does not issue manual retries by
default. An empty ticket list delegates selection to the processor's
configured filters.

The skill and commands below are the supported headless/manual equivalent of
the UI and use the same processor-owned state, verified-pair boundary, and
publisher.

### Manual commands

#### 1. Start and monitor the Planner run

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"status","runId":"<runId>"}'
```

For explicit tickets, populate `ticketKeys`. Continue polling while
`state.isTerminal` is false: `queued`, `running`, `finalizing`, and recoverable
`error`. Successful items are `complete` and carry an `acceptedReceiptId`.
Current errors report `currentError`, `attemptsRemaining`,
`nextAutomaticRetryAt`, and `allowedActions`; the scheduler retries them
automatically after the minimum delay. Use `retry` only for an explicit
immediate-retry decision while `allowedActions.canRetryNow` is true. It may
bypass the delay but cannot expand the configured total-attempt budget:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
```

An operator may explicitly supersede a known non-actionable current error only
while `allowedActions.canSupersede` is true, which requires the currently
fenced run and no accepted receipt:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"ticket no longer requires implementation"}'
```

Exact run status `completed` or `completed-database-only` is lifecycle success.
If items are `superseded`, the outcome is partial rather than a run failure:
surface every item and reason, then continue snapshot/site publication from the
accepted plans.

Grouping is a required fenced finalization stage. A completed snapshot run has
current grouping receipts for every affected partition; there is no separate
direct database topic-population step.

At the processor/Orchestrator HTTP surface, structured start conflicts preserve
`error` and `detail` while returning `conflictingRunIds` and, for a single
conflict, `runId`. The Dev UI renders those coordinates as links; the CLI
retains its existing error envelope. Outer start, retry, and supersede calls
are single-attempt: after an ambiguous transport failure, reconcile with
read-only list/detail calls and do not resubmit automatically.

#### 2. Download the canonical snapshot

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\planner\\<runId>\\"}'
```

The promoted directory contains the descriptor, database, and
`verified-pair.json`. The ready manifest binds `Planner`, the run and snapshot
IDs, safe filenames, size, and descriptor/database SHA-256 digests.

#### 3. Publish the applying site

```powershell
dotnet run --project tools\ticket-site -- `
  --planner-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --title "Tickets for Applying" `
  --force
```

Open `cache\jira-ticket-site\index.html` and choose **Tickets for Applying**.
The applying sub-site records its immutable inputs in
`applying\site-manifest.json`.

## Apply and push

The Applier is a separate processor. It consumes the Planner's compatibility
projection for genuinely completed plans, applies each plan in per-ticket
repository worktrees, and commits successful changes locally. There is no
`orchestrate-applier` skill and no per-ticket enqueue endpoint. None of these
actions are part of the Dev UI operations workspace.

Start the Applier and monitor its queue:

```powershell
curl -X POST http://localhost:5173/processing/start
curl http://localhost:5173/processing/queue
```

Push a ticket's successful local commits only when ready:

```powershell
curl -X POST http://localhost:5173/api/v1/applied-tickets/FHIR-12345/push
```

The push returns `200` with a per-repository result, `404` when no applied
record exists, and `409` when no repository has a successful local commit.

## Failure boundaries

- Planner authoring retries rotate credentials only before a receipt is
  accepted. They occur automatically until the total attempt limit; the final
  unpersisted failure becomes an item-level `superseded` outcome.
- Accepted plan receipts survive hydration, grouping, snapshot, and site
  failures and are never re-authored or superseded.
- Snapshot creation is processor-owned and resumable; site publication is a
  separate client-side step.
- The initial cutover revalidation preserves the Planner completion
  coordinates used by the Applier when content is unchanged. Changed accepted
  plans receive new completion coordinates and become eligible for applying.
- On upgraded startup, legacy stalled errors are reconciled and allowed to
  retry or exhaust; the active run finalizes and releases its fence before the
  oldest queued run activates.
- Scheduled Planner discovery does not reselect an exhausted unchanged Jira
  revision. A newer revision remains eligible; an intentional retry of the same
  revision requires an explicit new run.

## Reference

- [Processors runbook](../technical/processors.md)
- [`ticket-site` reference](../../tools/ticket-site/README.md)
- [Configuration reference](../configuration.md#processing-services)
- [Generating Discussion Tickets](generating-discussion-tickets.md)
- [Generating Ballot Notes](generating-ballot-notes.md)
