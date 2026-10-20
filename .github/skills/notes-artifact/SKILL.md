---
name: notes-artifact
description: "Authors ballot-note prose for one hydrated FHIR artifact unit. USE FOR: resource, profile, terminology, and IG-artifact ballot notes. In outer-control mode it starts and monitors a processor-owned note run, downloads the immutable snapshot, and publishes the review site. In worker mode it reads processor-owned evidence, independently observes CurrentEvidenceRevision, produces one typed BallotNoteProse payload from the after-applied state, submits through the callback, and requires the exact durable receipt."
---

# Notes — Artifact Skill

## Canonical execution contract

Choose exactly one mode.

### Outer-control mode

All `FHIR_AUGURY_AUTHORING_*` worker variables must be absent. Start one
processor-owned run for the hydrated note ID:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":["<noteId>"],"databaseOnly":false}'
```

Capture `run.runId`; poll action `status`; retry only a current `error` item.
Success requires the item to be `complete` with `acceptedReceiptId` and the
run to be `completed`. `completed-database-only` is valid only when explicitly
requested. `error` or `superseded` fails with the hydration execution ID, run
ID, item ID, operation ID, and receipt ID.

For a normal run, download the verified snapshot pair:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

Publish only from that immutable pair:

```powershell
dotnet run --project tools\notes-site -- report --snapshot-db "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "cache\notes-site" --force
```

If publication fails, retain all receipts, snapshot bytes, and the descriptor,
but fail the outer command. Return `hydrationExecutionId`, `runId`,
`snapshotId`, every `acceptedReceiptId`, and the publication error.

### Worker mode

Require `FHIR_AUGURY_AUTHORING_WORKER=1` and complete run, item, callback,
operation, token, and source-revision values. Also require
`FHIR_AUGURY_BALLOT_NOTE_ID`, `FHIR_AUGURY_BALLOT_NOTE_TYPE`, and
`FHIR_AUGURY_BALLOT_NOTES_EXECUTION_ID`; the note ID must match the skill
argument and the type must be `Artifact`. Reject partial or mixed context.

The worker performs one persistence action:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"submit","observedSourceRevision":"<current CurrentEvidenceRevision>","prose":{...BallotNoteProse...}}'
```

Never start another run, issue a direct prose write, open the processor
database, or emit a separate authored artifact.

## Worker evidence workflow

1. Read the hydrated unit:

   ```text
   GET {processorBaseUrl}/api/v1/ballot-notes/{noteId}
   ```

   The processor base URL is derived from the callback origin or the supplied
   processor setting. Require type `Artifact`, the expected hydration
   execution, and a non-empty `CurrentEvidenceRevision`. This GET is the
   worker's independent source-revision observation; do not use
   `FHIR_AUGURY_AUTHORING_SOURCE_REVISION` as the observed value. Require the
   independently observed value to equal that frozen revision; otherwise
   fail without submitting.

2. Use only processor-owned evidence: identity and window fields,
   `sourceFiles`, `commits`, `tickets`, `structuralChanges`,
   `extensionRefs`, current note HTML, hand-authored HTML to preserve, and any
   prior authored prose. Do not run git or independently query Jira.

3. Derive the net after-applied state across the whole commit window. Ticket
   narratives are supporting evidence, not the roll-up itself. Reconcile
   overlapping, superseding, and reverted changes.

4. Build the typed prose in memory.

5. Immediately before submit, GET the unit again. Its
   `CurrentEvidenceRevision` must equal both the first observation and the
   frozen environment revision. If it changed, fail without submitting so
   the processor can supersede or replace the stale item.

6. Submit once. Transport retries must replay the same operation, prose,
   content hash, and observed revision.

## Artifact prose guidance

The typed payload has exactly these fields:

```json
{
  "needsNote": "yes",
  "proposedBallotNoteHtml": "...",
  "rollupSummaryMarkdown": "...",
  "notesForReviewerMarkdown": "...",
  "sourceFilesNote": "..."
}
```

### Roll-up

- Narrate StructureDefinition changes from the differential; snapshots are
  derived, so mention regeneration when relevant without enumerating snapshot
  edits.
- Cover material intro, scope, boundary, search-parameter, operation,
  example, and terminology changes.
- Identify possible UTG ownership for terminology changes.
- Preserve useful prior prose only when it still matches the after-applied
  state; explain dropped or superseded statements to reviewers.

### Proposed ballot-note HTML

- Produce exactly one consolidated tool-authored block:
  `<blockquote class="ballot-note" data-augury-generated="true" id="...">`.
  Preserve the existing ID when revising; otherwise choose the next free
  `bn<N>`.
- Preserve `preservedHandAuthoredHtml` verbatim and never rewrite it.
- Open with `Changes since {windowLabel}` when present; otherwise identify
  the short-SHA window.
- Use well-formed HTML only. Put change text first and finish each attributed
  line with bracketed Jira links.
- Group entries in this order: `Non-compatible`, `Compatible substantive`,
  `Non-substantive`, `Unclassified`. Use the ticket's supplied
  `changeImpact`; do not reclassify it. Omit empty groups.
- Render `changeCategory` as a small inline tag when present.
- Every called-out change needs a Jira key. Put genuinely unattributed
  changes in a final `Unattributed (needs Jira)` group.
- Add only processor-supplied structural badges and extension-to-core
  replacement references.
- Focus on balloter-visible intent and impact rather than mechanical XML
  churn.

Set `needsNote` to `yes`, `no`, or `unknown` from the net materiality.
Use empty prose with `needsNote:"no"` only when the evidence shows no
balloter-relevant change.

## Exact receipt gate

Require the returned receipt to match exactly:

- `runId`, `itemId`, and `operationId` from the worker environment
- `businessKey` = the note ID
- `expectedSourceRevision` =
  `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`
- `observedSourceRevision` = the independently observed current
  `CurrentEvidenceRevision`
- `contentHash` = the canonical hash of the submitted prose

Also require non-empty `receiptId` and `persistedAt`, plus a present
non-negative `authoringEpoch`. `isReplay:true` is valid only for the identical
operation and prose. Any mismatch is failure.

## Non-negotiable rules

- The processor owns hydration, unit routing, persistence, fencing,
  snapshots, and publication inputs.
- Never leak the operation token.
- Never substitute the expected revision for a fresh processor observation.
- Never claim success without the exact receipt.
