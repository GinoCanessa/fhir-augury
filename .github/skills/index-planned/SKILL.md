---
name: index-planned
description: "Builds a README.md index for one or more workgroup folders of planned FHIR ticket reports. USE FOR: indexing plan output, generating workgroup implementation indexes, grouping planned tickets by specification/type/topic, producing per-workgroup tables of contents. Reads `ticket-plan` reports under a planned directory (single workgroup folder OR parent planned dir), clusters tickets into topics using ticket content with prerequisite/related tickets prioritized, and writes a structured README.md per workgroup with a Table of Contents, per-specification sections, per-type subsections, topic groupings, prerequisite-ticket sub-groups, and ticket tables."
---

# Index Planned Skill

Builds a `README.md` index for each workgroup folder under a planned
output directory. The index gives a workgroup reviewer a navigable
table of contents over every planned ticket, grouped by Specification
and ticket Type, then clustered into Topics that surface the
relationships between tickets.

This skill is **read-mostly**: it parses the plan markdown produced by
the `ticket-plan` skill, may make small lookups via `fhir-augury-cli`
to fill in missing header fields, and writes one `README.md` per
workgroup. It does not modify the plan files themselves.

## Data Access

The skill works primarily off the on-disk plan files. It only invokes
the `fhir-augury-cli` skill as a **fallback** when a plan file is
missing expected header fields — see [Header field resolution](#header-field-resolution).
Follow the `fhir-augury-cli` fallback chain (CLI → MCP → direct HTTP →
`appsettings.json`) when the fallback is needed.

## Inputs

- **Source directory** *(required)* — either:
  - a **parent planned directory** containing one subfolder per
    workgroup (e.g., `./cache/output/planned/`), or
  - a **single workgroup directory** containing planned ticket
    markdown files directly (e.g.,
    `./cache/output/planned/OrdersAndObservations/`).

  Auto-detect by inspecting the directory contents:
  - If the directory contains one or more `FHIR-*.md` files at its top
    level, treat it as a **single workgroup directory** and write one
    `README.md` directly inside it.
  - Otherwise, treat it as a **parent planned directory** and recurse
    one level into each subdirectory that contains `FHIR-*.md` files,
    writing one `README.md` per workgroup subdirectory. Subdirectories
    that contain no plan files are skipped.

- **Working directory** *(optional)* — directory the agent may use for
  any transient files (intermediate JSON dumps, scratch notes,
  CLI-fetched ticket payloads). When supplied, **all transient files
  must be written under this directory** rather than the repo root or
  the source directory. Create it cross-platform if it does not exist
  (PowerShell `New-Item -ItemType Directory -Force` or bash `mkdir
  -p`). Do not write transient files outside this directory.

## Behaviour rules

- **Idempotent overwrite.** If a workgroup's `README.md` already
  exists, overwrite it. The skill is meant to be re-runnable as new
  plan files are added.
- **Each ticket appears exactly once** in the resulting README — in
  exactly one ticket table, under exactly one Topic (or under
  "Individual Tickets" / "No Implementation Required" when applicable).
- **Do not modify plan files.** The skill is read-only with respect to
  the plan markdown.
- **Do not invent data.** If a field cannot be parsed and the
  documented fallback also fails, render the field as `(unknown)` and
  continue. Do not fabricate titles, statuses, reporters, or dates.

## Workflow

When the user provides a source directory, execute the following steps.
Run independent file reads in parallel where possible.

### Step 1: Resolve scope

1. Verify the source directory exists. Abort with a clear message if
   not.
2. Auto-detect whether the path is a parent planned directory or a
   single workgroup directory (see [Inputs](#inputs)).
3. Build the list of **workgroup directories** to process. For each:
   - Workgroup `nameClean` = the directory name (matches the
     `orchestrate-plan` convention).
   - Display name = the `Work Group` value parsed from any plan file
     in that directory (these all agree within a workgroup).

### Step 2: Parse plan files for a workgroup

For each workgroup directory, enumerate `FHIR-*.md` files (top level
only — do not recurse). For each plan file, first detect whether it is
a **full plan** or a **no-op short report** (see
[No-op short reports](#no-op-short-reports) below), then extract
fields accordingly.

Full plans have a header table at the top of the document. Extract:

| Field | Source row | Notes |
|-------|-----------|-------|
| `key` | filename `FHIR-XXXXX.md` and `\| Ticket \|` row | Must agree; if mismatched, use the filename and warn. |
| `type` | `\| Ticket \|` row | The text after the ` : ` separator (e.g., `Change Request`). |
| `jiraUrl` | `\| Ticket \|` row | The link target inside the `[FHIR-XXXXX](…)` markdown link. |
| `title` | `\| Title \|` row | See [Header field resolution](#header-field-resolution). |
| `workGroup` | `\| Work Group \|` row | Display name. |
| `status` | `\| Status \|` row | The full value as written (e.g., `Highest Applied`). May embed a leading priority token plus the workflow status. |
| `specification` | `\| Specification \|` row | If empty, `(none reported)`, `None recorded`, or missing → use `Unspecified`. |
| `reporter` | `\| Reporter \|` row | |
| `resolved` | `\| Resolved \|` row | ISO `YYYY-MM-DD` if available. |
| `relatedArtifacts` | `\| Related Artifacts \|` row | Comma-separated list; used as a secondary clustering signal. |

Also extract:

- **Prerequisite tickets** — every `FHIR-XXXXX` key listed under the
  `### Prerequisites` subsection inside `## Implementation Plan`
  (these are the tickets the plan declares must complete first, and
  are the primary signal for Prerequisite Ticket Groups).
- **Related tickets** — every `FHIR-XXXXX` key listed under the
  `### Related Tickets` subsection inside `## Impact Analysis`
  (cross-referenced; used as a secondary signal for Topic clustering).
- **Affected repositories** — repository full names listed under
  `### Affected Repositories` inside `## Impact Analysis` (used as a
  Topic-clustering signal: tickets that touch the same repos are more
  likely to belong together).
- **Resolution Summary** — the contents of the `## Resolution Summary`
  section (used for Topic clustering and to draft the Topic
  description).
- **Proposed Change** — the contents of the `### Proposed Change`
  subsection inside `## Feature Proposal` (used for Topic clustering).

#### No-op short reports

The `ticket-plan` skill writes a **short report** (no header table,
no Implementation Plan section) when a ticket's resolution is `Not
Persuasive`, `Duplicate`, or `Withdrawn`. Detect this form by either
(a) the absence of the `## Resolution Summary` section, or (b) a body
that matches the documented short-report shape:

```
# Implementation Plan: FHIR-XXXXX

**Resolution:** {resolution}

No implementation required because the resolution is `{resolution}`.
```

For each detected no-op short report:

1. Parse the `key` from the filename and the `resolution` from the
   `**Resolution:** …` line.
2. Use the [Header field resolution](#header-field-resolution)
   fallback to fetch `title`, `type`, `workGroup`, `specification`,
   `reporter`, `status`, and `resolved` via `fhir-augury-cli`.
3. Mark the ticket as `noImplementation = true` and record its
   `resolution`.

No-op tickets do **not** participate in Topic clustering. They are
rendered in a dedicated `No Implementation Required` subsection at
the end of their `(Specification, Type)` bucket — see
[README format](#readme-format).

#### Header field resolution

For each header field in a full plan:

1. **Primary:** read the corresponding row from the header table.
2. **Fallback:** if the row is missing or empty (or, for no-op short
   reports, always), fetch the ticket via `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX"}'
   ```

   Map the response into the missing field(s) — e.g., `title`,
   `metadata.specification`, `metadata.work_group`, `metadata.status`,
   `metadata.reporter`, `metadata.resolved_date`,
   `metadata.resolution`.
3. **Last resort:** if the CLI call also fails, render the field as
   `(unknown)` (or `(title unavailable)` for the title) and continue.

Cache CLI lookups so the same ticket is not fetched twice within a
run.

### Step 3: Group tickets by Specification

Within a workgroup, partition the parsed tickets by `specification`.
Tickets with no `specification` go into the bucket `Unspecified`.

The order of Specification sections in the README is:

1. Specifications sorted alphabetically (case-insensitive, by the
   value as written in the plan file).
2. `Unspecified` last, only if it has at least one ticket.

### Step 4: Group each Specification by Type

Within each Specification bucket, partition tickets by `type`.

The order of Type subsections inside a Specification is **fixed** for
the two canonical types, then alphabetical for any others:

1. `Technical Correction`
2. `Change Request`
3. *Any other type, sorted alphabetically (case-insensitive).*

Rules:

- The two canonical types **must be present** in every Specification
  section, even if empty. When empty, render the heading and a single
  italicized note: `*No tickets of this type are planned.*`
- Other types — including `Comment` and `Question`, which are
  unlikely to appear in this state — are rendered **only when
  non-empty**.

### Step 5: Cluster each Type bucket into Topics

For each non-empty `(Specification, Type)` bucket, set aside any
no-op tickets (they are rendered separately — see Step 6) and cluster
the remaining tickets into **Topics**. Topic clustering is the
agent's analytic step — read the parsed Resolution Summary and
Proposed Change, and weigh signals as follows (strongest first):

1. **Prerequisite tickets** — tickets named under one another's
   `### Prerequisites` subsections are strongly co-clustered. Two
   tickets that share a prerequisite chain (directly or transitively)
   belong in the same Topic.
2. **Related tickets** (cross-references) — tickets that surface each
   other under `### Related Tickets` are likely co-clustered, but
   weaker than direct prerequisite edges.
3. **Shared subject matter** — overlapping FHIR resources, element
   paths, operation names, and domain terms drawn from Resolution
   Summary, Proposed Change, Affected Repositories, and Related
   Artifacts.

Each non-no-op ticket joins exactly one Topic. Prefer fewer, broader
Topics over many narrow ones; the goal is reviewer navigation, not
maximal specificity.

For each Topic, decide:

- **Short description** — title-length (≈ 3–8 words) phrase that names
  what the Topic is about. Used in the heading.
- **Longer description** — 1–3 sentences explaining what the tickets
  in this Topic collectively cover and why they belong together.

Then split the Topic's tickets:

- **Prerequisite Ticket Groups** — every connected component (size
  ≥ 2) formed purely by `### Prerequisites` edges among the Topic's
  tickets. The "first ticket" used in the sub-group heading is the
  one every other ticket in the group depends on (or is a prerequisite
  for) first; if there is no obvious dependency, use the **lowest
  ticket key** (ascending) in the group.
- **Remaining tickets** — every ticket in the Topic that is not part
  of a Prerequisite Ticket Group.

After Topics are formed, partition them:

- Topics with **2 or more tickets** are rendered as their own `Topic:
  …` subsection.
- Topics with **exactly 1 ticket** are *not* rendered as Topics.
  Instead, each such ticket is collected into a single trailing
  **Individual Tickets** section under the Type subsection.

### Step 6: Render the README

Compose `README.md` for the workgroup using the format in
[README format](#readme-format). Write it to:

```
<workgroup-directory>/README.md
```

Overwrite any existing `README.md` at that path.

Within each `(Specification, Type)` bucket the render order inside
the Type subsection is:

1. Topics with ≥ 2 tickets, in render order (see [Topic render ordering](#topic-render-ordering-within-a-type)).
2. The **Individual Tickets** section, if any 1-ticket Topics exist.
3. The **No Implementation Required** section, if any no-op tickets
   exist for this `(Specification, Type)`.

### Step 7: Repeat per workgroup

If processing a parent planned directory, repeat Steps 2–6 for every
workgroup subdirectory. Independent workgroups can be processed in
parallel where the agent runtime supports it.

### Step 8: Report back to the user

After all workgroups are processed, summarize:

- The list of workgroups processed and the README path written for
  each.
- Per-workgroup ticket counts (total, plus a breakdown by Type, plus
  a count of no-op tickets).
- Any workgroups skipped (e.g., subdirectories with no plan files).
- Any tickets where a field had to be rendered as `(unknown)` /
  `(title unavailable)`, with the reason.

## Ticket table format

Every ticket table — whether inside a Prerequisite Ticket Group, a
Topic's remaining-tickets table, the Individual Tickets section, or
the No Implementation Required section — uses the same five columns:

| Column | Content |
|--------|---------|
| Ticket | Two links separated by a space: `[FHIR-XXXXX](./FHIR-XXXXX.md)` (relative link to the plan file in the same directory) and `[Jira](https://jira.hl7.org/browse/FHIR-XXXXX)`. |
| Title | The ticket title. |
| Status | The full status string as parsed (e.g., `Highest Applied`). For no-op tickets where the workflow status is unavailable, render the resolution prefixed with `Resolution:` (e.g., `Resolution: Not Persuasive`). |
| Reporter | The reporter as parsed. |
| Resolved | The resolved date as parsed (ISO `YYYY-MM-DD` if available). |

Row ordering rules:

- **Prerequisite Ticket Group tables** — order by **dependency order**
  (a ticket that another ticket depends on comes first), then ascending
  by ticket key for ties.
- **Topic remaining-tickets tables** — ascending by ticket key.
- **Individual Tickets tables** — ascending by ticket key.
- **No Implementation Required tables** — ascending by ticket key.

Render the table in standard GitHub-flavoured Markdown.

## README format

The generated `README.md` MUST follow this structure. Sections in
braces are filled in per workgroup; instructional commentary in
braces is replaced by the actual generated content.

````markdown
# {Work Group display name}

Index of planned tickets in this folder. Generated by the
`index-planned` skill from the `ticket-plan` reports in this
directory.

- **Total planned tickets:** {N}
- **No-implementation tickets:** {M} *(omit this bullet when M = 0)*
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
order: Technical Correction, Change Request, then any other types
found, sorted alphabetically. The two canonical Type subsections are
always present even when empty; other Types only appear when they
have at least one ticket.}

### {Type}

{If the Type bucket is empty (only possible for Technical Correction
/ Change Request):}

*No tickets of this type are planned.*

{If the Type bucket has tickets, render every Topic with ≥ 2 tickets
first (in render order — see below), then a final Individual Tickets
section if any 1-ticket topics exist, then a final No Implementation
Required section if any no-op tickets exist.}

#### Topic: {short description}

{1–3 sentence longer description of what this Topic covers and why
the tickets belong together.}

{For each Prerequisite Ticket Group in the Topic (each is a connected
component of size ≥ 2 formed by the Prerequisites edges):}

##### Prerequisite Group: {first ticket key}

{1–3 sentence rationale for why these tickets must be tackled
together — e.g., dependency order, shared root cause, terminology
prerequisite.}

{Ticket table for the group, ordered by dependency then ascending
key.}

| Ticket | Title | Status | Reporter | Resolved |
|--------|-------|--------|----------|----------|
| [FHIR-XXXXX](./FHIR-XXXXX.md) [Jira](https://jira.hl7.org/browse/FHIR-XXXXX) | … | … | … | … |

{After all Prerequisite Ticket Groups in the Topic, render the
Topic's remaining tickets (Topic members not part of any Prerequisite
Ticket Group) as a single ticket table, ordered ascending by key.
Omit this table when the Topic has no remaining tickets.}

| Ticket | Title | Status | Reporter | Resolved |
|--------|-------|--------|----------|----------|
| … | … | … | … | … |

{After all Topics, if any 1-ticket Topics existed, render:}

#### Individual Tickets

{Ticket table of every 1-ticket-Topic ticket in the Type bucket,
ordered ascending by key.}

| Ticket | Title | Status | Reporter | Resolved |
|--------|-------|--------|----------|----------|
| … | … | … | … | … |

{After Individual Tickets, if any no-op tickets exist for this
(Specification, Type), render:}

#### No Implementation Required

Tickets in this list resolved as `Not Persuasive`, `Duplicate`, or
`Withdrawn`; their plan reports record no implementation work.

| Ticket | Title | Status | Reporter | Resolved |
|--------|-------|--------|----------|----------|
| … | … | … | … | … |

---

## {Next Specification name}

…
````

### Topic render ordering within a Type

Topics with ≥ 2 tickets are rendered in **descending order of ticket
count**, then alphabetically by short description for ties. The
Individual Tickets section, when present, is rendered after all
Topics. The No Implementation Required section, when present, is
always last within its Type subsection.

### Anchor generation

Use GitHub's standard heading-to-anchor algorithm: lowercase, replace
spaces with `-`, drop characters other than letters, digits, hyphens,
and underscores, collapse runs of hyphens. If two Specifications would
collide (rare), suffix the duplicate anchors `-1`, `-2`, … in render
order — and use the suffixed anchor in the matching ToC entry.

## Important Rules

- **One README per workgroup directory.** Always written as
  `README.md` directly inside the workgroup directory.
- **Each ticket appears in exactly one ticket table.** A ticket is
  either part of a Prerequisite Ticket Group, the remaining-tickets
  table of its Topic, the Individual Tickets table, or the No
  Implementation Required table — never more than one.
- **Always overwrite.** Do not preserve a prior `README.md`; the skill
  is meant to be re-runnable.
- **Canonical Type subsections are mandatory.** `Technical Correction`
  and `Change Request` MUST appear in every Specification section,
  even if empty (with the documented italic note). All other types —
  including `Comment` and `Question` (unlikely to appear in this
  state) — are rendered only when populated, sorted alphabetically.
- **`Unspecified` Specification.** Any ticket whose Specification is
  missing, blank, or a recognized "none" placeholder
  (`(none reported)`, `None recorded`, `None`, etc.) goes under a
  Specification named `Unspecified`. The `Unspecified` section is
  rendered last and only when it has tickets.
- **Cluster, do not invent.** Topic short descriptions and longer
  descriptions are written by the agent based on actual ticket content
  parsed from the plan files. Do not add tickets that are not in the
  workgroup directory, and do not describe behavior not supported by
  the parsed Resolution Summary / Proposed Change.
- **Prerequisite tickets dominate clustering.** If two tickets list
  each other under `### Prerequisites`, they belong to the same
  Topic, even if their Resolution Summary / Proposed Change differ in
  surface terms.
- **No-op tickets do not cluster.** Tickets whose plan is the no-op
  short report (`Not Persuasive` / `Duplicate` / `Withdrawn`) skip
  Topic clustering and land in the dedicated No Implementation
  Required subsection of their `(Specification, Type)` bucket.
- **Header fallback uses `fhir-augury-cli` only when needed.** Do not
  preemptively call the CLI when the header rows are present. For
  no-op short reports (which have no header table) the CLI fallback
  is expected.

## Example invocation

User: *"Index the planned tickets under `./cache/output/planned/`."*

The skill should:

1. Verify `./cache/output/planned/` exists; auto-detect that it is a
   parent planned directory (no `FHIR-*.md` files at its top level).
2. Enumerate workgroup subdirectories that contain `FHIR-*.md` files.
3. For each workgroup, parse every plan file, group by Specification
   → Type → Topic, identify Prerequisite Ticket Groups, segregate
   no-op short reports, and write `<workgroup>/README.md`.
4. Report back with per-workgroup ticket counts (including no-op
   counts) and the list of README paths written.

User: *"Index just `./cache/output/planned/OrdersAndObservations/`."*

The skill should:

1. Verify the directory exists; auto-detect that it is a single
   workgroup directory (it contains `FHIR-*.md` files directly).
2. Parse every plan file in that directory and write
   `./cache/output/planned/OrdersAndObservations/README.md`.
3. Report back with the ticket count and the README path written.
