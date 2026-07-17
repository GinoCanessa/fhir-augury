---
name: index-notes
description: "Builds a README.md index for a directory of ballot-note draft reports produced by the `notes-artifact`, `notes-page`, and `notes-datatype` skills. USE FOR: indexing ballot-note output, generating a navigable cross-workgroup summary of drafted ballot notes, surfacing per-workgroup tables of which artifacts/pages/datatypes need (or don't need) a ballot note this cycle. Reads ballot-note markdown reports under a root ballot-notes directory (subfolders `artifacts/`, `pages/`, `datatypes/`, and any sibling type folders) and writes a single `README.md` at the root with an alphabetical 'all contents' table followed by one filtered per-workgroup section."
---

# Index Notes Skill

Builds a `README.md` index for a directory of **ballot-note draft
reports** — the markdown files produced by the `notes-artifact`,
`notes-page`, and `notes-datatype` skills (typically orchestrated by
`orchestrate-notes`). The index gives a reviewer a single navigable
table of every drafted ballot note, plus per-workgroup filtered tables
so each work group can find their own artifacts/pages quickly.

This skill is **read-only with respect to the note files**. It parses
each report's header table and body, may make small lookups via
`fhir-augury-cli` to fill in a missing workgroup attribution, and
writes one `README.md` at the root of the supplied directory.

## Data Access

The skill works primarily off the on-disk note reports. It only
invokes the `fhir-augury-cli` skill as a **fallback** to resolve a
missing workgroup attribution — see [Workgroup resolution](#workgroup-resolution).
Follow the `fhir-augury-cli` fallback chain (CLI → MCP → direct HTTP
→ `appsettings.json`) when the fallback is needed.

## Inputs

- **Source directory** *(required)* — the root directory containing
  the ballot-note reports, e.g., `./cache/output/ballot-notes/`. The
  skill expects one or more **type subfolders** directly inside it:

  | Subfolder | Type label | Produced by |
  |-----------|-----------|-------------|
  | `artifacts/` | `Artifact` | `notes-artifact` |
  | `pages/` | `Page` | `notes-page` |
  | `datatypes/` | `DataType` | `notes-datatype` |

  Any other subfolder containing `*.md` files is treated as a custom
  type whose label is the folder name in **TitleCase** (e.g.,
  `profiles/` → `Profile`). The skill does **not** recurse below the
  type subfolder — only top-level `*.md` files inside each type
  subfolder are indexed.

  `README.md` itself is ignored if it appears inside a type subfolder.

- **Working directory** *(optional)* — directory the agent may use for
  any transient files (intermediate JSON dumps, scratch notes,
  CLI-fetched payloads). When supplied, **all transient files must be
  written under this directory** rather than the repo root or the
  source directory. Create it cross-platform if it does not exist
  (PowerShell `New-Item -ItemType Directory -Force` or bash `mkdir
  -p`). Do not write transient files outside this directory.

## Behaviour rules

- **Idempotent overwrite.** If `README.md` already exists at the root
  of the source directory, overwrite it. The skill is meant to be
  re-runnable as new note reports are added.
- **Each note appears exactly once** in the all-contents table and
  exactly once in its workgroup's per-workgroup table.
- **Do not modify note files.** The skill is read-only with respect
  to the ballot-note markdown reports.
- **Do not invent data.** If a field cannot be parsed and the
  documented fallback also fails, render the field as `(unknown)` and
  continue. Do not fabricate workgroups, ticket counts, or
  recommendations.

## Workflow

When the user provides a source directory, execute the following
steps. Run independent file reads in parallel where possible.

### Step 1: Resolve scope

1. Verify the source directory exists. Abort with a clear message if
   not.
2. Enumerate its immediate subdirectories. For each subdirectory that
   contains at least one top-level `*.md` file (excluding `README.md`),
   record it as a type subfolder. Determine its `Type` label using the
   table in [Inputs](#inputs).
3. If no type subfolder contains any `*.md` files, write a minimal
   README explaining that no notes were found, and stop.

### Step 2: Parse each note report

For each note file (top-level `*.md` inside a type subfolder, except
`README.md`), extract the following fields. All fields except `Name`
and `Type` come from the report's leading header table or its body
sections.

| Field | Source | Notes |
|-------|--------|-------|
| `name` | filename without the `.md` extension | Used as the row's Name and the link text. |
| `type` | type subfolder | Per the table in [Inputs](#inputs). |
| `relPath` | source-directory-relative path to the file | E.g., `artifacts/Observation.md`. Used as the link target in the README. |
| `commits` | `\| Commits in window \|` row of the header table | Extract the leading integer. If absent or unparseable → `(unknown)`. |
| `tickets` | `\| Tickets attributed \|` row of the header table | Extract the leading integer (e.g., `3 (BDP-relevant); 1 commit unattributed` → `3`). If absent or unparseable → `(unknown)`. |
| `workgroup` | per-ticket `**Work group:**` lines in the body | See [Workgroup resolution](#workgroup-resolution). |
| `needsNote` | the report's recommendation about whether to add / update a ballot note | See [Needs Note resolution](#needs-note-resolution). |

#### Workgroup resolution

The work group is the *owning* work group of the artifact / page /
datatype, not the work group of any individual ticket. Resolve in
order:

1. **Header table.** If the header table contains a `| Work Group |`
   or `| Workgroup |` row, use it.
2. **Per-ticket lines.** Otherwise, scan the body for
   `- **Work group:** {value}` (or `Workgroup:`) lines under the
   `## Per-Ticket Detail` section. Take the **most frequent** value
   (the modal workgroup); on a tie, take the value attached to the
   ticket with the most commits, then the earliest-listed ticket.
3. **CLI fallback.** If no per-ticket workgroup lines exist (e.g.,
   the report is entirely `(unattributed)`), and the report names
   one or more `FHIR-XXXXX` tickets in its `## Tickets Applied in
   Window` table, look up the first such ticket via
   `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX"}'
   ```

   Use the `workGroup` field from the response. Cache lookups so the
   same ticket is not fetched twice within a run.
4. **Last resort.** If none of the above yields a value, render the
   workgroup as `(unknown)`.

For the **datatypes** report, the owning workgroup is conventionally
`FHIR Infrastructure (FHIR-I)`; if the report lacks workgroup
attribution and CLI lookup fails, fall back to that label rather than
`(unknown)`.

#### Needs Note resolution

Each note report ends with a recommendation about whether a ballot
note should actually be added / updated for this window. Decide
`needsNote` as follows:

- **`no`** — if the `## Proposed Ballot Note` (or
  `## Proposed Ballot Note (HTML)`) section, or the `## Notes for
  Reviewer` section, contains an explicit negative recommendation.
  Look for phrases like:
  - *"Recommendation: do not add a ballot note"*
  - *"do **not** add a ballot note"*
  - *"no ballot note required"*
  - *"recommendation is to **omit**"* / *"recommendation is to omit"*
  - *"No ballot-note insertion is warranted"*
  - *"This is the recommended outcome — a ballot note that says only…"*
  Use case-insensitive matching and tolerate Markdown emphasis
  (`**`, `*`, `_`).
- **`yes`** — otherwise. In particular:
  - The report's `## Proposed Ballot Note` / `## Proposed Ballot
    Note (HTML)` section contains a real `<blockquote
    class="ballot-note" …>` draft (whether brand-new or a revision
    of an existing note) **and** no negative recommendation appears
    elsewhere.
  - For the **datatypes** report (which proposes a page-level note
    plus per-datatype bullets), `yes` if any concrete bullet is
    proposed.

If the report has no `## Proposed Ballot Note` section at all (a
malformed report), record `(unknown)` and continue.

### Step 3: Build the global index

Sort all parsed entries **alphabetically by `name`, case-insensitive**.
This becomes the rows of the global "Table of Contents" table.

### Step 4: Build per-workgroup indexes

Group the parsed entries by `workgroup`. Sort the groups
alphabetically by workgroup display name (case-insensitive). Place a
`(unknown)` workgroup last when present. Within each group, sort
rows alphabetically by `name` (case-insensitive).

### Step 5: Render the README

Compose `README.md` for the root using the format in
[README format](#readme-format). Write it to:

```
<source-directory>/README.md
```

Overwrite any existing `README.md` at that path.

### Step 6: Report back to the user

After writing, summarize:

- The README path written.
- The total count of indexed notes, broken down by Type
  (`Artifact`, `Page`, `DataType`, …).
- The per-workgroup counts.
- The count of notes flagged `Needs Note = yes` vs `no` vs
  `(unknown)`.
- Any notes whose workgroup was rendered as `(unknown)`, with the
  filename (so the user can investigate).

## Note table format

Both the global table and every per-workgroup table use the same six
columns:

| Column | Content |
|--------|---------|
| Name | `[{name}](./{relPath})` — the filename (without `.md`), linking to the note file relative to the README. |
| Type | `Artifact`, `Page`, `DataType`, or any custom-type label per [Inputs](#inputs). |
| Workgroup | The resolved owning work group, as parsed (display value as written), or `(unknown)`. |
| Commits | The integer parsed from `Commits in window`, or `(unknown)`. |
| Tickets | The integer parsed from `Tickets attributed`, or `(unknown)`. |
| Needs Note | `yes`, `no`, or `(unknown)` per [Needs Note resolution](#needs-note-resolution). |

Render the table in standard GitHub-flavoured Markdown.

## README format

The generated `README.md` MUST follow this structure. Sections in
braces are filled in per run; instructional commentary in braces is
replaced by the actual generated content.

````markdown
# Ballot Note Index

Index of drafted ballot notes in this directory. Generated by the
`index-notes` skill from the reports produced by the `notes-artifact`,
`notes-page`, and `notes-datatype` skills.

- **Total drafted notes:** {N}
- **By type:** {comma-separated `Type: count` pairs in render order
  — Artifact, Page, DataType, then any custom types
  alphabetically; omit zero-count types}
- **Workgroups represented:** {comma-separated list of workgroup
  display names in render order, with `(unknown)` last if present}
- **Notes recommending an update:** {count of Needs Note = yes}
  / {count of Needs Note = no} not recommended / {count of
  `(unknown)`} unknown

## Table of Contents

{All notes, sorted alphabetically by Name (case-insensitive).}

| Name | Type | Workgroup | Commits | Tickets | Needs Note |
|------|------|-----------|---------|---------|------------|
| [{name}](./{relPath}) | {Type} | {Workgroup} | {N} | {M} | {yes/no/(unknown)} |
| … | … | … | … | … | … |

---

## By Workgroup

{One subsection per workgroup, in render order. Include the `By
Workgroup` parent heading even if there is only one workgroup.}

### {Workgroup display name}

{Filtered table of every note attributed to this workgroup, sorted
alphabetically by Name (case-insensitive). Same six columns as the
global table.}

| Name | Type | Workgroup | Commits | Tickets | Needs Note |
|------|------|-----------|---------|---------|------------|
| [{name}](./{relPath}) | {Type} | {Workgroup} | {N} | {M} | {yes/no/(unknown)} |
| … | … | … | … | … | … |

### {Next workgroup display name}

…
````

The `Workgroup` column is repeated in the per-workgroup tables (even
though it is constant within a section) so that the rows can be
copy-pasted out of the README without losing context.

## Important Rules

- **One README at the root.** Always written as `README.md` directly
  inside the source directory. Do **not** write a README inside the
  `artifacts/`, `pages/`, `datatypes/`, or other type subfolders.
- **Each note appears exactly twice in the README:** once in the
  global Table of Contents and once in its workgroup section.
- **Always overwrite.** Do not preserve a prior `README.md`; the
  skill is meant to be re-runnable.
- **Alphabetical, case-insensitive ordering** for both the global
  table rows and the workgroup section headings, and for rows within
  each workgroup section. The `(unknown)` workgroup section, when
  present, is rendered last.
- **Parse, do not invent.** Counts (`Commits`, `Tickets`),
  workgroups, and the `Needs Note` flag must come from the parsed
  report. If parsing fails after the documented fallbacks, render
  `(unknown)` rather than guessing.
- **Workgroup is *owning*, not per-ticket.** When the report has
  per-ticket workgroup lines that disagree, resolve with the modal
  rule in [Workgroup resolution](#workgroup-resolution); do not list
  multiple workgroups for one note.
- **CLI fallback only when needed.** Do not preemptively call
  `fhir-augury-cli` for workgroup data when the report already
  contains per-ticket workgroup lines or a header workgroup row.

## Example invocation

User: *"Build a ballot-note index under `./cache/output/ballot-notes/`."*

The skill should:

1. Verify `./cache/output/ballot-notes/` exists; enumerate its type
   subfolders (`artifacts/`, `pages/`, `datatypes/` — and any
   custom-type folders).
2. For each `*.md` file in each type subfolder, parse the header
   table and body to extract `name`, `type`, `commits`, `tickets`,
   `workgroup` (with CLI fallback), and `needsNote`.
3. Sort all entries alphabetically by `name`, group them by
   `workgroup`, and render the global Table of Contents plus one
   filtered table per workgroup.
4. Write `./cache/output/ballot-notes/README.md`, overwriting any
   prior copy.
5. Report back with the totals, per-type counts, per-workgroup
   counts, the `Needs Note` yes/no/unknown breakdown, and any notes
   whose workgroup resolved to `(unknown)`.
