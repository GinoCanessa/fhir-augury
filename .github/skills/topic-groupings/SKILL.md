---
name: topic-groupings
description: "Generates Topic and Linked Ticket Group groupings for one or more FHIR workgroups and writes them to the jira-preparer DB. USE FOR: producing the (Specification, Type) → Topic → Linked Ticket Group decomposition that the index-prepared-db skill renders, per-workgroup topic generation, recomputing groupings after new tickets have been hydrated. Calls the preparer's prepared-ticket-clustering-signals, prepared-ticket-hydration, and prepared-ticket-groupings endpoints (GET) for inputs and PUTs each non-empty partition back. Sibling of (not a replacement for) the file-driven index-prepared clustering."
---

# Topic Groupings Skill

Generates the **`(WorkGroup, Specification, Type) → Topic → Linked
Ticket Group`** decomposition for one workgroup at a time and writes
it into the **jira-preparer** service over HTTP. Each `(Spec, Type)`
partition is PUT to
`/api/v1/prepared-ticket-groupings/{workGroupClean}/{spec}/{type}` so
the [`index-prepared-db`](../index-prepared-db/SKILL.md) skill — which
reads the same endpoint — can render Topics and Linked Ticket Groups
directly from the DB instead of inventing them from on-disk markdown.

This skill is the **producer** counterpart to `index-prepared-db`'s
**consumer**. It is a sibling of [`index-prepared`](../index-prepared/SKILL.md),
not a replacement: `index-prepared` continues to cluster on the fly
from on-disk prep markdown; this skill makes the clustering durable in
the preparer DB.

The clustering itself follows the same weighting and rules that
[`index-prepared`](../index-prepared/SKILL.md) Step 5 uses (**linked >
related > shared subject matter**); the only difference is that all
inputs come from the preparer DB (via the new
`prepared-ticket-clustering-signals` endpoint), so the skill never
touches Jira / Zulip / GitHub directly.

## Data Access

The skill talks to **two** HTTP surfaces in this order, all
`GET`-only for reads and `PUT`/`DELETE` only against the grouping
write endpoint:

1. **jira-source — workgroup catalog**, via the `fhir-augury-cli`
   skill:

   ```bash
   fhir-augury-cli --json '{"command":"list-jira-workgroups"}'
   ```

   Use the response's `code`, `nameClean`, and (when present)
   `retired` fields directly. Per the `fhir-augury-cli` skill, **do
   not re-derive `nameClean`** — pass it through to the preparer.

2. **preparer — clustering signals** (default base URL
   `http://localhost:5171`):

   ```
   GET {preparerBaseUrl}/api/v1/prepared-ticket-clustering-signals/{workGroupClean}
   ```

   Returns a `PreparedTicketClusteringSignalsDto` with per-ticket
   `RequestSummary`, `CommentSummary`, `LinkedTicketSummary`,
   `RelatedTicketSummary`, `RelatedZulipSummary`,
   `RelatedGitHubSummary`, partition / display fields
   (`Title`, `Status`, `Specification`, `Type`), a
   `HasPreparedTicket` flag, and a `Links` list of
   `prepared_ticket_related_jira` edges (with `LinkType` typically
   `"linked"` or `"related"`). `404` means "no hydrated tickets" —
   the skill skips the workgroup and reports it; `5xx` aborts this
   workgroup.

3. **preparer — hydration display projection** (same base URL):

   ```
   GET {preparerBaseUrl}/api/v1/prepared-ticket-hydration/{workGroupClean}
   ```

   Returns a `PreparedJiraHydrationListResponse`. Used only for
   `WorkGroupDisplay` resolution (the heading the PUT payload must
   carry) and for tie-breaking display fields if the clustering
   response does not surface them. The skill **must not invent**
   `WorkGroupDisplay` — it always comes from this response (or, when
   the hydration response also lacks it, from the clustering
   response's `WorkGroupDisplay`).

4. **preparer — existing groupings** (same base URL), used only when
   the caller asks for the destructive `wipe-first` replace mode and
   for informational diffing:

   ```
   GET {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{workGroupClean}
   ```

5. **preparer — grouping write endpoints** (same base URL):

   ```
   PUT    {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{workGroupClean}/{spec}/{type}
   DELETE {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{workGroupClean}/{spec}/{type}
   ```

   The `PUT` body is a `PreparedTicketGroupingPutRequest`. The
   validator enforces:
   - every referenced ticket key must exist in `prepared_tickets`
     (the clustering-signals endpoint's `HasPreparedTicket` flag is
     the authoritative gate — drop hydration-only keys before
     building the payload);
   - each Topic must contain **≥ 2 tickets** total
     (singletons must be omitted; the preparer derives them as
     `IndividualTicketKeys` on read);
   - each `LinkedTicketGroup` must contain **≥ 2 members** and its
     `FirstTicketKey` must appear in its own `Members` list;
   - a ticket key may appear **at most once** across all Topics in a
     partition;
   - `ShortDescription`, `LongerDescription`, and `Rationale` are
     plain prose (no block markdown — no `#` headings, no fenced
     code blocks, no leading `- ` bullets at start-of-line);
   - `WorkGroupClean` matches `^[A-Za-z][A-Za-z0-9]+$`.

   Percent-encode `{spec}` and `{type}` — both routinely contain
   spaces (e.g. `FHIR Core`, `Change Request`).

## Inputs

- **Workgroup selector** *(required)* — one of:
  - a single HL7 work-group code (e.g., `oo`, `pc`, `fhir`);
  - a single `nameClean` slug (e.g., `OrdersAndObservations`);
  - a comma-separated list mixing the two forms; or
  - the literal `all`.

  Selectors are resolved against the catalog returned by
  `fhir-augury-cli list-jira-workgroups`, matching case-insensitively
  against both `code` and `nameClean`. The resolved `nameClean` is
  what gets passed to the preparer endpoints — **do not re-derive it**.
  Unresolved selectors are **reported and skipped** — the run does
  not abort.

- **Preparer base URL** *(optional, default `http://localhost:5171`)*
  — the preparer service's base URL.

- **Working directory** *(optional)* — scratch space for catalog
  dumps, intermediate JSON, agent notes. When supplied, **all
  transient files must be written under this directory** rather than
  the repo root.

- **Replace mode** *(optional, default `partition`)* — one of:
  - `partition` *(safer; default)* — `PUT` each `(Spec, Type)`
    partition that has at least one ≥ 2-ticket Topic. Existing
    partitions for `(Spec, Type)` tuples this run does not touch are
    **left alone**. Tickets that have since been dropped from a
    previously-written partition will linger until the next
    `wipe-first` run.
  - `wipe-first` *(destructive)* — before issuing any `PUT`, issue a
    `DELETE` for every `(Spec, Type)` pair the existing-groupings
    fetch reports, so removed tickets / partitions do not linger.
    Use this when the workgroup's hydration just changed materially.

## Behaviour rules

- **Read-only against jira-source.** Only `list-jira-workgroups`.
  No `get` calls — the preparer's `prepared_tickets` table is the
  authoritative summary source, and the clustering-signals endpoint
  exposes it directly.
- **Read-only against the preparer for clustering inputs.** The
  skill issues `GET` only against the clustering-signals, hydration,
  and (optionally) groupings read endpoints. The only state changes
  are `PUT` / `DELETE` against the grouping write endpoints.
- **Each ticket key in the payload must exist in `prepared_tickets`.**
  Drop hydration-only keys (`HasPreparedTicket = false`) before
  building any Topic — they belong nowhere this skill can write.
  Report them in the run summary.
- **Linked Ticket Group members must share a partition.** A
  `(Spec, Type)` partition is authoritative — cross-partition
  `"linked"` edges are **dropped** when building Linked Ticket Groups
  and reported in the run summary.
- **Do not invent edges.** Only the `Links` entries returned by the
  clustering-signals endpoint count for the linked / related
  subgraphs. The summary text fields can suggest **shared subject
  matter** clustering, but never a "linked" edge that the DB has not
  recorded.
- **Idempotent.** The same workgroup state should produce the same
  payload across runs: deterministic key sorting (ascending by
  ticket key), deterministic Topic ordering when no
  `RenderOrderHint` is set, agent-authored prose recomputed only
  when the underlying summaries change. Agent-authored short / long
  descriptions are intrinsically non-deterministic across providers
  and seeds; the skill SHOULD fix random seeds where the runtime
  supports it, and the run report must call out that text fields may
  shift between runs.
- **No README rendering.** This skill writes only to the DB. The
  caller pairs this skill with `index-prepared-db` to refresh the
  human-readable README.

## Workflow

### Step 1: Resolve scope

1. Call `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`
   exactly **once** at the start of the run. Build two in-memory
   maps from the response: `codeLower → nameClean` and
   `nameCleanLower → nameClean`.
2. Expand the workgroup selector:
   - **`all`** → every `nameClean` in the catalog. If the catalog
     response carries a `retired` flag, skip rows where `retired` is
     true unless the caller explicitly opts in.
   - **Comma-separated entries** → look each up in both maps
     (case-insensitive). On no match for an entry, report it and
     skip it; do **not** abort the run.
3. The resolved `nameClean` (let's call it `wg`) is the value passed
   to the preparer endpoints below.

### Step 2: Fetch clustering inputs for the workgroup

For each resolved `wg`, in this order:

1. `GET {preparerBaseUrl}/api/v1/prepared-ticket-hydration/{wg}`
   → `PreparedJiraHydrationListResponse`.
   - Build the `ticketKey → display` map keyed by `TicketKey`.
   - Record `WorkGroupDisplay` from the envelope. If the envelope's
     `WorkGroupDisplay` is null/empty, fall back to the clustering
     response (next step). If both are null/empty, **abort this
     workgroup** and report it — the validator requires a non-empty
     `WorkGroupDisplay`.
2. `GET {preparerBaseUrl}/api/v1/prepared-ticket-clustering-signals/{wg}`
   → `PreparedTicketClusteringSignalsDto`.
   - On `404`: no hydrated tickets, nothing to write. Report and
     move on.
   - On `5xx`: abort this workgroup and report; continue with the
     next workgroup.
   - Update `WorkGroupDisplay` from this envelope if it was missing
     in step 1.
3. **(Optional, for `wipe-first`)** `GET
   {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{wg}` →
   `PreparedTicketGroupingWorkGroupDto`. Capture the list of
   `(Specification, Type)` pairs currently stored so step 4.6 can
   DELETE them. A `404` here means "no existing partitions" and is
   fine — skip the DELETE pass.

### Step 3: Filter and partition

1. Build the **eligible-key set** from the clustering signals:
   - Drop tickets with `HasPreparedTicket = false` (cannot appear in
     a payload — the validator will reject them). Record the count
     and keys for the run report.
   - Drop tickets where both `Specification` and `Type` are absent
     (no partition to land them in). Record these as
     `unpartitionable` in the run report.
2. **Partition tickets by `(Specification, Type)`**:
   - **Specification**: when null/empty/equal to one of `Unspecified`,
     `(none reported)`, `None recorded`, or `None`, normalize to the
     literal string `Unspecified` (the preparer's storage convention
     and what `index-prepared` uses).
   - **Type**: when null/empty/equal to one of the same bucket names,
     normalize to the literal string `Unspecified`. Tickets with a
     normalized `Type` of `Unspecified` are skipped (the preparer's
     four canonical types — `Comment`, `Question`, `Technical
     Correction`, `Change Request` — are the rendered set; an
     unspecified type cannot be partitioned). Record these as
     `unspecified-type` in the run report.
   - Within each `(Specification, Type)` partition, sort tickets by
     `TicketKey` ascending and remember that order.

### Step 4: Cluster each partition into Topics

For each non-empty `(Specification, Type)` partition:

1. **Build the linked subgraph** from the partition's `Links`
   entries:
   - Edges with `LinkType = "linked"` (case-insensitive) and whose
     `AssociatedTicketKey` is **also in the same partition** form
     the linked subgraph.
   - Cross-partition `"linked"` edges are dropped here; add them to
     the per-workgroup `cross-partition-linked-edges-dropped` list
     for the run report.
   - The connected components of this subgraph are the
     "linked components". Components of size ≥ 2 will become Linked
     Ticket Groups inside their Topic.
2. **Form Topics** by widening the linked components with the rest
   of the partition, following the `index-prepared` Step 5 weighting:
   1. Two tickets in the **same linked component** are in the same
      Topic, always.
   2. Two tickets connected by a `LinkType = "related"` edge (in
      either direction) are likely in the same Topic — merge them
      unless doing so would create an obviously incoherent Topic
      (different specifications, opposite recommendation outcomes,
      etc.; in that case keep them separate and explain in the
      Topic's longer description).
   3. The remaining tickets join Topics by **shared subject
      matter** — overlapping FHIR resources, element paths,
      operation names, and domain terms drawn from
      `RequestSummary`, `CommentSummary`, and `LinkedTicketSummary`.
      Prefer fewer, broader Topics over many narrow ones.
   4. Every ticket joins exactly one Topic.
3. **Drop singleton Topics from the payload.** Topics with exactly
   one ticket are **omitted from the PUT body** — the preparer
   derives them automatically as `IndividualTicketKeys` (the
   "tickets in this partition not in any Topic" set) on read. Do
   **not** add them under `RemainingTicketKeys`; that field is for
   in-Topic tickets only.
4. **Author Topic prose** for every surviving Topic (≥ 2 tickets):
   - `ShortDescription` — 3–8 word title-style phrase. Plain prose,
     no markdown.
   - `LongerDescription` — 1–3 sentences. Plain prose, no block
     markdown.
   - Drive prose from the tickets' summary fields. If the summaries
     are sparse, fall back to the partition's `Specification` /
     `Type` and shared domain terms; never invent.
5. **Build Linked Ticket Groups inside each Topic**:
   - Every linked subgraph component of size ≥ 2 whose members are
     all in this Topic becomes one `LinkedTicketGroup`.
   - `FirstTicketKey` — the **lowest-keyed ticket** in the
     component (ascending Jira-key sort).
   - `Rationale` — a 1–3 sentence agent-authored explanation of why
     the linked edges co-cluster these tickets. Plain prose; no
     block markdown.
   - `Members` — every component member, in ascending key order,
     with sequential `Order` starting at `0`. `FirstTicketKey` must
     appear in its own `Members` list (the validator enforces this).
6. **Build `RemainingTicketKeys`** for each Topic — the Topic's
   tickets that are **not** in any Linked Ticket Group, in ascending
   key order. Topics with no remaining tickets emit an empty
   `RemainingTicketKeys` list.
7. **Optional render order.** Leave `RenderOrderHint` as `null`
   unless the agent has a strong opinion (e.g., a "must-read first"
   security topic). The preparer's read endpoint orders un-hinted
   Topics by descending total ticket count, then alphabetical short
   description.

### Step 5: Write partitions to the preparer

1. **(Optional, only when `Replace mode = wipe-first`)** Issue
   `DELETE {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{wg}/{spec}/{type}`
   for every `(spec, type)` pair the step 2.3 fetch reported.
   Treat `404` and `204` as success.
2. For each `(Spec, Type)` partition that has at least one Topic
   surviving step 4.3 (≥ 2 tickets), build the
   `PreparedTicketGroupingPutRequest`:
   ```json
   {
     "WorkGroupDisplay": "<from step 2>",
     "Topics": [
       {
         "ShortDescription": "...",
         "LongerDescription": "...",
         "RenderOrderHint": null,
         "LinkedTicketGroups": [
           {
             "FirstTicketKey": "FHIR-1",
             "Rationale": "...",
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
3. `PUT {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{wg}/{spec}/{type}`
   with that body. Percent-encode `{spec}` and `{type}`.
   - `200 OK` → record `TopicRows / TopicGroupRows / MemberRows`
     from the response in the run report.
   - `400 Bad Request` → the validator rejected the payload.
     Capture the `ProblemDetails.Detail` verbatim into the run
     report and continue with the next partition; do not retry with
     edited content.
   - `5xx` → abort this workgroup and report; continue with the
     next workgroup.

### Step 6: Per-workgroup report

After all partitions for the workgroup have been processed, append
to the per-workgroup section of the run report:

- Total tickets considered (clustering-signals row count).
- Tickets kept (eligible for a payload).
- Tickets dropped by reason:
  - `no-prepared-ticket` (`HasPreparedTicket = false`),
  - `unpartitionable` (no `Specification` or `Type`),
  - `unspecified-type` (normalized `Type = Unspecified`).
- Number of `(Spec, Type)` partitions written.
- Number of Topics and Linked Ticket Groups created.
- Number of cross-partition linked edges dropped, with a sample.
- Any partition-level `400` errors from the PUT.
- Note that agent-authored Topic short / long descriptions may
  shift between runs.

## Important Rules

- **Plan output is read-only.** Source request (`featurerequest.md` /
  `bugreport.md`) and `plan.md` are not consumed by this skill —
  this skill is invoked directly by the user (or by
  `orchestrate-topic-groupings`).
- **Never re-derive `nameClean`.** Always use the catalog's
  `nameClean` verbatim. The `index-prepared-db` skill documents the
  asymmetry between jira-source's `nameClean` (from
  `Hl7WorkGroupNameCleaner.Clean`) and the preparer's
  `REPLACE(WorkGroup, ' ', '')` rule; the clustering-signals
  endpoint uses the preparer rule. If the catalog `nameClean`
  resolves to zero clustering signals despite a non-empty hydration
  response (and zero PUTs go out), **flag this in the run report
  and skip the workgroup**.
- **Hydration freshness is the caller's problem.** This skill does
  not trigger the hydration sweeper. Stale hydration means stale
  partitions; run the sweeper first when freshness matters.
- **Each ticket exists at most once in the payload** — in a Linked
  Ticket Group **or** in `RemainingTicketKeys`, never both, and
  never in two Topics. Validate before sending.
- **No block markdown in prose fields.** No `#` headings, no fenced
  code, no leading `- ` bullets in `ShortDescription`,
  `LongerDescription`, or `Rationale`.
