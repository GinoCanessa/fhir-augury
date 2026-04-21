---
name: orchestrate-artifact-notes
description: "Orchestrates bulk drafting of per-artifact ballot notes for a GitHub repo, anchored at a since-commit. USE FOR: repo-wide ballot-note refresh after a tranche of ticket work has landed, batch artifact-notes generation. Requires a GitHub repo (e.g., HL7/fhir) and a since-commit SHA. Walks the commit window, groups changed files into artifacts using the per-repo briefing, dispatches up to N concurrent `artifact-notes` sub-agents (one per artifact), and saves markdown reports to a structured output directory."
---

# Orchestrate Artifact Notes Skill

Bulk-drafts per-artifact ballot notes for a GitHub repository by
walking the commit window between a caller-supplied **since-commit**
and the cached clone's HEAD, grouping the changed files into
artifacts, and dispatching one `artifact-notes` sub-agent per
artifact. Unlike the Jira-driven orchestrators (`orchestrate-prep`,
`orchestrate-plan`), the trigger here is a **commit**, not a Jira
queue; there is no `ProcessedLocally` flag to consult.

## Prerequisites

- The `fhir-augury-cli` skill must be available — it is the canonical
  entry point for talking to FhirAugury sources. Follow its fallback
  order (CLI → MCP → direct HTTP) for every data interaction.
- The `artifact-notes` skill must be available — it defines the
  per-artifact workflow and report format that each sub-agent runs.
  The report structure, ticket attribution, diff strategy, ballot-note
  drafting rules, and source-file resolution are all owned by
  `artifact-notes` and must not be replicated here.
- The orchestrator service and GitHub source service must be
  reachable. Verify with the CLI's `services` health check before
  starting.
- The GitHub source clone cache must contain `<owner>_<name>` with
  the since-commit reachable from HEAD. The orchestrator does **not**
  refresh clones — that is upstream of this skill.
- A current per-repo briefing under
  `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` must
  exist. The orchestrator reads it once for the artifact-grouping
  rules; each sub-agent reads it again per `artifact-notes`. If
  missing or stale per the `repo-analysis` skill's staleness rules,
  stop and ask the user to refresh.
- `git` must be available on `PATH`.

## Inputs

The user must provide or you must determine:

1. **Repo** *(required)* — `owner/name`, e.g., `HL7/fhir`.
2. **Since-commit** *(required)* — full or short SHA. The window is
   `since-commit..HEAD` of the cached clone.
3. **Output directory** *(required)* — where artifact reports are
   saved. Reports land at
   `<OutputDir>/<owner>_<name>/<since-shortSha>..<head-shortSha>/<artifact>.md`.
   Example:
   `C:\ai\git\fhir-augury-content\artifact-notes\`.
4. **Concurrency** *(optional, default `3`)* — maximum number of
   concurrent sub-agents. Each sub-agent does substantial git + Jira
   work; default is conservative.
5. **Artifact filter** *(optional)* — a list or glob of artifact names
   to include. If omitted, every artifact whose files were touched in
   the window is processed.
6. **Artifact exclude** *(optional)* — a list or glob of artifact
   names to skip (commonly used to skip primitive datatypes for
   FhirCore, or `tools/` / publisher harness paths if they slipped
   through).
7. **Working directory** *(optional, default `temp/artifact-notes/`
   relative to the repo root)* — directory the orchestrator and each
   sub-agent may use for transient files. Created if it does not
   already exist.
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
   and ask the user to run `repo-analysis` for the repo.
4. Apply defaults: concurrency → `3`, working directory →
   `temp/artifact-notes/`, skip existing → `true`.

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

### Step 3: Group files into artifacts

Use the per-repo briefing's category and artifact map to group the
filtered files into a `Map<artifact, files[]>`. Concrete grouping
rules by category:

- **FhirCore (`HL7/fhir`)** — group by the first segment under
  `source/`. Each `source/<name>/` is one artifact whose name is
  `<Name>` (PascalCase / canonical resource name from the SD; use the
  folder name verbatim if uncertain). Skip `source/datatypes/`
  spreadsheet files (see Pitfalls in the FhirCore briefing). Files in
  `source/<name>/` whose stem starts with `structuredefinition-` but
  does **not** match `<name>` (extra profile artifacts shipped
  alongside the resource) are still rolled into the `<Name>`
  artifact — `artifact-notes` lists them under "Source Files".
  Top-level `source/*.html` pages do not belong to any single
  artifact; group them under a synthetic artifact named `_pages` if
  the user wants them processed (off by default — i.e., excluded
  unless explicitly listed in the artifact filter).
- **Ig / FhirExtensionsPack / Incubator** — group FSH and resource
  files by the artifact id declared in the FSH header / resource
  `id`. Use the briefing's Artifact Map to resolve `input/fsh/**`,
  `input/resources/**`, `input/pagecontent/**`, and
  `fsh-generated/resources/**` to a stable artifact id. Files that
  cannot be attributed (e.g., shared `_includes`) go into a synthetic
  `_shared` artifact, off by default.
- **Utg (`HL7/UTG`)** — group by the canonical declared in each
  changed source-of-truth file.
- **JiraSpecArtifacts** — group by the per-spec artifact id from the
  Artifact Map.

After grouping, apply **artifact filter** / **artifact exclude** and
the **skip existing** rule (drop artifacts whose
`<OutputDir>/<owner>_<name>/<since-shortSha>..<head-shortSha>/<artifact>.md`
already exists when `skip existing = true`).

If the grouped set is empty after filtering, report that and exit
cleanly — there is nothing to do.

### Step 4: Confirm with the user

Present a one-screen summary and **ask the user to confirm**. Use the
`ask_user` tool with `Yes, start` / `Cancel` choices:

```
About to draft artifact ballot notes:

  Repo            : HL7/fhir (FhirCore)
  Since-commit    : 1a2b3c4d5e6f
  HEAD            : 9f8e7d6c5b4a (descendant: yes)
  Window          : 1a2b3c4..9f8e7d6
  Output directory: C:\ai\git\fhir-augury-content\artifact-notes\HL7_fhir\1a2b3c4..9f8e7d6\
  Working dir     : temp/artifact-notes/   (relative to repo root)
  Artifact filter : (all)
  Excludes        : (none)
  Skip existing   : true
  Concurrency     : 3
  Artifacts found : 12
    • Observation   (8 commits, 6 files)
    • Patient       (3 commits, 2 files)
    • MedicationRequest (2 commits, 1 file)
    …

Proceed?
```

Show **every** artifact in the planned batch — the user often spots
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

- `pending` — queue of artifacts not yet dispatched.
- `inFlight` — set of artifacts currently assigned to a running
  sub-agent.
- `completed` / `failed` counters and a per-artifact result map for
  the final summary.

There is **no durable persistent state**. Re-running with the same
inputs and `skip existing = true` is the natural resume mechanism —
already-written reports are skipped in Step 3.

### Step 7: Dispatch loop

Loop until `pending` is empty AND `inFlight` is empty:

1. While `len(inFlight) < concurrency` and `pending` is non-empty:
   1. Pop the next artifact off `pending`.
   2. Dispatch a sub-agent (Step 8). Add the artifact to `inFlight`.
2. Wait for the next sub-agent completion (Step 9) before continuing
   the outer loop.

### Step 8: Dispatch a sub-agent

For each artifact, launch a **general-purpose background agent** that
runs the `artifact-notes` skill. Use the same model as the
orchestrator. Do **not** inline the `artifact-notes` SKILL.md content
— sub-agents resolve the skill by name.

The sub-agent prompt:

````
Run the `artifact-notes` skill for the following artifact.

## Inputs

- **Repo:** {OWNER}/{NAME}
- **Since-commit:** {SINCE_SHA}
- **Artifact:** {ARTIFACT}
- **Output file:** {OUTPUT_DIR}/{OWNER}_{NAME}/{SINCE_SHORT}..{HEAD_SHORT}/{ARTIFACT}.md
- **Working directory:** {WORKING_DIR}
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `artifact-notes` skill exactly, including all
   data-gathering steps, the briefing dependency, and the report
   format.
2. Use the supplied **Working directory** for any transient files.
   Each sub-agent gets its own subdirectory:
   `{WORKING_DIR}/{ARTIFACT}/`.
3. Save the completed report to the output file path above.
4. When finished, confirm success and state the full path of the
   saved file.
````

Use forward slashes in paths inside the prompt; both PowerShell and
bash accept them, and the sub-agent can normalise as needed.

### Step 9: Handle completion

When a sub-agent completes:

1. **Read the agent result** to confirm success and that the report
   file exists at the expected path.
2. **Remove the artifact from `inFlight`**.
3. **Record** success / failure (and the failure message, if any) in
   the result map.
4. **Loop back to Step 7** to dispatch the next artifact.

### Step 10: Error handling

- **Sub-agent failure** — log the artifact + error; do **not** retry
  automatically in the same run. The artifact remains absent from the
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
  artifact just finished and its output path.
- Final summary: a table of `artifact | status | report path | error
  (if any)`. State the output directory path so the user can review
  reports as a batch.

## Resumability

There is **no local persistent state**. Resume relies on the
`skip existing` rule in Step 3:

- Re-invoke the skill with the same repo, since-commit, output
  directory, and working directory.
- Step 3 will drop any artifact whose report file already exists and
  re-dispatch only the rest.

To force a full re-run, pass `skip existing = false` (or delete the
`<since-shortSha>..<head-shortSha>/` directory beforehand).

## Example Invocation

User: *"Draft updated ballot notes for `HL7/fhir` since commit
`1a2b3c4`, saving reports to
`C:\ai\git\fhir-augury-content\artifact-notes\`, 3 concurrent
agents."*

The orchestrator should:

1. Health-check services; confirm the cache clone has `1a2b3c4`
   reachable from HEAD; load the FhirCore briefing.
2. `git diff --name-only 1a2b3c4..HEAD`, filter to `source/**`, and
   group by `source/<name>/` folder. Drop `source/datatypes/`
   primitives and any artifact with no in-window changes.
3. Apply defaults (concurrency `3`, working directory
   `temp/artifact-notes/`, skip existing `true`) and present a
   confirmation summary listing every artifact in the batch via
   `ask_user`.
4. After confirmation, ensure the per-window output directory and
   the working directory exist (cross-platform).
5. Loop: dispatch up to 3 background `artifact-notes` sub-agents at
   a time, one per artifact, until the queue drains.
6. Report completion and the final per-artifact status table.

## Performance Notes

- Each artifact sub-agent typically takes **1–4 minutes** depending
  on commit count, ticket count, and diff size. A FhirCore window
  spanning a multi-month tranche of work commonly produces 10–30
  artifacts.
- Concurrency 2–4 is a sane default; the bottleneck is usually the
  per-ticket Jira fetches inside each sub-agent. Raise carefully if
  the host has the headroom and the orchestrator / Jira source remain
  healthy.
- The orchestrator itself is cheap — almost all wall-clock time is
  inside sub-agents.
