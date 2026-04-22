---
name: orchestrate-prep
description: "Orchestrates bulk ticket preparation directly from the Jira source. USE FOR: batch ticket prep, bulk preparation, random ticket prep across (or within) work groups. Queries the Jira source for work groups, randomly draws unprocessed tickets matching the configured filters, dispatches up to N concurrent ticket-prep sub-agents, saves reports to a structured output directory, and reports completion back to the Jira source via the local-processing flag."
---

# Orchestrate Prep Skill

Bulk-prepares FHIR Jira tickets for workgroup review by drawing unprocessed
tickets directly from the Jira source, dispatching concurrent `ticket-prep`
sub-agents, and reporting completion back to the source. There is **no
markdown worklist** — the Jira source's `ProcessedLocally` flag is the sole
persistent state.

## Prerequisites

- The `fhir-augury-cli` skill must be available — it is the canonical entry
  point for talking to the Jira source. Follow its fallback order (CLI →
  MCP → direct HTTP) for every data interaction.
- The `ticket-prep` skill must be available — it defines the per-ticket
  workflow and report format that each sub-agent runs. The report
  structure, all data-access steps, the `repo-analysis` briefing
  dependency, and the disposition rules are all owned by `ticket-prep`
  and must not be replicated here.
- The orchestrator service and Jira source service must be reachable. Verify
  with the CLI's `services` health check before starting.

## Inputs

The user must provide or you must determine:

1. **Output directory** — where prepared reports are saved, organized by
   work-group `nameClean` subfolder. Example:
   `./cache/output/prepared/`.
2. **Concurrency** *(optional, default `4`)* — maximum number of concurrent
   sub-agents.
3. **Work group** *(optional)* — a single work group to restrict the run to.
   May be supplied as the work group's `code`, `name`, or `nameClean` (case
   insensitive). The orchestrator resolves it against the work-group list
   returned by `list-jira-workgroups`. If omitted,
   tickets are drawn at random across **all** work groups (no per-WG
   round-robin — the Jira source's random draw handles distribution).
4. **Statuses** *(optional, default `["Triaged"]`)* — list of Jira statuses
   to consider unprocessed candidates from. Common alternatives:
   `["Submitted"]`, `["Submitted","Triaged"]`.
5. **Projects** *(optional, default `["FHIR"]`)* — list of Jira project keys
   to draw from. Common alternatives: `["UTG"]`, `["FHIR","UTG"]`.
6. **Additional Jira filters** *(optional)* — any subset of the
   `JiraLocalProcessingFilter` shape (e.g., `priorities`, `types`,
   `specifications`, `labels`, `reporters`, `changeCategories`,
   `changeImpacts`, `relatedArtifacts`). These are forwarded verbatim on
   every random-draw call.
7. **Max tickets** *(optional)* — overall cap on tickets to process this
   run. If omitted, run until the random draw returns 404 (no more
   unprocessed tickets matching the filter).
8. **Working directory** *(optional, default `temp/prep/` relative to the
   repo root)* — directory the orchestrator and each sub-agent may use for
   transient files. Created if it does not already exist. Must be writable
   on the current platform.

## Work-Group Names

Work-group records returned by `list-jira-workgroups`
include `code`, `name`, and `nameClean` directly. **Use `nameClean`
verbatim** for output subdirectory names — do not derive it locally.

> Note: the API surface is in the process of being updated to expose
> `code` and `nameClean` alongside `name`. They are present in the JSON
> payload even if the typed contract has not been regenerated yet. Read
> them straight from the JSON.

If a work group is supplied as `code`, match it against the `code` field;
if supplied as `name` or `nameClean`, match against those fields (case
insensitive). Reject the run with a clear error if no match is found.

## Jira-source operations used

All operations are reached via the `fhir-augury-cli` skill. Prefer the typed
`jira-local-processing` family (added in the 2026-04 sync); the generic
`call` form is documented as a fallback only.

| Purpose | CLI form (primary, typed) |
|---------|---------------------------|
| Health check | `{"command":"services","action":"health"}` |
| List work groups (with `code`/`name`/`nameClean`/`issueCount`) | `{"command":"list-jira-workgroups"}` |
| Draw a random unprocessed ticket | `{"command":"jira-local-processing","action":"random-ticket","body":{...filter...}}` |
| Mark a ticket processed locally | `{"command":"jira-local-processing","action":"set-processed","body":{"key":"FHIR-XXXXX","processedLocally":true}}` |
| Inspect processed flags (admin) | `{"command":"jira-local-processing","action":"tickets","body":{...filter...}}` |
| Clear all processed flags (admin) | `{"command":"jira-local-processing","action":"clear-all-processed"}` |

Valid `action` values for `jira-local-processing` (per
`src/FhirAugury.Cli/Dispatch/Handlers/JiraLocalProcessingHandler.cs`):
`tickets`, `random-ticket`, `set-processed`, `clear-all-processed`.

Fallback (older `call` form, still works): the resolver matches
`Jira.local-processing.<name>` first, then falls back to
`local-processing.<name>` — e.g.,
`{"command":"call","source":"jira","operation":"local-processing.get-random-ticket","body":{...}}`.
Use only when running against a build that predates the typed family.

### Pre-run ingestion (optional)

If the bulk run needs fresh Jira data for a single project, scope an
ingest with the `jiraProject` parameter before starting:

```bash
fhir-augury-cli --json '{"command":"ingest","action":"trigger","sources":["jira"],"jiraProject":"FHIR"}'
```

Only the Jira leg of the fan-out receives the parameter; other sources
ingest normally. Omit `jiraProject` to ingest every configured project.

### Random-draw filter shape

Build the body for `local-processing.get-random-ticket` per
`JiraLocalProcessingFilter` (in
`src/FhirAugury.Source.Jira/Api/JiraContracts.cs`). The orchestrator builds
one filter per draw using the configured inputs:

```json
{
  "workGroups": ["Orders & Observations"],
  "processedLocally": false,
  "statuses": ["Triaged"],
  "projects": ["FHIR"]
}
```

- Always include `"processedLocally": false`.
- Include `workGroups` only when a single work group was supplied.
- Always include `statuses` and `projects` (defaulted if not supplied).
- Merge any **Additional Jira filters** into this body before each draw.

A successful draw returns a `JiraIssueSummaryEntry` (`key`, `workGroup`,
`title`, `status`, …). A 404 means no unprocessed tickets remain matching
the filter — terminate draws (the run is **exhausted**).

## Workflow

### Step 1: Verify services and resolve inputs

1. Health-check the orchestrator and Jira source via the CLI's `services`
   command. Abort with a clear message if either is down.
2. Fetch the work-group list with `list-jira-workgroups`. Read `code`,
   `name`, `nameClean`, `issueCount` from the JSON response — the CLI
   surfaces `nameClean` directly; do not re-derive it from `name`.
3. If the user supplied a **Work group**, resolve it against `code` /
   `name` / `nameClean` (case insensitive). Abort if no match.
4. Apply defaults: statuses → `["Triaged"]`, projects → `["FHIR"]`,
   concurrency → `4`, working directory → `temp/prep/` relative to the
   repo root.

### Step 2: Confirm with the user

Before any draws or directory creation, present a one-screen summary and
**ask the user to confirm**. Use the `ask_user` tool. Include every
resolved input so the user can verify defaults haven't masked a typo:

```
About to start bulk prep with the following configuration:

  Output directory : C:\ai\git\fhir-augury-content\prepared\
  Working directory: temp/prep/   (relative to repo root)
  Work group       : (all)        — or: "Orders & Observations" (nameClean: OrdersAndObservations)
  Statuses         : Triaged
  Projects         : FHIR
  Other filters    : (none)       — or list any additional filters
  Concurrency      : 4
  Max tickets      : (no cap)     — or: 50

Proceed?
```

Choices: `Yes, start`, `Cancel`. Do not proceed on anything except
explicit confirmation.

### Step 3: Create directories (cross-platform)

Both the **output directory** and the **working directory** must exist
before dispatching sub-agents. Create them idempotently using a method
that works on both Windows (PowerShell) and Unix (bash). Two acceptable
patterns:

- **Tool-based** (preferred when available): use the agent's file-system
  tool to create directories — it abstracts the platform.
- **Shell-based**: detect the shell and run the appropriate command:
  - PowerShell: `New-Item -ItemType Directory -Path $Path -Force | Out-Null`
  - bash/sh: `mkdir -p "$Path"`

If a single work group was supplied, also create
`<OutputDir>/<nameClean>/` for that group. Otherwise, create each
work-group subdirectory **lazily** as tickets for that group are returned
by the random draw (avoids creating empty directories for inactive WGs).

The working directory should be passed through to each sub-agent so all
transient files land in the same controlled location.

### Step 4: In-flight tracking

Maintain in memory:

- `inFlight` — set of ticket keys currently assigned to a running
  sub-agent. This is the **only** tracking required, and exists solely to
  prevent assigning the same ticket to two sub-agents (the random draw is
  unaware of in-flight state).
- `completed` / `failed` counters for progress reporting.

Persist nothing locally; the Jira source's `ProcessedLocally` flag is the
durable state.

### Step 5: Draw and dispatch loop

Loop until the in-flight set is empty AND the last draw returned 404, or
the optional max-tickets cap is reached:

1. While `len(inFlight) < concurrency` and the run is not exhausted:
   1. Build the random-draw filter (work group if supplied,
      `processedLocally:false`, statuses, projects, any user filters)
      and call `local-processing.get-random-ticket`.
   2. Handle the response:
      - **404 / empty** → mark the run **exhausted**; stop drawing.
        Wait for in-flight agents to drain before exiting.
      - **Ticket already in `inFlight`** → retry the draw up to **5
        times**. If still colliding, pause new draws until the next
        completion frees space. (Collisions are rare but possible since
        `RANDOM()` does not see in-flight state.)
      - **Fresh ticket** → add to `inFlight`, ensure the
        `<OutputDir>/<nameClean>/` directory exists for the ticket's
        work group, then dispatch a sub-agent (Step 6).
2. Wait for the next sub-agent completion (Step 7) before continuing the
   outer loop.

### Step 6: Dispatch a sub-agent

For each fresh ticket, launch a **general-purpose background agent** that
runs the `ticket-prep` skill. Use the same model as the orchestrator.

The sub-agent prompt:

````
Run the `ticket-prep` skill for the following ticket.

## Inputs

- **Ticket key:** {TICKET_KEY}
- **Work group:** {WORK_GROUP}
- **Output file:** {OUTPUT_DIR}/{NAME_CLEAN}/{TICKET_KEY}.md
- **Working directory:** {WORKING_DIR}
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `ticket-prep` skill exactly, including all data-gathering
   steps, the repo-analysis briefing dependency, and the report format.
2. Use the supplied **Working directory** for any transient files.
3. Save the completed report to the output file path above.
4. When finished, confirm success and state the full path of the saved
   file.
````

Use forward slashes in paths inside the prompt; both PowerShell and bash
accept them, and the sub-agent can normalize as needed.

### Step 7: Handle completion and report back to Jira

When a sub-agent completes:

1. **Read the agent result** to confirm success and that the report file
   exists at the expected path.
2. **Mark the ticket processed locally** by calling
   `jira-local-processing` with `action: "set-processed"` and
   `processedLocally: true`:

   ```bash
   fhir-augury-cli --json '{"command":"jira-local-processing","action":"set-processed","body":{"key":"FHIR-XXXXX","processedLocally":true}}'
   ```

   Only mark on **success**. On failure (Step 8), leave the flag unset so
   the ticket remains eligible for a future run.
3. **Remove the ticket from `inFlight`**.
4. **Loop back to Step 5** to draw and dispatch the next ticket.

### Step 8: Error handling

- **Sub-agent failure** — log the ticket key and error; do **not** mark
  the ticket processed; remove it from `inFlight`; continue with the next
  draw. Do not retry the same ticket automatically in the same run (it
  can be re-drawn naturally on a future run).
- **Random-draw 404** — no more unprocessed tickets match the filter;
  mark the run exhausted and drain remaining in-flight agents.
- **Random-draw collision with `inFlight`** — retry up to 5 times; if
  still colliding, pause new draws until the next completion.
- **CLI unavailable** — fall back per the `fhir-augury-cli` skill's
  fallback order (MCP, then direct HTTP). Record which path was used in
  progress reports.
- **Service unhealthy mid-run** — pause new draws, wait for in-flight
  agents to complete, then surface the issue to the user before resuming.

### Step 9: Progress reporting

After each batch of completions, report to the user:

- Completed / failed / in-flight counts.
- Per-WG completion totals so far this run (derived from completed
  tickets' `workGroup` field).
- Any tickets currently in flight (key + WG).
- Whether the run has been marked exhausted (no more draws will occur).

## Resumability

There is **no local persistent state**. Resume is automatic:

- Already-processed tickets have `ProcessedLocallyAt` set in the Jira
  source and will not be drawn again.
- Re-invoking the skill with the same inputs simply continues from
  whatever the Jira source currently considers unprocessed.

If you need to clear processed flags (e.g., to re-run a batch from
scratch), use
`{"command":"jira-local-processing","action":"clear-all-processed"}` —
but be aware that this clears the flag for **every** ticket, not just
the ones in your current scope.

## Example Invocation

User: *"Prepare unprocessed tickets, saving reports to
`./cache/output/prepared/`, 4 concurrent agents."*

The orchestrator should:

1. Health-check services; list work groups via `list-jira-workgroups`
   and read `code`/`name`/`nameClean` from the JSON.
2. Apply defaults (statuses=`Triaged`, projects=`FHIR`, working
   directory=`temp/prep/`) and present a confirmation summary via
   `ask_user`.
3. After confirmation, ensure the output and working directories exist
   (cross-platform).
4. Loop: draw a random unprocessed ticket via
   `jira-local-processing action=random-ticket` (with
   `processedLocally:false`, the configured statuses/projects, and any
   user filters), keeping at most 4 sub-agents in flight; dedupe via the
   `inFlight` set.
5. On each sub-agent success, call `jira-local-processing
   action=set-processed` with `processedLocally:true`.
6. Continue until the random draw returns 404 (or the optional
   max-tickets cap is reached); report progress as agents complete.

## Performance Notes

- Each ticket typically takes ~60–200 seconds depending on cross-reference
  density and Zulip search results.
- With 4 concurrent agents, expect ~1–2 tickets/minute throughput.
- The Jira source handles concurrent CLI requests well; the
  random-with-collision-retry pattern keeps duplicate dispatches negligible
  in practice.
