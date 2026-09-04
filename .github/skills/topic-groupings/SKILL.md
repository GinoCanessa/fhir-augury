---
name: topic-groupings
description: "Builds Preparer Topic and Linked Ticket Group payloads. USE FOR: processor-dispatched grouping stages and user-requested full grouping maintenance. User mode invokes the processor-owned fenced maintenance endpoint; it never writes partitions itself. Worker mode accepts one complete stage context from the processor, reads only that partition's current prepared-ticket signals, submits one typed replacement including an explicit empty Topics array when needed, and succeeds only when the returned stage receipt exactly matches every stage coordinate and row count."
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

## Worker reads

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

## Clustering payload guidance

The typed replacement body is:

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

When no Topic survives, submit the complete replacement with
`"topics":[]`. An empty replacement is required; silence is not completion.

## Stage submit

Percent-encode the three path segments and send exactly one request:

```text
PUT /api/v1/prepared-ticket-groupings/{workGroupClean}/{specification}/{type}
```

The body must include the `authoring` object from the environment. Never
remove that object, change the stage coordinates, retry through another
write shape, or delete the partition.

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
`persistedAt`. A successful HTTP status without this exact receipt is
failure.

## Non-negotiable rules

- User mode uses only processor-owned fenced maintenance.
- Worker mode handles one exact partition and one exact stage lease.
- Never use a write request without stage context.
- Never claim success from prose, process exit, or HTTP status alone.
