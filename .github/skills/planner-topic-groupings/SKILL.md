---
name: planner-topic-groupings
description: "Generates Topic and Linked Ticket Group groupings for one FHIR workgroup at a time on the planner side and writes them to the jira-planner DB. USE FOR: producing the (Specification, Type) -> Topic -> Linked Ticket Group decomposition the applying sub-site under tools/ticket-site renders, per-workgroup planner-topic generation, recomputing groupings after new tickets have been hydrated. Calls the planner's planned-ticket-clustering-signals and planned-ticket-hydration endpoints (GET) for inputs and PUTs each non-empty partition back."
---

# Planner Topic Groupings Skill

Generates the **`(WorkGroup, Specification, Type) → Topic → Linked
Ticket Group`** decomposition for **one workgroup** at a time on the
**planner** side and writes it into the **jira-planner** service over
HTTP. Each `(Spec, Type)` partition is PUT to
`/api/v1/planned-ticket-topics` so the applying sub-site under
[`tools/ticket-site`](../../../tools/ticket-site/README.md) can
render the populated topic surface directly from the planner DB.

This skill is the planner-side counterpart to
[`topic-groupings`](../topic-groupings/SKILL.md). The two are
**structurally symmetric** but consume different signals: the
preparer side uses `prepared_ticket_related_jira` link edges; the
planner side uses repo / file-path / affected-file-path overlaps
because the planner has no analyst-authored "linked" edge concept.

The per-workgroup workflow owned here is what
[`orchestrate-planner-topic-groupings`](../orchestrate-planner-topic-groupings/SKILL.md)
fans out across many workgroups in parallel. Invoke this skill
directly for a single workgroup; invoke the orchestrator for bulk
runs.

## Data Access

The skill talks to **three** HTTP surfaces in this order. All planner
reads are `GET`; the only write is a `PUT` per non-empty partition.

1. **jira-source — workgroup catalog**, via the
   [`fhir-augury-cli`](../fhir-augury-cli/SKILL.md) skill:

   ```bash
   fhir-augury-cli --json '{"command":"list-jira-workgroups"}'
   ```

   Use the response's `code`, `name`, `nameClean`, and (when
   present) `retired` fields directly. The unified
   `WorkGroupResolver` accepts any of `code` / `name` / `nameClean`
   on the planner routes, but **`nameClean` is the canonical form**
   for folder paths and URL segments; surface `name` for human-facing
   headings.

2. **planner — clustering signals** (default base URL
   `http://localhost:5172`):

   ```
   GET {plannerBaseUrl}/api/v1/planned-ticket-clustering-signals/{workGroupClean}
   ```

   Returns a `PlannedTicketClusteringSignalsDto` whose `Tickets[]`
   carries, per ticket: `IssueKey`, partition / display fields
   (`Title`, `Status`, `Specification`, `Type`), the abort gate
   `HydrationStatus`, the parity flag `HasPlannedTicket`, prose
   fields (`ResolutionSummary`, `FeatureProposal`,
   `DesignRationale`), and three list projections that drive the
   four-tier clustering hierarchy: `Repos`, `RepoChanges`
   (`{RepoKey, FilePath}`), and `RepoImpacts`
   (`{RepoKey, AffectedFilePath}`).

   - `404` means "no hydrated tickets at all for this workgroup" —
     report the workgroup as `skipped: 404` and move on. Do **not**
     abort the entire run.
   - `5xx` aborts this workgroup; capture the body and continue with
     the next workgroup (handled by the orchestrator).

3. **planner — hydration display projection** (same base URL):

   ```
   GET {plannerBaseUrl}/api/v1/planned-ticket-hydration/{workGroupClean}
   ```

   The response is a `PlannedJiraHydrationDisplayResponse` whose
   envelope contains a `WorkGroupClean` (cleaner output) and a
   `Results[]` array of self-Jira hydration rows. **Note that the
   envelope does not include a `WorkGroupDisplay` field** — the
   display string can only be recovered from a row's `WorkGroup`
   value. The display-resolution fallback chain is therefore:

   1. The clustering-signals envelope's `WorkGroupDisplay` (resolved
      by the planner via the same two-tier preparer-parity rule);
   2. The first non-empty `WorkGroup` value across any
      `Results[].WorkGroup` row from this endpoint;
   3. The catalog `name` from `list-jira-workgroups`.

   If all three come up empty (extremely unlikely once any hydration
   has run), abort the workgroup and report — the validator gate is
   that the **payload must carry a non-empty `WorkGroupDisplay`**
   when the row is first written (a later run can recompute it from
   the topic table via the planner's two-tier fallback).

4. **planner — existing topics (per partition)**, used only for
   informational diff in the run report:

   ```
   GET {plannerBaseUrl}/api/v1/planned-ticket-topics/{wg}/{spec}/{type}
   ```

   Percent-encode `{spec}` and `{type}` — both routinely contain
   spaces (`FHIR Core`, `Change Request`).

5. **planner — topic write endpoint** (same base URL):

   ```
   PUT {plannerBaseUrl}/api/v1/planned-ticket-topics
   ```

   The route **does not include** `{wg}/{spec}/{type}` — those fields
   live on the request body. The body is a
   `PlannedTicketTopicGroupingRequest` carrying
   `WorkGroupClean`, `WorkGroupDisplay`, `Specification`, `Type`,
   and `Topics[]`. The validator
   (`PlannedTicketTopicGroupingPayloadValidator`) enforces:

   - `WorkGroupClean`, `WorkGroupDisplay`, `Specification`, `Type`
     are all required (non-blank);
   - each `Topic.ShortDescription` is required (non-blank);
   - each `SpannedRepos` entry matches `owner/name` (the
     `RepoKeyRegex`); case-insensitive duplicates are silently
     de-duplicated by `NormalizeSpannedRepos` at persist time
     (informational only — not a validation failure);
   - `LinkedTicketGroup.FirstTicketKey`, every
     `LinkedTicketGroup.Member.TicketKey`, and every
     `RemainingTicketKeys` entry match the Jira-key regex
     `^[A-Z]+-\d+$`.

   The planner controller only catches `ArgumentException` for 400s
   (validator failures). Anything else — including SQLite write
   contention — surfaces as a 5xx.

## Inputs

- **Workgroup selector** *(required)* — a **single** value, one of:
  - an HL7 work-group code (e.g., `oo`, `pc`, `fhir`);
  - a `name` (e.g., `Orders and Observations`);
  - a `nameClean` slug (e.g., `OrdersAndObservations`).

  Multi-value selection is the orchestrator's job; this skill rejects
  comma-separated input. The resolved `nameClean` is what gets passed
  to the planner endpoints below — **do not re-derive it**.

- **Planner base URL** *(optional, default `http://localhost:5172`)*
  — the planner service's base URL.

- **Working directory** *(optional)* — scratch space for catalog
  dumps, intermediate JSON, and agent notes. When supplied, **all
  transient files must be written under this directory** rather than
  the repo root.

- **Replace mode** — **`partition` only** in this slot. There is no
  `wipe-first` option here because the planner exposes neither a
  workgroup-level partition list endpoint nor a DELETE for topics,
  both of which a real `wipe-first` implementation would require to
  discover stale `(spec, type)` partitions and clear them. The
  per-tuple wipe primitive (an empty `Topics: []` PUT for a specific
  `(wg, spec, type)`) is preserved by `SaveTopicGroupingAsync` so a
  future slot can ship `wipe-first` on top of it, but this skill does
  not invoke it.

## Behaviour rules

- **Read-only against jira-source.** Only `list-jira-workgroups`.
- **Read-only against the planner for clustering inputs.** The skill
  issues `GET` only against the clustering-signals, hydration, and
  (optionally) topics read endpoints. The only state changes are the
  `PUT`s against `/api/v1/planned-ticket-topics`.
- **Drop `HasPlannedTicket = false` tickets before authoring prose.**
  The validator does **not** gate on plan existence
  (`PlannedTicketTopicGroupingPayloadValidator` does not query
  `planned_tickets`), so a payload that names a key without a
  `planned_tickets` row will succeed at PUT time. However, the
  applying sub-site's trimmer at
  [`tools/ticket-site/PlannerDbTrimmer.cs`](../../../tools/ticket-site/PlannerDbTrimmer.cs)
  (lines 94-127) strips topic members whose `TicketKey` is not in
  `planned_tickets` and then drops the orphan topic / topic-repo
  rows, so leaving such keys in silently produces topics that render
  as empty after trim. Drop these tickets before clustering; record
  the count in the run report under the `no-planned-ticket` drop
  reason.
- **`RemainingTicketKeys` is the only place in-partition tickets
  not in a Linked Ticket Group can appear.** The planner's read path
  does not auto-derive an "in this partition but not in any Topic"
  set the way the preparer's `IndividualTicketKeys` does, so every
  ticket the skill wants visible in the applying sub-site must
  appear explicitly in either a Linked Ticket Group or a
  `RemainingTicketKeys` entry.
- **Abort the whole workgroup on any missing or unresolved self-Jira
  hydration row** (Open Question 3 of slot 0605-01). Pre-flight every
  ticket returned by the clustering-signals envelope: abort whenever
  any returned ticket has `HydrationStatus` either `null` (no
  self-row at all) or not equal to the string `"resolved"`. The
  endpoint's anchor surfaces both cases via `LEFT JOIN`. Recovery
  instruction for the operator: run
  `POST /api/v1/admin/hydration/backfill` on the planner, then
  re-invoke the orchestrator.
- **Partition with the literal string `(unknown)`** when the
  resolved `Specification` or `Type` is null or blank. The applying
  SPA's topic-list query at
  [`tools/ticket-site/web-assets/applying/app.js`](../../../tools/ticket-site/web-assets/applying/app.js)
  (lines 214-238) reads `tt.Specification` / `tt.Type` directly from
  the planner DB with no COALESCE fallback, so the producer must
  write the literal string `(unknown)` — the SPA does not add it.
  (The ticket-list view at the same file, lines 103-108, *does*
  COALESCE to `(unknown)`; the topic surface does not. Cite both
  lines in any maintenance notes so the asymmetry stays obvious.)
- **Percent-encode `{spec}` and `{type}` when reading existing
  topics** via
  `GET /api/v1/planned-ticket-topics/{wg}/{spec}/{type}` — both
  routinely contain spaces (`FHIR Core`, `Change Request`). The PUT
  body carries the same values unencoded, since the route does not
  include them.
- **Retry transient write failures.** The planner's
  `SaveTopicGroupingAsync` runs each PUT under a single
  `BEGIN IMMEDIATE` transaction
  ([`PlannerDatabase.cs`](../../../src/FhirAugury.Processor.Jira.Fhir.Planner.Persistence/Database/PlannerDatabase.cs)
  lines 680-683), and the per-connection `busy_timeout` is 5 s
  ([`SourceDatabase.cs`](../../../src/FhirAugury.Common/Database/SourceDatabase.cs)
  lines 53-60). Under the orchestrator's default concurrency of 3 a
  writer can occasionally hit the timeout and surface as a 5xx
  because the controller only catches `ArgumentException`
  ([`PlannedTicketTopicsController.cs`](../../../src/FhirAugury.Processor.Jira.Fhir.Planner/Controllers/PlannedTicketTopicsController.cs)
  lines 39-50). Retry 5xx responses (and obvious SQLite-busy `error`
  payloads when surfaced as 4xx-with-busy-text) with short
  exponential backoff up to **3 attempts** total before recording
  the partition as a per-workgroup failure. Capture every retry in
  the run report.
- **Idempotent membership, drifting prose.** Cluster *membership* is
  deterministic per the hierarchy below; agent-authored
  `ShortDescription` / `LongerDescription` / `Rationale` may shift
  between runs. The run report must call this out.
- **No README rendering.** This skill writes only to the planner DB.
  The reviewer's applying sub-site under `tools/ticket-site` reads
  the rendered shape directly, and a future `index-planned-db`
  reader skill will mirror what `index-prepared-db` does on the
  preparer side.

## Clustering hierarchy (Open Question 2, frozen)

Cluster *membership* follows this strict four-tier hierarchy. The
prose may drift between runs; membership must not.

1. **Same-repo + intersecting file paths** *(highest signal)*. Two
   tickets that both list `RepoKey = R` in their `Repos[]` and share
   at least one `RepoChanges.FilePath` value within that repo
   (case-insensitive path comparison) are in the same Topic.

2. **Overlapping repo set, no path intersection.** Two tickets whose
   `Repos[]` sets overlap (case-insensitive) but share no path are
   in the same Topic *unless* they fail a sanity check (different
   `Specification`, opposite recommendation outcomes, etc.); in
   those cases keep them separate and note the reason in the
   Topic's `LongerDescription`.

3. **Shared `AffectedFilePath` across repo boundaries.** Two
   tickets that touch the same `AffectedFilePath` (case-insensitive)
   even across different `RepoKey` values join the same Topic.

4. **Prose similarity (tiebreaker only).** When tiers 1-3 do not
   merge two tickets and the `ResolutionSummary` / `FeatureProposal`
   / `DesignRationale` prose strongly overlap, prose may break ties.
   **Never the primary signal.**

Singletons (tickets that share no repo-and-path overlap with any
other ticket in the partition) become a **one-Topic-per-ticket**
entry where the singleton lives under `RemainingTicketKeys` — they
do **not** drop out of the topic surface entirely.

Schema reminder (from the slot's source request):
`planned_ticket_topic_repos (Id, TopicRowId, RepoKey, OrderInTopic)`
is the first-class table that makes `SpannedRepos` a primary
clustering signal — the planner stores per-Topic spanned repos
independently of any specific ticket's `Repos[]`, so the PUT
payload must carry `SpannedRepos` per Topic even though it is the
union of the Topic's member tickets' `Repos[]`.

## Workflow

### Step 1: Resolve scope

1. Call `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`
   exactly **once**. Build two in-memory maps:
   `codeLower → nameClean` and `nameCleanLower → nameClean` (and
   include `nameLower → nameClean` as a third for convenience).
2. Resolve the single workgroup selector against those maps
   (case-insensitive). On no match, abort with a clear error.
3. Let `wg` = the resolved `nameClean`. This is the value passed to
   every planner endpoint.

### Step 2: Fetch clustering inputs

1. `GET {plannerBaseUrl}/api/v1/planned-ticket-clustering-signals/{wg}`
   → `PlannedTicketClusteringSignalsDto`.
   - On `404`: report `skipped: 404` and exit.
   - On `5xx`: report `aborted: <status>` with the body and exit.
   - Capture `WorkGroupDisplay` from the envelope.

2. **Pre-flight Open Question 3.** For every ticket in
   `Tickets[]`, require `HydrationStatus == "resolved"`. Build a
   failure list of `(IssueKey, HydrationStatus)` for every ticket
   whose `HydrationStatus` is `null` or anything other than
   `"resolved"`. If the list is non-empty, abort the workgroup with
   `aborted: missing-or-unresolved-self-jira` and include the
   failure list verbatim in the run report. Direct the operator to
   run `POST /api/v1/admin/hydration/backfill` and re-invoke.

3. **Fallback `WorkGroupDisplay`.** If the clustering envelope's
   `WorkGroupDisplay` is null or empty, fetch
   `GET {plannerBaseUrl}/api/v1/planned-ticket-hydration/{wg}` and
   take the first non-empty `Results[].WorkGroup` value. If that
   is also empty, take the catalog `name` from step 1. If all three
   are empty, abort with `aborted: no-workgroup-display`.

4. Drop tickets with `HasPlannedTicket = false` from the clustering
   set. Record the count and the dropped `IssueKey`s in the run
   report under the `no-planned-ticket` drop reason.

### Step 3: Partition by `(Specification, Type)`

For each surviving ticket:

1. Substitute the literal string `(unknown)` for `Specification`
   when it is `null` or whitespace.
2. Substitute the literal string `(unknown)` for `Type` when it is
   `null` or whitespace.
3. Place the ticket into the partition keyed by the resulting
   `(Specification, Type)` tuple.

Within each partition, sort tickets by `IssueKey` ascending and
remember that order. Record partitions that ended up using
`(unknown)` in either coordinate so the run report can surface them
verbatim (the applying SPA will display the literal `(unknown)`).

### Step 4: Cluster each partition into Topics

For each non-empty `(Specification, Type)` partition, apply the
four-tier hierarchy above:

1. **Tier 1 — same-repo + intersecting file path.** Build an
   undirected graph whose nodes are the partition's tickets and
   whose edges connect two tickets that share at least one
   `(RepoKey, FilePath)` pair across their `RepoChanges[]` (compare
   `RepoKey` and `FilePath` case-insensitively). Each connected
   component of size ≥ 2 is a **Linked Ticket Group seed**.
2. **Tier 2 — overlapping repo set.** Merge two Tier-1 components
   (or attach a singleton to one) when their `Repos[]` sets overlap
   case-insensitively. Skip the merge when it would obviously
   produce an incoherent Topic (different `Specification`,
   contradictory `ResolutionSummary` directions, etc.).
3. **Tier 3 — shared `AffectedFilePath`.** Apply the same merge
   logic on `(AffectedFilePath)` (case-insensitive), ignoring
   `RepoKey`.
4. **Tier 4 — prose similarity.** Only as a tiebreaker. Do **not**
   form a Topic on prose alone; prefer leaving a ticket as its own
   Topic + `RemainingTicketKeys` entry over inventing a weak Topic.
5. Every surviving cluster becomes one Topic. Tickets that did not
   join any cluster become **one-Topic-per-ticket** entries where
   the singleton lives under that Topic's `RemainingTicketKeys`.

For every Topic:

- **`SpannedRepos`** — the case-insensitive union of every member
  ticket's `Repos[]`, ordered by first appearance in
  `IssueKey`-ascending traversal. The validator's
  `NormalizeSpannedRepos` will silently de-duplicate
  case-insensitively at persist time; doing it producer-side keeps
  the run report accurate.
- **`ShortDescription`** — 3-8 word title-style phrase. Plain prose,
  no block markdown. Drive from the cluster's shared repo / path /
  prose; never invent.
- **`LongerDescription`** — 1-3 sentences. Plain prose, no block
  markdown. Surface the dominant tier (e.g., "Same-repo edits to
  `source/observation.html`") plus any sanity-check note that
  blocked a merge.
- **`LinkedTicketGroups`** — every Tier-1 connected component of
  size ≥ 2 whose members are all in this Topic becomes one
  `LinkedTicketGroup`:
  - `FirstTicketKey` = the **lowest-keyed** ticket in the component
    (ascending Jira-key sort);
  - `Rationale` = 1-3 sentences naming the shared repo + file path
    that produced the edge. Plain prose, no block markdown;
  - `Members` = every component member, ascending `IssueKey`, with
    sequential `Order` starting at `0`. `FirstTicketKey` **must**
    appear in its own `Members` list (the validator enforces it).
- **`RemainingTicketKeys`** — every ticket assigned to this Topic
  that is **not** in any `LinkedTicketGroup`, in ascending key
  order. Singletons (one-ticket Topics) have a single entry here.
- **`RenderOrderHint`** — leave as `null` (Open Question 4 of slot
  0605-01 — the planner's read endpoint orders un-hinted Topics
  deterministically already).

### Step 5: Write each partition

For each non-empty partition:

1. Build a `PlannedTicketTopicGroupingRequest`:

   ```json
   {
     "WorkGroupClean": "OrdersAndObservations",
     "WorkGroupDisplay": "Orders and Observations",
     "Specification": "FHIR Core",
     "Type": "Change Request",
     "Topics": [
       {
         "ShortDescription": "...",
         "LongerDescription": "...",
         "RenderOrderHint": null,
         "SpannedRepos": ["HL7/fhir", "HL7/fhir-extensions"],
         "LinkedTicketGroups": [
           {
             "FirstTicketKey": "FHIR-1",
             "Rationale": "Both edit source/observation.html in HL7/fhir.",
             "Members": [
               { "TicketKey": "FHIR-1", "Order": 0 },
               { "TicketKey": "FHIR-2", "Order": 1 }
             ]
           }
         ],
         "RemainingTicketKeys": ["FHIR-3"]
       }
     ]
   }
   ```

2. `PUT {plannerBaseUrl}/api/v1/planned-ticket-topics` with that
   body.

   - `204 No Content` → success. Record `Topics`,
     `LinkedTicketGroups`, and member counts in the run report.
   - `400 Bad Request` → validator rejected the payload. Capture
     the `{error: ...}` body verbatim into the run report and
     continue with the next partition; do **not** retry with edited
     content. The body shape is `{ "error": "<concatenated
     messages>" }` per
     `PlannedTicketTopicsController.PutTopics`.
   - `5xx` → transient write failure (most likely SQLite busy under
     contention). Retry with short exponential backoff up to **3
     attempts** total; if all three fail, record the partition as
     failed and continue with the next partition.

### Step 6: Per-workgroup report

After every partition has been processed, emit a single per-workgroup
report block containing:

- **Totals**: tickets considered, tickets kept, partitions written,
  Topics created, Linked Ticket Groups created.
- **Drops**, with counts and keys:
  - `no-planned-ticket` — `HasPlannedTicket = false`;
  - `aborted: missing-or-unresolved-self-jira` — Open Question 3
    aborted the whole workgroup before partitioning (this is a
    *workgroup-level* abort, not a per-ticket drop, but the
    triggering keys are listed here);
  - `unpartitionable` — defensive: tickets whose post-substitution
    partition still ended up empty (should not happen given the
    `(unknown)` substitution, but record any anyway).
- **`(unknown)` chrome usage**: list any `(Spec, Type)` partitions
  that used the literal `(unknown)` in either coordinate; the
  applying SPA will render the string verbatim.
- **Validator failures**: every `400` response with its captured
  `error` body and the partition that triggered it.
- **Retry events**: every 5xx PUT response that was retried, plus
  whether the retry eventually succeeded.
- **Prose-drift caveat**: a one-line reminder that
  `ShortDescription` / `LongerDescription` / `Rationale` may shift
  between runs even when membership is stable, so this skill is not
  a deterministic regression baseline for prose fields.

## Important Rules

1. **Selectors accept `code`, `name`, or `nameClean`.** The planner's
   controllers route every selector through the shared
   `WorkGroupResolver` (and defensively call
   `Hl7WorkGroupNameCleaner.Clean` in
   `PlannedTicketHydrationController` /
   `PlannedTicketTopicsController` /
   `PlannedTicketClusteringSignalsController`), so any of the three
   forms resolves to the same canonical workgroup. Folder slugs are
   still produced by `Hl7WorkGroupNameCleaner.Clean` for output
   path stability.
2. **Hydration freshness is the caller's problem.** This skill does
   not trigger the planner's hydration sweeper. The
   missing-or-unresolved abort rule (Open Question 3) directs the
   operator to run the backfill admin endpoint and re-invoke the
   orchestrator.
3. **Each ticket exists at most once in the payload** — in a
   Linked Ticket Group **or** under `RemainingTicketKeys`, never
   both, and never under two Topics. Validate before sending.
4. **No block markdown in prose fields** — no `#` headings, no
   fenced code blocks, no leading `- ` bullets in
   `ShortDescription`, `LongerDescription`, or `Rationale`.
5. **Source request / `plan.md` are not consumed by this skill.**
   It is invoked directly by the user (or by
   `orchestrate-planner-topic-groupings`).
6. **`wipe-first` is not supported in this slot.** The planner
   exposes neither a workgroup-level partition list endpoint nor a
   DELETE. The per-tuple empty-PUT primitive — passing
   `"Topics": []` for a specific `(wg, spec, type)` body wipes that
   tuple, proven by the regression test
   `SaveTopicGroupingAsync_EmptyTopicsList_WipesExistingTuple` — is
   preserved by `SaveTopicGroupingAsync` but not invoked by this
   skill. A future slot can ship `wipe-first` once it also ships
   the listing primitive.
