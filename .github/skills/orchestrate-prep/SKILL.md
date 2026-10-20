---
name: orchestrate-prep
description: "Controls processor-owned bulk ticket preparation runs. USE FOR: scheduled or explicit multi-ticket preparation, observing finite automatic retry, applying explicit item dispositions, immutable Preparer snapshot download, and discussion-site publication. It never dispatches authoring agents itself or tracks completion in Jira or files. The processor owns selection, worker execution, receipts, retry timing, fenced finalization, grouping, and snapshot creation; this skill starts the run, polls authoritative state, downloads the verified snapshot pair, and publishes only from that pair."
---

# Orchestrate Prep Skill

## Scope

This is an outer-control skill. Reject any invocation where
`FHIR_AUGURY_AUTHORING_WORKER` or another authoring callback variable is
present. The processor owns candidate scheduling and worker dispatch.

A trusted local operator may use the Dev UI at
`/operations/prepare/new` instead. The UI is an alternative outer client of
the same processor-owned contracts and shared publisher; this skill and the
CLI workflow below remain the supported headless fallback. The UI does not
become a worker, receive callback tokens, start/stop Aspire resources, mutate
repositories, push commits, create pull requests, or perform other GitHub
writes.

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

At the processor/Orchestrator HTTP surface, a structured conflict preserves
`error`/`detail` and may provide `conflictingRunIds` plus a legacy-compatible
`runId`; the Dev UI turns those coordinates into links. The CLI retains its
existing error envelope, so use the read-only authoring list for headless
coordinate reconciliation rather than parsing prose. If transport loss makes
start outcome unknown, never replay the start. Reconcile with the ordinary
active/recent list and exact run reads, then require an explicit operator
decision before another start.

### 2. Poll authoritative state

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"status","runId":"<runId>"}'
```

Continue whenever `run.state.isTerminal` is false: `queued`, `running`,
`finalizing`, and recoverable `error`.

- An authored item is accepted only when it is `complete` with a non-empty
  `acceptedReceiptId`.
- A `superseded` item is terminal but not authored; surface it explicitly.
- A current `error` is processor-owned automatic retry state. Use
  `attemptsRemaining` and `nextAutomaticRetryAt`, and keep polling by default.
- Call action `retry` only for an explicitly listed immediate-retry item that
  is still in `error` and has `allowedActions.canRetryNow:true`. It may bypass
  the delay but cannot expand the budget.
- Call action `supersede` only for an item present in the explicit reason map
  that is still `error`, has no `acceptedReceiptId`, and has
  `allowedActions.canSupersede:true`:

  ```powershell
  fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"supersede","runId":"<runId>","itemId":"<itemId>","reason":"<explicit reason>"}'
  ```

- Never infer non-actionability, invent a reason, supersede a receipt-backed
  item, or treat item-level supersession as run-level supersession.
- Never restart the whole run to retry one item.
- Run `error` is non-terminal and recoverable; keep polling. A terminal run
  `superseded` is failure. Return the run ID, failed or superseded item IDs,
  current operation IDs, accepted receipt IDs, and the processor error.

The advertised actions can become stale. After success or `409`, read the
complete run again. If an immediate retry or supersede response is lost, report
**outcome unknown**, perform only a detail read, and never replay the mutation.

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

The CLI downloads the descriptor and bytes, verifies service/workflow/run and
snapshot identity, safe filenames, size, and SHA-256, then promotes one durable
pair directory. Its `verified-pair.json` ready manifest is written last and
binds `Preparer`, the coordinates, filenames, size, and both digests. Capture
the returned `snapshotId`, sequence, paths, and checksum. Never render from the
live processor store.

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
Successful output contains `discussion\site-manifest.json`. The Dev UI uses
the same publisher in-process and places its site at
`/review-sites/prepare/<runId>/discussion/`; processor success and publication
success remain separate.

## Completion result

Return a concise structured result containing the run ID, terminal status,
authoring epoch, item totals, accepted receipt IDs, superseded item IDs and
reasons, snapshot ID and sequence when present, snapshot pair paths, and
published site path. Processor state is the only completion authority.
