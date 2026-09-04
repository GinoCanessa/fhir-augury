---
name: notes-datatype
description: "Authors one consolidated ballot-note payload for the hydrated HL7 FHIR datatypes unit. USE FOR: source/datatypes changes, the shared datatypes page, and datatype own-page evidence folded into that unit. In outer-control mode it starts and monitors a processor-owned note run, downloads the immutable snapshot, and publishes the review site. In worker mode it independently observes CurrentEvidenceRevision, authors the after-applied datatype roll-up, submits typed prose through the callback, and requires the exact durable receipt."
---

# Notes — Datatype Skill

## Canonical execution contract

Choose exactly one mode.

### Outer-control mode

All `FHIR_AUGURY_AUTHORING_*` worker variables must be absent. Start a run
for the single hydrated datatypes unit:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":["<noteId>"],"databaseOnly":false}'
```

Poll action `status`, retry only a current `error` item, and require a
`complete` item with `acceptedReceiptId`. A normal run succeeds only at
`completed`; a deliberately storage-only run succeeds at
`completed-database-only`.

For a normal run, download the verified snapshot and descriptor:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

Publish the review site from that pair:

```powershell
dotnet run --project tools\notes-site -- report --snapshot-db "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "cache\notes-site" --force
```

Publication failure preserves the durable receipts and snapshot pair but
fails the outer command. Return the hydration execution ID, run ID, snapshot
ID, accepted receipt IDs, and publication error.

### Worker mode

Require the complete authoring worker environment plus
`FHIR_AUGURY_BALLOT_NOTE_ID`, `FHIR_AUGURY_BALLOT_NOTE_TYPE`, and
`FHIR_AUGURY_BALLOT_NOTES_EXECUTION_ID`. The note ID must match the argument,
the type must be `DataType`, and the hydrated unit name must be `datatypes`.

The sole persistence action is:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"submit","observedSourceRevision":"<current CurrentEvidenceRevision>","prose":{...BallotNoteProse...}}'
```

Never start another run, mutate prose directly, open the processor database,
or emit a separate authored artifact.

## Scope

The BallotNotes processor owns datatype routing. It folds all changed
`source/datatypes/**` definitions, the consolidated
`source/datatypes.html`, and touched own-pages into one `DataType` unit.
Examples of own-page routing include:

- `Reference` to `references`
- the metadata cluster (`ContactDetail`, `DataRequirement`, `Expression`,
  `ParameterDefinition`, `RelatedArtifact`, `TriggerDefinition`,
  `UsageContext`, `Contributor`) to `metadatatypes`
- datatype-specific pages such as `dosage`, `narrative`,
  `elementdefinition`, `marketingstatus`, and `productshelflife`

The worker does not recreate that map or split the unit.

## Worker evidence workflow

1. GET `{processorBaseUrl}/api/v1/ballot-notes/{noteId}`.
2. Require type `DataType`, name `datatypes`, the expected hydration
   execution, and a non-empty `CurrentEvidenceRevision`. Use that current
   value as the independently observed revision; never copy the expected
   environment revision. Require the observed and frozen revisions to match;
   otherwise fail without submitting.
3. Consume only hydrated source files, commits, tickets, structural changes,
   extension references, current note HTML, preserved hand-authored HTML,
   and prior prose.
4. Bucket StructureDefinition and supporting evidence by datatype; keep
   page-level and cross-cutting evidence in separate in-memory buckets.
5. Author per-datatype net changes, then reconcile them into one
   after-applied datatypes-surface summary and one ballot-note block.
6. GET the unit again immediately before submit. Its
   `CurrentEvidenceRevision` must equal both the first observation and the
   frozen environment revision. If it changed, fail without submitting.
7. Submit once; transport replay must preserve the exact operation and
   content.

## Datatype prose guidance

The typed payload is:

```json
{
  "needsNote": "yes",
  "proposedBallotNoteHtml": "...",
  "rollupSummaryMarkdown": "...",
  "notesForReviewerMarkdown": "...",
  "sourceFilesNote": "..."
}
```

### Per-datatype and consolidated roll-up

- For each datatype, describe differential element additions, removals,
  cardinality, type, binding, constraint, modifier, summary, and
  must-support changes.
- Treat snapshots as derived. State when regeneration is needed without
  enumerating snapshot deltas.
- Cover examples, terminology siblings, diagrams, and spreadsheets only to
  the extent they change balloter understanding.
- Identify cross-datatype patterns and reconcile overlapping or superseding
  tickets into the net after-applied state.
- Keep page-level narrative and shared terminology changes visible.
- Preserve prior statements only when still accurate; explain removed or
  superseded statements to reviewers.

### Proposed ballot-note HTML

- Produce exactly one consolidated
  `<blockquote class="ballot-note" data-augury-generated="true" id="...">`
  block for the whole datatype surface.
- Preserve the prior generated ID when revising and carry
  `preservedHandAuthoredHtml` verbatim.
- Begin with `Changes since {windowLabel}` when present; otherwise state the
  short-SHA window.
- Use well-formed HTML only.
- Organize meaningful changes by datatype or cross-cutting theme while
  grouping impact sections in the exact order `Non-compatible`,
  `Compatible substantive`, `Non-substantive`, `Unclassified`.
- Use the ticket-provided `changeImpact`; do not infer one. Render
  `changeCategory` as an inline tag.
- Put change text first and bracketed Jira links last on every attributed
  line. Put unattributed changes in `Unattributed (needs Jira)`.
- Add structural badges and extension-to-core replacement statements only
  when supplied by processor evidence.
- Avoid mechanical XML narration when the resulting datatype semantics can
  be stated directly.

Set `needsNote` to `yes`, `no`, or `unknown` based on the consolidated net
effect.

## Exact receipt gate

Require the receipt to match exactly:

- worker `runId`, `itemId`, and `operationId`
- `businessKey` = note ID
- `expectedSourceRevision` = environment source revision
- `observedSourceRevision` = independently observed current
  `CurrentEvidenceRevision`
- `contentHash` = canonical hash of the submitted prose

Also require non-empty `receiptId` and `persistedAt`, plus a present
non-negative `authoringEpoch`. Accept `isReplay:true` only for the identical
operation and prose. Any mismatch fails.

## Non-negotiable rules

- The processor owns hydration, datatype routing, persistence, fencing,
  snapshots, and publication inputs.
- Never expose the operation token.
- Never substitute the expected revision for a current observation.
- Never claim success without the exact durable receipt.
