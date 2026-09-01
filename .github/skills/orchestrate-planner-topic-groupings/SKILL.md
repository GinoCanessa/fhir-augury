---
name: orchestrate-planner-topic-groupings
description: "Orchestrates bulk Topic-grouping generation across FHIR workgroups on the planner side. USE FOR: refreshing the (Specification, Type) -> Topic -> Linked Ticket Group decomposition in the jira-planner DB for one workgroup, a comma-separated list, or all workgroups; bulk planner-topic generation after a tranche of hydration has landed. Resolves workgroups against fhir-augury-cli list-jira-workgroups, then fans up to N concurrent planner-topic-groupings sub-agents in parallel, one per resolved nameClean. Aggregates per-workgroup reports into a single run summary. Pairs with the future index-planned-db reader skill and with tools/ticket-site --planner-db to refresh the applying sub-site afterwards."
---

# Orchestrate Planner Topic Groupings Skill

Bulk-generates Topic / Linked Ticket Group decompositions across one
or more FHIR workgroups on the **planner** side by dispatching
concurrent
[`planner-topic-groupings`](../planner-topic-groupings/SKILL.md)
sub-agents and aggregating their per-workgroup reports.

The per-workgroup workflow — fetching clustering signals from the
planner, pre-flighting the missing-or-unresolved self-Jira hydration
contract, clustering tickets into Topics via the four-tier hierarchy,
and PUTing each `(Spec, Type)` partition back — is entirely owned by
`planner-topic-groupings`. **This skill must not replicate it.** The
orchestrator's job is exactly two things: (1) expand the workgroup
selector, and (2) fan out the per-workgroup skill with a concurrency
cap.

This skill is the planner-side analog of
[`orchestrate-topic-groupings`](../orchestrate-topic-groupings/SKILL.md).
A typical refresh cycle is:

1. `orchestrate-planner-topic-groupings` — writes
   `planned_ticket_topics*` rows for every selected workgroup.
2. Operator manually re-runs `tools/ticket-site` against the planner
   DB (e.g.,
   `dotnet run --project tools/ticket-site -- --planner-db ./cache/jira-planner.db --out ./cache/jira-ticket-site`)
   to refresh the applying sub-site. (A future `index-planned-db`
   reader skill will be the producer-side trigger for this step; it
   does not exist yet.)

## Prerequisites

- The [`fhir-augury-cli`](../fhir-augury-cli/SKILL.md) skill must be
  available — it is the canonical entry point for the workgroup
  catalog. Follow its documented fallback order (CLI → MCP → direct
  HTTP) for the catalog read.
- The
  [`planner-topic-groupings`](../planner-topic-groupings/SKILL.md)
  skill must be available — it defines the per-workgroup workflow,
  the clustering hierarchy, the validator contract, and the PUT
  semantics. **The orchestrator must not replicate any of those.**
- The planner service must be reachable at the configured base URL
  (default `http://localhost:5172`). A single connectivity probe
  before fan-out is required (see Workflow Step 2).

## Inputs

1. **Workgroup selector** *(required)* — one of:
   - a single HL7 work-group code (e.g., `oo`, `pc`, `fhir`);
   - a single `nameClean` slug (e.g., `OrdersAndObservations`);
   - a comma-separated list mixing the two forms; or
   - the literal `all`.

   Resolved against the catalog returned by
   `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`,
   matching case-insensitively against `code`, `name`, and
   `nameClean`. Unresolved entries are reported and **skipped** —
   the run does not abort on a single bad selector. Pass `nameClean`
   verbatim to each sub-agent — do **not** re-derive it.

2. **Concurrency** *(optional, default `3`, hard ceiling `8`)* —
   maximum number of concurrent `planner-topic-groupings` sub-agents
   (slot 0605-01 Open Question 5). `1` disables parallel fan-out
   entirely (useful when debugging a single failing workgroup or
   sharing a constrained planner). The cap is a **hard ceiling**;
   never exceed it.

3. **Planner base URL** *(optional, default
   `http://localhost:5172`)* — forwarded verbatim to every
   sub-agent.

4. **Working directory** *(optional, default
   `temp/planner-topic-groupings/` relative to the repo root)* —
   directory the orchestrator and each sub-agent may use for
   transient files (catalog dumps, scratch notes). Created if it
   does not already exist. Each sub-agent receives a per-workgroup
   subdirectory under this root (e.g.,
   `temp/planner-topic-groupings/OrdersAndObservations/`).

5. **Include retired** *(optional, default `false`)* — when the
   catalog response carries a `retired` flag and the selector is
   `all`, include retired workgroups when this is `true`.
   Comma-separated or single-name selectors always honour the user's
   explicit list regardless of `retired`.

> **Replace mode is NOT an input** — the per-workgroup skill ships
> `partition` mode only this slot. Do not expose a `wipe-first`
> passthrough that the sub-agent cannot honour. The per-tuple
> empty-PUT primitive is preserved by `SaveTopicGroupingAsync` for
> a future slot that ships `wipe-first` once a workgroup-level
> partition list endpoint is also designed.

## Work-Group Names

Work-group records returned by `list-jira-workgroups` include `code`,
`name`, and `nameClean` directly. **Use `nameClean` verbatim** for
the value passed to each sub-agent — do not derive it locally. The
planner-side controllers
(`PlannedTicketClusteringSignalsController`,
`PlannedTicketHydrationController`, `PlannedTicketTopicsController`)
defensively call `Hl7WorkGroupNameCleaner.Clean` on whatever they
receive, so a non-canonical form will still resolve — but the folder
slug each sub-agent uses for its working directory is the
`nameClean` value verbatim, and consistency between sub-agent
working directories matters when the orchestrator aggregates the
per-workgroup reports.

## Workflow

### Step 1: Resolve scope

1. Call `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`
   exactly **once** at the start of the run. Build three maps:
   `codeLower → nameClean`, `nameCleanLower → nameClean`, and
   `nameLower → nameClean`.
2. Expand the workgroup selector:
   - **`all`** → every `nameClean` in the catalog. If the catalog
     response carries a `retired` flag and the caller did not opt in
     via the `Include retired` input, skip retired entries. If the
     response shape does not include `retired`, note this in the run
     report and include every entry.
   - **Comma-separated entries** → look each up in all three maps
     (case-insensitive). On no match for an entry, report it and
     skip; do **not** abort the run.
3. Deduplicate the resolved list by `nameClean` (stable order).
4. If the resolved list is empty, abort with a clear error message.

### Step 2: Pre-flight the planner

Before dispatching any sub-agent, issue a single connectivity probe
against the planner's base URL. **Any well-formed HTTP response
counts as reachability confirmation**, regardless of status code:

- A simple cheap probe is
  `GET {plannerBaseUrl}/api/v1/planned-ticket-hydration/__probe__`.
  This always returns `200` with an empty `Results` array — the
  planner's `PlannedTicketHydrationController` has no 404 path
  ([`PlannedTicketHydrationController.cs`](../../../src/FhirAugury.Processor.Jira.Fhir.Planner/Controllers/PlannedTicketHydrationController.cs)
  lines 21-33). A `200 { "WorkGroupClean": "__probe__", "Results": [] }`
  body proves the service is up.
- An equally valid alternative is
  `GET {plannerBaseUrl}/api/v1/planned-tickets/__probe__`, which
  returns a `404` with a well-formed `ProblemDetails` body. The
  presence of a structured response — not the status code — is the
  signal.
- The new `GET /api/v1/planned-ticket-clustering-signals/__probe__`
  also returns a 404 (no hydration self-rows for the probe slug);
  same well-formed-response semantics apply.

On **network failure / DNS failure / connection refusal / timeout**,
abort the run with a clear error before any sub-agent is dispatched
— do not fan out only to have every sub-agent fail in parallel.

### Step 3: Fan out

Dispatch up to **N concurrent `planner-topic-groupings` sub-agents**
where `N` is the configured `Concurrency` (default `3`, hard ceiling
`8`). Each sub-agent gets:

- One resolved `nameClean` as its `Workgroup selector` (a single
  value — never the original comma-separated form);
- The configured `Planner base URL`;
- A per-workgroup subdirectory under the orchestrator's `Working
  directory` (e.g.,
  `temp/planner-topic-groupings/OrdersAndObservations/`).

**Do not pass a `Replace mode`** — the sub-agent does not accept
one this slot.

As sub-agents finish, dispatch the next pending workgroup until the
queue is empty.

**Concurrency cap is a hard ceiling.** Never run more than `N`
sub-agents at once; never exceed `8` total even if the caller asks
for more.

### Step 4: Aggregate per-workgroup reports

Collect each sub-agent's structured report. The orchestrator does
**not** read or write the planner DB directly — the sub-agents are
the only writers. After all sub-agents complete, emit a single
run-level report containing:

- **Resolved workgroups**: total resolved, total unresolved
  (with the user's original entries that did not match).
- **Per-workgroup rows**, each containing:
  - `nameClean`,
  - status — one of:
    - `completed` (sub-agent finished with at least one PUT
      attempted),
    - `skipped: 404` (clustering-signals returned 404 for the
      workgroup — no hydration self-rows),
    - `aborted: missing-or-unresolved-self-jira` (Open Question 3
      tripped — list the offending ticket keys + their actual
      `HydrationStatus` values from the sub-agent's report),
    - `aborted: no-workgroup-display` (all three display-resolution
      tiers came up empty),
    - `aborted: <other-reason>` (network failure on the sub-agent
      side, 5xx on the clustering-signals fetch, etc.);
  - tickets considered, kept, dropped (with the drop reasons
    surfaced by `planner-topic-groupings`);
  - partitions written, Topics created, Linked Ticket Groups
    created;
  - validator failures (each captured `400 {error: ...}` body);
  - transient-retry events (every 5xx PUT that was retried,
    including the final outcome);
  - any partition that used the literal `(unknown)` chrome in
    `Specification` or `Type`.
- **Aggregates** across all workgroups: total partitions written,
  total Topics, total Linked Ticket Groups, total validator
  failures, total transient-retry events.
- **Prose-drift caveat**: a one-line reminder that
  `ShortDescription` / `LongerDescription` / `Rationale` may shift
  between runs even when membership is stable.

## Behaviour rules

- **Read-only against jira-source.** The orchestrator calls
  `list-jira-workgroups` exactly once. No `get` or other reads, no
  ingestion triggers.
- **Read-only against the planner (orchestrator).** The orchestrator
  issues only the single connectivity probe in Step 2. All planner
  reads and the topic PUTs are owned by the sub-agents.
- **Independent failure scope per workgroup.** A failed sub-agent
  for one workgroup must not cause another workgroup's sub-agent to
  be skipped or restarted. Capture the failure in the run report
  and move on.
- **Concurrency cap is a hard ceiling.** Do not exceed the
  configured `Concurrency` value, and never more than `8`.
- **No README rendering here.** This skill is a producer; the
  reviewer refreshes the applying sub-site under
  [`tools/ticket-site`](../../../tools/ticket-site/README.md)
  manually after a successful run.

## Important Rules

1. **Selectors accept `name`, `nameClean`, or `code`
   interchangeably.** The planner-side controllers route every
   selector through the shared `WorkGroupResolver` and defensively
   call `Hl7WorkGroupNameCleaner.Clean`. Output folder slugs are
   produced from `nameClean` for path stability.
2. **`catalogJoinDegraded` is a proceed-with-warning signal.** When
   the `list-jira-workgroups` envelope returns
   `catalogJoinDegraded: true`, surface a single top-of-run banner;
   do not abort. (The planner-side does not have a destructive
   selector mode that would need extra confirmation here — see
   rule 4.)
3. **Each workgroup is processed independently.** Sub-agents do not
   share state apart from the read-only catalog the orchestrator
   fetched once.
4. **Do not expose `wipe-first`.** The per-workgroup
   `planner-topic-groupings` skill ships `partition` mode only this
   slot — the planner exposes neither a workgroup-level partition
   list endpoint nor a DELETE for topics, both of which a real
   `wipe-first` implementation requires. The per-tuple empty-PUT
   primitive is preserved by `SaveTopicGroupingAsync` for future
   work; do not invoke it here.
5. **Do not consume `plan.md` or any session artifacts.** This skill
   is invoked directly by the user. Source request / plan files are
   not inputs.
