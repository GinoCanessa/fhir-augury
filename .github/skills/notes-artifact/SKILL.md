---
name: notes-artifact
description: "Drafts an updated ballot note for a single FHIR artifact (resource / profile / IG artifact) from evidence hydrated by the BallotNotes processor. USE FOR: per-artifact ballot notes, ballot-comment drafting, change roll-ups for resources/profiles/IG artifacts. Requires a BallotNotes processor unit slug (the noteId) and the processor base URL (default http://localhost:5174). Reads the unit's hydrated evidence — resolved source files, attributed commits, the Jira tickets they applied, counters, and the current ballot-note HTML — then authors the after-applied roll-up and a draft HTML ballot note suitable for the artifact's intro file, and writes that prose back to the processor. The processor owns the deterministic gathering (commit-window walk, ticket attribution, source-file resolution, current-note capture). For specification *page* ballot notes (`source/*.html`), use `notes-page` instead. For the consolidated *datatypes* page (`source/datatypes/**`), use `notes-datatype`."
---

# Notes — Artifact Skill

Drafts an updated **ballot note** for a single FHIR artifact (resource,
profile, IG artifact, terminology bundle, …) from the evidence the
**BallotNotes processor** has already hydrated for the artifact's unit.
The processor owns the deterministic gathering — the commit-window
walk, source-file resolution, ticket attribution, and current-note
capture; this skill reads that evidence, authors the after-applied
roll-up and the proposed HTML ballot note, and writes the prose back to
the processor. It also optionally emits a human-readable markdown
report mirroring the same evidence.

The roll-up summary of changes **must reflect the after-applied
state** the processor hydrated (the net effect of the commit window),
not a stitch of per-ticket descriptions. Individual tickets frequently
overlap, expand, or revert each other — only the after-applied state
reflects reality.

This skill is the **artifact** counterpart to `notes-page` and
`notes-datatype`. The processor's unit `type` decides which skill
runs: `Artifact` → this skill; `Page` (a narrative `source/<page>.html`)
→ `notes-page`; `DataType` (the `source/datatypes/**` surface and any
per-datatype own-page) → `notes-datatype`. If the unit you fetched is
not an `Artifact`, stop and route to the matching skill.

## Data Access

This skill reads its evidence from — and writes its authored prose
back to — the **BallotNotes processor** over HTTP (default base URL
`http://localhost:5174`). The processor **owns all deterministic
gathering**: it has already walked the commit window, resolved the
artifact's source files, attributed commits to Jira tickets, and
captured the current ballot-note HTML. This skill never runs `git`,
never queries Jira directly, and never resolves source files itself —
it consumes the hydrated unit and authors prose.

Two endpoints are used, in this order:

1. **Read the hydrated unit** (GET-only):

   ```
   GET {processorBaseUrl}/api/v1/ballot-notes/{slug}
   ```

   Returns the unit's full hydrated evidence (source files, attributed
   commits, tickets, counters, the current ballot-note HTML) plus any
   prose already authored. `404` means the slug was never hydrated —
   stop and report it; the repo window must be hydrated first (via the
   `orchestrate-notes` skill or the processor's `hydrate` endpoint).

2. **Write the authored prose back** (the only state change):

   ```
   PUT {processorBaseUrl}/api/v1/ballot-notes/{slug}/note
   ```

   Body `{needsNote, proposedBallotNoteHtml, rollupSummaryMarkdown,
   notesForReviewerMarkdown, sourceFilesNote}`. Returns `200` with
   `{noteId, status:"authored"}`; `404` if the slug was never
   hydrated.

This read-evidence / write-back shape mirrors the
[`topic-groupings`](../topic-groupings/SKILL.md) skill, which GETs
clustering signals + hydration and PUTs groupings back.

## Inputs

- **Slug** *(required)* — the processor `noteId` for the artifact
  unit (the unit's slug, e.g., `hl7-fhir-artifact-observation`). The
  orchestrator (`orchestrate-notes`) passes it from the enumerated
  unit list; for ad-hoc runs, discover it via
  `GET {processorBaseUrl}/api/v1/ballot-notes?repo=<owner>/<name>&type=Artifact`.
- **Processor base URL** *(optional, default `http://localhost:5174`)*
  — the BallotNotes processor's base URL.
- **Output file** *(optional)* — full path where the human-readable
  markdown report should be written. The orchestrator passes a
  deterministic path; for ad-hoc invocations the agent may default to
  `<working-dir>/<repo-segment>_<artifact>.md` and report the path
  back. The **authoritative** persistence is the PUT in Step 4 — the
  markdown report is an additional convenience.
- **Working directory** *(optional)* — directory for transient files.
  When supplied, **all transient files must be written under this
  directory**. Create it with `New-Item -ItemType Directory -Force`
  (PowerShell), `mkdir -p` (bash), or your file-system tool if it does
  not exist.

## Prerequisites

- The **BallotNotes processor** (default `http://localhost:5174`) must
  be reachable, and the unit identified by **Slug** must already have
  been hydrated (its repo window walked by the processor). If
  `GET /api/v1/ballot-notes/{slug}` returns `404`, stop and ask the
  caller to hydrate the window first (via `orchestrate-notes` or the
  processor's `hydrate` endpoint).
- No clone, briefing, `git`, or Jira access is required by this skill
  — the processor performed that gathering server-side.

## Workflow

### Step 1: Read the hydrated unit

`GET {processorBaseUrl}/api/v1/ballot-notes/{slug}` and parse the
detail object. The processor is the **owner** of this evidence; use it
as-is — do **not** re-derive any of it:

- **Identity / window** — `type` (must be `Artifact`; if it is `Page`
  or `DataType`, stop and route to `notes-page` / `notes-datatype`),
  `name`, `repoOwner`, `repoName`, `repoCategory`, `sinceSha` /
  `sinceShortSha`, `headSha` / `headShortSha`, `windowLabel` (a
  human-readable window name such as `R6 Ballot 4`, when supplied),
  `workGroup` / `workGroupCode`, `hydratedAt`.
- **Counters** — `commitsInWindow`, `ticketsAttributed`, and the
  processor's first-pass `needsNote` (you refine it in Step 4).
- **`sourceFiles[]`** — `{path, role, touchedInWindow}` for every file
  the processor attributed to this artifact (the StructureDefinition,
  intro narrative, search-params bundle, operations list, examples,
  artifact-scoped terminology, …). The intro file is where the ballot
  note lives.
- **`commits[]`** — `{sha, shortSha, authorName, authorDate, subject,
  webUrl, ticketKeys[]}` for every window commit that touched this
  artifact. Commits with an empty `ticketKeys` are the "unattributed"
  group.
- **`tickets[]`** — `{ticketKey, title, resolution, workGroup,
  specification, url, commitCount, changeImpact, changeCategory}` for
  every attributed ticket. `changeImpact` is the ticket's own Jira
  change-impact classification (e.g. `Non-compatible`,
  `Compatible, substantive`, `Non-substantive`, or empty/unset);
  `changeCategory` is its change-category label.
- **`currentBallotNoteHtml`** — the verbatim ballot note(s) currently
  on the artifact's intro file at HEAD (empty if none).
- **Note classification** — `currentNoteIsAuguryGenerated` (whether the
  current note at HEAD was tool-generated and may be replaced) and
  `preservedHandAuthoredHtml` (hand-authored note blocks at HEAD that
  must be carried forward verbatim alongside your single regenerated
  note — never delete or rewrite them).
- **Existing prose** — `proposedBallotNoteHtml`, `rollupSummaryMarkdown`,
  `notesForReviewerMarkdown`, `sourceFilesNote`, and `status`
  (`authored` / `awaiting-note`). When `status` is already `authored`,
  treat the stored prose as a prior draft to revise rather than
  starting fresh.

If `sourceFiles[]` and `commits[]` are both empty, the artifact had no
changes in the window — write a short "No changes to artifact in
window" report, PUT `needsNote:"no"` with empty prose (Step 4), and
exit.

### Step 2: Curate the after-applied roll-up and per-ticket narrative

This is the skill's core value-add. Working **only** from the hydrated
evidence (never re-running `git` or re-querying Jira):

- Author the **roll-up summary** of what changed across the artifact
  in the window. It must reflect the **after-applied state** (the net
  effect of the whole window), not a stitch of per-ticket
  descriptions. Drive it from the `sourceFiles[]` roles and the
  attributed `commits[]` / `tickets[]`. Group observations by file
  role:
  - **StructureDefinition** — element additions / removals /
    cardinality / type / binding / constraint changes in the
    `<differential>` (treat `<snapshot>` as derived — note that
    snapshot regeneration is required, do not enumerate snapshot
    edits).
  - **Intro / narrative** — material narrative shifts (scope changes,
    boundary clarifications, deprecations, normative-status notes).
  - **Search parameters / operations** — added / removed / changed
    entries.
  - **Examples** — added / removed examples and updates forced by
    element changes.
  - **Terminology** — sibling `valueset-*` / `codesystem-*` changes;
    flag any that may belong in UTG.
- Author the **per-ticket "Changes Applied"** narrative for each entry
  in `tickets[]`, using its `title`, `resolution`, `specification`,
  `workGroup`, and the subjects of the `commits[]` whose `ticketKeys`
  include that ticket. Be honest about overlap: if two tickets touch
  the same area, say so and defer the authoritative summary to the
  roll-up.
- Reconcile against `currentBallotNoteHtml`: note which existing
  bullets are still accurate in the after-applied state (carry
  forward) and which were reverted or superseded (drop and explain in
  "Notes for Reviewer").

### Step 3: Draft the proposed ballot note

The proposed ballot note MUST:

- **Open with the change-window sentence.** When `windowLabel` is
  present in the GET payload, begin the note with
  "Changes since {windowLabel}" (e.g. "Changes since R6 Ballot 4");
  otherwise fall back to the `sinceShortSha..headShortSha` window. This
  states the window in human terms so balloters know what span the note
  covers.
- Be authored as **HTML**, ready to paste into the intro file inside a
  **single** tool-generated wrapper:
  `<blockquote class="ballot-note" data-augury-generated="true" id="…">…</blockquote>`.
  The `data-augury-generated="true"` marker is **required** — it is how
  the processor recognizes the block as tool-generated and replaces only
  that block on the next run. Preserve any existing `id` attribute when
  revising an existing note; pick the next free `bn<N>` id when adding a
  new note.
- **Produce exactly one consolidated note**, never two. A regenerated
  note replaces only the prior **tool-generated** block. If the GET
  payload's `preservedHandAuthoredHtml` is non-empty, those are
  hand-authored notes — carry them forward **verbatim** and never delete
  or rewrite them; your single marked note sits alongside them.
  `currentNoteIsAuguryGenerated` tells you whether the current note at
  HEAD was tool-generated (safe to replace) or hand-authored (preserve).
- Be **derived from the roll-up summary (Step 2)**, not a paste-up of
  the per-ticket descriptions. The roll-up reflects the actual
  after-applied state.
- **Incorporate the existing ballot note's substance.** If the
  existing note already calls out a change that is still present in
  the after-applied state, retain that bullet (revising wording for
  accuracy if the change has evolved). If the existing note refers to
  something that has since been reverted or superseded, remove it and
  briefly note the change in the report's "Notes for reviewer"
  section.
- Cite each underlying ticket with a Jira link of the form
  `<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
  next to the bullet it supports. Multiple tickets per bullet are
  fine.
- **Group entries strictly by the ticket's `changeImpact`**, under
  these four headers in this order: **Non-compatible** →
  **Compatible substantive** → **Non-substantive** → **Unclassified**.
  Defer entirely to the ticket's own classification — do **not**
  re-derive substantive vs non-substantive yourself. A ticket with an
  empty/unset `changeImpact` goes under **Unclassified** (rendered
  last); **never** fold an unset ticket into Non-substantive. Omit a
  header when its bucket is empty.
- When a ticket carries a `changeCategory`, render it as a small
  inline tag next to that entry (e.g. `<span class="tag">…</span>`).
- Avoid restating mechanics already obvious from the SD (e.g.,
  "renamed `Observation.referenceRange.normalValue.normalValue` to
  …"). Focus on intent, scope, and balloter-relevant impact.

### Step 4: Recommend, write the report, and persist back to the processor

1. **Decide `needsNote`** — `"yes"` if the after-applied changes
   warrant a ballot note, `"no"` if the window's net change is
   immaterial / purely editorial, `"unknown"` if you cannot tell.
   This refines the processor's first-pass `needsNote`.
2. **(Optional) Write the markdown report** to the **Output file**
   path, per the **Report Format** below — a human-readable
   convenience. Use the hydrated evidence to write substantive,
   specific content — no generic placeholders.
3. **Persist the authored prose back to the processor** (the
   authoritative step):

   ```
   PUT {processorBaseUrl}/api/v1/ballot-notes/{slug}/note
   ```

   with body:

   ```json
   {
     "needsNote": "yes",
     "proposedBallotNoteHtml": "<blockquote class='ballot-note' data-augury-generated='true' …>…</blockquote>",
     "rollupSummaryMarkdown": "…",
     "notesForReviewerMarkdown": "…",
     "sourceFilesNote": "…"
   }
   ```

   A `200` response (`{noteId, status:"authored"}`) confirms the note
   is stored. A `404` means the slug was never hydrated — report it and
   do not retry blindly. The PUT is idempotent (re-authoring replaces
   the stored prose), so a re-run is safe.

---

## Persisting back to the processor

The PUT in Step 4 carries **only** the authored prose and the
needs-note decision; every identity / window / counter / source-file /
commit / ticket field is read-only evidence the processor already
holds. The PUT body maps onto the report sections as:

| PUT field | Source in this skill |
|-----------|----------------------|
| `needsNote` | The Step 4 recommendation (`yes` / `no` / `unknown`). |
| `proposedBallotNoteHtml` | The drafted `<blockquote class="ballot-note" data-augury-generated="true">` from Step 3 (single consolidated note). |
| `rollupSummaryMarkdown` | The "Roll-up Summary" section body, as Markdown. |
| `notesForReviewerMarkdown` | The "Notes for Reviewer" section body, as Markdown. |
| `sourceFilesNote` | Any source-file caveat worth surfacing (optional). |

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Artifact Ballot Note Draft: {Artifact} ({owner/name})

| | |
|-|-|
| Repository | [{owner}/{name}](https://github.com/{owner}/{name}) ({repoCategory}) |
| Artifact | `{artifact}` |
| Window | [`{since-shortSha}`](https://github.com/{owner}/{name}/commit/{since-sha})..[`{head-shortSha}`](https://github.com/{owner}/{name}/commit/{head-sha}) |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Hydrated | BallotNotes processor unit `{slug}` @ `{hydratedAt}` |
| Generated | {ISO-8601 UTC timestamp} |

## Source Files

Files considered part of `{artifact}` for this run (as resolved by the
BallotNotes processor, from `sourceFiles[]`):

| Path | Role | Touched in window |
|------|------|-------------------|
| `source/{name}/structuredefinition-{name}.xml` | StructureDefinition | yes/no |
| `source/{name}/{name}-introduction.xml` | Narrative intro (ballot note lives here) | yes/no |
| `source/{name}/{name}-notes.xml` | Supplementary narrative | yes/no |
| `source/{name}/bundle-{name}-search-params.xml` | Search parameters | yes/no |
| `source/{name}/list-{name}-operations.xml` | Operations | yes/no |
| `source/{name}/list-{name}-examples.xml` | Examples list | yes/no |
| `source/{name}/valueset-*.xml` | Artifact-scoped ValueSets ({count}) | yes/no |
| `source/{name}/codesystem-*.xml` | Artifact-scoped CodeSystems ({count}) | yes/no |
| … | … | … |

{Any source-file caveat the processor surfaced (`sourceFilesNote`),
e.g., patterns that produced no match:}
- `<note>`

## Current Ballot Note

{If a ballot note exists at HEAD, paste its full HTML verbatim inside
a fenced ```html block. Include the `<blockquote …>` wrapper. If
multiple notes exist, include each with a heading line giving its
`id`. If none, write "No existing ballot note."}

```html
<blockquote class="ballot-note" id="bn1">
  …
</blockquote>
```

## Tickets Applied in Window

| Ticket | Title | Commits |
|--------|-------|---------|
| [{KEY}](https://jira.hl7.org/browse/{KEY}) | {ticket title} | [`{shortSha}`]({commitUrl}), [`{shortSha}`]({commitUrl}) |
| … | … | … |

{If commits in the window have no attributable ticket, add a final
row with `Ticket = (unattributed)` and list those commits.}

## Per-Ticket Detail

{One subsection per ticket. Order by descending commit count, then by
ticket key.}

### [{KEY}](https://jira.hl7.org/browse/{KEY}) — {title}

- **Work group:** {work_group}
- **Resolution:** {resolution}
- **Disposition summary:** {2–4 sentence neutral summary of what the
  disposition asked for, authored from the ticket's title, resolution,
  and the subjects of the commits that applied it. The hydrated
  evidence does not carry the verbatim applied-vote comment; do not
  invent one.}
- **Commits applying this ticket:**
  - [`{shortSha}`]({commitUrl}) — {commit subject} ({authorDate})
  - …
- **Changes applied (scoped to this artifact):**
  {2–6 sentences describing what these commits actually changed in
  this artifact's files. Be specific: name elements, files, and the
  nature of the change (added / removed / cardinality / binding /
  narrative). If overlap with other tickets means the per-ticket diff
  is misleading on its own, say so and reference the roll-up.}

{Include a final "(unattributed)" subsection if there are commits
without ticket attribution; it has no resolution / disposition fields
but lists the commits and what they changed.}

## Roll-up Summary (after-applied state)

{Authoritative summary of what changed across the artifact in the
window, derived from the after-applied evidence (Step 2). Group by
file role:}

- **StructureDefinition (`structuredefinition-{name}.xml`):**
  {bullets describing element-level changes in the differential —
  additions, removals, cardinality, type, binding, constraints. Note
  whether snapshot regeneration is required.}
- **Intro / narrative (`{name}-introduction.xml`, `{name}-notes.xml`):**
  {bullets describing material narrative shifts — scope changes,
  boundary clarifications, deprecations, normative-status notes.}
- **Search parameters (`bundle-{name}-search-params.xml`):**
  {added / removed / changed entries.}
- **Operations (`list-{name}-operations.xml`):**
  {added / removed / changed entries.}
- **Examples:**
  {added / removed examples and any updates required by element
  changes.}
- **Terminology (sibling `valueset-*` / `codesystem-*`):**
  {added / removed / changed entries; flag any that may belong in
  UTG.}

## Proposed Ballot Note (HTML)

{The draft ballot note, ready to drop into the intro file. Preserve
the existing `id` if revising; otherwise pick the next free `bn<N>`.
Use Jira links of the form
`<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
inline against the bullet they support.}

```html
<blockquote class="ballot-note" data-augury-generated="true" id="bn{N}">
  <p><b>Note to Balloters:</b> {one-paragraph framing of the change
  scope since the previous ballot, derived from the roll-up
  summary.}</p>
  <p><b>Non-compatible</b></p>
  <ul>
    <li>{Change from a Non-compatible ticket} (<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>) <span class="tag">{changeCategory}</span></li>
  </ul>
  <p><b>Compatible substantive</b></p>
  <ul>
    <li>{Change} (<a href="https://jira.hl7.org/browse/FHIR-YYYYY">FHIR-YYYYY</a>)</li>
  </ul>
  <p><b>Non-substantive</b></p>
  <ul>
    <li>{Change} (<a href="https://jira.hl7.org/browse/FHIR-ZZZZZ">FHIR-ZZZZZ</a>)</li>
  </ul>
  <p><b>Unclassified</b></p>
  <ul>
    <li>{Change from a ticket with no changeImpact set} (<a href="https://jira.hl7.org/browse/FHIR-WWWWW">FHIR-WWWWW</a>)</li>
  </ul>
</blockquote>
```

Omit any header whose bucket has no entries; keep the four in the order
shown, with **Unclassified** always last.

## Notes for Reviewer

{Free-form notes that did not fit elsewhere. Examples:
- Existing ballot-note bullets that were dropped because the change
  was reverted (cite the reverting commit and / or ticket).
- Tickets whose commits touched files outside the artifact's scope,
  with a one-line pointer to the other artifact.
- Anything the processor flagged in `sourceFilesNote`, or evidence
  that looked incomplete (e.g., a commit with no attributed ticket).

If none: "No additional notes."}
````

## Important Rules

- **Roll-up first, ticket bullets second.** The proposed ballot note
  must reflect the after-applied state from Step 2. Per-ticket
  descriptions are supporting evidence, not the source of truth.
- **Honour the existing ballot note.** Carry forward bullets that are
  still accurate in the after-applied state; drop and explain bullets
  that have been reverted or superseded.
- **Cite tickets inline in the proposed note.** Every bullet should
  point at the ticket(s) responsible. Use the Jira issue URL form
  shown above.
- **Use only the processor's hydrated evidence.** Do not re-run
  `git`, query Jira, or resolve source files yourself — the processor
  owns that gathering. Do not fabricate ticket details, file paths,
  commit SHAs, or disposition text; if the evidence lacks something,
  say so in the report.
- **Treat `<snapshot>` as derived.** Narrate `<differential>` changes
  in the SD; mention only that snapshot regeneration is required, do
  not enumerate snapshot edits.
- **Trust the processor's source-file resolution.** The `sourceFiles[]`
  list is authoritative for what belongs to this artifact; do not
  infer repo layout from memory or add files the processor did not
  attribute.
- **Be specific.** "Updated several elements" is not useful. Name the
  element, the field, the old vs. new value where relevant.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root or alongside the
  cached clone.
