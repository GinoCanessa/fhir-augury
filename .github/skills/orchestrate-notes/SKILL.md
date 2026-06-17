---
name: orchestrate-notes
description: "Orchestrates bulk drafting of ballot notes for a GitHub repo, anchored at a since-commit. USE FOR: repo-wide ballot-note refresh after a tranche of ticket work has landed, batch generation across artifacts, narrative pages, and (for `HL7/fhir`) the consolidated datatypes surface. Requires a GitHub repo (e.g., HL7/fhir) and a since-commit SHA. Hydrates the commit window once via the BallotNotes processor (which owns the commit-window walk, unit grouping, and the datatype-page map server-side), polls until hydration completes, enumerates the hydrated units (artifacts / pages / datatypes), and dispatches up to N concurrent authoring sub-agents (`notes-artifact`, `notes-page`, `notes-datatype`) — one per unit slug — that read the unit's evidence and PUT authored prose back to the processor. Optionally emits the self-contained `notes-site` review SPA from the processor-owned DB when the run completes."
---

# Orchestrate Notes Skill

Bulk-drafts ballot notes for a GitHub repository by **hydrating** the
commit window between a caller-supplied **since-commit** and the cached
clone's HEAD in the **BallotNotes processor**, then dispatching one
authoring sub-agent per hydrated **unit**. The processor owns all the
deterministic work — the commit-window walk, the grouping of changed
files into units, ticket attribution, source-file resolution, the
datatype-page map, and capture of each unit's current ballot note. The
orchestrator's job is to kick off hydration, wait for it, enumerate the
units, and fan out authoring sub-agents.

A unit is one of:

- an **artifact** (resource, profile, IG artifact, terminology bundle)
  → handled by the `notes-artifact` skill;
- a **page** (narrative `.html` / `.md` page) → handled by the
  `notes-page` skill;
- the **datatypes** surface in `HL7/fhir` (the consolidated
  `source/datatypes.html` plus any touched per-datatype own-pages such
  as `dosage.html` / `metadatatypes.html`) → handled by the
  `notes-datatype` skill. The processor applies the datatype-page map
  server-side and folds the whole datatypes surface into a single
  `datatypes` DataType unit, so own-page datatypes are never
  double-dispatched as pages.

Each unit is identified by a **slug** (the processor's `noteId`). The
authoring sub-agents read the unit's hydrated evidence
(`GET /api/v1/ballot-notes/{slug}`) and write authored prose back
(`PUT /api/v1/ballot-notes/{slug}/note`); the processor is the
authoritative store.

Unlike the Jira-driven orchestrators (`orchestrate-prep`,
`orchestrate-plan`), the trigger here is a **commit**, not a Jira
queue; there is no `ProcessedLocally` flag to consult.

## Prerequisites

- The **BallotNotes processor** (Aspire resource
  `processor-github-fhir-ballotnotes`, default base URL
  `http://localhost:5174`) must be reachable and running. It owns the
  hydration, unit grouping, datatype-page map, and the notes SQLite DB.
- The `notes-artifact`, `notes-page`, and `notes-datatype` skills must
  be available — they define the per-unit authoring workflow each
  sub-agent runs. The report structure, ticket narrative, ballot-note
  drafting rules, and the read-evidence / write-back contract are
  owned by those skills and must not be replicated here.
  `notes-datatype` is only used when the repo is `HL7/fhir`.
- The **GitHub source clone cache** must contain `<owner>_<name>` with
  the since-commit reachable from HEAD — the processor reads the clone
  to walk the window. Neither the orchestrator nor the processor
  refreshes clones; that is upstream of this skill. If the clone is
  missing or the since-commit is unreachable, the hydrate call returns
  `503` / `400` and the run aborts.
- A current per-repo briefing under
  `cache/github/repos/<owner>_<name>/repo-analysis/briefing.md` is
  recommended for repo context (the `repo-analysis` skill produces it).
  If missing or stale per that skill's rules, consider refreshing it
  before a large run.
- The `fhir-augury-cli` skill is optional here — the BallotNotes
  processor's HTTP API is the integration surface for this skill.

## Inputs

The user must provide or you must determine:

1. **Repo** *(required)* — `owner/name`, e.g., `HL7/fhir`.
2. **Since-commit** *(required)* — full or short SHA. The window is
   `since-commit..HEAD` of the cached clone; the processor walks it.
3. **Processor base URL** *(optional, default `http://localhost:5174`)*
   — the BallotNotes processor's base URL.
4. **Output directory** *(optional)* — where sub-agents may write their
   human-readable markdown reports (the **authoritative** persistence
   is each sub-agent's PUT back to the processor; the markdown reports
   are a convenience). Reports land at
   `<OutputDir>/<owner>_<name>/<slug>.md`. Example:
   `./cache/output/notes/`.
5. **Concurrency** *(optional, default `3`)* — maximum number of
   concurrent sub-agents. Each reads one unit's evidence and authors
   prose; default is conservative.
6. **Filter** *(optional)* — a single glob (case-insensitive) matched
   against the enumerated **unit names** (Step 3). Applies to artifact,
   page, and datatype unit names alike. If omitted, every hydrated unit
   is processed. Examples:
   - `Observation` — only the `Observation` artifact unit.
   - `us-core-*` — every unit whose name starts with `us-core-`.
   - `*` — everything (same as omitting the filter).
7. **Exclude datatypes** *(optional, default `false`)* — when `true`,
   skip every `DataType` unit. Only meaningful when the repo is
   `HL7/fhir`; ignored otherwise.
8. **Repo category** *(optional)* — passed to the hydrate call as
   `repoCategory` when known (e.g., `FhirCore`, `FhirIg`); the
   processor infers it otherwise.
9. **Work group hint** *(optional)* — passed to the hydrate call as
   `workGroupHint` when the caller wants to bias work-group
   attribution.
10. **Working directory** *(optional, default `temp/notes/` relative to
    the repo root)* — scratch space for the orchestrator and each
    sub-agent. Created if it does not already exist.
11. **Skip existing** *(optional, default `true`)* — when `true`, do
    not re-dispatch a unit whose processor `status` is already
    `authored`; only units with `status = awaiting-note` are
    dispatched. Set `false` to re-draft every unit.
12. **Notes DB** *(optional, default `./cache/ballot-notes.db`)* — the
    processor-owned SQLite DB, read by `notes-site report` to emit the
    review SPA. The orchestrator does **not** write to it; the
    processor does.
13. **Emit site** *(optional, default `false`)* — when `true`, run
    `notes-site report` after all sub-agents finish to emit the static
    review SPA (see [Step 11](#step-11-emit-the-notes-site-review-spa-optional)).
14. **Site output directory** *(optional, default `<OutputDir>/site/`)*
    — where `notes-site report` writes the emitted SPA. Only used when
    **Emit site** is `true`.

## Workflow

### Step 1: Verify the processor and inputs

1. Health-check the BallotNotes processor at `{processorBaseUrl}`
   (e.g., `GET /health`, or a cheap `GET /api/v1/ballot-notes?limit=1`).
   Abort if it is unreachable.
2. Apply defaults: processor base URL → `http://localhost:5174`,
   concurrency → `3`, working directory → `temp/notes/`, skip existing
   → `true`, exclude datatypes → `false`, Notes DB →
   `./cache/ballot-notes.db`, emit site → `false`.
3. The clone's existence and the since-commit's reachability are
   validated by the processor **synchronously** when you call hydrate
   (Step 2); you do not pre-check them with `git` here.

### Step 2: Hydrate the commit window

Kick off hydration **once** for the `(repo, since-commit)` window:

```
POST {processorBaseUrl}/api/v1/ballot-notes/hydrate
Content-Type: application/json

{
  "repoOwner": "<owner>",
  "repoName": "<name>",
  "sinceSha": "<since-commit>",
  "repoCategory": "<category, optional>",
  "workGroupHint": "<hint, optional>"
}
```

- `202 Accepted` → `{runKey, status:"running", unitsTotal}`. Capture
  the `runKey`.
- `503` → the clone is missing or `git` is unavailable on the server.
  Abort and ask the user to refresh the clone.
- `400` → a required field is missing. Fix the request and retry.

This call validates the clone + since-commit synchronously, then runs
the commit-window walk, unit grouping, the datatype-page map, ticket
attribution, and current-note capture **fire-and-forget** on the
server. The processor — not this skill — performs the grouping and the
datatype-page map.

Then **poll** until hydration finishes:

```
GET {processorBaseUrl}/api/v1/ballot-notes/hydrate/status?runKey={runKey}
```

(or `GET …/hydrate/status` for the latest run; the `runKey` is a query
parameter because it contains `/`). The response is
`{runKey, status, unitsTotal, unitsHydrated, commitsInWindow,
ticketsAttributed, startedAt, completedAt, error}`. Poll until `status`
is `"completed"` (proceed to Step 3) or `"failed"` (abort and surface
`error`). Surface progress (`unitsHydrated` / `unitsTotal`) to the user
while polling.

### Step 3: Enumerate the hydrated units

List the units the processor produced for the repo:

```
GET {processorBaseUrl}/api/v1/ballot-notes?repo=<owner>/<name>
```

The response is `{total, notes:[{noteId, type, name, repoOwner,
repoName, workGroup, workGroupCode, needsNote, commitsInWindow,
ticketsAttributed, status, hydratedAt, authoredAt, generatedAt}]}`.
Each row is one unit: `noteId` is the unit **slug**, `type` is
`Artifact` / `Page` / `DataType`, and `name` is the unit name (the
artifact / page / datatype-page stem). Page with `limit` / `offset`
if `total` exceeds the returned count.

Apply the selection rules to build the dispatch queue:

- **Filter glob** — when supplied, keep only units whose `name`
  matches (case-insensitive). When omitted, keep all.
- **Exclude datatypes** — when `true`, drop every `type = DataType`
  unit.
- **Skip existing** — when `true`, drop units whose `status` is
  already `authored` (equivalently, request only the work set with
  `…/ballot-notes?repo=<owner>/<name>&status=awaiting-note`).

The processor already performed the commit-window walk, the grouping,
and the datatype-page map, so there is **no client-side bucketing, no
`git diff`, and no datatype own-page computation** here — you only
filter the enumerated list.

Each unit's sub-agent will write its optional markdown report to a
deterministic path:

| Unit type | Report file |
|-----------|-------------|
| Artifact  | `<OutputDir>/<owner>_<name>/<slug>.md` |
| Page      | `<OutputDir>/<owner>_<name>/<slug>.md` |
| DataType  | `<OutputDir>/<owner>_<name>/<slug>.md` |

The slug already encodes the type and name, so a single flat naming
scheme avoids collisions.

If the unit set is empty after filtering, report that and exit
cleanly — there is nothing to draft.

### Step 4: Confirm with the user

Present a one-screen summary and **ask the user to confirm**. Use the
`ask_user` tool with `Yes, start` / `Cancel` choices:

```
About to draft ballot notes:

  Repo              : HL7/fhir (FhirCore)
  Since-commit      : 1a2b3c4d5e6f
  Processor         : http://localhost:5174
  Hydration         : completed (14 units, 28 commits, 19 tickets)
  Output directory  : ./cache/output/notes/HL7_fhir/
  Working dir       : temp/notes/   (relative to repo root)
  Filter            : (all)
  Exclude datatypes : false
  Skip existing     : true   (skips units already 'authored')
  Concurrency       : 3
  Units to draft    : 12 of 14   (2 already authored, skipped)
    Artifacts (10):
      • Observation   [hl7-fhir-artifact-observation]  (8 commits, awaiting-note)
      • Patient       [hl7-fhir-artifact-patient]       (3 commits, awaiting-note)
      …
    Pages (1):
      • security      [hl7-fhir-page-security]          (4 commits, awaiting-note)
    Datatypes (1):
      • datatypes     [hl7-fhir-datatype-datatypes]     (5 commits, awaiting-note)

Proceed?
```

Show **every** unit in the planned batch — the user often spots a
misrouted unit at this stage. Do not proceed on anything except
explicit confirmation.

### Step 5: Create directories (cross-platform)

Both the **output directory** (`<OutputDir>/<owner>_<name>/`) and the
**working directory** must exist before dispatching sub-agents. Use a
method that works on Windows (PowerShell) and Unix (bash):

- **Tool-based** (preferred when available): use the agent's
  file-system tool.
- **Shell-based**:
  - PowerShell: `New-Item -ItemType Directory -Path $Path -Force | Out-Null`
  - bash/sh: `mkdir -p "$Path"`

### Step 6: In-memory tracking

Maintain in memory:

- `pending` — queue of units not yet dispatched. Each entry carries
  `{slug, type, name, outputFile}` where `type` is `Artifact` /
  `Page` / `DataType`.
- `inFlight` — set of units currently assigned to a running
  sub-agent.
- `completed` / `failed` counters and a per-unit result map for the
  final summary.

The processor is the durable store. Re-running with the same inputs
and `skip existing = true` is the natural resume mechanism — units the
processor already marks `authored` are dropped in Step 3.

### Step 7: Dispatch loop

Loop until `pending` is empty AND `inFlight` is empty:

1. While `len(inFlight) < concurrency` and `pending` is non-empty:
   1. Pop the next unit off `pending`.
   2. Dispatch a sub-agent (Step 8). Add the unit to `inFlight`.
2. Wait for the next sub-agent completion (Step 9) before continuing
   the outer loop.

Order is not significant, but a sensible default is to dispatch any
`DataType` units early — the consolidated `datatypes` unit tends to be
the slowest and benefits from parallel headroom while artifact units
run.

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

Every sub-agent prompt passes the unit **slug** (`noteId`) and the
**processor base URL**; the per-unit skill GETs the unit's hydrated
evidence from the processor and PUTs its authored prose back. The
orchestrator never writes to the notes DB — the processor owns
persistence, so a re-dispatch simply re-PUTs (idempotent).

#### Artifact prompt

````
Run the `notes-artifact` skill for the following artifact.

## Inputs

- **Processor:** {PROCESSOR_URL}
- **Slug:** {SLUG}
- **Artifact:** {NAME}
- **Output file:** {OUTPUT_DIR}/{OWNER}_{NAME}/{SLUG}.md
- **Working directory:** {WORKING_DIR}/artifact_{NAME}/

## Instructions

1. Follow the `notes-artifact` skill exactly: GET the unit's hydrated
   evidence from `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}`, author
   the ballot-note prose + roll-up, and PUT it back to
   `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}/note`.
2. Use the supplied **Working directory** for any transient files.
3. Save the human-readable markdown report to the output file path.
4. When finished, confirm success and state the full path of the
   saved file.
````

#### Page prompt

````
Run the `notes-page` skill for the following page.

## Inputs

- **Processor:** {PROCESSOR_URL}
- **Slug:** {SLUG}
- **Page:** {NAME}
- **Output file:** {OUTPUT_DIR}/{OWNER}_{NAME}/{SLUG}.md
- **Working directory:** {WORKING_DIR}/page_{NAME}/

## Instructions

1. Follow the `notes-page` skill exactly: GET the unit's hydrated
   evidence from `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}`, author
   the ballot-note prose + roll-up, and PUT it back to
   `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}/note`.
2. Use the supplied **Working directory** for any transient files.
3. Save the human-readable markdown report to the output file path.
4. When finished, confirm success and state the full path of the
   saved file.
````

#### Datatypes prompt (HL7/fhir only)

````
Run the `notes-datatype` skill for the FHIR datatypes surface.

## Inputs

- **Processor:** {PROCESSOR_URL}
- **Slug:** {SLUG}
- **Output file:** {OUTPUT_DIR}/HL7_fhir/{SLUG}.md
- **Working directory:** {WORKING_DIR}/datatypes/

## Instructions

1. Follow the `notes-datatype` skill exactly: GET the datatypes unit's
   hydrated evidence from `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}`,
   author the ballot-note prose for the datatypes surface, and PUT it
   back to `{PROCESSOR_URL}/api/v1/ballot-notes/{SLUG}/note`.
2. Use the supplied **Working directory** for any transient files.
3. Save the human-readable markdown report to the output file path.
4. When finished, confirm success and state the full path of the
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
  automatically in the same run. The unit's processor `status` stays
  `awaiting-note`, so a subsequent re-run with `skip existing = true`
  picks it up naturally.
- **Processor unreachable mid-run** — pause new dispatches, wait for
  in-flight agents to complete, surface the issue to the user before
  resuming.
- **Empty unit set** — if Step 3 returns no units after filtering,
  report that and exit; do not present a confirmation prompt.

### Step 11: Emit the notes-site review SPA (optional)

After the dispatch loop drains (every sub-agent has completed or
failed) **and** **Emit site** is `true`, emit the static review SPA
from the processor-owned DB the sub-agents just populated:

```bash
notes-site report --db {NOTES_DB} --out {SITE_OUTPUT_DIR} --title "{TITLE}" --force
```

(or `dotnet run --project tools/notes-site -- report …` when the tool
is not on `PATH`). Default `{SITE_OUTPUT_DIR}` to `<OutputDir>/site/`
and `{TITLE}` to something descriptive (e.g.,
`"Ballot Notes — <owner>/<name> <since-short>..<head-short>"`).

Run this **once**, after all sub-agents finish — `notes-site report`
overwrites its output directory wholesale and renders whatever rows
exist in the DB at that moment. Surface the emitted `index.html` path
in the final summary. If `notes-site report` fails (e.g., the DB has
no rows because every sub-agent failed), report the failure but treat
the per-unit markdown reports as the primary deliverable. Skip this
step entirely when **Emit site** is `false`.

### Step 12: Progress and final summary

Report to the user:

- After each completion: completed / failed / in-flight counts; the
  unit just finished (with its kind) and its output path. For the
  `datatypes` unit, surface the list of target pages drafted in the
  report (parsed from the report header table's `Pages targeted`
  row, or stated as "see report" if parsing fails).
- When a **Notes DB** was supplied: the emitted `notes-site` SPA path
  (from Step 11).
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

1. Health-check the BallotNotes processor at `http://localhost:5174`.
   (The clone + since-commit are validated by the processor when
   hydrate is called.)
2. `POST …/api/v1/ballot-notes/hydrate` for `HL7/fhir` since
   `1a2b3c4`, then poll `…/hydrate/status?runKey={runKey}` until
   `completed`.
   The processor walks the window and groups the changes into units
   (artifacts per `source/<name>/`, pages per top-level
   `source/<page>.html`, and the consolidated `datatypes` unit) using
   its server-side datatype-page map.
3. `GET …/api/v1/ballot-notes?repo=HL7/fhir` to enumerate the units,
   apply defaults (concurrency `3`, working directory `temp/notes/`,
   skip existing `true`, exclude datatypes `false`), and present a
   confirmation summary listing every unit in the batch via
   `ask_user`.
4. After confirmation, ensure the output directory and the working
   directory exist (cross-platform).
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
