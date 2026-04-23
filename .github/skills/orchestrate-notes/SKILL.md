---
name: orchestrate-notes
description: "Orchestrates bulk drafting of ballot notes for a GitHub repo, anchored at a since-commit. USE FOR: repo-wide ballot-note refresh after a tranche of ticket work has landed, batch generation across artifacts, narrative pages, and (for `HL7/fhir`) the consolidated datatypes surface. Requires a GitHub repo (e.g., HL7/fhir) and a since-commit SHA. Walks the commit window, groups changed files into units (artifacts / pages / datatypes) using a shared datatype-page map so per-datatype own-pages (e.g., `dosage.html`, `metadatatypes.html`) are routed into the datatypes unit instead of being double-dispatched as pages, and dispatches up to N concurrent sub-agents (`notes-artifact`, `notes-page`, and — for `HL7/fhir` — a single `notes-datatype`) to produce per-unit markdown reports in a structured output directory."
---

# Orchestrate Notes Skill

Bulk-drafts ballot notes for a GitHub repository by walking the commit
window between a caller-supplied **since-commit** and the cached
clone's HEAD, grouping the changed files into **units**, and
dispatching one sub-agent per unit. A unit is one of:

- an **artifact** (resource, profile, IG artifact, terminology bundle)
  → handled by the `notes-artifact` skill;
- a **page** (narrative `.html` / `.md` page) → handled by the
  `notes-page` skill;
- the consolidated **datatypes** unit in `HL7/fhir` → handled by a
  single `notes-datatype` sub-agent that covers every datatype
  touched in the window, plus any per-datatype narrative page
  (`source/<page>.html`) the datatype-page map resolves to (e.g.,
  `dosage.html`, `marketingstatus.html`, `metadatatypes.html`).

Unlike the Jira-driven orchestrators (`orchestrate-prep`,
`orchestrate-plan`), the trigger here is a **commit**, not a Jira
queue; there is no `ProcessedLocally` flag to consult.

## Prerequisites

- The `fhir-augury-cli` skill must be available — it is the canonical
  entry point for talking to FhirAugury sources. Follow its fallback
  order (CLI → MCP → direct HTTP) for every data interaction.
- The `notes-artifact`, `notes-page`, and `notes-datatype` skills must
  be available — they define the per-unit workflow and report format
  that each sub-agent runs. The report structure, ticket attribution,
  diff strategy, ballot-note drafting rules, and source-file
  resolution are all owned by those skills and must not be replicated
  here. `notes-datatype` is only used when the repo is `HL7/fhir`.
- The orchestrator service and GitHub source service must be
  reachable. Verify with the CLI's `services` health check before
  starting.
- The GitHub source clone cache must contain `<owner>_<name>` with
  the since-commit reachable from HEAD. The orchestrator does **not**
  refresh clones — that is upstream of this skill.
- A current per-repo briefing under
  `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` must
  exist. The orchestrator reads it once for the unit-grouping rules;
  each sub-agent reads it again per its own skill. If missing or
  stale per the `repo-analysis` skill's staleness rules, stop and ask
  the user to refresh.
- `git` must be available on `PATH`.

## Inputs

The user must provide or you must determine:

1. **Repo** *(required)* — `owner/name`, e.g., `HL7/fhir`.
2. **Since-commit** *(required)* — full or short SHA. The window is
   `since-commit..HEAD` of the cached clone.
3. **Output directory** *(required)* — where unit reports are saved.
   Reports land at
   `<OutputDir>/<owner>_<name>/<since-shortSha>..<head-shortSha>/<unit-file>`,
   where `<unit-file>` follows the naming rules in Step 3. Example:
   `./cache/output/notes/`.
4. **Concurrency** *(optional, default `3`)* — maximum number of
   concurrent sub-agents. Each sub-agent does substantial git + Jira
   work; default is conservative.
5. **Filter** *(optional)* — a single glob (case-insensitive) matched
   against unit names. Applies to **both** artifact names and page
   names. If omitted, every unit whose files were touched in the
   window is processed. Examples:
   - `Observation` — only the `Observation` artifact.
   - `us-core-*` — every artifact / page whose name starts with
     `us-core-`.
   - `*` — everything (same as omitting the filter).

   The filter does **not** apply to the consolidated datatypes unit;
   that is controlled separately by **Exclude datatypes** below.
6. **Exclude datatypes** *(optional, default `false`)* — when `true`,
   do **not** dispatch the `notes-datatype` sub-agent even if files
   under `source/datatypes/` were touched in the window. Only
   meaningful when the repo is `HL7/fhir`; ignored otherwise.
7. **Working directory** *(optional, default `temp/notes/` relative to
   the repo root)* — directory the orchestrator and each sub-agent
   may use for transient files. Created if it does not already exist.
8. **Skip existing** *(optional, default `true`)* — if `true`, do not
   re-dispatch a sub-agent when its output file already exists. Set
   `false` to force a clean re-run.

## Workflow

### Step 1: Verify services and inputs

1. Health-check the orchestrator and GitHub source via the CLI's
   `services` command. Abort if either is down.
2. Confirm the cache clone exists and `cat-file -e <since-commit>^{commit}`
   succeeds inside it. Resolve the clone HEAD SHA.
3. Read and validate the briefing & meta. If missing or stale, stop
   and ask the user to run `repo-analysis` for the repo. Note the
   repo **category** (e.g., `FhirCore`, `FhirIg`,
   `FhirExtensionsPack`, `Incubator`, `Utg`, `JiraSpecArtifacts`) —
   it drives the grouping rules in Step 3.
4. Apply defaults: concurrency → `3`, working directory →
   `temp/notes/`, skip existing → `true`, exclude datatypes →
   `false`.

### Step 2: Enumerate changed files in the window

```bash
git -C cache/github/repos/<owner>_<name>/clone diff \
    --name-only <since-commit>..HEAD
```

Filter to the **authoring root(s)** declared in the briefing. Drop
any files in **generated areas (do not edit)** flagged by the
briefing (e.g., `tools/`, `publish/`, `output/` for FhirCore;
`fsh-generated/` for IG repos — though sub-agents may still inspect
those for after-applied SD shape).

### Step 3: Group files into units

Each touched file is classified into exactly one of three buckets:

- **`datatypes`** *(only when repo is `HL7/fhir`)* — any file under
  `source/datatypes/`, `source/datatypes.html`, **or any per-datatype
  own-page** (`source/<page>.html`) whose `<page>` resolves through
  the datatype-page map (see below). All such files collapse into a
  **single** `datatypes` unit handled by one `notes-datatype`
  sub-agent. Do **not** create one unit per datatype, and do **not**
  dispatch a separate `notes-page` unit for any own-page that the
  datatype map claims.
- **page** — a narrative page source file. Resolved per category:
  - **FhirCore (`HL7/fhir`)** — top-level `source/<page>.html` files
    (and any conventional siblings such as `source/<page>-notes.html`
    or `source/<page>-examples.html`). **Excludes** `source/datatypes.html`
    *and* every `source/<stem>.html` in the per-window
    `datatypeOwnedPages` set (handled by the `datatypes` unit), and
    excludes resource-intro files inside `source/<resource>/` folders
    (those belong to the resource artifact).
  - **FhirIg / FhirExtensionsPack / Incubator** — files under
    `input/pagecontent/**` (and any non-conventional page locations
    declared in the briefing's page index or `sushi-config.yaml`
    `pages:` block).
  - **Utg / JiraSpecArtifacts / other** — narrative files declared as
    pages in the briefing's page index.

  Group page files by their normalised **page name** (the filename
  stem of the primary page source — e.g., `security`,
  `us-core-patient`). Sibling files (`-notes.md`, `-examples.html`,
  `-intro.md`, page images) roll into the same page unit; the
  sub-agent's skill (`notes-page`) is responsible for the canonical
  sibling list.
- **artifact** — every other in-scope touched file. Grouping rules by
  category:
  - **FhirCore (`HL7/fhir`)** — group by the first segment under
    `source/`. Each `source/<name>/` is one artifact whose name is
    `<Name>` (PascalCase / canonical resource name from the SD; use
    the folder name verbatim if uncertain). Files in `source/<name>/`
    whose stem starts with `structuredefinition-` but does **not**
    match `<name>` (extra profile artifacts shipped alongside the
    resource) are still rolled into the `<Name>` artifact —
    `notes-artifact` lists them under "Source Files".
  - **Ig / FhirExtensionsPack / Incubator** — group FSH and resource
    files by the artifact id declared in the FSH header / resource
    `id`. Use the briefing's Artifact Map to resolve `input/fsh/**`,
    `input/resources/**`, and `fsh-generated/resources/**` to a
    stable artifact id. Files that cannot be attributed (e.g., shared
    `_includes` outside `input/pagecontent/`) go into a synthetic
    `_shared` artifact, off by default.
  - **Utg (`HL7/UTG`)** — group by the canonical declared in each
    changed source-of-truth file.
  - **JiraSpecArtifacts** — group by the per-spec artifact id from the
    Artifact Map.

After bucketing, apply selection rules:

#### Datatype-page map (FhirCore only)

For `HL7/fhir`, the orchestrator computes a per-window
`datatypeOwnedPages` set so that own-page datatypes are routed into
the `datatypes` unit instead of being dispatched as standalone
`notes-page` units. The map MUST be kept identical to the one
documented in `notes-datatype/SKILL.md` ("Datatype-page map" section)
— if the FHIR repo grows a new own-page datatype, update both files.

Computation (run once per orchestrator invocation):

1. List `source/datatypes/<dt>.xml` SD files at HEAD inside the
   cached clone.
2. For each `<dt>`, derive a candidate page stem:
   - **Default**: lowercase datatype name (e.g., `Quantity` →
     `quantity`).
   - **Explicit overrides**:
     - `Reference` → `references`.
     - The **MetaDataTypes cluster** — `ContactDetail`,
       `DataRequirement`, `Expression`, `ParameterDefinition`,
       `RelatedArtifact`, `TriggerDefinition`, `UsageContext`,
       `Contributor` — all → `metadatatypes`.
3. Test the candidate stem against HEAD:

   ```bash
   git -C cache/github/repos/HL7_fhir/clone \
       cat-file -e HEAD:source/<stem>.html
   ```

4. When `cat-file -e` succeeds, add `source/<stem>.html` to
   `datatypeOwnedPages`.

The orchestrator then:

- Removes any path in `datatypeOwnedPages` from the candidate **page**
  bucket before grouping by page name.
- Adds any **touched** path in `datatypeOwnedPages` to the
  `datatypes` unit's file scope (alongside `source/datatypes/**` and
  `source/datatypes.html`). The `notes-datatype` sub-agent
  re-discovers its own target pages from HEAD; the orchestrator's job
  is purely to prevent double-dispatch.

Apply selection rules:

- **Datatypes unit** — drop entirely when the repo is not `HL7/fhir`,
  when `exclude datatypes = true`, or when no `source/datatypes/**`
  / `source/datatypes.html` file was touched in the window. The
  filter glob does **not** apply to the datatypes unit.
- **Artifact and page units** — apply the **filter glob** (when
  supplied) against the unit name. Matching is case-insensitive.
- **Skip existing** — when `true`, drop any unit whose output file
  already exists at the deterministic path computed below.

Output file naming (under
`<OutputDir>/<owner>_<name>/<since-shortSha>..<head-shortSha>/`):

| Unit kind  | File name                  |
|------------|----------------------------|
| artifact   | `<artifact>.md`            |
| page       | `_page_<page>.md`          |
| datatypes  | `_datatypes.md`            |

The `_page_` and `_datatypes` prefixes (with a leading underscore)
keep page and datatypes reports visually grouped at the top of the
directory listing and prevent collisions with same-named artifacts.

If the unit set is empty after filtering, report that and exit
cleanly — there is nothing to do.

### Step 4: Confirm with the user

Present a one-screen summary and **ask the user to confirm**. Use the
`ask_user` tool with `Yes, start` / `Cancel` choices:

```
About to draft ballot notes:

  Repo              : HL7/fhir (FhirCore)
  Since-commit      : 1a2b3c4d5e6f
  HEAD              : 9f8e7d6c5b4a (descendant: yes)
  Window            : 1a2b3c4..9f8e7d6
  Output directory  : ./cache/output/notes/HL7_fhir/1a2b3c4..9f8e7d6/
  Working dir       : temp/notes/   (relative to repo root)
  Filter            : (all)
  Exclude datatypes : false
  Skip existing     : true
  Concurrency       : 3
  Units found       : 14
    Artifacts (12):
      • Observation        (8 commits, 6 files)
      • Patient            (3 commits, 2 files)
      • MedicationRequest  (2 commits, 1 file)
      …
    Pages (1):
      • security           (4 commits, 1 file)
    Datatypes (1):
      • datatypes          (5 commits, 9 files across 4 datatypes:
                            Dosage, MarketingStatus, Quantity, Period)
                           Pages targeted: datatypes.html,
                                           dosage.html,
                                           marketingstatus.html

Proceed?
```

Show **every** unit in the planned batch — the user often spots
mis-grouping at this stage. Do not proceed on anything except
explicit confirmation.

### Step 5: Create directories (cross-platform)

Both the **per-window output directory** (`<OutputDir>/<owner>_<name>/<since-shortSha>..<head-shortSha>/`)
and the **working directory** must exist before dispatching
sub-agents. Use a method that works on Windows (PowerShell) and Unix
(bash):

- **Tool-based** (preferred when available): use the agent's
  file-system tool.
- **Shell-based**:
  - PowerShell: `New-Item -ItemType Directory -Path $Path -Force | Out-Null`
  - bash/sh: `mkdir -p "$Path"`

### Step 6: In-memory tracking

Maintain in memory:

- `pending` — queue of units not yet dispatched. Each entry carries
  `{kind, name, outputFile}` where `kind` is `artifact` / `page` /
  `datatypes`.
- `inFlight` — set of units currently assigned to a running
  sub-agent.
- `completed` / `failed` counters and a per-unit result map for the
  final summary.

There is **no durable persistent state**. Re-running with the same
inputs and `skip existing = true` is the natural resume mechanism —
already-written reports are skipped in Step 3.

### Step 7: Dispatch loop

Loop until `pending` is empty AND `inFlight` is empty:

1. While `len(inFlight) < concurrency` and `pending` is non-empty:
   1. Pop the next unit off `pending`.
   2. Dispatch a sub-agent (Step 8). Add the unit to `inFlight`.
2. Wait for the next sub-agent completion (Step 9) before continuing
   the outer loop.

Order is not significant, but a sensible default is to dispatch the
`datatypes` unit (if present) early — it tends to be the slowest
unit and benefits from parallel headroom while artifact units run.

### Step 8: Dispatch a sub-agent

For each unit, launch a **general-purpose background agent** that
runs the appropriate skill. Use the same model as the orchestrator.
Do **not** inline the sub-skill SKILL.md content — sub-agents
resolve the skill by name.

Skill selection by unit kind:

| Unit kind  | Skill            |
|------------|------------------|
| artifact   | `notes-artifact` |
| page       | `notes-page`     |
| datatypes  | `notes-datatype` |

Each sub-agent gets its own working subdirectory:
`{WORKING_DIR}/{kind}_{name}/`. Use forward slashes in paths inside
the prompt; both PowerShell and bash accept them, and the sub-agent
can normalise as needed.

#### Artifact prompt

````
Run the `notes-artifact` skill for the following artifact.

## Inputs

- **Repo:** {OWNER}/{NAME}
- **Since-commit:** {SINCE_SHA}
- **Artifact:** {ARTIFACT}
- **Output file:** {OUTPUT_DIR}/{OWNER}_{NAME}/{SINCE_SHORT}..{HEAD_SHORT}/{ARTIFACT}.md
- **Working directory:** {WORKING_DIR}/artifact_{ARTIFACT}/
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `notes-artifact` skill exactly, including all
   data-gathering steps, the briefing dependency, and the report
   format.
2. Use the supplied **Working directory** for any transient files.
3. Save the completed report to the output file path above.
4. When finished, confirm success and state the full path of the
   saved file.
````

#### Page prompt

````
Run the `notes-page` skill for the following page.

## Inputs

- **Repo:** {OWNER}/{NAME}
- **Since-commit:** {SINCE_SHA}
- **Page:** {PAGE}
- **Output file:** {OUTPUT_DIR}/{OWNER}_{NAME}/{SINCE_SHORT}..{HEAD_SHORT}/_page_{PAGE}.md
- **Working directory:** {WORKING_DIR}/page_{PAGE}/
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `notes-page` skill exactly, including all
   data-gathering steps, the briefing dependency, and the report
   format.
2. Use the supplied **Working directory** for any transient files.
3. Save the completed report to the output file path above.
4. When finished, confirm success and state the full path of the
   saved file.
````

#### Datatypes prompt (HL7/fhir only)

````
Run the `notes-datatype` skill for the FHIR datatypes surface.

## Inputs

- **Repo:** HL7/fhir
- **Since-commit:** {SINCE_SHA}
- **Datatype focus:** (none — cover every datatype touched in the window)
- **Output file:** {OUTPUT_DIR}/HL7_fhir/{SINCE_SHORT}..{HEAD_SHORT}/_datatypes.md
- **Working directory:** {WORKING_DIR}/datatypes/
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `notes-datatype` skill exactly, including all
   data-gathering steps, the briefing dependency, and the report
   format.
2. This skill may produce **multiple** ballot-note drafts in a
   single report — one per target page (`source/datatypes.html`
   plus any per-datatype narrative pages such as `source/dosage.html`
   or `source/metadatatypes.html` resolved via the datatype-page
   map).
3. Use the supplied **Working directory** for any transient files.
4. Save the completed report to the output file path above.
5. When finished, confirm success and state the full path of the
   saved file.
````

### Step 9: Handle completion

When a sub-agent completes:

1. **Read the agent result** to confirm success and that the report
   file exists at the expected path.
2. **Remove the unit from `inFlight`**.
3. **Record** success / failure (and the failure message, if any) in
   the result map.
4. **Loop back to Step 7** to dispatch the next unit.

### Step 10: Error handling

- **Sub-agent failure** — log the unit + error; do **not** retry
  automatically in the same run. The unit remains absent from the
  output directory, so a subsequent re-run with `skip existing =
  true` will pick it up naturally.
- **CLI unavailable** — fall back per the `fhir-augury-cli` skill's
  fallback order (MCP, then direct HTTP). Record which path was used
  in the final summary.
- **Service unhealthy mid-run** — pause new dispatches, wait for
  in-flight agents to complete, surface the issue to the user before
  resuming.
- **Empty window** — if Step 2 returns no files in scope, report
  that and exit; do not present a confirmation prompt.

### Step 11: Progress and final summary

Report to the user:

- After each completion: completed / failed / in-flight counts; the
  unit just finished (with its kind) and its output path. For the
  `datatypes` unit, surface the list of target pages drafted in the
  report (parsed from the report header table's `Pages targeted`
  row, or stated as "see report" if parsing fails).
- Final summary: a table of `kind | unit | status | report path |
  error (if any)`. State the output directory path so the user can
  review reports as a batch.

## Resumability

There is **no local persistent state**. Resume relies on the
`skip existing` rule in Step 3:

- Re-invoke the skill with the same repo, since-commit, output
  directory, and working directory.
- Step 3 will drop any unit whose report file already exists and
  re-dispatch only the rest.

To force a full re-run, pass `skip existing = false` (or delete the
`<since-shortSha>..<head-shortSha>/` directory beforehand).

## Example Invocation

User: *"Draft updated ballot notes for `HL7/fhir` since commit
`1a2b3c4`, saving reports to `./cache/output/notes/`, 3 concurrent
agents."*

The orchestrator should:

1. Health-check services; confirm the cache clone has `1a2b3c4`
   reachable from HEAD; load the FhirCore briefing.
2. `git diff --name-only 1a2b3c4..HEAD` and bucket the result into
   artifacts (per `source/<name>/` folder), pages (per top-level
   `source/<page>.html`, **excluding** `source/datatypes.html` and
   any per-datatype own-page in the computed `datatypeOwnedPages`
   set), and datatypes (any file under `source/datatypes/`,
   `source/datatypes.html`, or any touched own-page in
   `datatypeOwnedPages`, collapsed into one unit).
3. Apply defaults (concurrency `3`, working directory `temp/notes/`,
   skip existing `true`, exclude datatypes `false`) and present a
   confirmation summary listing every unit in the batch via
   `ask_user`.
4. After confirmation, ensure the per-window output directory and
   the working directory exist (cross-platform).
5. Loop: dispatch up to 3 background sub-agents at a time —
   `notes-artifact` for each artifact, `notes-page` for each page,
   and a single `notes-datatype` for the consolidated datatypes
   unit — until the queue drains.
6. Report completion and the final per-unit status table.

## Performance Notes

- Each artifact / page sub-agent typically takes **1–4 minutes**
  depending on commit count, ticket count, and diff size. The single
  `notes-datatype` sub-agent for `HL7/fhir` is usually the slowest
  unit because it spans every datatype touched in the window — budget
  **3–8 minutes** for it on a multi-month tranche. A FhirCore window
  spanning a multi-month tranche of work commonly produces 10–30
  artifact units plus a handful of page units and (when not excluded)
  one datatypes unit.
- Concurrency 2–4 is a sane default; the bottleneck is usually the
  per-ticket Jira fetches inside each sub-agent. Raise carefully if
  the host has the headroom and the orchestrator / Jira source remain
  healthy.
- The orchestrator itself is cheap — almost all wall-clock time is
  inside sub-agents.
