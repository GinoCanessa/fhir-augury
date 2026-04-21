---
name: artifact-notes
description: "Drafts an updated ballot note for a single FHIR artifact based on changes made since a specified commit. USE FOR: per-artifact ballot notes, ballot-comment drafting, change roll-ups for resources/profiles/IG artifacts. Requires a GitHub repo (e.g., HL7/fhir), a since-commit SHA, and an artifact name (e.g., Observation). Walks the artifact's source files between the since-commit and HEAD, attributes commits to the FHIR Jira tickets they applied, summarises what actually changed in the after-applied state, and writes a markdown report containing a draft HTML ballot note suitable for the artifact's intro file."
---

# Artifact Notes Skill

Drafts an updated **ballot note** for a single FHIR artifact (resource,
profile, IG artifact, terminology bundle, …) by analysing the changes
that have landed in its source files since a caller-supplied commit.
The output is a markdown review report containing the proposed HTML
ballot note plus the supporting evidence (per-commit / per-ticket
breakdown, rolled-up summary, current ballot note for context).

The roll-up summary of changes **must be derived from the
after-applied diff** (since-commit → HEAD), not by stitching together
per-ticket descriptions. Individual tickets frequently overlap, expand,
or revert each other — only the after-applied state reflects reality.

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

- **Repo** *(required)* — `owner/name`, e.g., `HL7/fhir`.
- **Since-commit** *(required)* — full or short SHA. The roll-up window
  is `since-commit..HEAD` of the cached clone (fast-forward range; if
  HEAD is not a descendant of since-commit, fall back to the symmetric
  difference and note the deviation in the report).
- **Artifact** *(required)* — the artifact identifier as it appears in
  the repo's authoring layout. Examples by category:
  - **FhirCore** (`HL7/fhir`): a resource or datatype name matching a
    `source/<name>/` folder (e.g., `Observation`, `Patient`,
    `MedicationRequest`). Case-insensitive against the folder name.
  - **Ig** / **FhirExtensionsPack** / **Incubator**: the FSH/profile
    identifier or the publisher artifact id (e.g., `us-core-patient`,
    `Profile-MyProfile`).
  - **Utg**: the canonical id of the ValueSet / CodeSystem (e.g.,
    `v3-ActCode`).
- **Output file** *(required)* — full path where the markdown report
  should be written. The orchestrator passes a deterministic path; for
  ad-hoc invocations the agent may default to
  `<working-dir>/<repo-segment>_<artifact>.md` and report the path
  back.
- **Working directory** *(optional)* — directory for transient files
  (intermediate diffs, commit lists, ticket dumps). When supplied,
  **all transient files must be written under this directory**. Create
  it with `New-Item -ItemType Directory -Force` (PowerShell),
  `mkdir -p` (bash), or your file-system tool if it does not exist.

## Prerequisites

- The GitHub source clone cache must be populated and current enough
  that the since-commit is reachable from the cached clone HEAD. If
  the since-commit is missing, ask the user to refresh the clone (or
  fall back to fetching the commit via `gh api` and noting the
  deviation in the report).
- A current per-repo briefing under
  `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` must
  exist. The artifact-to-files resolution in Step 2 leans on the
  briefing's **Artifact Map** and **Authoring root(s)**. If the
  briefing is missing or stale (per the staleness rules in the
  `repo-analysis` skill), stop and ask the user to run `repo-analysis`
  before continuing.
- `git` must be available on `PATH`. `gh` is required only if the
  cache clone cannot resolve the since-commit or a commit URL needs to
  be confirmed against `github.com`.

## Workflow

Run independent calls in parallel where possible.

### Step 1: Verify services and briefing

1. Health-check via `fhir-augury-cli`:

   ```bash
   fhir-augury-cli --json '{"command":"services","action":"health"}'
   ```

2. Read the briefing and metadata:
   - `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md`
   - `cache/github/repos/<owner>_<name>/repo-analysis/meta.json`

   Apply the staleness rules from the `repo-analysis` skill. If
   missing or stale, stop and ask the user.

3. Confirm the cache clone and resolve HEAD:

   ```powershell
   $clone = "cache/github/repos/<owner>_<name>/clone"
   git -C $clone rev-parse HEAD
   git -C $clone cat-file -e <since-commit>^{commit}
   ```

   If `cat-file -e` fails, the since-commit isn't in the cache clone —
   stop and ask the user to refresh the clone, or fall back to `gh api
   /repos/<owner>/<name>/commits/<since-commit>` and note the
   limitation in the report.

### Step 2: Resolve artifact → source files

Use the briefing's **Artifact Map** as the authoritative mapping from
artifact → on-disk paths. Concrete patterns by category:

- **FhirCore (`HL7/fhir`)** — for an artifact `<Name>` whose folder is
  `source/<name>/` (folder name is lowercase per repo convention),
  collect:
  - `source/<name>/structuredefinition-<name>.xml` — the canonical SD
    (filename stem must match the folder name).
  - `source/<name>/<name>-introduction.xml` — narrative intro (this is
    where the ballot note lives).
  - `source/<name>/<name>-notes.xml` — supplementary narrative.
  - `source/<name>/bundle-<name>-search-params.xml` — search
    parameters bundle.
  - `source/<name>/list-<name>-operations.xml` — operations bundle.
  - `source/<name>/list-<name>-examples.xml` — examples list.
  - `source/<name>/<name>-examples.xml` and `<name>-example*.xml` —
    examples.
  - `source/<name>/valueset-*.xml`, `source/<name>/codesystem-*.xml` —
    artifact-scoped terminology.
  - `source/<name>/<name>-spreadsheet.xml` — legacy spreadsheet (note
    in the report; SD is authoritative for resources / non-primitive
    datatypes).
  - Any sibling `structuredefinition-*.xml` whose name does **not**
    match the folder (extra profile artifacts that ship alongside the
    resource).

- **Ig / FhirExtensionsPack / Incubator** — use the briefing's Artifact
  Map to find the FSH source(s) (`input/fsh/**/*.fsh`), the rendered
  IG resource (`input/resources/**` or `fsh-generated/resources/**`),
  the page mark-up (`input/pagecontent/**`), and any related examples
  (`input/examples/**`). Treat `fsh-generated/` as derived; prefer
  `input/fsh/` for "source" diffs but include `fsh-generated/` when
  necessary to demonstrate the after-applied SD shape.

- **Utg (`HL7/UTG`)** — use the briefing's Artifact Map for the
  canonical's path (typically `input/sourceOfTruth/**` or
  `input/codesystems/**`, depending on the canonical).

- **JiraSpecArtifacts** — the per-spec generated XML(s) under the
  configured artifact root.

If the briefing's Artifact Map does **not** have an explicit mapping
for the requested artifact, list every file matched by the patterns
above that exists in the clone, and list the patterns that produced no
match in the report's "Source files" section.

Materialise the file list as both:

- **`workingFileList`** — paths relative to the clone root, used by
  `git` calls in Steps 3–5.
- **`displayFileList`** — paths shown in the report, with a one-line
  role for each.

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

If the window is empty (no commits touched the file set), write a
short report noting "No changes to artifact in window" and exit.

### Step 4: Attribute commits to Jira tickets

For each commit, extract the candidate Jira ticket keys (regex
`(FHIR|UTG)-\d+`) from the commit subject + body, and union with any
keys returned by `cross-referenced` for the commit SHA:

```bash
fhir-augury-cli --json '{"command":"cross-referenced","value":"<sha>","limit":50}'
```

Build two indexes in memory:

- `commitToTickets[sha] = [key, …]`
- `ticketToCommits[key] = [{sha, shortSha, subject, webUrl}, …]`

Tickets attributable to **multiple** commits in the window are
expected — the per-ticket section must list every commit that applied
work for that ticket.

For each unique ticket key, fetch its details once, in parallel
across keys:

```bash
fhir-augury-cli --json '{"command":"get","source":"jira","id":"FHIR-XXXXX","includeContent":true,"includeComments":true,"includeSnapshot":true}'
```

Extract:
- `metadata.title`, `metadata.resolution`, `metadata.resolution_description`
- `metadata.work_group`, `metadata.specification`
- `content` (full description) — used for context but **not** for the
  per-ticket "what changed in the repo" summary
- The applied-vote comment (if present), where the workgroup recorded
  the exact disposition text. Look for the disposition / applied-vote
  marker the work group uses in `comments`.

Commits with **no** discoverable ticket keys are still listed in the
commit table (Step 5 / report) under an "Unattributed" group; their
diffs are still rolled into Step 5.

### Step 5: Compute diffs (per-ticket and rollup)

Two diff sets are required.

**5a. Roll-up diff (since-commit → HEAD).**

```bash
git -C <clone> diff <since-commit>..HEAD -- <workingFileList>
```

Also capture per-file stats:

```bash
git -C <clone> diff --stat <since-commit>..HEAD -- <workingFileList>
```

Use this diff to write the **roll-up summary** of what changed in the
after-applied state (Step 6 / report). Group observations by file role
(SD differential vs. intro narrative vs. search params vs.
operations, etc.). Call out:

- Element additions / removals / cardinality / type / binding changes
  in the StructureDefinition's `<differential>` (treat
  `<snapshot>` edits as derived — note them but do not narrate
  individual snapshot edits).
- Material narrative changes in the intro/notes file.
- New or removed search parameters (entries in
  `bundle-<name>-search-params.xml`).
- New or removed operations (entries in
  `list-<name>-operations.xml`).
- New or removed examples.
- Terminology changes (sibling `valueset-*` / `codesystem-*` files).

**5b. Per-ticket diff.**

For each ticket with at least one commit in the window, compute the
union diff of that ticket's commits, scoped to the file list:

```bash
git -C <clone> show --stat --pretty=fuller <sha1> <sha2> ... -- <workingFileList>
```

Or, for a tighter per-ticket diff, walk each commit individually with
`git show --first-parent` and concatenate the per-file hunks. Use this
to author the **per-ticket "Changes Applied"** paragraph. Be honest
about overlap: if two tickets touch the same lines, say so and defer
the authoritative summary to the roll-up.

### Step 6: Read the current ballot note

Read the current intro file (e.g.,
`source/<name>/<name>-introduction.xml`) at HEAD and locate any
`<blockquote class="ballot-note" …>…</blockquote>` blocks. Extract
their full inner content verbatim. If multiple ballot notes exist
(distinct `id`s), capture them all.

If no ballot note exists, record "No existing ballot note." and draft
a fresh one in Step 7.

### Step 7: Draft the proposed ballot note

The proposed ballot note MUST:

- Be authored as **HTML**, ready to paste into the intro file inside a
  `<blockquote class="ballot-note" id="…">…</blockquote>` wrapper.
  Preserve any existing `id` attribute when revising an existing note;
  pick the next free `bn<N>` id when adding a new note.
- Be **derived from the roll-up summary (Step 5a)**, not a paste-up of
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
- Avoid restating mechanics already obvious from the SD (e.g.,
  "renamed `Observation.referenceRange.normalValue.normalValue` to
  …"). Focus on intent, scope, and balloter-relevant impact.

### Step 8: Write the report

Compose the markdown report per the **Report Format** below and save
it to the output file path. Use the gathered data to write
substantive, specific content — no generic placeholders.

---

## Report Format

The report MUST follow this structure. Every section is required;
sections may note "None" when no data exists.

````markdown
# Artifact Ballot Note Draft: {Artifact} ({owner/name})

| | |
|-|-|
| Repository | [{owner}/{name}](https://github.com/{owner}/{name}) ({category from briefing}) |
| Artifact | `{artifact}` |
| Window | [`{since-shortSha}`](https://github.com/{owner}/{name}/commit/{since-sha})..[`{head-shortSha}`](https://github.com/{owner}/{name}/commit/{head-sha}) |
| Commits in window | {N} |
| Tickets attributed | {M} |
| Briefing | `cache/github/repos/{owner}_{name}/repo-analysis/briefing.md` @ clone `{briefing-shortSha}` |
| Generated | {ISO-8601 UTC timestamp} |

## Source Files

Files considered part of `{artifact}` for this run (from the briefing's
Artifact Map):

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

{Patterns from the briefing that produced no match in the clone:}
- `<pattern>` — no files matched.

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
- **Disposition (verbatim):**

  > {Exact disposition text from the applied-vote comment, quoted
  > verbatim. If unavailable, write "Disposition text not recorded in
  > Jira."}

- **Disposition summary:** {2–4 sentence neutral summary of what the
  disposition asked for.}
- **Commits applying this ticket:**
  - [`{shortSha}`]({commitUrl}) — {commit subject} ({authorDate})
  - …
- **Changes applied (per Step 5b, scoped to this artifact):**
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
window, derived from the Step 5a diff. Group by file role:}

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
  UTG per the FhirCore briefing's cross-repo touch points.}

## Proposed Ballot Note (HTML)

{The draft ballot note, ready to drop into the intro file. Preserve
the existing `id` if revising; otherwise pick the next free `bn<N>`.
Use Jira links of the form
`<a href="https://jira.hl7.org/browse/FHIR-XXXXX">FHIR-XXXXX</a>`
inline against the bullet they support.}

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
- Tickets whose commits touched files outside the artifact's scope,
  with a one-line pointer to the other artifact.
- Cases where the HEAD is not a descendant of the since-commit and
  the symmetric difference was used instead.
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
- **Use only data from `fhir-augury-cli`, the cached clone (`git`),
  and `gh` as a last-resort.** Do not fabricate ticket details, file
  paths, commit SHAs, or disposition text. If a call fails or returns
  no data, say so in the report.
- **Treat `<snapshot>` as derived.** Narrate `<differential>` changes
  in the SD; mention only that snapshot regeneration is required, do
  not enumerate snapshot edits.
- **Trust the saved briefing for paths and gotchas.** Do not infer
  repo layout from memory. If the briefing flags a gotcha (e.g.,
  legacy spreadsheet vs. SD authority for FhirCore, or
  `fsh-generated/` being derived for IG repos), the relevant section
  must respect it.
- **Be specific.** "Updated several elements" is not useful. Name the
  element, the field, the old vs. new value where relevant.
- **All transient files go under the supplied working directory.**
  Never write scratch files into the repo root or alongside the
  cached clone.
