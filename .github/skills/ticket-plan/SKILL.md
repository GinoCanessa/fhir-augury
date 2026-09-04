---
name: ticket-plan
description: "Authors the typed implementation-plan payload for a resolved FHIR Jira ticket. USE FOR: implementation planning, impact analysis, and single-ticket planner runs. In outer-control mode it starts and monitors a processor-owned run, then downloads the immutable snapshot and publishes the applying site. In worker mode it independently observes Jira updatedAt, grounds the plan in current repository briefings and source evidence, submits one PlannedTicketPayload through the processor callback, and requires the exact durable receipt."
---

# Ticket Plan Skill

## Canonical execution contract

Choose exactly one mode.

### Outer-control mode

Outer-control mode requires every `FHIR_AUGURY_AUTHORING_*` worker variable
to be absent. Start one processor-owned run:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"start","ticketKeys":["FHIR-55197"],"databaseOnly":false}'
```

Capture `run.runId`, poll action `status`, and judge progress only from the
returned run and item records:

- Every authored item must reach `complete` with a non-empty
  `acceptedReceiptId`.
- Use action `retry` only for a current `error` item.
- `error` and `superseded` run states fail with the run, item, operation, and
  receipt IDs.
- A normal run succeeds only at `completed`.
- `completed-database-only` is success only when the caller explicitly chose
  `databaseOnly:true`.

For a normal run, download the immutable snapshot and descriptor together:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\planner\\<runId>\\"}'
```

Publish the applying site from that pair:

```powershell
dotnet run --project tools\ticket-site -- --planner-snapshot "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "cache\jira-ticket-site" --force
```

Publication failure does not invalidate receipts or the snapshot. Preserve
both, fail the outer command, and return `runId`, `snapshotId`, all
`acceptedReceiptId` values, and the publication error.

### Worker mode

Worker mode requires `FHIR_AUGURY_AUTHORING_WORKER=1` plus complete run,
item, callback, operation, token, and source-revision variables. Reject
partial context and never start a nested run.

The processor may supply `--repos <json-array>` with the ticket invocation.
Treat it as an exact case-insensitive `owner/repo` allow-list during
repository selection; `[]` means no restriction. Do not invent repositories
outside a non-empty allow-list.

The worker's only persistence action is:

```powershell
fhir-augury-cli --json '{"command":"planned-ticket-authoring","action":"submit","observedSourceRevision":"<current Jira updatedAt>","payload":{...PlannedTicketPayload...}}'
```

The CLI obtains callback coordinates and the token from the environment,
computes the content hash, posts the typed request, and validates the returned
receipt. Do not perform direct processor-database writes or emit a separate
authored artifact.

## Worker evidence workflow

1. Fetch the ticket with content, comments, and snapshot:

   ```powershell
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-55197","includeComments":true,"includeContent":true,"includeSnapshot":true}'
   ```

   Independently record the exact current Jira `updatedAt`. The environment's
   expected revision is a receipt coordinate, not the observation to submit.
   After canonical timestamp normalization, require the observed value to
   equal that frozen revision; otherwise fail without submitting.

2. Read cross-references and keywords:

   ```powershell
   fhir-augury-cli --json '{"command":"cross-referenced","value":"FHIR-55197","limit":50}'
   fhir-augury-cli --json '{"command":"keywords","source":"jira","id":"FHIR-55197","limit":30}'
   ```

3. Treat the Jira resolution and resolution description as the approved
   outcome. The original description explains the problem but does not
   override the resolution.

4. Resolve affected repositories from specification metadata, linked GitHub
   items, related artifacts, and keywords. Apply any processor-supplied repo
   filters exactly.

5. For every selected repository, require a current persisted
   `repo-analysis` briefing and metadata. Use its authoring roots, generated
   areas, artifact map, change recipes, warnings, and cross-repo touch points.
   Verify every proposed file path against the cached clone. Fail rather than
   inventing a path or continuing with partial repository context.

6. Inspect only the source needed to make the plan precise. Generated files
   remain non-authoritative; plan changes against authoring sources.

7. Build the complete typed payload in memory.

8. Immediately before submit, fetch the Jira ticket again. Its canonically
   normalized `updatedAt` must equal both the first observation and the frozen
   environment revision. If it changed, fail without submitting so the
   processor can supersede or replace the stale run item.

9. Submit once. A transport retry must replay byte-equivalent content and the
   same operation coordinates.

## PlannedTicketPayload guidance

The payload is the sole authored product:

```json
{
  "key": "FHIR-55197",
  "resolution": "...",
  "resolutionSummary": "...",
  "featureProposal": "...",
  "designRationale": "...",
  "repos": [],
  "repoChanges": [],
  "repoImpacts": [],
  "changeValidations": [],
  "testingConsiderations": [],
  "openQuestions": []
}
```

### Core prose

- `Resolution`: preserve the source resolution content.
- `ResolutionSummary`: state exactly what the approved resolution requires.
- `FeatureProposal`: combine a clear problem statement with the concrete
  approved change. For elements, name path, type, cardinality, definition,
  binding, and constraints as applicable. For terminology, identify the
  CodeSystem or ValueSet change. For narrative changes, identify the section
  and intended text.
- `DesignRationale`: explain consistency with FHIR patterns, alternatives,
  compatibility, and the recorded discussion.

For resolutions such as `Not Persuasive`, `Duplicate`, or `Withdrawn`,
submit a valid no-implementation payload: explain the outcome in the core
prose and leave implementation collections empty unless a verification action
is genuinely required.

### Repository graph

- `Repos`: one row per `owner/name`, with the briefing's clone revision and
  why that repository is in scope.
- Assign a stable in-payload `TicketRepoId` per repository and reuse it in
  all child rows.
- `RepoChanges`: one ordered, actionable source-file change per row. Include
  exact path, title, description, reason, optional source-line bounds, and
  `ReplacementLines` as an array, including `[]` when no literal replacement
  is appropriate.
- `RepoImpacts`: blast-radius or dependent-file effects. Leave
  `TicketRepoChangeId` absent unless the processor has supplied a stable
  persisted change ID; newly authored change rows do not expose their
  generated IDs in the wire payload.
- `ChangeValidations`: concrete checks in execution order, scoped to the
  corresponding repository.
- `TestingConsiderations`: edge cases, examples, validation, compatibility,
  and interoperability concerns.
- `OpenQuestions`: only unresolved implementation ambiguities. Address known
  briefing warnings in a change, impact, or question instead of omitting them.

Every task must be executable without returning to the Jira description.
Assess breaking changes honestly, include dependent specifications and
terminology effects, and never name a file that was not verified.

## Exact receipt gate

Worker success requires a non-empty receipt whose fields exactly match:

- `runId` = `FHIR_AUGURY_AUTHORING_RUN_ID`
- `itemId` = `FHIR_AUGURY_AUTHORING_ITEM_ID`
- `operationId` = `FHIR_AUGURY_AUTHORING_OPERATION_ID`
- `businessKey` = the ticket key
- `expectedSourceRevision` =
  `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`
- `observedSourceRevision` = the independently observed current Jira
  `updatedAt`
- `contentHash` = the canonical hash of the submitted
  `PlannedTicketPayload`

Also require non-empty `receiptId` and `persistedAt`, plus a present
non-negative `authoringEpoch`. `isReplay:true` is acceptable only when the
exact same operation and payload were previously accepted. Any coordinate
mismatch is failure.

## Non-negotiable rules

- The processor owns run state, mutation fencing, persistence, grouping,
  snapshot production, and publication inputs.
- Never place the operation token in argv or output.
- Never use the expected revision as a substitute for an independent Jira
  read.
- Never claim completion without the exact durable receipt.
