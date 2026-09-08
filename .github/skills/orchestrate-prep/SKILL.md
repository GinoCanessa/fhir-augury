---
name: orchestrate-prep
description: "Controls processor-owned bulk ticket preparation runs. USE FOR: scheduled or explicit multi-ticket preparation, observing finite automatic retry, applying explicit item dispositions, immutable Preparer snapshot download, and discussion-site publication. It never dispatches authoring agents itself or tracks completion in Jira or files. The processor owns selection, worker execution, receipts, retry timing, fenced finalization, grouping, and snapshot creation; this skill starts the run, polls authoritative state, downloads the verified snapshot pair, and publishes only from that pair."
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
- **Immediate-retry item IDs** *(optional, default none)* — explicit current
  `error` items whose automatic delay should be bypassed. This cannot expand
  the processor's total attempt budget.
- **Supersede reasons** *(optional, default none)* — explicit map from item ID
  to a non-blank operator reason for a known non-actionable current error.
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
- A current `error` is processor-owned automatic retry state. Use
  `attemptsRemaining` and `nextAutomaticRetryAt`, and keep polling by default.
- Call action `retry` only for an explicitly listed immediate-retry item that
  is still in `error`. It may bypass the delay but cannot expand the budget.
- Call action `supersede` only for an item present in the explicit reason map
  that is still `error` and has no `acceptedReceiptId`:

  ```powershell
  fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
  ```

- Never infer non-actionability, invent a reason, supersede a receipt-backed
  item, or treat item-level supersession as run-level supersession.
- Never restart the whole run to retry one item.
- Run `error` or `superseded` is failure. Return the run ID, failed or
  superseded item IDs, current operation IDs, accepted receipt IDs, and the
  processor error.

Normal success is exact run status `completed`. Explicit storage-only success
is exact run status `completed-database-only`. Either is lifecycle success
when individual items are `superseded`: classify the result as partial
authoring, surface every item and reason, and continue snapshot/site
publication from accepted results.

For scheduled selection, an exhausted unchanged Jira revision is not selected
again automatically. A newer revision remains eligible; deliberately retrying
the unchanged revision requires an explicit new run.

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
authoring epoch, item totals, accepted receipt IDs, superseded item IDs and
reasons, snapshot ID and sequence when present, snapshot pair paths, and
published site path. Processor state is the only completion authority.
