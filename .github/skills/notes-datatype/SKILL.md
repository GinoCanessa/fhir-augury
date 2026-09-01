---
name: notes-datatype
description: "Drafts the updated ballot note for the FHIR *datatypes* surface from evidence hydrated by the BallotNotes processor. USE FOR: ballot notes covering changes under `source/datatypes/` in `HL7/fhir`. Most datatypes render into the consolidated `source/datatypes.html` page; a subset (Dosage, MarketingStatus, Narrative, Reference, MetaDataTypes cluster, etc.) ship their own `source/<page>.html`. The processor folds the whole datatypes surface — every changed datatype SD, `source/datatypes.html`, and any touched own-pages — into one `datatypes` DataType unit (one slug); this skill drafts that consolidated note. Requires a processor unit slug (the noteId) and base URL (default http://localhost:5174). Reads the unit's hydrated evidence — datatype SDs, commits, applied Jira tickets, and current ballot-note HTML — then authors the after-applied roll-up and draft HTML note and writes it back to the processor. For per-resource/profile notes use `notes-artifact`; for other narrative pages use `notes-page`."
---

# Notes — Datatype Skill

Drafts the updated **ballot note** for the FHIR **datatypes surface**
in `HL7/fhir` from the evidence the **BallotNotes processor** has
already hydrated. The datatypes surface lives under
`source/datatypes/**` and renders mainly into the consolidated
**`source/datatypes.html`** page, while a subset of datatypes ship
their own narrative page in the source root (e.g., `source/dosage.html`,
`source/marketingstatus.html`, `source/narrative.html`,
`source/productshelflife.html`, `source/elementdefinition.html`,
`source/references.html` for `Reference`, and
`source/metadatatypes.html` for the MetaDataTypes cluster).

The processor applies the datatype-page map **server-side** and folds
the **entire** datatypes surface — every changed datatype
StructureDefinition, the consolidated `source/datatypes.html`, and any
touched per-datatype own-pages — into a **single `datatypes` DataType
unit** (one slug, with `name` = `datatypes`). This skill therefore
drafts **one** consolidated datatypes ballot note per invocation; its
hydrated evidence spans every datatype touched in the window plus the
page files those changes render into. The processor owns the
deterministic gathering — the commit-window walk, datatype grouping,
source-file resolution, ticket attribution, and current-note capture;
this skill reads that evidence, authors the after-applied roll-up and
the proposed ballot note, and writes the prose back to the processor.

The roll-up summary of changes **must reflect the after-applied
state** the processor hydrated (the net effect of the commit window),
not a stitch of per-ticket descriptions. Individual tickets frequently
overlap, expand, or revert each other — only the after-applied state
reflects reality.

This skill is the **datatypes** counterpart to `notes-artifact` and
`notes-page`. The processor's unit `type` decides which skill runs:
`DataType` → this skill; `Artifact` (a resource / profile) →
`notes-artifact`; `Page` (a non-datatype narrative page) →
`notes-page`. If the unit you fetched is not a `DataType`, stop and
route to the matching skill. The three skills share the same report
layout and ballot-note authoring conventions; only the file scope
differs.

## Why a dedicated skill

In `HL7/fhir`, every primitive and many complex datatypes are authored
as one or more files under `source/datatypes/` (one StructureDefinition
per datatype, plus shared narrative, examples, diagrams, and
terminology). Most of those source files render into a *single* ballot
page — `source/datatypes.html` — with anchor sub-sections per datatype.
The ballot note for the consolidated page is therefore a **page-level
note that spans many StructureDefinitions**. Treating each datatype as
an independent `notes-artifact` unit would fragment the note; treating
the page via `notes-page` would miss the per-datatype StructureDefinition
changes. This skill bridges the two: per-datatype roll-ups feeding a
single page-level ballot note.

A subset of datatypes ship their own narrative page in the source root
(e.g., `Dosage`, `MarketingStatus`, `Narrative`, `ProductShelfLife`,
`ElementDefinition`, `Reference` → `references.html`, and the
MetaDataTypes cluster — `ContactDetail`, `DataRequirement`,
`Expression`, `ParameterDefinition`, `RelatedArtifact`,
`TriggerDefinition`, `UsageContext`, `Contributor` →
`metadatatypes.html`). The processor's datatype-page map exists so those
own-pages are **folded into the single `datatypes` unit** rather than
mis-dispatched as standalone `notes-page` units (which would lose the
SD-side evidence under `source/datatypes/<dt>.xml`). The one datatypes
unit you receive therefore already carries the evidence for the
consolidated page **and** any touched own-pages; the note you draft
covers the whole datatypes surface.

## Datatype-page map

The processor routes each datatype bucket to its target ballot-note
page using this map (applied **server-side** — this skill does not run
it, but understanding it explains which own-pages fold into the
`datatypes` unit and how the consolidated note's evidence is grouped):

- **Default rule** — `<datatype>` (lowercase) → candidate stem
  `<datatype-lowercase>`. If `source/<stem>.html` exists, the datatype
  is **own-page**; otherwise it falls back to `source/datatypes.html`.
- **Explicit overrides** for known stem mismatches:
  - `Reference` → `references` (note the trailing `s`).
  - The **MetaDataTypes cluster** — `ContactDetail`, `DataRequirement`,
    `Expression`, `ParameterDefinition`, `RelatedArtifact`,
    `TriggerDefinition`, `UsageContext`, `Contributor` — all →
    `metadatatypes` (one shared own-page; folded into the `datatypes`
    unit, its changes summarized in the consolidated note).
- **Page-level / cross-cutting buckets** (changelog, shared diagrams,
  cross-cutting terminology, "Other / unassigned") **always** belong to
  `source/datatypes.html`.

If the FHIR repo grows a new own-page datatype whose stem does not
match its lowercase name, the override belongs in the processor's
datatype-page map (it owns the routing); this section documents that
behaviour.

## Data Access

This skill reads its evidence from — and writes its authored prose
back to — the **BallotNotes processor** over HTTP (default base URL
`http://localhost:5174`). The processor **owns all deterministic
gathering**: it has already walked the commit window, routed each
datatype to its target page, resolved the page's source files,
attributed commits to Jira tickets, and captured the current
ballot-note HTML. This skill never runs `git`, never queries Jira
directly, and never resolves source files or target pages itself — it
consumes the hydrated unit and authors prose.

Two endpoints are used, in this order:

1. **Read the hydrated unit** (GET-only):

   ```
   GET {processorBaseUrl}/api/v1/ballot-notes/{slug}
   ```

   Returns the unit's full hydrated evidence (the datatype SDs routed
   to this page, attributed commits, tickets, counters, the current
   ballot-note HTML) plus any prose already authored. `404` means the
   slug was never hydrated — stop and report it; the repo window must
   be hydrated first (via the `orchestrate-notes` skill or the
   processor's `hydrate` endpoint).

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

- **Slug** *(required)* — the processor `noteId` for the datatypes
  unit (a single consolidated unit with `name` = `datatypes`, e.g.,
  `hl7-fhir-datatype-datatypes`). The orchestrator
  (`orchestrate-notes`) passes it from the enumerated unit list; for
  ad-hoc runs, discover it via
  `GET {processorBaseUrl}/api/v1/ballot-notes?repo=HL7/fhir&type=DataType`.
- **Processor base URL** *(optional, default `http://localhost:5174`)*
  — the BallotNotes processor's base URL.
- **Output file** *(optional)* — full path where the human-readable
  markdown report should be written. The orchestrator passes a
  deterministic path; for ad-hoc invocations the agent may default to
  `<working-dir>/HL7_fhir_datatype_<page>.md` and report the path
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
  datatype-to-page routing and source-file resolution.

## Workflow

### Step 1: Read the hydrated unit

`GET {processorBaseUrl}/api/v1/ballot-notes/{slug}` and parse the
detail object. The processor is the **owner** of this evidence; use it
as-is — do **not** re-derive any of it:

- **Identity / window** — `type` (must be `DataType`; if it is
  `Artifact` or `Page`, stop and route to `notes-artifact` /
  `notes-page`), `name` (the consolidated datatypes unit is named
  `datatypes`), `repoOwner`, `repoName`,
  `repoCategory`, `sinceSha` / `sinceShortSha`, `headSha` /
  `headShortSha`, `windowLabel` (a human-readable window name such as
  `R6 Ballot 4`, when supplied), `workGroup` / `workGroupCode`,
  `hydratedAt`.
- **Counters** — `commitsInWindow`, `ticketsAttributed`, and the
  processor's first-pass `needsNote` (you refine it in Step 4).
- **`sourceFiles[]`** — `{path, role, touchedInWindow}` for every file
  the processor routed to this page: the per-datatype
  StructureDefinitions (`source/datatypes/<dt>.xml`), their examples /
  terminology siblings / spreadsheets / diagrams, any shared
  page-level files, **and** the page files where the notes render
  (`source/datatypes.html` plus any touched own-pages such as
  `source/dosage.html` or `source/metadatatypes.html`). This spans
  every datatype touched in the window.
- **`commits[]`** — `{sha, shortSha, authorName, authorDate, subject,
  webUrl, ticketKeys[]}` for every window commit that touched this
  page's file set. Commits with an empty `ticketKeys` are the
  "unattributed" group.
- **`tickets[]`** — `{ticketKey, title, resolution, workGroup,
  specification, url, commitCount, changeImpact, changeCategory,
  relatedTicketKeys}` for every attributed ticket. `changeImpact` is
  the ticket's own Jira change-impact classification (`Non-compatible`,
  `Compatible, substantive`, `Non-substantive`, or empty/unset);
  `changeCategory` is its change-category label; `relatedTicketKeys[]`
  are the related/linked Jira tickets needed to interpret the change.
- **`structuralChanges[]`** — `{sourcePath, elementPath, changeKind,
  detail, ticketKeys[]}` for each structural StructureDefinition delta
  detected over the window (`changeKind` ∈ `Added`/`Removed`/
  `Cardinality`/`Type`/`Modifier`/`Summary`/`MustSupport`).
- **`extensionRefs[]`** — `{extensionUrl, extensionName,
  replacementCoreElement, rationale}` for referenced extensions the CI
  build maps to a replacing core element (already filtered to those with
  a core counterpart).
- **`currentBallotNoteHtml`** — the verbatim ballot note(s) currently
  on this page at HEAD (empty if none).
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

Bucket the `sourceFiles[]` by datatype (using each SD's stem, e.g.,
`source/datatypes/quantity.xml` → `Quantity`) so the per-datatype
roll-up in Step 2 and the report's per-datatype tables have a stable
grouping. Page-level / cross-cutting files (changelog, shared
diagrams, cross-cutting terminology, the page file) form their own
buckets.

If `sourceFiles[]` and `commits[]` are both empty, this page had no
changes in the window — write a short "No changes to datatypes page in
window" report, PUT `needsNote:"no"` with empty prose (Step 4), and
exit.

### Step 2: Curate the after-applied roll-up (per datatype)

This is the skill's core value-add. Working **only** from the hydrated
evidence (never re-running `git` or re-querying Jira):

- Author a **per-datatype roll-up** for each datatype bucket on this
  page, narrated by file role:
  - **StructureDefinition** — element additions / removals /
    cardinality / type / binding / constraint changes in the
    `<differential>` (treat `<snapshot>` as derived — note that
    snapshot regeneration is required, do not enumerate snapshot
    edits).
  - **Examples** — added / removed / changed examples and updates
    forced by element changes.
  - **Terminology siblings** — added / removed / changed
    `valueset-*` / `codesystem-*` entries; flag any that may belong in
    UTG.
  - **Diagrams / spreadsheets** — note presence of changes; do not
    enumerate spreadsheet edits if the SD reflects them.
- Author a **per-page roll-up** that reconciles the per-datatype
  roll-ups into the page's whole-window change story. It must reflect
  the **after-applied state** (the net effect of the whole window),
  not a stitch of per-ticket descriptions. Call out any change that
  crosses datatypes within the page (e.g., a shared element-type
  rename).
- Author the **per-ticket "Changes Applied"** narrative for each entry
  in `tickets[]`, using its `title`, `resolution`, `specification`,
  `workGroup`, and the subjects of the `commits[]` whose `ticketKeys`
  include that ticket; record which datatype(s) each ticket touched.
  Be honest about overlap: if two tickets touch the same element of
  the same datatype, say so and defer the authoritative summary to the
  per-datatype roll-up.
- Reconcile against `currentBallotNoteHtml`: note which existing
  bullets are still accurate in the after-applied state (carry
  forward) and which were reverted or superseded (drop and explain in
  "Notes for Reviewer").

### Step 3: Draft the proposed ballot note

Produce **one** HTML ballot-note draft for this unit's target page
(`source/<name>.html`). It MUST:

- **Open with the change-window sentence.** When `windowLabel` is
  present in the GET payload, begin the note with
  "Changes since {windowLabel}" (e.g. "Changes since R6 Ballot 4");
  otherwise fall back to the `sinceShortSha..headShortSha` window.
- Be authored as **HTML**, ready to paste into the page inside a
  **single** tool-generated wrapper:
  `<blockquote class="ballot-note" data-augury-generated="true" id="…">…</blockquote>`.
  The `data-augury-generated="true"` marker is **required** so the
  processor replaces only this tool-generated block next run. Preserve
  any existing `id` when revising an existing note on this page; pick the
  next free `bn<N>` id when adding a new note.
- **Produce exactly one consolidated note**, never two. A regenerated
  note replaces only the prior **tool-generated** block. When
  `preservedHandAuthoredHtml` is non-empty, carry those hand-authored
  notes forward **verbatim** — never delete or rewrite them.
  `currentNoteIsAuguryGenerated` tells you whether the current note is
  tool-generated (replace) or hand-authored (preserve).
- Be **derived from the per-page roll-up (Step 2)**, reconciled
  against the per-datatype roll-ups for the buckets on this page. Do
  **not** stitch together per-ticket descriptions.
- **Honour the existing ballot note** (`currentBallotNoteHtml`). Carry
  forward bullets that are still accurate in the after-applied state;
  drop and explain bullets that have been reverted or superseded.
- Cite each underlying ticket with a Jira link, placed at the **end of
  the line** it supports as a bracketed list:
  `[<a href="https://jira.hl7.org/browse/FHIR-12345">FHIR-12345</a>, <a href="https://jira.hl7.org/browse/FHIR-23456">FHIR-23456</a>]`.
  Put the **change text first**, then the bracketed `[FHIR-…]` list at
  end-of-line; multi-ticket changes list every contributing ticket.
- **Emit well-formed HTML only — never raw markdown** in
  `proposedBallotNoteHtml` (it is pasted verbatim into the page). Use
  HTML elements (`<ul>`, `<li>`, `<p>`, `<b>`, `<a href>`, `<code>`),
  not markdown syntax.
- **Every called-out change must carry at least one Jira key.** If a
  change has no attributable ticket, surface it under a final
  **Unattributed (needs Jira)** heading rather than dropping it; the SPA
  flags entries lacking attribution.
- **Make cross-ticket relationships explicit.** When a ticket's
  `relatedTicketKeys[]` are needed to interpret a change, add an inline
  "(see also <a …>FHIR-…</a>)" after that line.
- **Flag structural changes inline.** For a line matching a
  `structuralChanges[]` entry, attach
  `<span class="structural-badge" title="{changeKind}: {detail}" aria-label="structural change: {changeKind}">structural</span>`
  after the change text. Only badge the deltas the processor detected;
  the SPA also renders a separate "Structural changes" evidence panel.
- **Cross-reference replaced extensions.** For each `extensionRefs[]`
  entry, add "extension {extensionName} → replaced by core element
  <code>{replacementCoreElement}</code> ({rationale})". Do not surface
  extension-to-extension churn with no core counterpart.
- **Group entries strictly by the ticket's `changeImpact`**, under
  these four headers in this order: **Non-compatible** →
  **Compatible substantive** → **Non-substantive** → **Unclassified**.
  Defer entirely to the ticket's own classification — do **not**
  re-derive substantive vs non-substantive. A ticket with an
  empty/unset `changeImpact` goes under **Unclassified** (last);
  **never** fold an unset ticket into Non-substantive. Omit empty
  headers. Render any `changeCategory` as a small inline
  `<span class="tag">…</span>` next to the entry.
- Avoid restating mechanics already obvious from the SD ("renamed
  `Quantity.foo` to `Quantity.bar`"). Focus on intent, scope, and
  balloter-relevant impact.
- Skip pure editorial churn (typo fixes, link normalisation,
  whitespace) unless substantial enough to warrant a closing
  sentence.

Bullet shape depends on which page this unit covers:

- **`source/datatypes.html`** (consolidated) — a short framing
  paragraph followed by `<ul>` with one bullet per datatype routed to
  this page (or per closely related cluster, e.g.,
  `Quantity` / `SimpleQuantity`). Page-level / cross-cutting buckets
  get their own bullets. The reader navigates `datatypes.html` by
  datatype anchor, so mirror that mental model.
- **Single-datatype own-page** (e.g., `source/dosage.html`) — a short
  framing paragraph followed by `<ul>` whose bullets are organised by
  **change topic** (SD differential change, page narrative change,
  terminology change, examples) rather than by datatype name, since
  the page covers a single datatype.
- **`source/metadatatypes.html`** — a page-scoped note covering every
  MetaDataTypes-cluster datatype routed here. Group bullets by datatype
  within the cluster (one bullet per datatype touched in the window),
  then a final bullet for any cross-cutting / shared changes.

### Step 4: Recommend, write the report, and persist back to the processor

1. **Decide `needsNote`** — `"yes"` if the after-applied changes on
   this page warrant a ballot note, `"no"` if the window's net change
   is immaterial / purely editorial, `"unknown"` if you cannot tell.
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
   the stored prose), so a re-run is safe. Each DataType unit (target
   page) is its own slug, so the orchestrator dispatches and persists
   each page independently.

---

## Persisting back to the processor

The PUT in Step 4 carries **only** the authored prose and the
needs-note decision for this page; every identity / window / counter /
source-file / commit / ticket field is read-only evidence the
processor already holds. The PUT body maps onto the report sections as:

| PUT field | Source in this skill |
|-----------|----------------------|
| `needsNote` | The Step 4 recommendation (`yes` / `no` / `unknown`). |
| `proposedBallotNoteHtml` | The drafted `<blockquote class="ballot-note" data-augury-generated="true">` for this page from Step 3 (single consolidated note). |
| `rollupSummaryMarkdown` | The per-page "Roll-up Summary" section body, as Markdown. |
| `notesForReviewerMarkdown` | The "Notes for Reviewer" section body, as Markdown. |
| `sourceFilesNote` | Any source-file caveat worth surfacing (optional). |

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Datatypes Ballot Note Draft: {target page} (HL7/fhir)

| | |
|-|-|
| Repository | [HL7/fhir](https://github.com/HL7/fhir) ({repoCategory}) |
| Target page | `source/{page}.html` |
| Source root | `source/datatypes/` |
| Window | [`{since-shortSha}`](https://github.com/HL7/fhir/commit/{since-sha})..[`{head-shortSha}`](https://github.com/HL7/fhir/commit/{head-sha}) |
| Datatypes on page | {D} |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Hydrated | BallotNotes processor unit `{slug}` @ `{hydratedAt}` |
| Generated | {ISO-8601 UTC timestamp} |

## Datatypes In This Window

{Every datatype touched in the window — those whose SDs changed under
`source/datatypes/` — plus any page-level / cross-cutting buckets.}

| Datatype | Files touched | Tickets |
|----------|---------------|---------|
| `Quantity` | 3 | [FHIR-XXXXX](…), [FHIR-YYYYY](…) |
| `Period` | 1 | [FHIR-ZZZZZ](…) |
| (Cross-cutting terminology) | 2 | [FHIR-CCCCC](…) |
| (Page-level) | 1 (`source/{page}.html`) | — |
| … | … | … |

## Source Files

Files the processor routed to this page (`sourceFiles[]`), grouped by
datatype bucket:

### `Quantity`

| Path | Role | Touched in window |
|------|------|-------------------|
| `source/datatypes/quantity.xml` | StructureDefinition | yes |
| `source/datatypes/quantity-example.xml` | Example | yes |
| … | … | … |

### (Cross-cutting terminology)

| Path | Role | Touched in window |
|------|------|-------------------|
| `source/datatypes/valueset-…xml` | ValueSet | yes |
| `source/datatypes/codesystem-…xml` | CodeSystem | yes |

### (Page-level)

| Path | Role | Touched in window |
|------|------|-------------------|
| `source/{page}.html` | Datatypes page (ballot note lives here) | yes/no |
| `source/datatypes/_changelog.txt` | Changelog | yes/no |

## Current Ballot Note

{The page's existing ballot-note HTML at HEAD (`currentBallotNoteHtml`),
verbatim inside a fenced ```html block (including the `<blockquote …>`
wrapper), preserving each note's `id`. If the page has multiple notes,
include each with a heading line giving its `id`. If none, write "No
existing ballot note." and state where the proposed note will be
inserted (top of the body, after the page title / intro paragraph).}

```html
<blockquote class="ballot-note" id="bn1">
  …
</blockquote>
```

## Tickets Applied in Window

| Ticket | Title | Datatypes | Commits |
|--------|-------|-----------|---------|
| [{KEY}](https://jira.hl7.org/browse/{KEY}) | {ticket title} | `Quantity`, `Period` | [`{shortSha}`]({commitUrl}), [`{shortSha}`]({commitUrl}) |
| [{KEY}](https://jira.hl7.org/browse/{KEY}) | {ticket title} | `Period` | [`{shortSha}`]({commitUrl}) |
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
- **Disposition summary:** {2–4 sentence neutral summary of what the
  disposition asked for, authored from the ticket's title, resolution,
  and the subjects of the commits that applied it. The hydrated
  evidence does not carry the verbatim applied-vote comment; do not
  invent one.}
- **Commits applying this ticket:**
  - [`{shortSha}`]({commitUrl}) — {commit subject} ({authorDate})
  - …
- **Changes applied (scoped to this page's datatypes):**
  {2–6 sentences describing what these commits actually changed.
  Be specific: name the datatype, the element, the field, the nature
  of the change. If overlap with other tickets means the per-ticket
  view is misleading on its own, say so and reference the per-datatype
  roll-up.}

{Include a final "(unattributed)" subsection if there are commits
without ticket attribution; it lists the commits, their datatype
buckets, and what they changed.}

## Per-Datatype Roll-up (after-applied state)

{One subsection per datatype with at least one touched file on this
page, in alphabetical order (page-level / cross-cutting buckets last).}

### `Quantity`

- **StructureDefinition (`source/datatypes/quantity.xml`):**
  {bullets describing element-level changes in the differential —
  additions, removals, cardinality, type, binding, constraints.
  Note whether snapshot regeneration is required.}
- **Examples:**
  {added / removed / changed examples.}
- **Terminology:**
  {sibling valueset/codesystem changes, or "None".}

### (Cross-cutting terminology)

{Terminology files used by multiple datatypes; list which datatypes
they bind and what changed.}

### (Page-level)

{Changes to `source/{page}.html` itself (intro / framing changes,
section reorganisations) and to shared narrative / diagrams under
`source/datatypes/`.}

## Roll-up Summary (after-applied state)

{The authoritative whole-page change story, derived from the
after-applied evidence (Step 2), reconciling the per-datatype roll-ups
above. Call out any change that crosses datatypes within the page
(e.g., a shared element-type rename).}

## Proposed Ballot Note (HTML)

{The single draft ballot note for this page, ready to drop into
`source/{page}.html`. Preserve the existing `id` if revising; otherwise
pick the next free `bn<N>`. Bullets are grouped by datatype for
`datatypes.html` / `metadatatypes.html`, or by change topic for a
single-datatype own-page. Use Jira links of the form
`<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
inline against the bullet they support.}

```html
<blockquote class="ballot-note" data-augury-generated="true" id="bn{N}">
  <p><b>Note to Balloters:</b> {one-paragraph framing of the change
  scope on this page since the previous ballot, derived from the
  roll-up summary.}</p>
  <p><b>Non-compatible</b></p>
  <ul>
    <li><b>Quantity:</b> {change} <span class="tag">{changeCategory}</span> [<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>]</li>
  </ul>
  <p><b>Compatible substantive</b></p>
  <ul>
    <li><b>Period:</b> {change} [<a href="https://jira.hl7.org/browse/FHIR-YYYYY">FHIR-YYYYY</a>]</li>
  </ul>
  <p><b>Non-substantive</b></p>
  <ul>
    <li><b>Range:</b> {change} [<a href="https://jira.hl7.org/browse/FHIR-ZZZZZ">FHIR-ZZZZZ</a>]</li>
  </ul>
  <p><b>Unclassified</b></p>
  <ul>
    <li><b>Ratio:</b> {change from a ticket with no changeImpact set} [<a href="https://jira.hl7.org/browse/FHIR-WWWWW">FHIR-WWWWW</a>]</li>
  </ul>
</blockquote>
```

Omit any header whose bucket has no entries; keep the four in the order
shown, with **Unclassified** always last.

## Notes for Reviewer

{Free-form notes that did not fit elsewhere. Examples:
- Existing ballot-note bullets that were dropped because the change
  was reverted (cite the reverting commit and / or ticket).
- Commits in the window that touched files outside this page's scope
  (resource SDs, other narrative pages, terminology in other folders).
  Add a one-line pointer to `notes-artifact` / `notes-page` for each.
- Anything the processor flagged in `sourceFilesNote`, or evidence
  that looked incomplete (e.g., a commit with no attributed ticket).

If none: "No additional notes."}
````

## Important Rules

- **Per-datatype roll-up first, per-page reconciliation second,
  ticket bullets last.** This page's proposed ballot note must reflect
  the after-applied state. Per-ticket descriptions are supporting
  evidence, not the source of truth.
- **One consolidated ballot note.** The processor emits a single
  `datatypes` DataType unit and folds every changed datatype via the
  datatype-page map (default lowercase stem; explicit overrides for
  `Reference → references` and the MetaDataTypes cluster →
  `metadatatypes`). This skill drafts the single note for the
  datatypes surface and PUTs it back.
- **Group `datatypes.html` ballot bullets by datatype.** The reader of
  `datatypes.html` navigates by datatype anchor; the ballot note there
  should mirror that mental model. When the note also covers own-page
  datatypes (e.g., `dosage.html`), group their bullets by change topic
  within their own sub-section.
- **Honour the page's existing ballot note.** Carry forward bullets
  that are still accurate in the after-applied state; drop and explain
  bullets that have been reverted or superseded.
- **Cite tickets inline in the proposed note.** Every bullet should
  point at the ticket(s) responsible. Use the Jira issue URL form
  shown above.
- **Stay in your lane.** This skill owns the datatypes surface
  (`source/datatypes/**`, `source/datatypes.html`, and the per-datatype
  own-pages the processor routes here). Resource / profile changes
  belong to `notes-artifact`; other narrative pages belong to
  `notes-page`. The processor's routing guarantees own-page datatypes
  are not double-dispatched as `notes-page` units.
- **Treat `<snapshot>` as derived.** Narrate `<differential>` changes
  in each SD; mention only that snapshot regeneration is required, do
  not enumerate snapshot edits.
- **Spreadsheets are legacy.** If a `<name>-spreadsheet.xml` is touched
  but the SD is not, flag it; otherwise rely on the SD as authoritative
  and do not enumerate spreadsheet edits.
- **Use only the processor's hydrated evidence.** Do not re-run `git`,
  query Jira, or resolve source files / target pages yourself — the
  processor owns that gathering. Do not fabricate ticket details, file
  paths, commit SHAs, or disposition text; if the evidence lacks
  something, say so in the report.
- **Be specific.** Name the datatype, the element, the field, the old
  vs. new value where relevant.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root.
