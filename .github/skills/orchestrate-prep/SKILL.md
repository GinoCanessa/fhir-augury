---
name: orchestrate-prep
description: "Controls processor-owned bulk ticket preparation runs. USE FOR: scheduled or explicit multi-ticket preparation, retrying failed run items, immutable Preparer snapshot download, and discussion-site publication. It never dispatches authoring agents itself or tracks completion in Jira or files. The processor owns selection, worker execution, receipts, fenced finalization, grouping, and snapshot creation; this skill starts the run, polls authoritative state, downloads the verified snapshot pair, and publishes only from that pair."
---

# Orchestrate Prep Skill

## Scope

This is an outer-control skill. Reject any invocation where
`FHIR_AUGURY_AUTHORING_WORKER` or another authoring callback variable is
present. The processor owns candidate scheduling and worker dispatch.

## Inputs

- **Ticket keys** *(optional)* — explicit Jira keys. Omit or pass an empty
  list to let the Preparer create its configured scheduled run.
- **Database only** *(optional, default `false`)* — suppress snapshot and site
  publication only when explicitly true.
- **Retry failed items** *(optional, default `true`)* — retry each current
  `error` item at most once unless the caller sets a different finite limit.
- **Snapshot directory** *(optional, default
  `cache\authoring-snapshots\preparer\<runId>\`)*.
- **Site output** *(optional, default `cache\jira-ticket-site`)*.
- **Site title** *(optional, default `Ticket Site`)*.

Do not accept candidate-selection filters, authored-content paths, processor
database paths, concurrency controls, or worker prompts. Those concerns are
owned by processor configuration.

## Workflow

### 1. Start

For explicit keys:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":["FHIR-100","FHIR-101"],"databaseOnly":false}'
```

For a scheduled run:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":[],"databaseOnly":false}'
```

`no-candidates` is a successful no-op. Otherwise capture the returned
`run.runId`, `authoringEpoch`, and all item IDs.

### 2. Poll authoritative state

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
```

Continue while the run is `queued`, `running`, or `finalizing`.

- An authored item is accepted only when it is `complete` with a non-empty
  `acceptedReceiptId`.
- A `superseded` item is terminal but not authored; surface it explicitly.
- If enabled, call action `retry` only for an item currently in `error`, and
  only within the configured finite retry limit.
- Never restart the whole run to retry one item.
- Run `error` or `superseded` is failure. Return the run ID, failed or
  superseded item IDs, current operation IDs, accepted receipt IDs, and the
  processor error.

Normal success is exact run status `completed`. Explicit storage-only success
is exact run status `completed-database-only`.

### 3. Download the canonical snapshot

Skip this step only for explicit storage-only runs.

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

The CLI downloads the descriptor and bytes, verifies `runId`, file name,
size, and SHA-256, then promotes both files atomically. Capture the returned
`snapshotId`, sequence, paths, and checksum. Never render from the live
processor store.

### 4. Publish the discussion site

```powershell
dotnet run --project tools\ticket-site -- --preparer-snapshot "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "<siteOutput>" --title "<siteTitle>" --force
```

Publication success does not alter processor state. If it fails, preserve the
receipts and snapshot pair, fail this outer command, and return:

- `runId`
- `snapshotId`
- all accepted receipt IDs
- snapshot and descriptor paths
- the publication error

Do not retry authoring or delete the snapshot because publication failed.

## Completion result

Return a concise structured result containing the run ID, terminal status,
authoring epoch, item totals, accepted receipt IDs, superseded item IDs,
snapshot ID and sequence when present, snapshot pair paths, and published
site path. Processor state is the only completion authority.
