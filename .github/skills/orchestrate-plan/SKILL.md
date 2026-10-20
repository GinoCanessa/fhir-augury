---
name: orchestrate-plan
description: "Controls processor-owned bulk implementation-planning runs. USE FOR: scheduled or explicit multi-ticket planner runs, observing finite automatic retry, applying explicit item dispositions, immutable Planner snapshot download, and applying-site publication. It never dispatches authoring agents itself or tracks completion in Jira or files. The processor owns selection, worker execution, receipts, retry timing, fenced finalization, grouping, and snapshot creation; this skill starts the run, polls authoritative state, downloads the verified snapshot pair, and publishes only from that pair."
---

# Orchestrate Plan Skill

## Scope

This is an outer-control skill. Refuse to run when any
`FHIR_AUGURY_AUTHORING_*` worker or callback variable is present. The Planner
processor owns candidate scheduling, worker execution, grouping, and
finalization.

A trusted local operator may instead use the Dev UI at
`/operations/plan/new`. It is an outer client of the same processor contracts
and shared publisher; this skill and the CLI remain the supported headless
fallback. The UI does not become a worker, receive callback tokens, control
Aspire resources or the Applier, mutate repositories, push commits, create
pull requests, or perform other GitHub writes.

## Inputs

- **Ticket keys** *(optional)* — explicit Jira keys. An empty list requests
  the processor's configured scheduled run.
- **Database only** *(optional, default `false`)*.
- **Immediate-retry item IDs** *(optional, default none)* — explicit current
  `error` items whose automatic delay should be bypassed without expanding the
  processor's total attempt budget.
- **Supersede reasons** *(optional, default none)* — explicit item-ID to
  non-blank reason map for known non-actionable current errors.
- **Snapshot directory** *(optional, default
  `cache\authoring-snapshots\planner\<runId>\`)*.
- **Site output** *(optional, default `cache\jira-ticket-site`)*.
- **Site title** *(optional, default `Ticket Site`)*.

Do not accept candidate-draw filters, processor database paths, local
authored-content destinations, concurrency controls, or worker prompts.

## Workflow

### 1. Start

Explicit selection:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":["FHIR-200","FHIR-201"],"databaseOnly":false}'
```

Processor scheduling:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
```

Treat `no-candidates` as a successful no-op. Otherwise retain the run ID,
authoring epoch, and every item coordinate.

At the processor/Orchestrator HTTP surface, a structured `409` supplies
`conflictingRunIds` (and the legacy-compatible single `runId`); the Dev UI
turns those coordinates into links. The CLI retains its existing error
envelope, so use the read-only authoring list for headless coordinate
reconciliation rather than parsing `detail`. If a lost response makes start
outcome unknown, do not replay. Reconcile through the ordinary run list and
exact reads, then require explicit operator review before any new start.

### 2. Poll and retry by item

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"status","runId":"<runId>"}'
```

Poll whenever `run.state.isTerminal` is false: `queued`, `running`,
`finalizing`, and recoverable `error`.

- Authored success for an item requires `status:"complete"` and a non-empty
  `acceptedReceiptId`.
- Surface `superseded` items as terminal, non-authored outcomes.
- Treat a current `error` as processor-owned automatic retry state. Inspect
  `attemptsRemaining` and `nextAutomaticRetryAt`, and keep polling by default.
- Use action `retry` only for an explicitly listed immediate-retry item still
  in `error` with `allowedActions.canRetryNow:true`; it bypasses waiting, not
  the total attempt limit.
- Use action `supersede` only for an explicitly mapped item that is still
  `error`, has no accepted receipt, and has
  `allowedActions.canSupersede:true`:

  ```powershell
  fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
  ```

- Never infer non-actionability, invent a reason, supersede a receipt-backed
  item, or equate item-level supersession with run-level supersession.
- Do not create a replacement run to retry a single item.
- Run `error` is recoverable and non-terminal; keep polling. A terminal run
  `superseded` fails with run ID, item IDs, operation IDs, receipt IDs, and
  processor error.

Capabilities can become stale. Refresh the full run after an action succeeds
or returns `409`. If retry/supersede transport fails after the mutation may
have arrived, report **outcome unknown**, reconcile only with a detail read,
and never replay it.

A normal run must finish as `completed`. A caller-selected storage-only run
must finish as `completed-database-only`. Either is lifecycle success when
items are `superseded`; report partial authoring, surface every item and reason,
and continue snapshot/site publication from accepted results.

Scheduled discovery does not reselect an exhausted unchanged Jira revision.
A newer revision remains eligible; an intentional retry of the unchanged
revision requires an explicit new run.

### 3. Download the canonical snapshot

For a normal run:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\planner\\<runId>\\"}'
```

The CLI validates service/workflow/run and snapshot identity, safe filenames,
size, and digests, then atomically promotes a pair directory. It writes
`verified-pair.json` last; that ready manifest binds `Planner`, the
coordinates, descriptor/database filenames, size, and both SHA-256 digests.
Capture the snapshot ID, sequence, checksum, and paths. Never publish from the
live Planner store.

### 4. Publish the applying site

```powershell
dotnet run --project tools\ticket-site -- --planner-snapshot "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "<siteOutput>" --title "<siteTitle>" --force
```

If publication fails, retain the accepted receipts, snapshot bytes, and
descriptor but fail this outer command. Return `runId`, `snapshotId`, all
accepted receipt IDs, both snapshot paths, and the publication error. Do not
rerun authoring to repair a publication failure.
Successful output contains `applying\site-manifest.json`. The Dev UI invokes
the same publisher in-process and serves its run site at
`/review-sites/plan/<runId>/applying/`; it does not extend into Applier or
GitHub-write work.

## Completion result

Return the terminal processor status, run ID, authoring epoch, item totals,
accepted receipt IDs, superseded item IDs and reasons, snapshot identity and
paths when present, and the published site path. No local artifact or Jira
flag is a completion signal.
