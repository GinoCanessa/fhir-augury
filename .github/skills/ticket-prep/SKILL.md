---
name: ticket-prep
description: "Authors the typed preparation payload for a FHIR Jira ticket. USE FOR: ticket review preparation, disposition analysis, and single-ticket preparation runs. In outer-control mode it starts and monitors a processor-owned authoring run, then downloads the immutable snapshot and publishes the discussion site. In worker mode it gathers current Jira and cross-source evidence, independently observes Jira updatedAt, submits one PreparedTicketPayload through the processor callback, and succeeds only with the exact durable receipt."
---

# Ticket Prep Skill

## Canonical execution contract

Choose exactly one mode before doing any work.

### Outer-control mode

Outer-control mode requires all of these variables to be absent:

- `FHIR_AUGURY_AUTHORING_WORKER`
- `FHIR_AUGURY_AUTHORING_RUN_ID`
- `FHIR_AUGURY_AUTHORING_ITEM_ID`
- `FHIR_AUGURY_AUTHORING_CALLBACK_URL`
- `FHIR_AUGURY_AUTHORING_OPERATION_ID`
- `FHIR_AUGURY_AUTHORING_OPERATION_TOKEN`
- `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`

A trusted local operator may use the Dev UI route
`/operations/prepare/new`; it is an outer-control alternative over the same
processor-owned run and publisher contracts. The command flow below remains
the supported headless equivalent. Neither outer surface receives operation
tokens or performs repository mutation, commit pushes, pull-request creation,
or other GitHub writes.

Start one processor-owned run:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"start","ticketKeys":["FHIR-50738"],"databaseOnly":false}'
```

Then:

1. Capture `run.runId` and every returned item coordinate.
2. Poll `prepared-ticket-authoring` action `status` for that run.
3. An item is authored only when its status is `complete` and it has a
   non-empty `acceptedReceiptId`. Never infer completion from prose, a local
   file, or process exit alone.
4. Continue polling while `run.state.isTerminal` is false, including
   recoverable run status `error`. Automatic retry is the default.
5. Use action `retry` only for a current `error` item with
   `allowedActions.canRetryNow:true`. Use `supersede` only with an explicit
   non-blank reason when `allowedActions.canSupersede:true`; never supersede a
   receipt-backed item.
6. Success is `completed` for a normal run or `completed-database-only` only
   when the caller explicitly requested `databaseOnly:true`.
7. A terminal run `superseded` is failure. Include run, item, operation, and
   receipt IDs. There is no generic terminal `failed` state.

At the processor/Orchestrator HTTP surface, structured start conflicts supply
authoritative `conflictingRunIds` and may include the legacy single `runId`;
the Dev UI turns them into links, while the CLI retains its existing error
envelope. If start, retry, or supersede loses its response after it may have
reached the processor, report **outcome unknown**, never replay it, and
reconcile only through list/detail reads. Require explicit review before
another ambiguous start.

For a normal run, download the processor snapshot and trusted descriptor as
one verified pair:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"snapshot","runId":"<runId>","snapshotPath":"cache\\authoring-snapshots\\preparer\\<runId>\\"}'
```

Publish only from that immutable pair:

```powershell
dotnet run --project tools\ticket-site -- --preparer-snapshot "<snapshotPath>" --snapshot-descriptor "<descriptorPath>" --out "cache\jira-ticket-site" --force
```

The directory-valued snapshot output contains the descriptor, database, and
`verified-pair.json`. The ready manifest binds `Preparer`, run/snapshot
coordinates, safe filenames, size, and both SHA-256 digests. Successful
publication writes the exact sub-site manifest `site-manifest.json`.

If publication fails, keep the accepted receipts, snapshot bytes, and
descriptor untouched, but fail the outer command. Return `runId`,
`snapshotId`, every `acceptedReceiptId`, and the publication error. Do not
retry authoring merely because publication failed.

### Worker mode

Worker mode requires `FHIR_AUGURY_AUTHORING_WORKER=1` and all six callback
variables (`RUN_ID`, `ITEM_ID`, `CALLBACK_URL`, `OPERATION_ID`,
`OPERATION_TOKEN`, `SOURCE_REVISION`) to be non-empty. Partial or mixed
context is an error. Never start or monitor a run from worker mode.

The processor supplies the ticket key as the skill argument and normally as
`FHIR_AUGURY_TICKET_KEY`. If both are present, require an exact
case-insensitive match.

Worker mode performs exactly one persistence action:

```powershell
fhir-augury-cli --json '{"command":"prepared-ticket-authoring","action":"submit","observedSourceRevision":"<current Jira updatedAt>","payload":{...PreparedTicketPayload...}}'
```

The CLI reads the callback coordinates and secret token from the environment,
sends the token only in the callback header, computes the canonical content
hash, and validates the receipt. Do not call a direct write command, open a
processor database, or write an authored artifact.

## Worker evidence workflow

All Jira, Zulip, GitHub, cross-reference, search, and keyword reads use the
`fhir-augury-cli` skill and its documented fallback chain.

1. Fetch the main ticket with content, comments, and snapshot:

   ```powershell
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-50738","includeComments":true,"includeContent":true,"includeSnapshot":true}'
   ```

   Record the exact current Jira `updatedAt`. This value is independently
   observed evidence; do not copy
   `FHIR_AUGURY_AUTHORING_SOURCE_REVISION` into
   `observedSourceRevision`. After canonical timestamp normalization, require
   it to equal the frozen environment revision. If it differs, fail as stale
   without submitting.

2. Fetch cross-references and keywords in parallel where possible:

   ```powershell
   fhir-augury-cli --json '{"command":"cross-referenced","value":"FHIR-50738","limit":50}'
   fhir-augury-cli --json '{"command":"keywords","source":"jira","id":"FHIR-50738","limit":30}'
   ```

3. Fetch each material related Jira ticket and Zulip thread. Record GitHub
   items by stable item ID. Do not invent unavailable evidence.

4. For every related repository, use the current persisted
   `repo-analysis` briefing and metadata. If required repository context is
   missing or stale, fail the worker rather than guessing repository facts.

5. Build the typed payload entirely in memory using the guidance below.

6. Immediately before submit, fetch the main Jira ticket again and observe
   its current `updatedAt`. After canonical normalization it must equal both
   the earlier independent observation and
   `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`. If not, fail without submitting;
   the frozen run item is stale and must be superseded or replaced by the
   processor.

7. Submit once. Transient transport retries may replay the same operation,
   payload, hash, and observed revision; never mutate content between replay
   attempts.

## PreparedTicketPayload guidance

The payload is the sole authored product:

```json
{
  "key": "FHIR-50738",
  "requestSummary": "...",
  "commentSummary": "...",
  "linkedTicketSummary": "...",
  "relatedTicketSummary": "...",
  "relatedZulipSummary": "...",
  "relatedGitHubSummary": "...",
  "existingProposed": "...",
  "proposalA": "...",
  "proposalAJustification": "...",
  "proposalAImpact": "Compatible, substantive",
  "proposalB": "...",
  "proposalBJustification": "...",
  "proposalBImpact": "Non-compatible",
  "proposalC": "...",
  "proposalCJustification": "...",
  "recommendation": "A",
  "recommendationJustification": "...",
  "repos": [],
  "relatedJiraTickets": [],
  "relatedZulipThreads": [],
  "relatedGitHubItems": []
}
```

Populate it as follows:

- `RequestSummary`: a self-contained third-person account of the request,
  including material linked-ticket context.
- `CommentSummary`: positions, consensus, disagreement, decisions, and open
  questions from Jira comments.
- `LinkedTicketSummary` and `RelatedTicketSummary`: distinguish explicit
  Jira links from looser cross-references.
- `RelatedZulipSummary`: summarize participants' positions honestly; do not
  collapse disagreement into false consensus.
- `RelatedGitHubSummary`: identify item type, repository, state, and
  relevance.
- `ExistingProposed`: preserve an existing concrete proposal from the ticket
  when present.
- Proposal A accepts the request as written; Proposal B offers a concrete
  alternative; Proposal C declines with a specific rationale. Name the
  affected resource, element, constraint, terminology, repository, and
  authoring root when known.
- `ProposalAImpact` and `ProposalBImpact` must be exactly one of
  `Non-substantive`, `Compatible, substantive`, or `Non-compatible`.
- `Recommendation` must be exactly `existing`, `A`, `B`, or `C`, with a
  direct comparison of trade-offs.
- Related collections contain stable identifiers plus concise
  justifications. Do not duplicate the main ticket.

## Exact receipt gate

Worker success requires a response containing `receipt` and `isReplay`, with
the receipt matching all of these values exactly:

- `runId` = `FHIR_AUGURY_AUTHORING_RUN_ID`
- `itemId` = `FHIR_AUGURY_AUTHORING_ITEM_ID`
- `operationId` = `FHIR_AUGURY_AUTHORING_OPERATION_ID`
- `businessKey` = the ticket key
- `expectedSourceRevision` =
  `FHIR_AUGURY_AUTHORING_SOURCE_REVISION`
- `observedSourceRevision` = the independently observed current Jira
  `updatedAt` submitted by this worker
- `contentHash` = the canonical hash of the submitted typed payload

Require a non-empty `receiptId` and `persistedAt`, plus a present non-negative
`authoringEpoch`. Any mismatch or missing field is failure, even if the HTTP
response is successful.

## Non-negotiable rules

- The processor owns run state, mutation fencing, persistence, grouping,
  snapshots, and publication inputs.
- Never expose the operation token in argv, logs, prose, or diagnostics.
- Never substitute the expected source revision for an independent Jira
  observation.
- Never claim success without the exact durable receipt.
