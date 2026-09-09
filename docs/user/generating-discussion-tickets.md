# Generating Discussion Tickets

This guide produces a static **Tickets for Discussion** review site from a
processor-owned Preparer run. The Preparer owns ticket selection, authoring
workers, result receipts, hydration, topic grouping, and snapshot creation.
`ticket-site` reads only the immutable snapshot and descriptor downloaded after
the run completes.

## What you'll produce

- A completed Preparer authoring run with one durable receipt per accepted
  ticket.
- A verified snapshot pair under
  `cache\authoring-snapshots\preparer\<runId>\`.
- A self-contained discussion site under
  `cache\jira-ticket-site\discussion\`.

The live processor database is not a site input.

## Prerequisites

- `source-jira` (`:5160`) and the Orchestrator (`:5150`) are healthy.
- `processor-jira-fhir-preparer` (`:5171`) is started. Under Aspire it uses
  explicit start.
- `fhir-augury-cli` is installed, or use
  `dotnet run --project src\FhirAugury.Cli --` in place of the executable.

```powershell
fhir-augury-cli --json '{"command":"services","action":"status"}'
curl http://localhost:5171/health
```

## Recommended flow

Invoke the [`orchestrate-prep`](../../.github/skills/orchestrate-prep/SKILL.md)
skill. With no ticket list it requests the processor's configured scheduled
selection; with ticket keys it requests an explicit frozen run. The skill
polls processor state while the shared scheduler applies bounded automatic
retry, downloads the trusted snapshot pair, and publishes the site. Immediate
retry or early item supersession occurs only when explicitly requested.

Set `databaseOnly` only when the caller explicitly wants structured processor
state without a snapshot or site.

## Manual equivalent

### 1. Start the authoring run

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
capacity. A second start returns HTTP `409` with
`active-run-capacity-reached` and identifies the existing run instead of
silently queuing another. Start the next run only after the current run reaches
`completed`, `completed-database-only`, or `superseded`.

The processor stores the normalized start request (the effective ticket keys
and `databaseOnly` value) with the frozen run. After a service restart it
continues that same run ID and request; operators should poll the existing run
rather than submit a replacement start.

### 2. Poll authoritative state

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
```

Continue while the run is `queued`, `running`, or `finalizing`. An authored
item is successful only when it is `complete` and has an
`acceptedReceiptId`. For an `error`, inspect `attemptsRemaining` and
`nextAutomaticRetryAt`; the processor retries automatically after the
configured minimum delay. Do not issue manual retries by default. An explicit
immediate retry may bypass that delay, but cannot increase the total attempt
budget:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
```

Only when an operator has determined that a current, non-receipt-backed error
is non-actionable may it be closed with an explicit reason:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"duplicate request"}'
```

Normal success is `completed`. `completed-database-only` is valid only for a
run that was explicitly started with `databaseOnly:true`. Either status is
lifecycle success even when item-level `superseded` outcomes make the result
partial. Continue snapshot and site publication from accepted results, and
surface every superseded item and reason.

Topic grouping is part of fenced finalization. Do not run a separate direct
database grouping pass. The grouping maintenance endpoint exists only for an
intentional later refresh and still runs as a processor-owned fenced operation.

### 3. Download the canonical snapshot

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

The CLI verifies the descriptor, file name, size, run identity, and SHA-256
before atomically promoting the pair.

### 4. Publish the site

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\jira-ticket-site `
  --title "Tickets for Discussion" `
  --force
```

Open `cache\jira-ticket-site\index.html` and choose **Tickets for Discussion**.

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
  only `ticket-site`.
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
