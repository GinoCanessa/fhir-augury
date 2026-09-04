---
name: orchestrate-notes
description: "Controls BallotNotes hydration and processor-owned bulk authoring for a repository commit window. USE FOR: hydrating a repo window, selecting hydrated note units, starting and monitoring the authoring run, retrying failed items, downloading the immutable BallotNotes snapshot, and publishing the review site. The processor owns commit walking, unit routing, worker execution, receipts, fenced persistence, and snapshot creation; this skill performs outer control only."
---

# Orchestrate Notes Skill

## Scope

This is an outer-control skill. Reject any authoring worker or callback
environment. It never dispatches note workers itself. The BallotNotes
processor owns hydration, unit selection records, worker execution, receipts,
finalization, and snapshots.

## Inputs

- **Repository** *(required)* — `owner/name`.
- **Since commit** *(required)*.
- **Window label**, **repository category**, and **workgroup hint**
  *(optional hydration metadata)*.
- **Note IDs** *(optional)* — explicit hydrated unit IDs. When omitted, use
  all completed units from the new hydration execution.
- **Unit-name or unit-type filter** *(optional)* — resolve to note IDs after
  hydration, before starting authoring.
- **Database only** *(optional, default `false`)*.
- **Retry failed items** *(optional, default one retry per current error)*.
- **Snapshot directory** *(optional, default
  `cache\authoring-snapshots\ballot-notes\<runId>\`)*.
- **Site output** *(optional, default `cache\notes-site`)*.
- **Site title** *(optional, default `FHIR Ballot Notes`)*.
- **Processor base URL** *(optional, default `http://localhost:5174`)*.

Do not accept worker concurrency, worker prompts, authored-content
destinations, or a processor database path.

## Workflow

### 1. Hydrate exactly once

```text
POST {processorBaseUrl}/api/v1/ballot-notes/hydrate
Content-Type: application/json

{
  "repoOwner": "<owner>",
  "repoName": "<name>",
  "sinceSha": "<sinceCommit>",
  "windowLabel": "<optional>",
  "repoCategory": "<optional>",
  "workGroupHint": "<optional>"
}
```

Require `202 Accepted`; capture both `executionId` and `runKey`. A mutation
fence conflict, invalid commit, missing clone, or unavailable processor is a
hard failure. Do not substitute an older hydration execution.

Poll:

```text
GET {processorBaseUrl}/api/v1/ballot-notes/hydrate/status?executionId={executionId}
```

Proceed only when that exact execution reaches `completed`. On `failed`,
return `executionId`, `runKey`, and the processor error.

### 2. Resolve the immutable work set

If explicit note IDs were not supplied, enumerate the units belonging to the
completed repository window and select only rows whose
`currentHydrationExecutionId` equals the captured execution ID. Apply any
requested name or type filter in memory. Preserve each unit's `noteId`,
`type`, and `CurrentEvidenceRevision`; do not recompute artifact, page, or
datatype routing.

If the selected set is empty, return a successful no-op with the hydration
execution ID.

### 3. Start processor-owned authoring

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":["<noteId-1>","<noteId-2>"],"databaseOnly":false}'
```

Capture the authoring `runId`, authoring epoch, and all item coordinates.

### 4. Poll and retry

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"status","runId":"<runId>"}'
```

- Continue while `queued`, `running`, or `finalizing`.
- An authored item must be `complete` with `acceptedReceiptId`.
- Retry only a current `error` item, within the configured finite limit.
- Surface `superseded` items explicitly.
- Run `error` or `superseded` fails with hydration execution ID, run ID,
  item IDs, operation IDs, receipt IDs, and processor error.
- Normal success requires `completed`; explicit storage-only success requires
  `completed-database-only`.

### 5. Download the canonical snapshot

Skip only for an explicit storage-only run.

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

The CLI verifies and atomically promotes the descriptor and snapshot bytes.
Capture `snapshotId`, sequence, SHA-256, size, and both paths.

### 6. Publish the review site

```powershell
dotnet run --project tools\notes-site -- report --snapshot-db "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "<siteOutput>" --title "<siteTitle>" --force
```

Publication reads only the immutable pair. If it fails, preserve all accepted
receipts, snapshot bytes, and descriptor, but fail the outer command. Return:

- hydration `executionId` and `runKey`
- authoring `runId`
- `snapshotId`
- all accepted receipt IDs
- snapshot and descriptor paths
- publication error

Never rerun hydration or authoring merely because publication failed.

## Completion result

Return the hydration execution identity, selected note IDs, authoring run ID
and terminal status, accepted receipt IDs, superseded items, snapshot
identity and paths, and published site path. Processor run state is the only
authoring completion authority.
