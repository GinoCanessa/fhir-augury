---
name: notes-datatype
description: "Drafts an updated ballot note for the consolidated FHIR *datatypes* page based on changes made since a specified commit. USE FOR: ballot notes covering changes under `source/datatypes/` in `HL7/fhir`, which all render into the single `datatypes.html` page (with sub-pages per primitive / complex type). Requires a GitHub repo (must be HL7/fhir) and a since-commit SHA. Optionally accepts a focus list of specific datatype names; defaults to all datatypes touched in the window. Walks every changed file under `source/datatypes/` between the since-commit and HEAD, attributes commits to the FHIR Jira tickets they applied, summarizes what actually changed in the after-applied state grouped by datatype, and writes a markdown report containing a draft HTML ballot note for the datatypes page. For per-resource/profile ballot notes, use `notes-artifact`. For other narrative pages, use `notes-page`."
---

# Notes — Datatype Skill

Drafts an updated **ballot note** for the consolidated FHIR
**datatypes page** (`source/datatypes.html` in `HL7/fhir`) by
analyzing the changes that have landed in `source/datatypes/**` since
a caller-supplied commit.

The output is a markdown review report containing the proposed HTML
ballot note plus the supporting evidence (per-commit / per-ticket
breakdown, per-datatype change roll-up, current ballot note for
context).

The roll-up summary of changes **must be derived from the
after-applied diff** (since-commit → HEAD), not by stitching together
per-ticket descriptions. Individual tickets frequently overlap, expand,
or revert each other — only the after-applied state reflects reality.

This skill is the **datatypes** counterpart to `notes-artifact` and
`notes-page`. The three skills share the same workflow shape,
ticket-attribution rules, report layout, and ballot-note authoring
conventions; only the file scope and artifact-resolution rules
differ. When in doubt about a generic step, consult `notes-artifact`.

## Why a dedicated skill

In `HL7/fhir`, every primitive and many complex datatypes are authored 
as one or more files under `source/datatypes/` (one StructureDefinition per
datatype, plus shared narrative, examples, diagrams, and terminology).
All of those source files render into a *single* ballot page —
`source/datatypes.html` — with anchor sub-sections per datatype. The
ballot note for "datatypes" is therefore a **page-level note that
spans many StructureDefinitions**. Treating each datatype as an
independent artifact via `notes-artifact` would fragment the note;
treating the page via `notes-page` would miss the per-datatype
StructureDefinition changes. This skill bridges the two: per-datatype
roll-ups feeding a single page-level ballot note.

## Data Access

All FhirAugury data access (Jira, GitHub cross-references, repo
listing, item fetches) goes through the **`fhir-augury-cli`** skill.
That skill documents the CLI envelope, recipes, and the fallback chain
(CLI → MCP → direct HTTP → `appsettings.json`). Do not duplicate CLI
knowledge here.

In addition to the CLI, this skill is allowed to invoke **`git`**
directly against the cached clone (`cache/github/repos/<owner>_<name>/clone/`)
and may use **`gh`** (the GitHub CLI) when commit metadata or commit
URLs need to be resolved against `github.com`. `git` against the cache
clone is preferred — it is offline, fast, and authoritative for the
state FhirAugury has already ingested.

When a CLI command is shown below, it is in the form documented by
`fhir-augury-cli`:

```bash
fhir-augury-cli --json '<json>' [--pretty]
```

## Inputs

- **Repo** *(required)* — `owner/name`. Must be `HL7/fhir` (the FHIR
  Core repo); other repos do not have the `source/datatypes/` layout
  this skill targets. If a different repo is supplied, stop and ask
  the user whether they meant `notes-artifact`.
- **Since-commit** *(required)* — full or short SHA. The roll-up window
  is `since-commit..HEAD` of the cached clone (fast-forward range; if
  HEAD is not a descendant of since-commit, fall back to the symmetric
  difference and note the deviation in the report).
- **Datatype focus** *(optional)* — a comma- or space-separated list
  of datatype names to highlight (e.g., `Quantity, Period, Range`).
  The skill always considers *all* changes under `source/datatypes/`
  in the window, but when a focus list is supplied:
  - the per-datatype roll-up table orders focus datatypes first;
  - the proposed ballot note's bullets prioritize focus datatypes;
  - non-focus datatypes still appear in the per-datatype roll-up but
    may be summarized more briefly.

  If omitted (the default), every datatype with at least one touched
  file in the window is treated equally.
- **Output file** *(required)* — full path where the markdown report
  should be written. The orchestrator passes a deterministic path; for
  ad-hoc invocations the agent may default to
  `<working-dir>/HL7_fhir_datatypes.md` and report the path back.
- **Working directory** *(optional)* — directory for transient files
  (intermediate diffs, commit lists, ticket dumps). When supplied,
  **all transient files must be written under this directory**. Create
  it with `New-Item -ItemType Directory -Force` (PowerShell),
  `mkdir -p` (bash), or your file-system tool if it does not exist.

## Prerequisites

- The GitHub source clone cache for `HL7/fhir` must be populated and
  current enough that the since-commit is reachable from the cached
  clone HEAD. If the since-commit is missing, ask the user to refresh
  the clone (or fall back to fetching the commit via `gh api` and
  noting the deviation in the report).
- A current per-repo briefing under
  `cache/github/repos/HL7_fhir/repo-analysis/briefing.md` must exist.
  The datatype-to-files grouping in Step 2 leans on the briefing's
  **Artifact Map** for any datatype with non-conventional file names;
  for the common case (one SD per datatype, named after the datatype)
  the convention below is sufficient. If the briefing is missing or
  stale (per the staleness rules in the `repo-analysis` skill), warn
  the user but proceed; record the staleness in the report's "Notes
  for reviewer" section.
- `git` must be available on `PATH`. `gh` is required only if the
  cache clone cannot resolve the since-commit or a commit URL needs to
  be confirmed against `github.com`.

## Workflow

Run independent calls in parallel where possible.

### Step 1: Verify services and resolve scope

1. Health-check via `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"services","action":"health"}'
   ```

2. Read the briefing and metadata (best-effort — do not block on
   staleness, but record it):
   - `cache/github/repos/HL7_fhir/repo-analysis/briefing.md`
   - `cache/github/repos/HL7_fhir/repo-analysis/meta.json`

3. Confirm the cache clone and resolve HEAD:

   ```powershell
   $clone = "cache/github/repos/HL7_fhir/clone"
   git -C $clone rev-parse HEAD
   git -C $clone cat-file -e <since-commit>^{commit}
   ```

   If `cat-file -e` fails, the since-commit isn't in the cache clone —
   stop and ask the user to refresh the clone, or fall back to
   `gh api /repos/HL7/fhir/commits/<since-commit>` and note the
   limitation in the report.

4. Confirm the page file exists at HEAD:

   ```powershell
   git -C $clone cat-file -e HEAD:source/datatypes.html
   ```

   If absent, stop with an error — the consolidated datatypes page is
   the ballot-note target, and a missing page invalidates the run.

### Step 2: Resolve scope → source files (per datatype)

The full file scope is everything under `source/datatypes/` in the
clone. Build the working list dynamically from the diff window
(Step 3) — there is no fixed file list. Conventions for grouping
files by datatype:

- **Primary StructureDefinition** — `source/datatypes/<name>.xml`
  (filename stem matches the datatype name, lowercase per repo
  convention). This is the canonical SD for the datatype.
- **Extension SDs** — `source/datatypes/structuredefinition-*.xml`
  for shared / cross-cutting datatype extensions. Group by the
  filename stem after `structuredefinition-`.
- **Spreadsheets** — `source/datatypes/<name>-spreadsheet.xml` (legacy
  authoring; SD is authoritative — note in the report but do not
  enumerate spreadsheet edits separately if the SD reflects them).
- **Examples** — `source/datatypes/<name>-example*.xml` and any
  `source/datatypes/<name>-examples.xml`.
- **Terminology siblings** — `source/datatypes/valueset-*.xml` and
  `source/datatypes/codesystem-*.xml`. Attribute to the datatype that
  binds them (per the briefing or the SD's `binding/valueSet`); if
  cross-cutting (used by multiple datatypes), group under a shared
  "Cross-cutting terminology" datatype bucket.
- **Diagrams** — `source/datatypes/*.diagram` and any
  `source/datatypes/*.png` / `*.svg` referenced from the page.
- **Shared narrative / glossary** — files such as
  `source/datatypes/_changelog.txt`,
  `source/datatypes/abstracts.diagram`,
  `source/datatypes/allprimitivetypes.diagram`,
  `source/datatypes/alltypes.diagram`. Group under a "Page-level"
  bucket; do not assign to an individual datatype.

The page itself, `source/datatypes.html`, is **always included** in
the working file list (the ballot note lives there) regardless of
whether it was touched in the window.

For any file the convention above cannot place, fall back to the
briefing's Artifact Map. If still ambiguous, group under a "Other /
unassigned" datatype bucket and flag it in the report.

Materialise the file list as both:

- **`workingFileList`** — paths relative to the clone root, used by
  `git` calls in Steps 3–5.
- **`displayFileList`** — paths shown in the report, with the
  datatype bucket and one-line role for each.

### Step 3: Enumerate commits in the window

Enumerate commits that touched any file under `source/datatypes/`
(plus `source/datatypes.html`) between `since-commit` and `HEAD`:

```bash
git -C cache/github/repos/HL7_fhir/clone log \
    --no-merges \
    --pretty=format:'%H%x09%an%x09%aI%x09%s' \
    <since-commit>..HEAD \
    -- source/datatypes/ source/datatypes.html
```

Then derive the full per-commit file list with
`git show --name-only <sha>` (or pre-compute with `git log
--name-only`) so each commit can be bucketed by datatype in Step 5.

For each commit row, capture:
- `sha` (full)
- `shortSha` (`git rev-parse --short=12 <sha>`)
- `authorName`
- `authorDate` (ISO-8601)
- `subject` (first line of the commit message)
- `body` (full message via `git show -s --format=%B <sha>`)
- `webUrl` — `https://github.com/HL7/fhir/commit/<sha>`
- `touchedFiles` — files in this commit that fall within the working
  file list, classified by datatype bucket.

If the window is empty (no commits touched any datatype file or the
page), write a short report noting "No changes to datatypes in
window" and exit.

### Step 4: Attribute commits to Jira tickets

Identical to `notes-artifact` Step 4. For each commit, extract
candidate Jira ticket keys (regex `(FHIR|UTG)-\d+`) from the commit
subject + body, and union with any keys returned by
`cross-referenced` for the commit SHA:

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"<sha>","limit":50}'
```

Build three indexes in memory:

- `commitToTickets[sha] = [key, …]`
- `ticketToCommits[key] = [{sha, shortSha, subject, webUrl, datatypes:[…]}, …]`
- `datatypeToTickets[name] = [key, …]` — derived from the datatype
  bucket of each commit's `touchedFiles`.

For each unique ticket key, fetch its details once, in parallel
across keys:

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":true,"includeComments":true,"includeSnapshot":true}'
```

Extract `metadata.title`, `metadata.resolution`,
`metadata.resolution_description`, `metadata.work_group`,
`metadata.specification`, `content`, and the applied-vote /
disposition comment.

Commits with no discoverable ticket keys are listed in the commit
table under an "Unattributed" group; their diffs are still rolled
into Step 5.

### Step 5: Compute diffs (per-ticket, per-datatype, and rollup)

Three diff sets are required.

**5a. Roll-up diff (since-commit → HEAD).**

```bash
git -C <clone> diff <since-commit>..HEAD -- <workingFileList>
git -C <clone> diff --stat <since-commit>..HEAD -- <workingFileList>
```

This is the authoritative after-applied view. Use it to validate
per-datatype roll-ups (5b) and to drive the page-level summary that
opens the proposed ballot note (Step 7).

**5b. Per-datatype roll-up.**

For each datatype bucket with at least one touched file, compute the
roll-up diff scoped to that bucket:

```bash
git -C <clone> diff <since-commit>..HEAD -- <bucket-files>
```

Narrate per-datatype changes by file role:

- **StructureDefinition (`<name>.xml` and any
  `structuredefinition-<name>-*`):** element additions / removals /
  cardinality / type / binding / constraint changes in the
  `<differential>`. Treat `<snapshot>` edits as derived — note that
  snapshot regeneration is required, do not enumerate snapshot edits.
- **Examples:** added / removed / changed examples and any updates
  required by element changes.
- **Terminology siblings:** added / removed / changed
  `valueset-*` / `codesystem-*` entries; flag any that may belong in
  UTG per the briefing's cross-repo touch points.
- **Diagrams / spreadsheets:** note presence of diagram / spreadsheet
  changes; do not narrate spreadsheet edits if the SD reflects them.

**5c. Per-ticket diff.**

For each ticket with at least one commit in the window, compute the
union diff of that ticket's commits, scoped to the working file list:

```bash
git -C <clone> show --stat --pretty=fuller <sha1> <sha2> ... -- <workingFileList>
```

Use this to author the **per-ticket "Changes Applied"** paragraph,
and to record which datatype(s) the ticket touched. Be honest about
overlap: if two tickets touch the same element of the same datatype,
say so and defer the authoritative summary to the per-datatype
roll-up.

### Step 6: Read the current ballot note

Read `source/datatypes.html` at HEAD and locate any
`<blockquote class="ballot-note" …>…</blockquote>` blocks. Extract
their full inner content verbatim. If multiple ballot notes exist
(distinct `id`s), capture them all.

If no ballot note exists, record "No existing ballot note." and
draft a fresh one in Step 7. The conventional location for a page
ballot note is at the top of the body, immediately after the page
title / intro paragraph; record where you propose to insert it.

### Step 7: Draft the proposed ballot note

The proposed ballot note MUST:

- Be authored as **HTML**, ready to paste into
  `source/datatypes.html` inside a
  `<blockquote class="ballot-note" id="…">…</blockquote>` wrapper.
  Preserve any existing `id` attribute when revising an existing
  note; pick the next free `bn<N>` id when adding a new note.
- Be **derived from the per-datatype roll-ups (Step 5b) reconciled
  against the page-level diff (Step 5a)**, not a paste-up of the
  per-ticket descriptions.
- **Group bullets by datatype.** The expected shape is a short
  framing paragraph followed by `<ul>` with one bullet per datatype
  (or per closely related datatype cluster, e.g.,
  `Quantity` / `SimpleQuantity`). Page-level / cross-cutting changes
  (e.g., new abstract type, glossary additions) get their own bullets.
- **Honour the focus list** when one was supplied: focus datatypes
  appear first and may justify multiple bullets; non-focus datatypes
  may be condensed.
- **Incorporate the existing ballot note's substance.** If the
  existing note already calls out a change that is still present in
  the after-applied state, retain that bullet (revising wording for
  accuracy if the change has evolved). If the existing note refers
  to something that has since been reverted or superseded, remove it
  and briefly note the change in the report's "Notes for reviewer"
  section.
- Cite each underlying ticket with a Jira link of the form
  `<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
  next to the bullet it supports. Multiple tickets per bullet are
  fine; bullets covering multi-datatype changes should cite every
  contributing ticket.
- Avoid restating mechanics already obvious from the SD ("renamed
  `Quantity.foo` to `Quantity.bar`"). Focus on intent, scope, and
  balloter-relevant impact.
- Skip pure editorial churn (typo fixes, link normalisation,
  whitespace) unless substantial enough to warrant a closing
  sentence.

### Step 8: Write the report

Compose the markdown report per the **Report Format** below and save
it to the output file path. Use the gathered data to write
substantive, specific content — no generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Datatypes Ballot Note Draft (HL7/fhir)

| | |
|-|-|
| Repository | [HL7/fhir](https://github.com/HL7/fhir) (FhirCore) |
| Page | `source/datatypes.html` |
| Source root | `source/datatypes/` |
| Window | [`{since-shortSha}`](https://github.com/HL7/fhir/commit/{since-sha})..[`{head-shortSha}`](https://github.com/HL7/fhir/commit/{head-sha}) |
| Datatypes touched | {D} |
| Focus datatypes | {comma-separated focus list, or "(all touched)"} |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Briefing | `cache/github/repos/HL7_fhir/repo-analysis/briefing.md` @ clone `{briefing-shortSha}` |
| Generated | {ISO-8601 UTC timestamp} |

## Datatypes Touched

| Datatype | Files touched | Tickets | Page-level? |
|----------|---------------|---------|-------------|
| `Quantity` | 3 | [FHIR-XXXXX](…), [FHIR-YYYYY](…) | no |
| `Period` | 1 | [FHIR-ZZZZZ](…) | no |
| (Cross-cutting terminology) | 2 | [FHIR-AAAAA](…) | yes |
| (Page-level) | 1 (`source/datatypes.html`) | — | yes |
| … | … | … | … |

## Source Files

Files considered in this run, grouped by datatype bucket:

### `Quantity`

| Path | Role | Touched in window |
|------|------|-------------------|
| `source/datatypes/quantity.xml` | StructureDefinition | yes |
| `source/datatypes/quantity-example.xml` | Example | yes |
| … | … | … |

### `Period`

| … | … | … |

### (Cross-cutting terminology)

| `source/datatypes/valueset-…xml` | ValueSet | yes |
| `source/datatypes/codesystem-…xml` | CodeSystem | yes |

### (Page-level)

| `source/datatypes.html` | Datatypes page (ballot note lives here) | yes/no |
| `source/datatypes/_changelog.txt` | Changelog | yes/no |
| `source/datatypes/alltypes.diagram` | All-types diagram | yes/no |

## Current Ballot Note

{If a ballot note exists at HEAD on `source/datatypes.html`, paste its
full HTML verbatim inside a fenced ```html block. Include the
`<blockquote …>` wrapper. If multiple notes exist, include each with a
heading line giving its `id`. If none, write "No existing ballot
note." and state where the proposed note will be inserted.}

```html
<blockquote class="ballot-note" id="bn1">
  …
</blockquote>
```

## Tickets Applied in Window

| Ticket | Title | Datatypes | Commits |
|--------|-------|-----------|---------|
| [{KEY}](https://jira.hl7.org/browse/{KEY}) | {ticket title} | `Quantity`, `Period` | [`{shortSha}`]({commitUrl}), [`{shortSha}`]({commitUrl}) |
| … | … | … | … |

{If commits in the window have no attributable ticket, add a final
row with `Ticket = (unattributed)` and list those commits with their
datatype buckets.}

## Per-Ticket Detail

{One subsection per ticket. Order by descending commit count, then by
ticket key.}

### [{KEY}](https://jira.hl7.org/browse/{KEY}) — {title}

- **Work group:** {work_group}
- **Resolution:** {resolution}
- **Datatypes touched:** `Quantity`, `Period`
- **Disposition (verbatim):**

  > {Exact disposition text from the applied-vote comment, quoted
  > verbatim. If unavailable, write "Disposition text not recorded in
  > Jira."}

- **Disposition summary:** {2–4 sentence neutral summary of what the
  disposition asked for.}
- **Commits applying this ticket:**
  - [`{shortSha}`]({commitUrl}) — {commit subject} ({authorDate})
  - …
- **Changes applied (per Step 5c, scoped to the datatypes page):**
  {2–6 sentences describing what these commits actually changed.
  Be specific: name the datatype, the element, the field, the nature
  of the change. If overlap with other tickets means the per-ticket
  diff is misleading on its own, say so and reference the
  per-datatype roll-up.}

{Include a final "(unattributed)" subsection if there are commits
without ticket attribution; it lists the commits, their datatype
buckets, and what they changed.}

## Per-Datatype Roll-up (after-applied state)

{One subsection per datatype with at least one touched file in the
window, in focus-first then alphabetical order.}

### `Quantity`

- **StructureDefinition (`source/datatypes/quantity.xml`):**
  {bullets describing element-level changes in the differential —
  additions, removals, cardinality, type, binding, constraints.
  Note whether snapshot regeneration is required.}
- **Examples:**
  {added / removed / changed examples.}
- **Terminology:**
  {sibling valueset/codesystem changes, or "None".}

### `Period`

…

### (Cross-cutting terminology)

{Terminology files used by multiple datatypes; list which datatypes
they bind and what changed.}

### (Page-level)

{Changes to `source/datatypes.html` itself (intro / framing changes,
section reorganisations) and to shared narrative / diagrams under
`source/datatypes/`.}

## Page-level Roll-up Summary (after-applied state)

{Authoritative whole-page summary derived from the Step 5a diff. Use
this to verify that the per-datatype roll-ups together account for
the visible page changes. Call out any change that crosses datatypes
(e.g., a shared element type rename) here.}

## Proposed Ballot Note (HTML)

{The draft ballot note, ready to drop into `source/datatypes.html`.
Preserve the existing `id` if revising; otherwise pick the next free
`bn<N>`. Use Jira links of the form
`<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
inline against the bullet they support.}

```html
<blockquote class="ballot-note" id="bn{N}">
  <p><b>Note to Balloters:</b> {one-paragraph framing of the change
  scope across the datatypes since the previous ballot, derived from
  the page-level roll-up.}</p>
  <ul>
    <li><b>Quantity:</b> {substantive change} (<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>)</li>
    <li><b>Period:</b> {substantive change} (<a href="https://jira.hl7.org/browse/FHIR-YYYYY">FHIR-YYYYY</a>)</li>
    <li><b>Cross-cutting:</b> {substantive change} (<a href="https://jira.hl7.org/browse/FHIR-ZZZZZ">FHIR-ZZZZZ</a>)</li>
    <li>…</li>
  </ul>
</blockquote>
```

## Notes for Reviewer

{Free-form notes that did not fit elsewhere. Examples:
- Existing ballot-note bullets that were dropped because the change
  was reverted (cite the reverting commit and / or ticket).
- Commits in the window that touched files outside `source/datatypes/`
  and `source/datatypes.html` (resource SDs, narrative pages,
  terminology in other folders). Add a one-line pointer to
  `notes-artifact` / `notes-page` for each.
- Datatypes the bucketing rule could not place automatically (under
  "Other / unassigned") and how you handled them.
- Cases where the HEAD is not a descendant of the since-commit and
  the symmetric difference was used instead.
- Briefing staleness or absence.
- Any time `gh api` was used because the cache clone could not
  resolve a referenced commit.

If none: "No additional notes."}
````

## Important Rules

- **Per-datatype roll-up first, page-level reconciliation second,
  ticket bullets last.** The proposed ballot note must reflect the
  after-applied state. Per-ticket descriptions are supporting
  evidence, not the source of truth.
- **Group ballot bullets by datatype.** The reader of
  `datatypes.html` navigates by datatype anchor; the ballot note
  should mirror that mental model.
- **Honour the existing ballot note.** Carry forward bullets that are
  still accurate in the after-applied state; drop and explain bullets
  that have been reverted or superseded.
- **Cite tickets inline in the proposed note.** Every bullet should
  point at the ticket(s) responsible. Use the Jira issue URL form
  shown above.
- **Stay in your lane.** This skill owns *only* `source/datatypes/**`
  and `source/datatypes.html`. Resource / profile changes belong to
  `notes-artifact`; other narrative pages belong to `notes-page`.
- **Treat `<snapshot>` as derived.** Narrate `<differential>` changes
  in each SD; mention only that snapshot regeneration is required, do
  not enumerate snapshot edits.
- **Spreadsheets are legacy.** If a `<name>-spreadsheet.xml` is
  touched but the SD is not, flag it; otherwise rely on the SD as
  authoritative and do not enumerate spreadsheet edits.
- **Use only data from `fhir-augury-cli`, the cached clone (`git`),
  and `gh` as a last resort.** Do not fabricate ticket details, file
  paths, commit SHAs, or disposition text. If a call fails or returns
  no data, say so in the report.
- **Be specific.** Name the datatype, the element, the field, the old
  vs. new value where relevant.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root or alongside the
  cached clone.
