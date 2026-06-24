---
name: notes-page
description: "Drafts an updated ballot note for a single FHIR specification *page* from evidence hydrated by the BallotNotes processor. USE FOR: per-page ballot notes, ballot-comment drafting, change roll-ups for narrative/spec pages in `HL7/fhir` (`source/<page>.html`) and IG `input/pagecontent` pages. Requires a BallotNotes processor unit slug (the noteId) and the processor base URL (default http://localhost:5174). Reads the unit's hydrated evidence — resolved page source files, attributed commits, the Jira tickets they applied, counters, and the current ballot-note HTML — then authors the after-applied roll-up and a draft ballot note suitable for inlining at the top of the page, and writes that prose back to the processor. The processor owns the deterministic gathering (commit-window walk, ticket attribution, page-source resolution, current-note capture). For per-resource/profile ballot notes, use `notes-artifact` instead. For the consolidated datatypes page, use `notes-datatype`."
---

# Notes — Page Skill

Drafts an updated **ballot note** for a single FHIR specification page
(a narrative `.html` file directly under `source/` in `HL7/fhir`, such
as `security.html`, `extensibility.html`, `terminologies.html`,
`narrative.html`, `references.html`, …, or an IG `input/pagecontent/`
page) from the evidence the **BallotNotes processor** has already
hydrated for the page's unit. The processor owns the deterministic
gathering — the commit-window walk, page-source resolution, ticket
attribution, and current-note capture; this skill reads that evidence,
authors the after-applied roll-up and the proposed ballot note, and
writes the prose back to the processor. It also optionally emits a
human-readable markdown report mirroring the same evidence.

The roll-up summary of changes **must reflect the after-applied
state** the processor hydrated (the net effect of the commit window),
not a stitch of per-ticket descriptions. Individual tickets frequently
overlap, expand, or revert each other — only the after-applied state
reflects reality.

This skill is the **page** counterpart to `notes-artifact` and
`notes-datatype`. The processor's unit `type` decides which skill
runs: `Page` → this skill; `Artifact` (a resource / profile / IG
artifact) → `notes-artifact`; `DataType` (the `source/datatypes/**`
surface and any per-datatype own-page) → `notes-datatype`. If the unit
you fetched is not a `Page`, stop and route to the matching skill. The
three skills share the same report layout and ballot-note authoring
conventions; only the file scope differs.

## Data Access

This skill reads its evidence from — and writes its authored prose
back to — the **BallotNotes processor** over HTTP (default base URL
`http://localhost:5174`). The processor **owns all deterministic
gathering**: it has already walked the commit window, resolved the
page's source files, attributed commits to Jira tickets, and captured
the current ballot-note HTML. This skill never runs `git`, never
queries Jira directly, and never resolves page sources itself — it
consumes the hydrated unit and authors prose.

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

- **Slug** *(required)* — the processor `noteId` for the page unit
  (the unit's slug, e.g., `hl7-fhir-page-security`). The orchestrator
  (`orchestrate-notes`) passes it from the enumerated unit list; for
  ad-hoc runs, discover it via
  `GET {processorBaseUrl}/api/v1/ballot-notes?repo=<owner>/<name>&type=Page`.
- **Processor base URL** *(optional, default `http://localhost:5174`)*
  — the BallotNotes processor's base URL.
- **Output file** *(optional)* — full path where the human-readable
  markdown report should be written. The orchestrator passes a
  deterministic path; for ad-hoc invocations the agent may default to
  `<working-dir>/<owner>_<name>_page_<page>.md` and report the path
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
  — the processor performed that gathering server-side, including the
  per-category page-source resolution.

## Workflow

### Step 1: Read the hydrated unit

`GET {processorBaseUrl}/api/v1/ballot-notes/{slug}` and parse the
detail object. The processor is the **owner** of this evidence; use it
as-is — do **not** re-derive any of it:

- **Identity / window** — `type` (must be `Page`; if it is `Artifact`
  or `DataType`, stop and route to `notes-artifact` / `notes-datatype`),
  `name` (the page stem), `repoOwner`, `repoName`, `repoCategory`,
  `sinceSha` / `sinceShortSha`, `headSha` / `headShortSha`,
  `windowLabel` (a human-readable window name such as `R6 Ballot 4`,
  when supplied), `workGroup` / `workGroupCode`, `hydratedAt`.
- **Counters** — `commitsInWindow`, `ticketsAttributed`, and the
  processor's first-pass `needsNote` (you refine it in Step 4).
- **`sourceFiles[]`** — `{path, role, touchedInWindow}` for the
  primary page source the processor resolved (per repo category:
  `source/<page>.html` for `HL7/fhir`,
  `input/pagecontent/<page>.{md,xml}` for IG repos) plus any
  conventional sibling fragments / images. The primary page file is
  where the ballot note lives.
- **`commits[]`** — `{sha, shortSha, authorName, authorDate, subject,
  webUrl, ticketKeys[]}` for every window commit that touched the
  page's file set. Commits with an empty `ticketKeys` are the
  "unattributed" group.
- **`tickets[]`** — `{ticketKey, title, resolution, workGroup,
  specification, url, commitCount, changeImpact, changeCategory}` for
  every attributed ticket. `changeImpact` is the ticket's own Jira
  change-impact classification (`Non-compatible`,
  `Compatible, substantive`, `Non-substantive`, or empty/unset);
  `changeCategory` is its change-category label.
- **`currentBallotNoteHtml`** — the verbatim ballot note(s) currently
  on the page at HEAD (empty if none). The processor captures these
  regardless of marker convention (`ballot-note` / `stu-note` /
  IG-Publisher include).
- **Note classification** — `currentNoteIsAuguryGenerated` (whether the
  current note was tool-generated and may be replaced) and
  `preservedHandAuthoredHtml` (hand-authored note blocks at HEAD to
  carry forward verbatim alongside your single regenerated note — never
  delete or rewrite them).
- **Existing prose** — `proposedBallotNoteHtml`, `rollupSummaryMarkdown`,
  `notesForReviewerMarkdown`, `sourceFilesNote`, and `status`
  (`authored` / `awaiting-note`). When `status` is already `authored`,
  treat the stored prose as a prior draft to revise rather than
  starting fresh.

If `sourceFiles[]` and `commits[]` are both empty, the page had no
changes in the window — write a short "No changes to page in window"
report, PUT `needsNote:"no"` with empty prose (Step 4), and exit. If
the evidence indicates the primary page file was removed in the window
(the processor flags it via `sourceFilesNote`, or its `sourceFiles[]`
entry is gone at HEAD), draft a "page removed" note pointing at the
redirect / replacement the commit subjects indicate.

### Step 2: Curate the after-applied roll-up and per-ticket narrative

This is the skill's core value-add. Working **only** from the hydrated
evidence (never re-running `git` or re-querying Jira):

- Author the **roll-up summary** of what changed across the page in
  the window. It must reflect the **after-applied state** (the net
  effect of the whole window), not a stitch of per-ticket
  descriptions. Pages are narrative content (HTML in `HL7/fhir`,
  markdown in IG repos); group observations by section heading where
  possible. Call out:
  - New / removed / restructured headings.
  - Material narrative shifts within a section: scope changes,
    boundary clarifications, normative-status notes, deprecations,
    added / removed examples or code snippets, conformance-language
    deltas (`SHALL` / `SHOULD` / `MAY`), changed cross-page links,
    updated diagrams or images.
  - Added / removed / changed ballot-note blocks.
  - Editorial-only churn (typos, link normalisation, whitespace),
    bucketed together — it should not drive ballot-note bullets.
- Author the **per-ticket "Changes Applied"** narrative for each entry
  in `tickets[]`, using its `title`, `resolution`, `specification`,
  `workGroup`, and the subjects of the `commits[]` whose `ticketKeys`
  include that ticket. Be honest about overlap: if two tickets touch
  the same paragraph, say so and defer the authoritative summary to
  the roll-up.
- Reconcile against `currentBallotNoteHtml`: note which existing
  bullets are still accurate in the after-applied state (carry
  forward) and which were reverted or superseded (drop and explain in
  "Notes for Reviewer").


### Step 3: Draft the proposed ballot note

The proposed ballot note MUST:

- **Open with the change-window sentence.** When `windowLabel` is
  present in the GET payload, begin the note with
  "Changes since {windowLabel}" (e.g. "Changes since R6 Ballot 4");
  otherwise fall back to the `sinceShortSha..headShortSha` window.
- Be authored in the **format the page expects**:
  - HL7/fhir (HTML pages): an HTML
    `<blockquote class="ballot-note" data-augury-generated="true" id="…">…</blockquote>`
    wrapper. The `data-augury-generated="true"` marker is **required**
    so the processor replaces only this tool-generated block next run.
  - IG / extension-pack / incubator (markdown pages): the IG's
    ballot-note convention (typically an HTML `<blockquote
    class="stu-note" data-augury-generated="true">` block embedded in
    the markdown, or the IG-Publisher include used elsewhere in the same
    IG — match the style already in use in the repo, but keep the
    `data-augury-generated="true"` marker on the block you generate).
  - Other categories: match the style of the existing ballot note
    in `currentBallotNoteHtml`, or ask the reviewer to choose.

  Preserve any existing `id` attribute when revising an existing
  note; pick the next free `bn<N>` id when adding a new note.
- **Produce exactly one consolidated note**, never two. A regenerated
  note replaces only the prior **tool-generated** block. When
  `preservedHandAuthoredHtml` is non-empty, carry those hand-authored
  notes forward **verbatim** — never delete or rewrite them.
  `currentNoteIsAuguryGenerated` tells you whether the current note is
  tool-generated (replace) or hand-authored (preserve).
- Be **derived from the roll-up summary (Step 2)**, not a paste-up
  of the per-ticket descriptions. The roll-up reflects the actual
  after-applied state.
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
  fine.
- **Group entries strictly by the ticket's `changeImpact`**, under
  these four headers in this order: **Non-compatible** →
  **Compatible substantive** → **Non-substantive** → **Unclassified**.
  Defer entirely to the ticket's own classification — do **not**
  re-derive substantive vs non-substantive. A ticket with an
  empty/unset `changeImpact` goes under **Unclassified** (last);
  **never** fold an unset ticket into Non-substantive. Omit empty
  headers. Render any `changeCategory` as a small inline
  `<span class="tag">…</span>` next to the entry.
- Skip pure editorial churn (typo fixes, link normalization,
  whitespace) — those do not deserve a ballot bullet. Bundle them
  under a final sentence ("editorial cleanup throughout") only if
  they are substantial enough to warrant mentioning.
- Be specific: name the section, the conformance-language change,
  the added / removed paragraph, the new diagram. Avoid generic
  phrasing.

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
     "proposedBallotNoteHtml": "<blockquote class='ballot-note' …>…</blockquote>",
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
| `proposedBallotNoteHtml` | The drafted ballot note from Step 3 (HTML for HL7/fhir; the IG's convention for IG pages). |
| `rollupSummaryMarkdown` | The "Roll-up Summary" section body, as Markdown. |
| `notesForReviewerMarkdown` | The "Notes for Reviewer" section body, as Markdown. |
| `sourceFilesNote` | Any source-file caveat worth surfacing (optional). |

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Page Ballot Note Draft: {page} ({owner}/{name})

| | |
|-|-|
| Repository | [{owner}/{name}](https://github.com/{owner}/{name}) ({repoCategory}) |
| Page | `{primary page source path}` |
| Resolution rule | {how the processor resolved the primary page source — e.g., "FhirCore convention", "IG Publisher convention"} |
| Window | [`{since-shortSha}`](https://github.com/{owner}/{name}/commit/{since-sha})..[`{head-shortSha}`](https://github.com/{owner}/{name}/commit/{head-sha}) |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Hydrated | BallotNotes processor unit `{slug}` @ `{hydratedAt}` |
| Generated | {ISO-8601 UTC timestamp} |

## Source Files

Files considered part of the `{page}` page for this run:

| Path | Role | Touched in window |
|------|------|-------------------|
| `{primary path}` | Page source (ballot note lives here) | yes/no |
| `{sibling path}` | {role — e.g., "Supplementary narrative", "Examples appendix", "Page image"} | yes/no |
| … | … | … |

## Current Ballot Note

{If a ballot note exists at HEAD, paste its full HTML verbatim inside
a fenced ```html block. Include the `<blockquote …>` wrapper. If
multiple notes exist, include each with a heading line giving its
`id`. If none, write "No existing ballot note." and state where the
proposed note will be inserted.}

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
- **Changes applied (scoped to this page):**
  {2–6 sentences describing what these commits actually changed in
  the page. Be specific: name the section, the paragraph, the
  conformance-language change, the added/removed example. If overlap
  with other tickets means the per-ticket diff is misleading on its
  own, say so and reference the roll-up.}

{Include a final "(unattributed)" subsection if there are commits
without ticket attribution; it lists the commits and what they
changed.}

## Roll-up Summary (after-applied state)

{Authoritative summary of what changed across the page in the window,
derived from the after-applied evidence (Step 2). Group by section
heading where possible:}

- **Section: `<h2 id="…">…</h2>`:**
  {bullets describing material narrative shifts in this section.}
- **Section: `<h2 id="…">…</h2>`:**
  {…}
- **Examples / code snippets:**
  {added / removed / changed snippets.}
- **Diagrams / images:**
  {added / removed / replaced figures.}
- **Cross-page links:**
  {notable redirected or removed links.}
- **Editorial cleanup:**
  {typo / whitespace / link-normalization churn, summarized in one
  bullet.}

## Proposed Ballot Note

{The draft ballot note, ready to drop into the page. Preserve the
existing `id` if revising; otherwise pick the next free `bn<N>`.
Match the page's authoring format (HTML for HL7/fhir; the IG's
ballot-note convention for IG markdown pages — typically an HTML
`<blockquote class="stu-note">` or an IG-Publisher include). Use
Jira links of the form
`<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
(or the IG's preferred markdown link form) inline against the bullet
they support.}

```html
<blockquote class="ballot-note" data-augury-generated="true" id="bn{N}">
  <p><b>Note to Balloters:</b> {one-paragraph framing of the change
  scope since the previous ballot, derived from the roll-up
  summary.}</p>
  <ul>
    <li>{Substantive change} (<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>{, <a href="…">FHIR-YYYYY</a> if multiple})</li>
    <li>…</li>
  </ul>
</blockquote>
```

## Notes for Reviewer

{Free-form notes that did not fit elsewhere. Examples:
- Existing ballot-note bullets that were dropped because the change
  was reverted (cite the reverting commit and / or ticket).
- Commits in the window that touched files outside the page's scope
  (resource SDs, datatype XML, terminology). Add a one-line pointer
  to `notes-artifact` / `notes-datatype` for each.
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
- **Stay in your lane.** This skill owns *only* page sources (the
  primary page file plus its conventional siblings, as resolved by
  the processor). Resource / profile / datatype-SD changes belong to
  `notes-artifact`; in `HL7/fhir` the consolidated datatypes page
  belongs to `notes-datatype`.
- **Match the page's authoring format.** Output HTML for HTML pages
  (HL7/fhir), and the IG's ballot-note convention for IG markdown
  pages — do not silently emit HTML into a markdown page when the IG
  uses a different convention.
- **Editorial churn is not a ballot bullet.** Bundle pure typo /
  whitespace / link-normalization work into a single closing sentence
  if at all.
- **Use only the processor's hydrated evidence.** Do not re-run
  `git`, query Jira, or resolve page sources yourself — the processor
  owns that gathering. Do not fabricate ticket details, file paths,
  commit SHAs, or disposition text; if the evidence lacks something,
  say so in the report.
- **Be specific.** Name the section heading, the paragraph, the
  conformance-language delta, the added/removed example.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root.
