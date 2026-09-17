---
name: topic-groupings
description: "Builds Preparer Topic and Linked Ticket Group payloads. USE FOR: processor-dispatched grouping stages and user-requested full grouping maintenance. User mode invokes the processor-owned fenced maintenance endpoint; it never writes partitions itself. Ordinary worker mode retains fenced canonical grouping maintenance. Reconciliation worker mode reads only the exact run-scoped candidate-overlay partition and submits a staged replacement. Every worker succeeds only when the returned stage receipt exactly matches the stage coordinates and row counts."
---

# Topic Groupings Skill

## Canonical modes

### User mode

User mode requires every `FHIR_AUGURY_GROUPING_*` variable to be absent.
Invoke processor-owned fenced maintenance:

```text
POST {preparerBaseUrl}/api/v1/prepared-ticket-groupings/maintenance
Content-Type: application/json

{}
```

To resume a known maintenance run, send only:

```json
{ "runId": "<runId>" }
```

Do not enumerate workgroups, read grouping inputs, or write partitions from
user mode. The processor creates or resumes the storage-only run, acquires the
mutation fence, dispatches partition stages, and finalizes the run.

Success requires a non-empty returned `runId`, exact status
`completed-database-only`, and a non-negative `itemCount`. Any other status or
coordinate is failure and must retain the returned run ID.

### Run-stage worker mode

Require `FHIR_AUGURY_GROUPING_WORKER=1` and all of:

- `FHIR_AUGURY_GROUPING_PROCESSOR_URL`
- `FHIR_AUGURY_GROUPING_RUN_ID`
- `FHIR_AUGURY_GROUPING_STAGE_ID`
- `FHIR_AUGURY_GROUPING_STAGE_LEASE_ID`
- `FHIR_AUGURY_GROUPING_INPUT_FINGERPRINT`
- `FHIR_AUGURY_GROUPING_WORK_GROUP_CLEAN`
- `FHIR_AUGURY_GROUPING_SPECIFICATION`
- `FHIR_AUGURY_GROUPING_TYPE`

Reject partial context and user selectors. Process exactly this partition and
never invoke maintenance from worker mode.

### Ordinary grouping-maintenance stage

When `FHIR_AUGURY_GROUPING_RECONCILIATION` is absent, require every
`FHIR_AUGURY_GROUPING_*` reconciliation variable below to be absent. This is
the existing grouping-maintenance mode. Use the ordinary worker reads and the
four-field `authoring` object (`runId`, `stageId`, `stageLeaseId`,
`inputFingerprint`) described below.

### Publication-reconciliation stage

When `FHIR_AUGURY_GROUPING_RECONCILIATION=1`, additionally require:

- `FHIR_AUGURY_GROUPING_PARTITION_KEY`
- `FHIR_AUGURY_GROUPING_OVERLAY_CORPUS_FINGERPRINT`
- `FHIR_AUGURY_GROUPING_REVISED_TICKET_KEYS_JSON`
- `FHIR_AUGURY_GROUPING_TICKET_KEYS_JSON`

Parse both JSON values as arrays of unique, non-empty Jira keys. The ticket
keys array is the complete candidate-overlay membership, including unchanged
members of the impacted partition. The revised array identifies why the
partition is in the impact closure; it is not the membership to group.

Require the input fingerprint and overlay corpus fingerprint to be identical.
Require the partition key to equal the three trimmed partition coordinates
joined with the single separator U+001F. Reject any partial, additional, or
internally inconsistent reconciliation context.

## Ordinary worker reads

Using the processor URL from the environment:

```text
GET /api/v1/prepared-ticket-clustering-signals/{workGroupClean}
GET /api/v1/prepared-ticket-hydration/{workGroupClean}
```

Filter both responses to the exact environment `Specification` and `Type`.
Use the returned workgroup display value; never invent it. The mutation fence
stabilizes the run while this stage executes.

Eligible tickets must:

- belong to the exact partition;
- have `HasPreparedTicket = true`;
- have a stable Jira key.

Use only processor-provided summary text and link edges. Do not query Jira,
Zulip, GitHub, local authored files, or a processor database.

## Reconciliation worker reads

Percent-encode the three partition path segments and every query value. Read
both projections with the exact durable stage coordinates:

```text
GET /api/v1/prepared-ticket-clustering-signals/{workGroupClean}/{specification}/{type}?runId={runId}&stageId={stageId}&stageLeaseId={stageLeaseId}&inputFingerprint={overlayCorpusFingerprint}
GET /api/v1/prepared-ticket-hydration/{workGroupClean}/{specification}/{type}?runId={runId}&stageId={stageId}&stageLeaseId={stageLeaseId}&inputFingerprint={overlayCorpusFingerprint}
```

These routes are overlay-only. Never fall back to the workgroup-only routes
in a reconciliation stage: those routes are live canonical reads. Require
both responses to contain exactly the environment ticket-keys array, in the
same order, with the exact partition coordinates. Use the returned workgroup
display value. A successful empty response is an empty impacted partition,
not a reason to read canonical state.

## Clustering payload guidance

The ordinary grouping-maintenance replacement body is:

```json
{
  "workGroupDisplay": "Orders and Observations",
  "topics": [
    {
      "shortDescription": "Observation interpretation",
      "longerDescription": "...",
      "renderOrderHint": null,
      "linkedTicketGroups": [
        {
          "firstTicketKey": "FHIR-100",
          "rationale": "...",
          "members": [
            { "ticketKey": "FHIR-100", "order": 0 },
            { "ticketKey": "FHIR-101", "order": 1 }
          ]
        }
      ],
      "remainingTicketKeys": ["FHIR-102"]
    }
  ],
  "authoring": {
    "runId": "<environment run>",
    "stageId": "<environment stage>",
    "stageLeaseId": "<environment lease>",
    "inputFingerprint": "<environment fingerprint>"
  }
}
```

For a publication-reconciliation stage, the `authoring` object must carry the
complete purpose-specific context unchanged:

```json
{
  "runId": "<environment run>",
  "stageId": "<environment stage>",
  "stageLeaseId": "<environment lease>",
  "inputFingerprint": "<environment overlay fingerprint>",
  "partitionKey": "<environment partition key>",
  "overlayCorpusFingerprint": "<environment overlay fingerprint>",
  "revisedTicketKeys": ["<exact environment values>"],
  "ticketKeys": ["<exact complete environment values>"]
}
```

Apply these rules deterministically:

1. Build the explicit linked graph from `Links` whose `LinkType` is
   `linked`; ignore edges leaving the exact partition.
2. A linked connected component of two or more tickets stays in one Topic
   and becomes one Linked Ticket Group.
3. Widen Topics using explicit `related` edges, then strong shared subject
   matter from request, comment, and related-item summaries. Never invent a
   link edge.
4. Prefer coherent broader Topics over artificial fragmentation, but do not
   combine contradictory or unrelated requests.
5. Omit singleton Topics. The Preparer derives ungrouped individual tickets.
6. `shortDescription` is plain title-style prose of 3–8 words.
   `longerDescription` and group `rationale` are plain prose of 1–3
   sentences. Do not use headings, fences, or leading bullet syntax.
7. `firstTicketKey` is the lowest Jira key in the linked component.
   `members` are ascending by Jira key with zero-based sequential `order`.
8. `remainingTicketKeys` contains Topic members not present in a linked
   group, sorted ascending.
9. Leave `renderOrderHint` null unless the evidence establishes a compelling
   review order.
10. Membership and ordering must be deterministic for the same inputs.

When no Topic survives, submit the complete replacement with `"topics":[]`.
An empty replacement is required; silence is not completion. The processor
normally completes a zero-member reconciliation partition itself without
launching a worker; if a worker receives zero complete-membership keys, it
must still use the reconciliation read and submit contracts and must not
invent members.

## Stage submit

Percent-encode the three path segments and send exactly one request:

```text
PUT /api/v1/prepared-ticket-groupings/{workGroupClean}/{specification}/{type}
```

The body must include the mode-appropriate `authoring` object from the
environment. Never remove that object, change the stage coordinates, retry
through another write shape, or delete the partition. In reconciliation mode
the exact context routes this request to staged reconciliation storage;
ordinary grouping-maintenance context continues to replace canonical grouping
under its run fence.

The response is successful only when it contains both save counts and
`authoringReceipt`.

## Exact stage receipt gate

Construct the expected partition key by joining the trimmed environment
workgroup, specification, and type with the single separator U+001F. Require
the receipt to match exactly:

- `runId`
- `stageId`
- `partitionKey`
- `inputFingerprint`
- `topicRows`
- `topicGroupRows`
- `memberRows`

The three receipt counts must also equal the top-level save counts and the
rows represented by the submitted payload. Require a non-empty
`persistedAt`. In reconciliation mode the receipt input fingerprint must be
the candidate `OverlayCorpusFingerprint`. A successful HTTP status without
this exact receipt is failure.

## Non-negotiable rules

- User mode uses only processor-owned fenced maintenance.
- Ordinary worker mode retains the canonical grouping-maintenance contract.
- Reconciliation worker mode handles one exact overlay partition and one
  exact durable stage lease.
- Reconciliation reads never use live canonical workgroup projections.
- Never use a write request without stage context.
- Never claim success from prose, process exit, or HTTP status alone.
