---
name: orchestrate-topic-groupings
description: "Controls Preparer grouping maintenance. USE FOR: rebuilding all prepared-ticket Topic and Linked Ticket Group partitions, resuming a known maintenance run, or forwarding a processor-supplied partition stage to topic-groupings. User mode invokes one processor-owned fenced maintenance operation and leaves all partition dispatch to the processor. Stage mode executes the worker skill synchronously and returns its exact receipt or failure."
---

# Orchestrate Topic Groupings Skill

## Mode selection

- If every `FHIR_AUGURY_GROUPING_*` value is absent, use user mode.
- If `FHIR_AUGURY_GROUPING_WORKER=1` and the complete run, stage, lease,
  fingerprint, processor URL, workgroup, specification, and type values are
  present, use stage mode.
- Reject partial or mixed context.

## User mode

Invoke exactly one processor-owned fenced maintenance operation:

```text
POST {preparerBaseUrl}/api/v1/prepared-ticket-groupings/maintenance
Content-Type: application/json

{}
```

Resume only when the caller supplies a known maintenance run ID:

```json
{ "runId": "<runId>" }
```

Do not list workgroups, resolve selectors, set concurrency, read clustering
signals, dispatch writers, or mutate partitions. The Preparer owns the
maintenance work set, mutation fence, stage dispatch, receipts, and
finalization.

Success requires:

- non-empty `runId`;
- exact `status:"completed-database-only"`;
- non-negative `itemCount`.

Any other response fails while preserving the returned run ID for diagnosis
or resume.

## Stage mode

Invoke `topic-groupings` synchronously in its run-stage worker mode. Pass no
user selectors or maintenance options. Return only after that skill validates
the exact stage receipt:

- run ID
- stage ID
- U+001F-joined partition key
- input fingerprint
- topic, group, and member row counts
- persisted timestamp

Do not add an orchestration-side completion record. HTTP success or worker
process exit without the exact receipt is failure.

## Result

User mode returns the maintenance run ID, terminal status, and item count.
Stage mode returns the exact durable stage receipt. No authored file or local
state participates in completion.
