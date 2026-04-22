---
name: notes-page
description: "Drafts an updated ballot note for a single FHIR specification *page* based on changes made since a specified commit. USE FOR: per-page ballot notes, ballot-comment drafting, change roll-ups for narrative/spec pages in `HL7/fhir` (`source/<page>.html`). Requires a GitHub repo (e.g., HL7/fhir), a since-commit SHA, and a page name (e.g., `security`, `extensibility`, `terminologies`). Walks the page's single `.html` file between the since-commit and HEAD, attributes commits to the FHIR Jira tickets they applied, summarises what actually changed in the after-applied state, and writes a markdown report containing a draft HTML ballot note suitable for inlining at the top of the page. For per-resource/profile ballot notes, use `notes-artifact` instead. For the consolidated datatypes page, use `notes-datatype`."
---

# Notes — Page Skill

Drafts an updated **ballot note** for a single FHIR specification page
(a narrative `.html` file directly under `source/` in `HL7/fhir`, such
as `security.html`, `extensibility.html`, `terminologies.html`,
`narrative.html`, `references.html`, …) by analyzing the changes that
have landed in that page's source file since a caller-supplied commit.

The output is a markdown review report containing the proposed HTML
ballot note plus the supporting evidence (per-commit / per-ticket
breakdown, rolled-up summary, current ballot note for context).

The roll-up summary of changes **must be derived from the
after-applied diff** (since-commit → HEAD), not by stitching together
per-ticket descriptions. Individual tickets frequently overlap, expand,
or revert each other — only the after-applied state reflects reality.

This skill is the **page** counterpart to `notes-artifact`. The two
skills share the same workflow shape, ticket-attribution rules, report
layout, and ballot-note authoring conventions; only the file scope and
artifact-resolution rules differ. When in doubt about a generic step,
consult `notes-artifact`.

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

- **Repo** *(required)* — `owner/name` (e.g., `HL7/fhir`,
  `HL7/fhir-us-core`, `HL7/fhir-extensions`). Any FHIR repo category
  is accepted; the page-resolution rules in Step 2 select the
  appropriate layout based on the briefing's category. If the
  briefing is missing and the category cannot be inferred, stop and
  ask the user (or have them run `repo-analysis` first).
- **Since-commit** *(required)* — full or short SHA. The roll-up window
  is `since-commit..HEAD` of the cached clone (fast-forward range; if
  HEAD is not a descendant of since-commit, fall back to the symmetric
  difference and note the deviation in the report).
- **Page** *(required)* — the page identifier as it appears in the
  repo's authoring layout. Accepted forms (case-insensitive):
  - bare stem, e.g., `security`, `extensibility`, `us-core-patient`;
  - filename with extension, e.g., `security.html`,
    `us-core-patient.md`;
  - repo-relative path, e.g., `source/security.html`,
    `input/pagecontent/us-core-patient.md`.

  The skill normalises the input and resolves it to exactly one
  primary page source file (plus any conventional sibling files for
  that repo category) per Step 2. Do **not** include
  artifact-introduction pages (resource / profile intros) — those
  belong to `notes-artifact`. In `HL7/fhir`, do not include the
  consolidated datatypes page — that belongs to `notes-datatype`.
- **Output file** *(required)* — full path where the markdown report
  should be written. The orchestrator passes a deterministic path; for
  ad-hoc invocations the agent may default to
  `<working-dir>/<owner>_<name>_page_<page>.md` and report the path
  back.
- **Working directory** *(optional)* — directory for transient files
  (intermediate diffs, commit lists, ticket dumps). When supplied,
  **all transient files must be written under this directory**. Create
  it with `New-Item -ItemType Directory -Force` (PowerShell),
  `mkdir -p` (bash), or your file-system tool if it does not exist.

## Prerequisites

- The GitHub source clone cache for the repo must be populated and
  current enough that the since-commit is reachable from the cached
  clone HEAD. The clone path is
  `cache/github/repos/<owner>_<name>/clone/`. If the since-commit is
  missing, ask the user to refresh the clone (or fall back to
  fetching the commit via `gh api` and noting the deviation in the
  report).
- A current per-repo briefing under
  `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` must
  exist. It supplies the **repo category** that drives page
  resolution in Step 2 and any non-conventional page locations. For
  `HL7/fhir` the convention (`source/<page>.html`) is sufficient and
  the briefing is consulted only for cross-repo context. For IG /
  extension-pack / incubator repos the briefing's page index (and
  `sushi-config.yaml` `pages:` block, when present) is the
  authoritative source. If the briefing is missing or stale (per the
  staleness rules in the `repo-analysis` skill), warn the user but
  proceed; record the staleness in the report's "Notes for reviewer"
  section.
- `git` must be available on `PATH`. `gh` is required only if the
  cache clone cannot resolve the since-commit or a commit URL needs to
  be confirmed against `github.com`.

## Workflow

Run independent calls in parallel where possible.

### Step 1: Verify services and resolve the page

1. Health-check via `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"services","action":"health"}'
   ```

2. Read the briefing and metadata (best-effort — do not block on
   staleness, but record it):
   - `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md`
   - `cache/github/repos/<owner>_<name>/repo-analysis/meta.json`

   Note the repo **category** (e.g., `FhirCore`, `FhirIg`,
   `FhirExtensionsPack`, `Incubator`, `Utg`, `JiraSpecArtifacts`).
   It selects the page-resolution rule in Step 2.

3. Confirm the cache clone and resolve HEAD:

   ```powershell
   $clone = "cache/github/repos/<owner>_<name>/clone"
   git -C $clone rev-parse HEAD
   git -C $clone cat-file -e <since-commit>^{commit}
   ```

   If `cat-file -e` fails, the since-commit isn't in the cache clone —
   stop and ask the user to refresh the clone, or fall back to
   `gh api /repos/<owner>/<name>/commits/<since-commit>` and note the
   limitation in the report.

4. Resolve the page → primary source file (Step 2 details the
   per-category rules) and verify it exists at HEAD:

   ```powershell
   git -C $clone cat-file -e HEAD:<resolved-primary-path>
   ```

   If the file is missing at HEAD but present at the since-commit, the
   page was deleted in the window — record the deletion, draft a
   "page removed" note pointing at the redirect / replacement (if the
   commit messages indicate one), and exit after Step 8.

   If the file is missing in both, stop with an error: there is no
   such page.

### Step 2: Resolve page → source files

Page resolution is **per repo category**. Pick the matching block;
fall back to "Other categories" when the primary block does not yield
an existing file.

#### FhirCore (`HL7/fhir`)

The page source is a single `.html` file under `source/`:

- `source/<page>.html` — primary page source (ballot note lives here).

Optional sibling files that *may* belong to the page (include only
when present at HEAD; list each role explicitly in the report):

- `source/<page>-notes.html` — supplementary narrative, if present.
- `source/<page>-examples.html` — examples appendix, if present.

In `HL7/fhir`, page sources **do not** own `structuredefinition-*`,
`bundle-*`, `list-*-operations`, `valueset-*`, or `codesystem-*`
sibling files. If a commit in the window touches such a file, that
change belongs to a resource/profile/datatype artifact and must be
left for `notes-artifact` or `notes-datatype` — note it in "Notes
for reviewer".

#### FhirIg / FhirExtensionsPack / Incubator

These repos follow the FHIR IG Publisher layout. The page source is
typically:

- `input/pagecontent/<page>.md` — primary page source (markdown).
- `input/pagecontent/<page>.xml` — fallback when the page is XHTML
  rather than markdown.

Optional sibling files that *may* belong to the page (include only
when present at HEAD):

- `input/pagecontent/<page>-intro.md` / `-notes.md` / `-examples.md`
  — companion fragments, if present (the IG Publisher concatenates
  these into the rendered page).
- `input/images/<page>*.{png,svg}` — images referenced by the page.
- `input/includes/<page>*.{xml,xhtml}` — included fragments, if
  present.

Cross-check the briefing's page index and any
`sushi-config.yaml` `pages:` block for non-conventional locations
(some IGs author pages under `input/pages/` or use
`input/pagecontent/<group>/<page>.md`). The briefing is
authoritative when conventions diverge.

In IG repos, page sources **do not** own files under `input/fsh/`,
`input/resources/`, `fsh-generated/`, or `input/examples/` — those
belong to `notes-artifact`. Note any commits that touch them in
"Notes for reviewer".

#### Utg

Page-style narrative is rare in `HL7/UTG`. If a page is requested,
defer to the briefing's page index. Common candidates: top-level
`README.md`, narrative under `input/sourceOfTruth/` describing a
canonical. If no clear match exists, stop and ask the user to point
at the file.

#### JiraSpecArtifacts / other categories

This skill does not pre-canonicalise paths for these categories. Use
the briefing's page index (or any explicit hint the user supplies)
to identify the primary page source and any siblings. Record the
resolution in the report under "Source Files" so reviewers can
verify it. If the briefing has no relevant entry and the user did
not pass a clear path, stop and ask.

#### Materialisation

Once the primary page source and sibling list are resolved,
materialise the file list as both:

- **`workingFileList`** — paths relative to the clone root, used by
  `git` calls in Steps 3–5.
- **`displayFileList`** — paths shown in the report, with a one-line
  role for each (and the resolution rule that produced it: "FhirCore
  convention", "IG Publisher convention", "from briefing page
  index", "user-supplied", etc.).

### Step 3: Enumerate commits in the window

For the resolved file list, enumerate commits that touched any of the
files between `since-commit` and `HEAD`. Use `git log` against the
cache clone:

```bash
git -C cache/github/repos/<owner>_<name>/clone log \
    --no-merges \
    --pretty=format:'%H%x09%an%x09%aI%x09%s' \
    <since-commit>..HEAD \
    -- <space-separated workingFileList>
```

For each commit row, capture:
- `sha` (full)
- `shortSha` (`git rev-parse --short=12 <sha>`)
- `authorName`
- `authorDate` (ISO-8601)
- `subject` (first line of the commit message)
- `body` (full message via `git show -s --format=%B <sha>`)
- `webUrl` — `https://github.com/<owner>/<name>/commit/<sha>`

If the window is empty (no commits touched the page file set), write
a short report noting "No changes to page in window" and exit.

### Step 4: Attribute commits to Jira tickets

Identical to `notes-artifact` Step 4. For each commit, extract
candidate Jira ticket keys (regex `(FHIR|UTG)-\d+`; extend to other
project keys per the briefing if the repo's tickets live in a
different Jira project) from the commit subject + body, and union
with any keys returned by
`cross-referenced` for the commit SHA:

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"<sha>","limit":50}'
```

Build two indexes in memory:

- `commitToTickets[sha] = [key, …]`
- `ticketToCommits[key] = [{sha, shortSha, subject, webUrl}, …]`

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

### Step 5: Compute diffs (per-ticket and rollup)

Two diff sets are required.

**5a. Roll-up diff (since-commit → HEAD).**

```bash
git -C <clone> diff <since-commit>..HEAD -- <workingFileList>
git -C <clone> diff --stat <since-commit>..HEAD -- <workingFileList>
```

Use this diff to write the **roll-up summary** of what changed in the
after-applied state (Step 6 / report). Pages are narrative content
(HTML in `HL7/fhir`, markdown in IG repos); group observations by
section heading where possible. Call out:

- New / removed / restructured headings (`<h1>`–`<h4>` for HTML pages;
  `#`–`####` for markdown pages).
- Material narrative shifts within a section: scope changes, boundary
  clarifications, normative-status notes, deprecations,
  added/removed examples or code snippets, conformance-language
  changes (`SHALL` / `SHOULD` / `MAY` deltas), changed cross-page
  links, and updated diagrams or images.
- Added / removed / changed ballot-note blocks. In HL7/fhir these are
  `<blockquote class="ballot-note">` blocks; in IG repos they are
  often inserted by IG-Publisher templating (look for
  `{% include ballot-note … %}`, `<blockquote class="stu-note">`, or
  the IG's own ballot-note convention recorded in the briefing).
- Editorial-only churn (typo fixes, link normalization, whitespace).
  Bucket these together — they should not drive ballot-note bullets.

**5b. Per-ticket diff.**

For each ticket with at least one commit in the window, compute the
union diff of that ticket's commits, scoped to the file list:

```bash
git -C <clone> show --stat --pretty=fuller <sha1> <sha2> ... -- <workingFileList>
```

Or walk each commit individually with `git show --first-parent` and
concatenate the per-file hunks. Use this to author the **per-ticket
"Changes Applied"** paragraph. Be honest about overlap: if two
tickets touch the same paragraph, say so and defer the authoritative
summary to the roll-up.

### Step 6: Read the current ballot note

Read the primary page source at HEAD and locate any existing ballot
note. The marker depends on the repo:

- **FhirCore (`HL7/fhir`)** — `<blockquote class="ballot-note" …>…</blockquote>`
  blocks inside `source/<page>.html`.
- **FhirIg / FhirExtensionsPack / Incubator** — markdown / XHTML
  blocks using the IG's ballot-note convention (commonly a
  `<blockquote class="stu-note">…</blockquote>` block, an
  IG-Publisher include such as `{% include ballot-note … %}`, or a
  fenced "Ballot Note" admonition). Use the briefing's "ballot-note
  convention" section if present; otherwise grep the page for both
  `class="ballot-note"` and `class="stu-note"` and ask the user if
  ambiguous.
- **Other categories** — fall back to grepping for `ballot-note` and
  `stu-note` markers; if none, record "No existing ballot note." and
  flag the convention question for the reviewer.

Extract any matched note's full inner content verbatim. If multiple
ballot notes exist (distinct `id`s), capture them all.

If no ballot note exists, record "No existing ballot note." and draft
a fresh one in Step 7. The conventional location for a page ballot
note is at the top of the body, immediately after the page title /
intro paragraph; record where you propose to insert it.

### Step 7: Draft the proposed ballot note

The proposed ballot note MUST:

- Be authored in the **format the page expects**:
  - HL7/fhir (HTML pages): an HTML
    `<blockquote class="ballot-note" id="…">…</blockquote>` wrapper.
  - IG / extension-pack / incubator (markdown pages): the IG's
    ballot-note convention (typically an HTML `<blockquote
    class="stu-note">` block embedded in the markdown, or the
    IG-Publisher include used elsewhere in the same IG — match the
    style already in use in the repo).
  - Other categories: match the style of the existing ballot note
    found in Step 6, or ask the reviewer to choose.

  Preserve any existing `id` attribute when revising an existing
  note; pick the next free `bn<N>` id when adding a new note.
- Be **derived from the roll-up summary (Step 5a)**, not a paste-up
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
- Skip pure editorial churn (typo fixes, link normalization,
  whitespace) — those do not deserve a ballot bullet. Bundle them
  under a final sentence ("editorial cleanup throughout") only if
  they are substantial enough to warrant mentioning.
- Be specific: name the section, the conformance-language change,
  the added / removed paragraph, the new diagram. Avoid generic
  phrasing.

### Step 8: Write the report

Compose the markdown report per the **Report Format** below and save
it to the output file path. Use the gathered data to write
substantive, specific content — no generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Page Ballot Note Draft: {page} ({owner}/{name})

| | |
|-|-|
| Repository | [{owner}/{name}](https://github.com/{owner}/{name}) ({category from briefing}) |
| Page | `{primary page source path}` |
| Resolution rule | {e.g., "FhirCore convention", "IG Publisher convention", "from briefing page index", "user-supplied"} |
| Window | [`{since-shortSha}`](https://github.com/{owner}/{name}/commit/{since-sha})..[`{head-shortSha}`](https://github.com/{owner}/{name}/commit/{head-sha}) |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Briefing | `cache/github/repos/{owner}_{name}/repo-analysis/briefing.md` @ clone `{briefing-shortSha}` |
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
- **Disposition (verbatim):**

  > {Exact disposition text from the applied-vote comment, quoted
  > verbatim. If unavailable, write "Disposition text not recorded in
  > Jira."}

- **Disposition summary:** {2–4 sentence neutral summary of what the
  disposition asked for.}
- **Commits applying this ticket:**
  - [`{shortSha}`]({commitUrl}) — {commit subject} ({authorDate})
  - …
- **Changes applied (per Step 5b, scoped to this page):**
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
derived from the Step 5a diff. Group by section heading where
possible:}

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
<blockquote class="ballot-note" id="bn{N}">
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
- Cases where the HEAD is not a descendant of the since-commit and
  the symmetric difference was used instead.
- Briefing staleness or absence.
- Any time `gh api` was used because the cache clone could not
  resolve a referenced commit.

If none: "No additional notes."}
````

## Important Rules

- **Roll-up first, ticket bullets second.** The proposed ballot note
  must reflect the after-applied state from Step 5a. Per-ticket
  descriptions are supporting evidence, not the source of truth.
- **Honour the existing ballot note.** Carry forward bullets that are
  still accurate in the after-applied state; drop and explain bullets
  that have been reverted or superseded.
- **Cite tickets inline in the proposed note.** Every bullet should
  point at the ticket(s) responsible. Use the Jira issue URL form
  shown above.
- **Stay in your lane.** This skill owns *only* page sources (the
  primary page file plus its conventional siblings as resolved in
  Step 2). Resource / profile / datatype-SD changes belong to
  `notes-artifact`; in `HL7/fhir` the consolidated datatypes page
  belongs to `notes-datatype`.
- **Match the page's authoring format.** Output HTML for HTML pages
  (HL7/fhir), and the IG's ballot-note convention for IG markdown
  pages — do not silently emit HTML into a markdown page when the IG
  uses a different convention.
- **Editorial churn is not a ballot bullet.** Bundle pure typo /
  whitespace / link-normalization work into a single closing sentence
  if at all.
- **Use only data from `fhir-augury-cli`, the cached clone (`git`),
  and `gh` as a last resort.** Do not fabricate ticket details, file
  paths, commit SHAs, or disposition text. If a call fails or returns
  no data, say so in the report.
- **Be specific.** Name the section heading, the paragraph, the
  conformance-language delta, the added/removed example.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root or alongside the
  cached clone.
