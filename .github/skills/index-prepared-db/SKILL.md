---
name: index-prepared-db
description: "Builds a README.md index for one or more workgroups of prepared FHIR tickets, sourced from the jira-preparer service. USE FOR: indexing prep output from the canonical DB, generating workgroup ballot indexes from already-grouped data, producing per-workgroup tables of contents without re-clustering. Calls the preparer's prepared-ticket-groupings and prepared-ticket-hydration endpoints, plus jira-source for workgroup enumeration and the fhir-augury-cli fallback chain for Reporter / Created back-fill, then writes a structured README.md per workgroup with a Table of Contents, per-specification sections, four canonical type subsections, topic groupings, linked-ticket sub-groups, and ticket tables. Sibling of (not a replacement for) index-prepared."
---

# Index Prepared (DB) Skill

Builds a `README.md` index for each requested workgroup, sourced from
the **jira-preparer** service rather than from on-disk prep markdown.
The reviewer-facing layout is the same one
[`index-prepared`](../index-prepared/SKILL.md) produces — Table of
Contents, per-Specification sections, the four canonical Type
subsections, Topics, Linked Ticket Groups, and ticket tables — but the
decomposition and per-ticket display fields come from the preparer DB
(via HTTP) so two runs against the same backing database always agree
and so the on-disk prep files are no longer the source of truth for
grouping.

This skill is a **sibling** of `index-prepared`, not a replacement.
`index-prepared` continues to own the file-driven path; pick this skill
when you want the preparer DB to be authoritative.

## Data Access

The skill talks to three HTTP surfaces, in this order, all via
`GET`-only requests:

1. **jira-source — workgroup catalog**, via the `fhir-augury-cli`
   skill:

   ```bash
   fhir-augury-cli --json '{"command":"list-jira-workgroups"}'
   ```

   Use the response's `code`, `nameClean`, and (when present)
   `retired` fields directly. Per the `fhir-augury-cli` skill, **do
   not re-derive `nameClean`** — pass it through to the preparer.

2. **preparer — grouping** (default base URL
   `http://localhost:5171`):

   ```
   GET {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{workGroupClean}
   ```

   Returns a `PreparedTicketGroupingWorkGroupDto` — the authoritative
   `(Specification, Type) → Topic → Linked Ticket Group` decomposition,
   the workgroup display name, and per-partition `IndividualTicketKeys`
   and `UnattributedTicketCount`. `404` means "no partitions" (render
   an empty README skeleton — see Workflow); `5xx` aborts that
   workgroup.

3. **preparer — hydration display projection** (same base URL):

   ```
   GET {preparerBaseUrl}/api/v1/prepared-ticket-hydration/{workGroupClean}
   ```

   Returns a `PreparedJiraHydrationListResponse` — per-ticket display
   fields (`Title`, `Status`, `Type`, `Specification`, `WorkGroup`,
   `Url`, `UpdatedAt`) keyed by ticket key. Always `200 OK`; empty
   `Items` means "no hydrated tickets for this workgroup".

4. **fhir-augury-cli — per-ticket back-fill for Reporter and Created
   only**, one call per ticket per run (cache by key):

   ```bash
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":false,"includeComments":false,"includeSnapshot":false}'
   ```

   The preparer does not persist Reporter / Created today, so the
   standard `fhir-augury-cli` fallback chain (CLI → MCP → direct HTTP
   → `appsettings.json`) is used to fill those two fields. **Do not
   use the CLI to back-fill Title / Status / Type / Specification /
   WorkGroup / Url / UpdatedAt** — the preparer is the source of
   truth for those; render `(unknown)` instead.

## Inputs

- **Workgroup selector** *(required)* — one of:
  - a single HL7 work-group code (e.g., `oo`, `pc`, `fhir`) — the
    preferred user-facing identifier;
  - a single `nameClean` slug (e.g., `OrdersAndObservations`) — accepted
    for convenience;
  - a comma-separated list mixing the two forms; or
  - the literal `all`.

  Selectors are resolved against the catalog returned by
  `fhir-augury-cli list-jira-workgroups`, matching case-insensitively
  against both `code` and `nameClean`. The resolved `nameClean` is
  what gets passed to the preparer endpoints. Unresolved selectors are
  **reported and skipped** — the run does not abort.

- **Output directory** *(required)* — root directory under which the
  skill writes `<outputDirectory>/<workGroupClean>/README.md`,
  creating intermediate directories as needed. The output
  subdirectory always uses the resolved `nameClean` (never the code),
  matching the layout `orchestrate-prep` and `index-prepared` already
  produce.

- **Preparer base URL** *(optional, default `http://localhost:5171`)*
  — the preparer service's base URL. The skill issues only `GET`
  requests against it.

- **Working directory** *(optional)* — directory the agent may use
  for transient files (catalog dump, CLI responses, scratch notes).
  When supplied, all transient files must be written under this
  directory rather than the repo root or the output directory.
  Create cross-platform if missing (`mkdir -p` / `New-Item
  -ItemType Directory -Force`).

## Behaviour rules

- **Read-only against the preparer.** Only `GET` requests — never
  `PUT`, `POST`, or `DELETE`. Hydration freshness is the caller's
  problem; the skill does not gate on `HydrationStatus` and does
  not trigger the preparer's hydration sweeper.
- **Read-only against jira-source.** Only `list-jira-workgroups` and
  per-ticket `get` for Reporter / Created back-fill. No ingestion
  triggers.
- **Idempotent overwrite of `README.md`.** Always overwrite if
  present. Never read or write any other file in the output
  directory — this skill is not allowed to garbage-collect old
  per-ticket `FHIR-*.md` files (`orchestrate-prep` owns those).
- **Do not re-cluster.** The preparer response is the authority for
  Topics and Linked Ticket Groups; the skill must not invent,
  merge, or split them.
- **Each ticket appears exactly once** in the rendered README — in
  its Linked Ticket Group, its Topic's remaining-tickets table, or
  its Type's Individual Tickets table.
- **Do not invent data.** If both the hydration projection and the
  CLI back-fill chain return blank/unknown for a field, render the
  field as `(unknown)` and continue. Do not fabricate titles,
  statuses, reporters, or dates.

## Workflow

### Step 1: Resolve scope

1. Verify the output directory parent is writable; create it if it
   does not exist.
2. Call `fhir-augury-cli --json '{"command":"list-jira-workgroups"}'`
   exactly **once** at the start of the run. Build two in-memory
   maps from the response: `codeLower → nameClean` and
   `nameCleanLower → nameClean`.
3. Expand the workgroup selector:
   - **`all`** → every `nameClean` in the catalog. If the catalog
     response carries a `retired` flag, skip rows where `retired` is
     true unless the caller explicitly opts in. If the response shape
     does not include `retired`, flag this in the run report and
     include every entry.
   - **Comma-separated entries** → look each up in both maps
     (case-insensitive). On no match for an entry, report it and skip
     it; do **not** abort the run.
4. The resolved `nameClean` (let's call it `wg`) is the value passed
   to the preparer endpoints. The output subdirectory uses the same
   `wg`.

### Step 2: For each workgroup, fetch grouping + hydration

For each resolved `wg`, in this order:

1. `GET {preparerBaseUrl}/api/v1/prepared-ticket-groupings/{wg}`
   → `PreparedTicketGroupingWorkGroupDto`.
   - On `404`: treat the workgroup as having zero partitions. Still
     render the empty README skeleton (top-level `# {Work Group
     display name}` heading from the catalog `name`, an empty Table
     of Contents, no Specification sections). Continue to step 3 of
     this workgroup so the optional hydration-only summary still
     gets reported.
   - On `5xx`: abort **this workgroup** with a clear error message
     and **do not write** a `README.md` for it. Continue with the
     next workgroup.
2. `GET {preparerBaseUrl}/api/v1/prepared-ticket-hydration/{wg}`
   → `PreparedJiraHydrationListResponse`. Build an in-memory
   `ticketKey → display` map keyed by `TicketKey`.
   - On `5xx`: abort this workgroup the same way as for the grouping
     endpoint.
3. For every ticket key referenced in the grouping response but
   missing from the hydration map, record the gap; the README row
   for that ticket renders `(unknown)` for Title / Status (no CLI
   fallback for those — the FR constrains the preparer to be the
   source of truth).
4. For every ticket key referenced in the grouping response, call
   `fhir-augury-cli --json
   '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":false,"includeComments":false,"includeSnapshot":false}'`
   once per run to back-fill Reporter and Created. Cache the result
   by ticket key for the remainder of the run. If every step of the
   `fhir-augury-cli` fallback chain fails, render Reporter and/or
   Created as `(unknown)` and continue.

### Step 3: Render the README

Compose `README.md` per [README format](#readme-format) and write it
to `<outputDirectory>/<wg>/README.md`, overwriting any prior content.
The H1 heading **always** comes from
`PreparedTicketGroupingWorkGroupDto.WorkGroupDisplay` — never from any
per-ticket `WorkGroup` field in the hydration response (see
[Important Rules](#important-rules)).

### Step 4: Report per-workgroup stats

After writing the README, append to the per-workgroup section of the
run report:

- Total tickets rendered (sum across partitions).
- Breakdown by Type (`Comment`, `Question`, `Technical Correction`,
  `Change Request`, plus any extras).
- Count of `(unknown)` fields, with reasons (hydration missing /
  CLI fallback failed / etc.).
- `UnattributedTicketCount`, summed across partitions — this is
  **not rendered** in the README, only reported here.
- `hydrated-without-partition: N` — count of ticket keys present in
  the hydration response but absent from every grouping partition.

## Ticket table format

Every ticket table — whether inside a Linked Ticket Group, a Topic's
remaining-tickets table, or the Individual Tickets section — uses the
same five columns, identical to [`index-prepared`](../index-prepared/SKILL.md):

| Column | Content |
|--------|---------|
| Ticket | Two links separated by a space: `[FHIR-XXXXX](./FHIR-XXXXX.md)` (relative link inside this directory — the actual file may not exist when this skill runs, but the link is stable and matches `orchestrate-prep`'s layout) and `[Jira](https://jira.hl7.org/browse/FHIR-XXXXX)`. Use the hydration projection's `Url` for the Jira link target when present; only fall back to building `https://jira.hl7.org/browse/{key}` when `Url` is null. |
| Title | From the hydration projection's `Title`. `(unknown)` when missing. |
| Status | From the hydration projection's `Status`. `(unknown)` when missing. |
| Reporter | From the `fhir-augury-cli get` back-fill. `(unknown)` when the back-fill chain fails. |
| Created | From the `fhir-augury-cli get` back-fill, formatted as `YYYY-MM-DD` when parseable. `(unknown)` when the back-fill chain fails. |

Row ordering rules:

- **Linked Ticket Group tables** — ascending by ticket key. (The
  preparer payload supplies a `Members` list with an explicit
  `Order` field; render in that order, falling back to ascending key
  for ties.)
- **Topic remaining-tickets tables** — ascending by ticket key, matching
  the order in `PreparedTicketGroupingTopicDto.RemainingTicketKeys`.
- **Individual Tickets tables** — ascending by ticket key.

Render the table in standard GitHub-flavoured Markdown.

## README format

The generated `README.md` MUST follow this structure. Sections in
braces are filled in per workgroup; instructional commentary in
braces is replaced by actual generated content.

````markdown
# {PreparedTicketGroupingWorkGroupDto.WorkGroupDisplay}

Index of prepared tickets for this workgroup. Generated by the
`index-prepared-db` skill from the `jira-preparer` service.

- **Total prepared tickets:** {N}
- **Specifications covered:** {comma-separated list of Specification
  names, in render order}

## Table of Contents

{One bullet per Specification section, in render order. The link
target is the GitHub-style anchor of the Specification heading.}

- [{Specification name}](#{anchor})
- …

---

## {Specification name}

{Repeat the block below for every Type subsection in the canonical
order: Comment, Question, Technical Correction, Change Request, then
any other types found, sorted alphabetically (case-insensitive). The
four canonical Type subsections are always present even when empty;
other Types only appear when they have at least one ticket. When a
canonical (Specification, Type) pair has no partition in the
grouping response, synthesise an empty subsection client-side using
the italicised note below.}

### {Type}

{If the Type bucket is empty (only possible for Comment / Question /
Technical Correction / Change Request):}

*No tickets of this type are prepared.*

{If the Type bucket has tickets, render every Topic with ≥ 2 tickets
first (in Topic render order — see below), then a final Individual
Tickets section if any 1-ticket topics or partition-individual
tickets exist.}

#### Topic: {topic.ShortDescription}

{topic.LongerDescription verbatim — 1–3 sentences.}

{For each LinkedTicketGroup in topic.LinkedTicketGroups, in
LinkedTicketGroup.OrderInTopic order:}

##### Linked Ticket Group: {linkedTicketGroup.FirstTicketKey}

{linkedTicketGroup.Rationale verbatim — 1–3 sentences.}

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| [FHIR-XXXXX](./FHIR-XXXXX.md) [Jira](https://jira.hl7.org/browse/FHIR-XXXXX) | … | … | … | … |

{After all Linked Ticket Groups in the Topic, render the Topic's
remaining tickets (topic.RemainingTicketKeys) as a single ticket
table, ordered ascending by key. Omit this table when the Topic has
no remaining tickets.}

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| … | … | … | … | … |

{After all Topics, if the partition has any IndividualTicketKeys
(tickets attributed to the partition but not to any Topic), render:}

#### Individual Tickets

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| … | … | … | … | … |

---

## {Next Specification name}

…
````

### Topic render ordering within a Type

The preparer payload's `PreparedTicketGroupingTopicDto.RenderOrderHint`
is the **primary** sort key when non-null: ascending hint value first
(stable). For topics whose `RenderOrderHint` is null, fall through to
the `index-prepared` ordering: descending by total ticket count
(linked-group members + remaining), then alphabetically by
`ShortDescription` for ties. Topics with null hints are rendered after
all topics with hints (nulls-last).

The Individual Tickets section, when present, is always last within
its Type subsection.

### Anchor generation

Use GitHub's standard heading-to-anchor algorithm: lowercase, replace
spaces with `-`, drop characters other than letters, digits, hyphens,
and underscores, collapse runs of hyphens. If two Specifications would
collide (rare), suffix the duplicate anchors `-1`, `-2`, … in render
order — and use the suffixed anchor in the matching ToC entry.

### `Unspecified` Specification bucket

Collect every partition whose `Specification` is the empty string, the
literal string `Unspecified`, or any of the recognised "none"
placeholders (`(none reported)`, `None recorded`, `None`) into a
single trailing `Unspecified` section. Render only when non-empty.

## Important Rules

- **Read-only against the preparer and the jira-source.** `GET`-only
  HTTP, `list-jira-workgroups` and `get` only on the CLI.
- **One `README.md` per workgroup.** Always written as
  `<outputDirectory>/<workGroupClean>/README.md`. Do not write any
  other file under the output directory.
- **Always overwrite.** Do not preserve any prior `README.md`; the
  skill is meant to be re-runnable.
- **Canonical Type subsections are mandatory.** `Comment`,
  `Question`, `Technical Correction`, and `Change Request` MUST
  appear in every Specification section, even when the preparer
  returns no partition for that `(Specification, Type)` pair (use
  the italicised "No tickets of this type are prepared." note).
  Other types appear only when populated.
- **Source-of-truth split.** The preparer is authoritative for
  Topics, Linked Ticket Groups, Title, Status, Type, Specification,
  Url, and UpdatedAt. The `fhir-augury-cli get` back-fill is allowed
  **only** for Reporter and Created.
- **Do not re-cluster Topics.** Pass the preparer's decomposition
  through unchanged.
- **H1 heading comes from
  `PreparedTicketGroupingWorkGroupDto.WorkGroupDisplay`** — never
  from any per-ticket `WorkGroup` field in the hydration response.
  A stale `prepared_jira_hydration.WorkGroup` would otherwise
  diverge from the grouping endpoint's view.
- **Do not render `UnattributedTicketCount` in the README.** Surface
  it (per partition, per workgroup) only in the per-workgroup run
  report.
- **Do not invent a separate "orphan hydration" section.** Tickets
  present in the hydration endpoint but in no grouping partition
  (e.g., hydrated with null/empty `Type`) are intentionally
  invisible — the grouping endpoint is the authority for what is
  renderable. Report any hydrated-without-partition keys in the
  per-workgroup summary so the engineer can act on them, but never
  render them in the README.
- **Workgroup-clean asymmetry is a known risk.** The jira-source
  `nameClean` comes from `Hl7WorkGroupNameCleaner.Clean`; the
  preparer matches on `REPLACE(WorkGroup, ' ', '')`. The two can
  disagree on names with hyphens or punctuation. The skill passes
  the jira-source `nameClean` through unchanged; if the preparer
  responds empty, report it as such in the run report.

## Example invocations

User: *"Index the prepared tickets for `oo` into `./out/prep/`."*

The skill should:

1. Call `fhir-augury-cli list-jira-workgroups` and resolve `oo` →
   `OrdersAndObservations`.
2. `GET http://localhost:5171/api/v1/prepared-ticket-groupings/OrdersAndObservations`
   and
   `GET http://localhost:5171/api/v1/prepared-ticket-hydration/OrdersAndObservations`.
3. Back-fill Reporter / Created via `fhir-augury-cli get` per ticket
   key, caching by key.
4. Write `./out/prep/OrdersAndObservations/README.md`.
5. Report back with the ticket count, per-Type breakdown,
   `(unknown)`-field counts, `UnattributedTicketCount`, and any
   hydrated-without-partition keys.

User: *"Index every workgroup into `./out/prep/`."*

The skill should:

1. Call `fhir-augury-cli list-jira-workgroups` once. Build the
   `code → nameClean` and `nameCleanLower → nameClean` maps. Expand
   the selector `all` to every non-retired `nameClean` in the catalog.
2. For each resolved `nameClean`, repeat the per-workgroup steps
   above and write `./out/prep/<nameClean>/README.md`.
3. Report back with one section per workgroup, plus a top-level
   summary listing any workgroups skipped (`404` grouping, `5xx`
   from the preparer, etc.).
