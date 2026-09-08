# Generating Application Tickets

This guide produces a static **Tickets for Applying** site from a
processor-owned Planner run, then describes the separate Applier flow. The
Planner owns worker dispatch, accepted receipts, hydration, topic grouping,
and snapshot creation. The site is built only from the downloaded immutable
snapshot pair.

## What you'll produce

- A completed Planner run and accepted plan receipts.
- A verified Planner snapshot pair under
  `cache\authoring-snapshots\planner\<runId>\`.
- A self-contained applying site under `cache\jira-ticket-site\applying\`.
- Optionally, local commits produced by the Applier and pushed on demand.

## Prerequisites

- `source-jira` (`:5160`), `source-github` (`:5190`), and the Orchestrator
  (`:5150`) are healthy.
- `processor-jira-fhir-planner` (`:5172`) is started.
- Start `processor-jira-fhir-applier` (`:5173`) only when you are ready to
  consume completed plans.
- `fhir-augury-cli` is installed, or use the local CLI project.

## Recommended flow

Invoke the [`orchestrate-plan`](../../.github/skills/orchestrate-plan/SKILL.md)
skill. It requests a frozen Planner run, polls authoritative state while the
processor applies its finite retry policy, downloads the verified snapshot
pair, and publishes the applying site. It does not issue manual retries by
default. An empty ticket list delegates selection to the processor's
configured filters.

## Manual equivalent

### 1. Start and monitor the Planner run

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"status","runId":"<runId>"}'
```

For explicit tickets, populate `ticketKeys`. Continue polling while the run is
`queued`, `running`, or `finalizing`. Successful items are `complete` and carry
an `acceptedReceiptId`. Current errors report `attemptsRemaining` and
`nextAutomaticRetryAt`; the scheduler retries them automatically after the
minimum delay. Use `retry` only for an explicit immediate-retry decision. It may
bypass the delay but cannot expand the configured total-attempt budget:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
```

An operator may explicitly supersede a known non-actionable current error only
when it has no accepted receipt:

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

### 2. Download the canonical snapshot

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\planner\\<runId>\\"}'
```

### 3. Publish the applying site

```powershell
dotnet run --project tools\ticket-site -- `
  --planner-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --title "Tickets for Applying" `
  --force
```

Open `cache\jira-ticket-site\index.html` and choose **Tickets for Applying**.

## Apply and push

The Applier is a separate processor. It consumes the Planner's compatibility
projection for genuinely completed plans, applies each plan in per-ticket
repository worktrees, and commits successful changes locally. There is no
`orchestrate-applier` skill and no per-ticket enqueue endpoint.

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
