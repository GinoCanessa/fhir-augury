---
name: orchestrate-topic-groupings
description: "Orchestrates bulk Topic-grouping generation across FHIR workgroups. USE FOR: refreshing the (Specification, Type) -> Topic -> Linked Ticket Group decomposition in the jira-preparer DB for one workgroup, a comma-separated list, or all workgroups; bulk topic generation after a tranche of hydration has landed. Resolves workgroups against fhir-augury-cli list-jira-workgroups, then fans up to N concurrent topic-groupings sub-agents in parallel, one per resolved nameClean. Aggregates per-workgroup reports into a single run summary. Pairs with index-prepared-db to refresh the rendered README afterwards."
---

# Orchestrate Topic Groupings Skill

Bulk-generates Topic / Linked Ticket Group decompositions across one
or more FHIR workgroups by dispatching concurrent
[`topic-groupings`](../topic-groupings/SKILL.md) sub-agents and
aggregating their per-workgroup reports.

The per-workgroup workflow — fetching clustering signals from the
preparer, clustering tickets into Topics, building Linked Ticket
Groups, and PUTing each `(Spec, Type)` partition back — is entirely
owned by `topic-groupings`. **This skill must not replicate it.** The
orchestrator's job is exactly two things: (1) expand the workgroup
selector, and (2) fan out the per-workgroup skill with a concurrency
cap.

This skill is the producer-side counterpart to the consumer-side
[`index-prepared-db`](../index-prepared-db/SKILL.md). A typical
refresh cycle is:

1. `orchestrate-topic-groupings` (writes groupings to the DB)
2. `index-prepared-db` (renders the README from those groupings)

## Prerequisites

- The `fhir-augury-cli` skill must be available — it is the canonical
  entry point for the workgroup catalog. Follow its fallback order
  (CLI → MCP → direct HTTP) for the catalog read.
- The `topic-groupings` skill must be available — it defines the
  per-workgroup workflow and report format that each sub-agent runs.
  The clustering rules, validator contract, and PUT semantics are
  owned by `topic-groupings` and must not be replicated here.
- The preparer service must be reachable at the configured base URL.
  A quick `GET {preparerBaseUrl}/healthz` (or any read endpoint)
  before fan-out is recommended; on connection refusal, abort the
  run with a clear error.

## Inputs

1. **Workgroup selector** *(required)* — one of:
   - a single HL7 work-group code (e.g., `oo`, `pc`, `fhir`);
   - a single `nameClean` slug (e.g., `OrdersAndObservations`);
   - a comma-separated list mixing the two forms; or
   - the literal `all`.

   Resolved against the catalog returned by
   `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`,
   matching case-insensitively against both `code` and `nameClean`.
   Unresolved entries are reported and **skipped** — the run does not
   abort. Use `nameClean` verbatim — do **not** re-derive it.

2. **Concurrency** *(optional, default `3`)* — maximum number of
   concurrent `topic-groupings` sub-agents. Hard upper bound: `8`.
   `1` disables parallel fan-out entirely (useful when debugging a
   single failing workgroup or sharing a constrained preparer).

3. **Preparer base URL** *(optional, default `http://localhost:5171`)*
   — forwarded verbatim to every sub-agent.

4. **Working directory** *(optional, default `temp/topic-groupings/`
   relative to the repo root)* — directory the orchestrator and each
   sub-agent may use for transient files (catalog dumps, scratch
   notes). Created if it does not already exist. Each sub-agent
   receives a per-workgroup subdirectory under this root.

5. **Replace mode** *(optional, default `partition`)* — passthrough
   to `topic-groupings`: one of `partition` (safer) or `wipe-first`
   (destructive). When the selector is `all`, the default `partition`
   is strongly recommended; `wipe-first` against `all` will DELETE
   every `(Spec, Type)` pair the preparer currently knows about
   before regenerating.

6. **Include retired** *(optional, default `false`)* — when the
   catalog response carries a `retired` flag and the selector is
   `all`, include retired workgroups when this is `true`. Single-name
   or comma-separated selectors always honour the user's explicit
   list regardless of `retired`.

## Work-Group Names

Work-group records returned by `list-jira-workgroups` include `code`,
`name`, and `nameClean` directly. **Use `nameClean` verbatim** for
the value passed to the preparer — do not derive it locally. The
`index-prepared-db` skill documents the asymmetry between
jira-source's `nameClean` (from `Hl7WorkGroupNameCleaner.Clean`) and
the preparer's `REPLACE(WorkGroup, ' ', '')` rule; the
`topic-groupings` skill flags workgroups for which the asymmetry
returns zero clustering signals.

## Workflow

### Step 1: Resolve scope

1. Call `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`
   exactly **once** at the start of the run. Build two maps:
   `codeLower → nameClean` and `nameCleanLower → nameClean`.
2. Expand the workgroup selector:
   - **`all`** → every `nameClean` in the catalog. If the catalog
     response carries a `retired` flag and the user did not opt in
     via the `Include retired` input, skip retired entries. If the
     response shape does not include `retired`, note this in the run
     report and include every entry.
   - **Comma-separated entries** → look each up in both maps
     (case-insensitive). On no match for an entry, report it and
     skip; do **not** abort the run.
3. Deduplicate the resolved list by `nameClean` (stable order).
4. If the resolved list is empty, abort with a clear error message.

### Step 2: Pre-flight the preparer

Before dispatching sub-agents, issue a single connectivity probe
against the preparer's base URL — e.g.,
`GET {preparerBaseUrl}/api/v1/prepared-ticket-hydration/_health` or
any cheap existing read (catch network errors, not 404s — a 404 with
a well-formed `ProblemDetails` body still proves the preparer is
reachable). On connection refusal / DNS failure / timeout, abort
with a clear error; do not fan out only to have every sub-agent
fail.

### Step 3: Fan out

Dispatch up to **N concurrent `topic-groupings` sub-agents** (where
`N` is the configured `Concurrency`, default `3`). Each sub-agent
gets:

- One resolved `nameClean` as its `Workgroup selector` (single
  value, never the original comma-separated form);
- The configured `Preparer base URL`;
- A per-workgroup subdirectory under the orchestrator's `Working
  directory` (e.g.,
  `temp/topic-groupings/OrdersAndObservations/`);
- The configured `Replace mode` (passed through unchanged).

As sub-agents finish, dispatch the next pending workgroup until the
queue is empty.

**Concurrency cap is a hard ceiling.** Never run more than `N`
sub-agents at once.

### Step 4: Aggregate per-workgroup reports

Collect each sub-agent's structured report. The orchestrator does
**not** read or write the preparer DB directly — the sub-agents are
the only writers. After all sub-agents complete, emit a single
run-level report containing:

- **Resolved workgroups**: total resolved, total unresolved
  (with the user's original entries that did not match).
- **Per-workgroup rows**, each containing:
  - `nameClean`,
  - status (`completed` / `skipped: 404` / `aborted: <reason>`),
  - tickets considered / kept / dropped (with the three drop
    reasons surfaced by `topic-groupings`),
  - partitions written,
  - Topics created,
  - Linked Ticket Groups created,
  - cross-partition linked edges dropped,
  - any per-partition `400` validator errors.
- **Aggregates**: totals across all workgroups for partitions
  written, Topics, Linked Ticket Groups, edges dropped, and
  validator failures.
- **Catalog-asymmetry note** — list every workgroup the sub-agent
  flagged as having zero clustering signals despite a non-empty
  hydration response (the `nameClean` ↔ `REPLACE(WorkGroup, ' ',
  '')` mismatch).
- A reminder that agent-authored Topic short / long descriptions
  may shift between runs, so this orchestrator should not be used
  as a deterministic regression baseline for prose fields.

## Behaviour rules

- **Read-only against jira-source.** The orchestrator calls
  `list-jira-workgroups` exactly once. No `get` or other reads, no
  ingestion triggers.
- **Read-only against the preparer (orchestrator).** The
  orchestrator does not issue any `GET` / `PUT` / `DELETE` against
  the preparer beyond an optional connectivity probe. All grouping
  writes go through `topic-groupings` sub-agents.
- **Independent failure scope per workgroup.** A failed sub-agent
  for one workgroup must not cause another workgroup's sub-agent to
  be skipped or restarted. Capture the failure in the run report
  and move on.
- **Concurrency cap is a hard ceiling.** Do not exceed the
  configured `Concurrency` value, and never more than `8`.
- **No README rendering here.** This skill is a producer; pair it
  with `index-prepared-db` afterwards if a refreshed README is also
  wanted.

## Important Rules

- **Never re-derive `nameClean`.** Always use the catalog's
  `nameClean` verbatim.
- **Each workgroup is processed independently.** Sub-agents do not
  share state (apart from the read-only catalog the orchestrator
  fetched once).
- **`wipe-first` against `all` is destructive.** Confirm with the
  user, or default to `partition`, when the selector is `all`.
- **Do not consume `plan.md` or any session artifacts.** This skill
  is invoked directly by the user. Source request / plan files are
  not inputs.
