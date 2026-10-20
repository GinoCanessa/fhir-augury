# Generating Ballot Notes

This guide hydrates one repository commit window, runs processor-owned note
authoring, downloads the canonical BallotNotes snapshot, and publishes the
static review site. Hydration and authoring are separate durable lifecycles;
site publication never writes to the processor database.

## What you'll produce

- One immutable hydration execution for the selected repository window.
- A completed BallotNotes authoring run with accepted receipts.
- A verified snapshot pair under
  `cache\authoring-snapshots\ballot-notes\<runId>\`.
- A self-contained site under `cache\notes-site`.

## Prerequisites

- `source-jira` (`:5160`), `source-github` (`:5190`), and the Orchestrator
  (`:5150`) are healthy.
- The target repository clone exists at
  `cache\github\repos\<owner>_<name>\clone`.
- `processor-github-fhir-ballotnotes` (`:5174`) is started.
- `fhir-augury-cli` is installed, or use the local CLI project.

## Recommended flow

Invoke the [`orchestrate-notes`](../../.github/skills/orchestrate-notes/SKILL.md)
skill with `owner/name` and `sinceSha`. It creates exactly one hydration
execution, selects units from that execution, starts processor-owned
authoring, observes processor-owned finite automatic retry, downloads the
snapshot pair, and publishes the site. It issues an immediate retry or early
supersession only when explicitly directed.

## Manual equivalent

### 1. Hydrate the repository window

```powershell
curl -X POST http://localhost:5174/api/v1/ballot-notes/hydrate `
  -H "Content-Type: application/json" `
  -d '{"repoOwner":"HL7","repoName":"fhir","sinceSha":"<sha>","windowLabel":"R6 ballot"}'
```

Require `202 Accepted` and retain both `executionId` and `runKey`. Poll that
exact execution:

```powershell
curl "http://localhost:5174/api/v1/ballot-notes/hydrate/status?executionId=<executionId>"
```

Proceed only when `status` is `completed`.

### 2. Start processor-owned authoring

Use all completed units from the captured execution, or pass an explicit
subset of `noteIds`:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":[],"databaseOnly":false}'
```

The processor routes artifact, page, and datatype items to the matching
worker skill and supplies callback credentials through the worker environment.
There is no bare prose-write endpoint for outer callers.

### 3. Poll and retry by item

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"status","runId":"<runId>"}'
```

Continue through `queued`, `running`, and `finalizing`. Success requires
`completed`; each authored item must be `complete` with an
`acceptedReceiptId`. An `error` reports `attemptsRemaining` and
`nextAutomaticRetryAt`, and the scheduler retries it automatically after the
minimum delay. Use `retry` only for an explicit immediate retry; it cannot
increase the configured total attempt budget:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"retry","runId":"<runId>","itemId":"<itemId>"}'
```

Only an explicitly identified non-actionable current error without a receipt
may be superseded:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"duplicate note unit"}'
```

`completed` or explicit storage-only `completed-database-only` is lifecycle
success even when superseded items make the authoring result partial. Surface
their IDs and reasons, then continue snapshot/site publication from accepted
receipts.

### 4. Download the canonical snapshot

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

### 5. Publish the site

```powershell
dotnet run --project tools\notes-site -- report `
  --snapshot-db "<snapshotPath>" `
  --snapshot-descriptor "<descriptorPath>" `
  --out cache\notes-site `
  --title "FHIR Ballot Notes" `
  --force
```

Open `cache\notes-site\index.html`.

## Maintenance and failures

- Workgroup reallocation reads evidence locally but submits one
  expected-revision batch to the BallotNotes processor. Generate a new
  authoring snapshot before republishing the site.
- A missing clone or invalid `sinceSha` fails hydration before authoring.
- An accepted receipt remains valid if later finalization or publication
  fails and can never be re-authored or superseded. Do not repeat hydration or
  authoring solely because `notes-site` failed.
- Automatic retries stop at the total attempt limit. The final unpersisted
  failure becomes item-level `superseded`, after which the run finalizes
  normally and releases its fence before the oldest queued run starts.
- On upgraded startup, legacy `error` rows with open attempts are reconciled
  into this same retry/exhaustion path without touching accepted receipts.
- The first run-backed startup classifies pre-cutover prose as
  `legacy-unverified` and creates one baseline hydration execution for
  revalidation. No canonical snapshot is available until real receipts clear
  that gate.

## Reference

- [Processors runbook](../technical/processors.md)
- [`notes-site` reference](../../tools/notes-site/README.md)
- [`ballotnotes-reallocate-wg` reference](../../tools/ballotnotes-reallocate-wg/README.md)
- [BallotNotes configuration](../configuration.md#ballotnotes-processor-service)
- [Generating Discussion Tickets](generating-discussion-tickets.md)
- [Generating Application Tickets](generating-application-tickets.md)
