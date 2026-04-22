---
name: index-prepared
description: "Builds a README.md index for one or more workgroup folders of prepared FHIR ticket reports. USE FOR: indexing prep output, generating workgroup ballot indexes, grouping prepared tickets by specification/type/topic, producing per-workgroup tables of contents. Reads `ticket-prep` reports under a prep directory (single workgroup folder OR parent prep dir), clusters tickets into topics using ticket content with linked/referenced tickets prioritized, and writes a structured README.md per workgroup with a Table of Contents, per-specification sections, per-type subsections, topic groupings, linked-ticket sub-groups, and ticket tables."
---

# Index Prepared Skill

Builds a `README.md` index for each workgroup folder under a prep output
directory. The index gives a workgroup reviewer a navigable table of
contents over every prepared ticket, grouped by Specification and ticket
Type, then clustered into Topics that surface the relationships between
tickets.

This skill is **read-mostly**: it parses the prep markdown produced by
the `ticket-prep` skill, may make small lookups via `fhir-augury-cli` to
fill in missing titles, and writes one `README.md` per workgroup. It
does not modify the prep files themselves.

## Data Access

The skill works primarily off the on-disk prep files. It only invokes
the `fhir-augury-cli` skill as a **fallback** when a prep file is
missing the expected `| Title |` row in the header table — see
[Title resolution](#title-resolution). Follow the `fhir-augury-cli`
fallback chain (CLI → MCP → direct HTTP → `appsettings.json`) when the
fallback is needed.

## Inputs

- **Source directory** *(required)* — either:
  - a **parent prep directory** containing one subfolder per workgroup
    (e.g., `./cache/output/prep/`), or
  - a **single workgroup directory** containing prepared ticket
    markdown files directly (e.g.,
    `./cache/output/prep/OrdersAndObservations/`).

  Auto-detect by inspecting the directory contents:
  - If the directory contains one or more `FHIR-*.md` files at its top
    level, treat it as a **single workgroup directory** and write one
    `README.md` directly inside it.
  - Otherwise, treat it as a **parent prep directory** and recurse one
    level into each subdirectory that contains `FHIR-*.md` files,
    writing one `README.md` per workgroup subdirectory. Subdirectories
    that contain no prep files are skipped.

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
  prep files are added.
- **Each ticket appears exactly once** in the resulting README — in
  exactly one ticket table, under exactly one Topic (or under
  "Individual Tickets" when its topic has only one ticket).
- **Do not modify prep files.** The skill is read-only with respect to
  the prep markdown.
- **Do not invent data.** If a field cannot be parsed and the
  documented fallback also fails, render the field as `(unknown)` and
  continue. Do not fabricate titles, statuses, reporters, or dates.

## Workflow

When the user provides a source directory, execute the following steps.
Run independent file reads in parallel where possible.

### Step 1: Resolve scope

1. Verify the source directory exists. Abort with a clear message if
   not.
2. Auto-detect whether the path is a parent prep directory or a single
   workgroup directory (see [Inputs](#inputs)).
3. Build the list of **workgroup directories** to process. For each:
   - Workgroup `nameClean` = the directory name (matches the
     `orchestrate-prep` convention).
   - Display name = the `Work Group` value parsed from any prep file
     in that directory (these all agree within a workgroup).

### Step 2: Parse prep files for a workgroup

For each workgroup directory, enumerate `FHIR-*.md` files (top level
only — do not recurse). For each prep file, extract the following
fields from the header table at the top of the document:

| Field | Source row | Notes |
|-------|-----------|-------|
| `key` | filename `FHIR-XXXXX.md` and `\| Ticket \|` row | Must agree; if mismatched, use the filename and warn. |
| `type` | `\| Ticket \|` row | The text after the ` : ` separator (e.g., `Change Request`). |
| `jiraUrl` | `\| Ticket \|` row | The link target inside the `[FHIR-XXXXX](…)` markdown link. |
| `title` | `\| Title \|` row | See [Title resolution](#title-resolution). |
| `workGroup` | `\| Work Group \|` row | Display name. |
| `status` | `\| Status \|` row | The full value as written (e.g., `Highest Triaged`). |
| `specification` | `\| Specification \|` row | If empty, `(none reported)`, `None recorded`, or missing → use `Unspecified`. |
| `reporter` | `\| Reporter \|` row | |
| `created` | `\| Created \|` row | |

Also extract:

- **Linked tickets** — every `FHIR-XXXXX` key listed under the
  `## Linked Jira Tickets` section (these are the ticket's
  *explicitly* linked tickets and are the primary signal for
  Linked Ticket Groups).
- **Related tickets** — every `FHIR-XXXXX` key listed under the
  `## Related Jira Tickets` section (cross-referenced; used as a
  secondary signal for Topic clustering).
- **Keywords** — the contents of the `## Keywords` section (used for
  Topic clustering).
- **Summary** — the contents of the `## Summary` section (used for
  Topic clustering and to draft the Topic short description and longer
  description).

#### Title resolution

1. **Primary:** read the `| Title |` row from the header table.
2. **Fallback:** if the `| Title |` row is missing or empty, fetch the
   ticket via `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX"}'
   ```

   Take the `title` field from the response.
3. **Last resort:** if the CLI call also fails, render the title as
   `(title unavailable)` and continue.

Cache CLI lookups so the same ticket is not fetched twice within a run.

### Step 3: Group tickets by Specification

Within a workgroup, partition the parsed tickets by `specification`.
Tickets with no `specification` go into the bucket `Unspecified`.

The order of Specification sections in the README is:

1. Specifications sorted alphabetically (case-insensitive, by the
   value as written in the prep file).
2. `Unspecified` last, only if it has at least one ticket.

### Step 4: Group each Specification by Type

Within each Specification bucket, partition tickets by `type`.

The order of Type subsections inside a Specification is **fixed** for
the four canonical types, then alphabetical for any others:

1. `Comment`
2. `Question`
3. `Technical Correction`
4. `Change Request`
5. *Any other type, sorted alphabetically (case-insensitive).*

Rules:

- The four canonical types **must be present** in every Specification
  section, even if empty. When empty, render the heading and a single
  italicized note: `*No tickets of this type are prepared.*`
- Other types are rendered **only when non-empty**.

### Step 5: Cluster each Type bucket into Topics

For each non-empty `(Specification, Type)` bucket, cluster its tickets
into **Topics**. Topic clustering is the agent's analytic step — read
the parsed Summary and Keywords, and weigh signals as follows
(strongest first):

1. **Linked tickets** — tickets that name each other under
   `## Linked Jira Tickets` are strongly co-clustered. Two tickets
   linked to each other (directly or through a chain of linked
   tickets) belong in the same Topic.
2. **Related tickets** (cross-references) — tickets that surface each
   other under `## Related Jira Tickets` are likely co-clustered, but
   weaker than direct links.
3. **Shared subject matter** — overlapping FHIR resources, element
   paths, operation names, and domain terms drawn from the Keywords
   and Summary sections.

Each ticket joins exactly one Topic. Prefer fewer, broader Topics over
many narrow ones; the goal is reviewer navigation, not maximal
specificity.

For each Topic, decide:

- **Short description** — title-length (≈ 3–8 words) phrase that names
  what the Topic is about. Used in the heading.
- **Longer description** — 1–3 sentences explaining what the tickets
  in this Topic collectively cover and why they belong together.

Then split the Topic's tickets:

- **Linked Ticket Groups** — every connected component (size ≥ 2)
  formed purely by `## Linked Jira Tickets` edges among the Topic's
  tickets. The "first ticket" used in the sub-group heading is the one
  every other ticket in the group depends on (or is linked from)
  first; if there is no obvious dependency, use the **lowest ticket
  key** (ascending) in the group.
- **Remaining tickets** — every ticket in the Topic that is not part
  of a Linked Ticket Group.

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

### Step 7: Repeat per workgroup

If processing a parent prep directory, repeat Steps 2–6 for every
workgroup subdirectory. Independent workgroups can be processed in
parallel where the agent runtime supports it.

### Step 8: Report back to the user

After all workgroups are processed, summarize:

- The list of workgroups processed and the README path written for
  each.
- Per-workgroup ticket counts (total, plus a breakdown by Type).
- Any workgroups skipped (e.g., subdirectories with no prep files).
- Any tickets where a field had to be rendered as `(unknown)` /
  `(title unavailable)`, with the reason.

## Ticket table format

Every ticket table — whether inside a Linked Ticket Group, a Topic's
remaining-tickets table, or the Individual Tickets section — uses the
same five columns:

| Column | Content |
|--------|---------|
| Ticket | Two links separated by a space: `[FHIR-XXXXX](./FHIR-XXXXX.md)` (relative link to the prep file in the same directory) and `[Jira](https://jira.hl7.org/browse/FHIR-XXXXX)`. |
| Title | The ticket title. |
| Status | The full status string as parsed (e.g., `Highest Triaged`). |
| Reporter | The reporter as parsed. |
| Created | The created date as parsed (ISO `YYYY-MM-DD` if available). |

Row ordering rules:

- **Linked Ticket Group tables** — order by **dependency order**
  (a ticket that another ticket depends on comes first), then ascending
  by ticket key for ties.
- **Topic remaining-tickets tables** — ascending by ticket key.
- **Individual Tickets tables** — ascending by ticket key.

Render the table in standard GitHub-flavoured Markdown.

## README format

The generated `README.md` MUST follow this structure. Sections in
braces are filled in per workgroup; instructional commentary in
braces is replaced by the actual generated content.

````markdown
# {Work Group display name}

Index of prepared tickets in this folder. Generated by the
`index-prepared` skill from the `ticket-prep` reports in this
directory.

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
any other types found, sorted alphabetically. The four canonical Type
subsections are always present even when empty; other Types only
appear when they have at least one ticket.}

### {Type}

{If the Type bucket is empty (only possible for Comment / Question /
Technical Correction / Change Request):}

*No tickets of this type are prepared.*

{If the Type bucket has tickets, render every Topic with ≥ 2 tickets
first (in render order — see below), then a final Individual Tickets
section if any 1-ticket topics exist.}

#### Topic: {short description}

{1–3 sentence longer description of what this Topic covers and why
the tickets belong together.}

{For each Linked Ticket Group in the Topic (each is a connected
component of size ≥ 2 formed by the Linked Jira Tickets edges):}

##### Linked Ticket Group: {first ticket key}

{1–3 sentence rationale for why these tickets must be tackled
together — e.g., prerequisites, duplicates, shared root cause.}

{Ticket table for the group, ordered by dependency then ascending
key.}

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| [FHIR-XXXXX](./FHIR-XXXXX.md) [Jira](https://jira.hl7.org/browse/FHIR-XXXXX) | … | … | … | … |

{After all Linked Ticket Groups in the Topic, render the Topic's
remaining tickets (Topic members not part of any Linked Ticket Group)
as a single ticket table, ordered ascending by key. Omit this table
when the Topic has no remaining tickets.}

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| … | … | … | … | … |

{After all Topics, if any 1-ticket Topics existed, render:}

#### Individual Tickets

{Ticket table of every 1-ticket-Topic ticket in the Type bucket,
ordered ascending by key.}

| Ticket | Title | Status | Reporter | Created |
|--------|-------|--------|----------|---------|
| … | … | … | … | … |

---

## {Next Specification name}

…
````

### Topic render ordering within a Type

Topics with ≥ 2 tickets are rendered in **descending order of ticket
count**, then alphabetically by short description for ties. The
Individual Tickets section, when present, is always last within its
Type subsection.

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
  either part of a Linked Ticket Group, the remaining-tickets table
  of its Topic, or the Individual Tickets table — never more than one.
- **Always overwrite.** Do not preserve a prior `README.md`; the skill
  is meant to be re-runnable.
- **Canonical Type subsections are mandatory.** `Comment`, `Question`,
  `Technical Correction`, and `Change Request` MUST appear in every
  Specification section, even if empty (with the documented italic
  note). Other types appear only when populated.
- **`Unspecified` Specification.** Any ticket whose Specification is
  missing, blank, or a recognized "none" placeholder
  (`(none reported)`, `None recorded`, `None`, etc.) goes under a
  Specification named `Unspecified`. The `Unspecified` section is
  rendered last and only when it has tickets.
- **Cluster, do not invent.** Topic short descriptions and longer
  descriptions are written by the agent based on actual ticket content
  parsed from the prep files. Do not add tickets that are not in the
  workgroup directory, and do not describe behavior not supported by
  the parsed Summary / Keywords.
- **Linked tickets dominate clustering.** If two tickets reference
  each other under `## Linked Jira Tickets`, they belong to the same
  Topic, even if their Keywords / Summary differ in surface terms.
- **Title fallback uses `fhir-augury-cli` only when needed.** Do not
  preemptively call the CLI when the `| Title |` row is present.

## Example invocation

User: *"Index the prepared tickets under `./cache/output/prep/`."*

The skill should:

1. Verify `./cache/output/prep/` exists; auto-detect that it is a
   parent prep directory (no `FHIR-*.md` files at its top level).
2. Enumerate workgroup subdirectories that contain `FHIR-*.md` files.
3. For each workgroup, parse every prep file, group by Specification
   → Type → Topic, identify Linked Ticket Groups, and write
   `<workgroup>/README.md`.
4. Report back with per-workgroup ticket counts and the list of
   README paths written.

User: *"Index just `./cache/output/prep/OrdersAndObservations/`."*

The skill should:

1. Verify the directory exists; auto-detect that it is a single
   workgroup directory (it contains `FHIR-*.md` files directly).
2. Parse every prep file in that directory and write
   `./cache/output/prep/OrdersAndObservations/README.md`.
3. Report back with the ticket count and the README path written.
