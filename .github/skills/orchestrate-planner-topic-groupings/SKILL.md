---
name: orchestrate-planner-topic-groupings
description: "Controls Planner grouping maintenance. USE FOR: rebuilding all planned-ticket Topic and Linked Ticket Group partitions, resuming a known maintenance run, or forwarding a processor-supplied partition stage to planner-topic-groupings. User mode invokes one processor-owned fenced maintenance operation and leaves all partition dispatch to the processor. Stage mode executes the worker skill synchronously and returns its exact receipt or failure."
---

# Orchestrate Planner Topic Groupings Skill

## Mode selection

- Use user mode only when every `FHIR_AUGURY_GROUPING_*` value is absent.
- Use stage mode only when `FHIR_AUGURY_GROUPING_WORKER=1` and all processor
  URL, run, stage, lease, fingerprint, workgroup, specification, and type
  values are present.
- Reject partial or mixed context.

## User mode

Invoke exactly one processor-owned fenced maintenance operation:

```text
POST {plannerBaseUrl}/api/v1/planned-ticket-topics/maintenance
Content-Type: application/json

{}
```

Resume only with a known maintenance run:

```json
{ "runId": "<runId>" }
```

Do not enumerate workgroups, resolve selectors, configure concurrency, read
planning signals, dispatch writers, or mutate topic partitions. The Planner
owns the work set, mutation fence, stage dispatch, receipts, and finalization.

Success requires non-empty `runId`, exact
`status:"completed-database-only"`, and non-negative `itemCount`. Preserve
the returned run ID on failure.

## Stage mode

Invoke `planner-topic-groupings` synchronously in run-stage worker mode with
no user options. It must submit the exact partition replacement, including an
explicit empty `topics` array when appropriate, and validate the exact stage
receipt:

- run ID
- stage ID
- U+001F-joined partition key
- input fingerprint
- topic, group, and member row counts
- persisted timestamp

Propagate that receipt or the failure. Do not create an orchestration-side
completion record, and never treat HTTP status or worker process exit as
sufficient.

## Result

User mode returns the maintenance run ID, terminal status, and item count.
Stage mode returns the exact durable stage receipt. No local authored
artifact participates in completion.
