---
name: planner-topic-groupings
description: "Builds Planner Topic and Linked Ticket Group payloads. USE FOR: processor-dispatched Planner grouping stages and user-requested full grouping maintenance. User mode invokes the processor-owned fenced maintenance endpoint; it never writes topic partitions itself. Worker mode accepts one complete stage context, reads only that partition's current planning signals, submits one typed replacement including an explicit empty Topics array when needed, and succeeds only when the returned stage receipt exactly matches every stage coordinate and row count."
---

# Planner Topic Groupings Skill

## Canonical modes

### User mode

User mode requires all `FHIR_AUGURY_GROUPING_*` values to be absent. Invoke
processor-owned fenced maintenance:

```text
POST {plannerBaseUrl}/api/v1/planned-ticket-topics/maintenance
Content-Type: application/json

{}
```

Resume only by sending the known maintenance run:

```json
{ "runId": "<runId>" }
```

The processor creates or resumes the storage-only run, acquires the mutation
fence, dispatches every partition stage, and finalizes. User mode must not
enumerate workgroups, launch grouping agents, read signals, or write topics.

Success requires non-empty `runId`, status `completed-database-only`, and
non-negative `itemCount`. Preserve and return the run ID on any failure.

### Run-stage worker mode

Require `FHIR_AUGURY_GROUPING_WORKER=1` plus:

- `FHIR_AUGURY_GROUPING_PROCESSOR_URL`
- `FHIR_AUGURY_GROUPING_RUN_ID`
- `FHIR_AUGURY_GROUPING_STAGE_ID`
- `FHIR_AUGURY_GROUPING_STAGE_LEASE_ID`
- `FHIR_AUGURY_GROUPING_INPUT_FINGERPRINT`
- `FHIR_AUGURY_GROUPING_WORK_GROUP_CLEAN`
- `FHIR_AUGURY_GROUPING_SPECIFICATION`
- `FHIR_AUGURY_GROUPING_TYPE`

Reject partial context and all user scope options. Process only the supplied
partition and never invoke maintenance from worker mode.

## Worker reads

Read:

```text
GET /api/v1/planned-ticket-clustering-signals/{workGroupClean}
GET /api/v1/planned-ticket-hydration/{workGroupClean}
```

Filter to the exact environment `Specification` and `Type`. Resolve
`workGroupDisplay` from the clustering envelope, then hydration rows. Fail if
it remains empty.

Every included ticket must have `HasPlannedTicket = true` and resolved
self-Jira hydration. If any ticket in the exact partition has a missing or
non-`resolved` hydration status, fail the stage; do not silently omit it.

Use only processor-provided repository, file, impact, and planning prose
signals. Do not read a processor database or use local authored artifacts.

## Clustering payload guidance

Submit a `PlannedTicketTopicGroupingRequest`:

```json
{
  "workGroupClean": "OrdersAndObservations",
  "workGroupDisplay": "Orders and Observations",
  "specification": "FHIR Core",
  "type": "Change Request",
  "topics": [
    {
      "shortDescription": "Observation source changes",
      "longerDescription": "...",
      "renderOrderHint": null,
      "spannedRepos": ["HL7/fhir"],
      "linkedTicketGroups": [
        {
          "firstTicketKey": "FHIR-200",
          "rationale": "...",
          "members": [
            { "ticketKey": "FHIR-200", "order": 0 },
            { "ticketKey": "FHIR-201", "order": 1 }
          ]
        }
      ],
      "remainingTicketKeys": ["FHIR-202"]
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

Membership follows this strict hierarchy:

1. **Same repository and intersecting file path** — highest signal. Build
   linked-group seeds from connected components sharing a case-insensitive
   `(RepoKey, FilePath)`.
2. **Overlapping repository set** — merge unless the planning outcomes are
   clearly incoherent.
3. **Shared affected file path across repositories** — merge on
   case-insensitive `AffectedFilePath`.
4. **Planning-prose similarity** — tiebreaker only; never the primary signal.

For every Topic:

- Include every eligible ticket exactly once.
- A ticket that does not merge still receives a one-ticket Topic and appears
  in `remainingTicketKeys`; Planner does not derive individual rows.
- `spannedRepos` is the case-insensitive union of member repositories,
  ordered by first appearance during ascending Jira-key traversal.
- `shortDescription` is 3–8 words; `longerDescription` and group rationale
  are 1–3 plain-prose sentences.
- Each tier-1 component of at least two tickets becomes one linked group.
- `firstTicketKey` is the lowest Jira key; members are ascending with
  zero-based sequential order.
- `remainingTicketKeys` contains Topic members outside linked groups,
  ascending.
- Leave `renderOrderHint` null unless evidence requires ordering.
- Keep membership and ordering deterministic.

When the exact partition has no eligible tickets, submit a complete request
with `"topics":[]`.

## Stage submit

Send exactly one stage-scoped request:

```text
PUT /api/v1/planned-ticket-topics
```

The body coordinates must equal the environment partition, and `authoring`
must carry the unchanged run, stage, lease, and fingerprint. Never retry
through a request without stage context or use another mutation endpoint.

## Exact stage receipt gate

The expected partition key is the exact environment workgroup,
specification, and type joined with U+001F. Require the response receipt to
match exactly:

- `runId`
- `stageId`
- `partitionKey`
- `inputFingerprint`
- `topicRows`
- `topicGroupRows`
- `memberRows`

The counts must equal the submitted payload's represented rows. Require
non-empty `persistedAt`. HTTP success without the exact receipt is failure.

## Non-negotiable rules

- User mode uses only processor-owned fenced maintenance.
- Worker mode handles one frozen partition under one exact stage lease.
- Explicit empty replacement is required for an empty partition.
- Never claim success without the exact stage receipt.
