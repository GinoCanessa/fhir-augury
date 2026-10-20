---
name: notes-page
description: "Authors ballot-note prose for one hydrated FHIR narrative page unit. USE FOR: core specification pages and IG pagecontent ballot notes. In outer-control mode it starts and monitors a processor-owned note run, downloads the immutable snapshot, and publishes the review site. In worker mode it reads processor-owned page evidence, independently observes CurrentEvidenceRevision, produces one typed BallotNoteProse payload from the after-applied page state, submits through the callback, and requires the exact durable receipt."
---

# Notes — Page Skill

## Canonical execution contract

Choose exactly one mode.

### Outer-control mode

Require every `FHIR_AUGURY_AUTHORING_*` worker variable to be absent. Start
one run for the hydrated page unit:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"start","hydrationExecutionId":"<executionId>","noteIds":["<noteId>"],"databaseOnly":false}'
```

Capture the run and item coordinates, poll action `status`, and retry only an
item currently in `error`. An authored item must be `complete` with a
non-empty `acceptedReceiptId`. A normal run succeeds only at `completed`;
`completed-database-only` is valid only when explicitly requested.

Download the verified snapshot pair for a normal run:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\ballot-notes\\<runId>\\"}'
```

Publish from the snapshot and descriptor:

```powershell
dotnet run --project tools\notes-site -- report --snapshot-db "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "cache\notes-site" --force
```

If publication fails, preserve receipts and the snapshot pair but fail the
outer command. Return the hydration execution ID, run ID, snapshot ID,
accepted receipt IDs, and publication error.

### Worker mode

Require `FHIR_AUGURY_AUTHORING_WORKER=1`, all six callback variables, and the
BallotNotes note ID, type, and hydration execution variables. The note ID must
match the argument and the type must be `Page`. Partial context is an error.

Submit exactly one typed payload:

```powershell
fhir-augury-cli --json '{"command":"ballot-note-authoring","action":"submit","observedSourceRevision":"<current CurrentEvidenceRevision>","prose":{...BallotNoteProse...}}'
```

Do not start a nested run, call a direct prose mutation endpoint, open the
processor database, or write a separate authored artifact.

## Worker evidence workflow

1. GET `{processorBaseUrl}/api/v1/ballot-notes/{noteId}`.
2. Require type `Page`, the expected hydration execution, and a non-empty
   `CurrentEvidenceRevision`. This current value is the independent
   `observedSourceRevision`; the environment value remains only the expected
   receipt coordinate. Require the two revisions to match; otherwise fail
   without submitting.
3. Consume only the hydrated page evidence: source files, commits, tickets,
   structural changes, extension references, current note HTML, preserved
   hand-authored HTML, and prior prose. Do not rerun git or Jira discovery.
4. Author the net after-applied page state in memory.
5. GET the unit again immediately before submit. Its
   `CurrentEvidenceRevision` must equal both the first observation and the
   frozen environment revision. If it changed, fail without submitting.
6. Submit once; any transport replay must be byte-equivalent.

## Page prose guidance

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

### Roll-up

- Summarize the net effect by page section, not by concatenating ticket text.
- Call out added, removed, or reorganized headings; material scope and
  boundary changes; conformance-language changes; deprecations; examples;
  diagrams; and changed cross-page links.
- Treat typo, whitespace, and link-normalization churn as editorial and do
  not let it drive a ballot bullet.
- For a removed page, identify the replacement or redirect only when the
  hydrated evidence supports it.
- Reconcile prior ballot-note statements against the current after-applied
  state.

### Proposed ballot-note HTML

- Produce exactly one marked block. For core HTML pages use
  `<blockquote class="ballot-note" data-augury-generated="true" id="...">`.
  For an IG page use its established block convention while retaining
  `data-augury-generated="true"`.
- Preserve the current ID when revising and preserve all
  `preservedHandAuthoredHtml` verbatim.
- Begin with `Changes since {windowLabel}` when available; otherwise name the
  short-SHA window.
- Use well-formed HTML only in `proposedBallotNoteHtml`.
- Put change text first and end every attributed line with bracketed Jira
  links.
- Use this exact impact order: `Non-compatible`, `Compatible substantive`,
  `Non-substantive`, `Unclassified`; never infer a missing classification.
- Render any `changeCategory` as an inline tag.
- Put unsupported attribution in `Unattributed (needs Jira)` rather than
  hiding it.
- Add structural badges and extension-to-core references only from the
  corresponding processor evidence.
- Be specific about the affected section and balloter-visible consequence.

Set `needsNote` to `yes`, `no`, or `unknown`. Empty prose with `no` is
appropriate only for a materially empty or editorial-only window.

## Exact receipt gate

Success requires an exact match for:

- environment `runId`, `itemId`, and `operationId`
- `businessKey` = note ID
- `expectedSourceRevision` = environment source revision
- `observedSourceRevision` = independently observed current
  `CurrentEvidenceRevision`
- `contentHash` = canonical hash of the submitted prose

Require non-empty `receiptId` and `persistedAt`, plus a present non-negative
`authoringEpoch`. `isReplay:true` is acceptable only for the identical
operation and content. Any mismatch fails.

## Non-negotiable rules

- The processor owns hydration, page routing, persistence, fencing,
  snapshots, and publication inputs.
- Never expose the token.
- Never use the expected revision as the observation.
- Never claim success without the exact durable receipt.
