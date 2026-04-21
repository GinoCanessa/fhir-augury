---
name: orchestrate-prep
description: "Orchestrates bulk ticket preparation directly from the Jira source. USE FOR: batch ticket prep, bulk preparation, round-robin ticket prep across work groups. Queries the Jira source for work groups and randomly draws unprocessed tickets per WG, dispatches up to N concurrent ticket-prep sub-agents, saves reports to a structured output directory, and reports completion back to the Jira source via the local-processing flag."
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
   cleaned work-group subfolder. Example:
   `C:\ai\git\fhir-augury-content\prepared\`
2. **Concurrency** — maximum number of concurrent sub-agents (default: 4).
3. **Ordering strategy** — how to draw the next ticket. Default is
   **round-robin** (one ticket per work group per round). Alternative is
   **sequential** (drain one work group's unprocessed tickets before moving
   to the next).
4. **Work group filter** *(optional)* — explicit list of work-group names to
   include. If omitted, all work groups with at least one unprocessed ticket
   are considered.
5. **Additional Jira filters** *(optional)* — any subset of the
   `JiraLocalProcessingFilter` shape (e.g., `statuses`, `priorities`,
   `types`, `specifications`, `labels`, `projects`). These are forwarded
   verbatim to the Jira source on every random-draw call.
6. **Max tickets** *(optional)* — overall cap on tickets to process this
   run. If omitted, run until every selected work group is exhausted.

## Work-Group Name Cleaning

Output subdirectories use "cleaned" work-group names:

1. Replace the ampersand (`&`) with "and".
2. Replace all non-alphanumeric, non-space characters (`/`, `-`, `'`, …) with spaces
3. Split into words
4. Capitalize each word
5. Join without separators

| Work Group Name | Cleaned Name |
|-----------------|--------------|
| Orders & Observations | OrdersAndObservations |
| Biomedical Research & Regulation | BiomedicalResearchAndRegulation |
| Community-Based Care and Privacy | CommunityBasedCareAndPrivacy |
| Payer/Provider Information Exchange | PayerProviderInformationExchange |
| FHIR Infrastructure | FhirInfrastructure |
| HL7 Australia AU Core | Hl7AustraliaAuCore |

## Jira-source operations used

All operations are reached via the `fhir-augury-cli` skill. Prefer the named
convenience commands; use `call` for the local-processing endpoints (which
are not exposed as top-level CLI commands).

| Purpose | CLI form |
|---------|----------|
| Health check | `{"command":"services","action":"health"}` |
| List work groups (with issue counts) | `{"command":"list-jira-dimension","dimension":"workgroups"}` |
| Draw a random unprocessed ticket for a WG | `{"command":"call","source":"jira","operation":"local-processing.get-random-ticket","body":{...filter...}}` |
| Mark a ticket processed locally | `{"command":"call","source":"jira","operation":"local-processing.set-processed","body":{"key":"FHIR-XXXXX","processedLocally":true}}` |
| Inspect/clear processed flags (admin) | `local-processing.get-tickets` / `local-processing.clear-all-processed` |

Note on operation IDs: the resolver matches `Jira.local-processing.<name>`
first, then falls back to `local-processing.<name>`. Either form works; the
unqualified form above is the recommended default.

### Random-draw filter shape

Build the body for `local-processing.get-random-ticket` per
`JiraLocalProcessingFilter` (in
`src/FhirAugury.Source.Jira/Api/JiraContracts.cs`). For round-robin pulls,
constrain to one work group at a time and require unprocessed:

```json
{
  "workGroups": ["Orders & Observations"],
  "processedLocally": false,
  "statuses": ["Triaged"],
  "priorities": [],
  "types": [],
  "specifications": [],
  "labels": [],
  "projects": [],
  "reporters": [],
  "changeCategories": [],
  "changeImpacts": [],
  "relatedArtifacts": []
}
```

Only include the fields you actually want to filter on; omit empty arrays
when convenient. Merge any user-supplied `Additional Jira filters` into this
body before each draw.

A successful draw returns a `JiraIssueSummaryEntry` (`key`, `workGroup`,
`title`, `status`, …). A 404 means there are no more unprocessed tickets
matching the filter — treat that work group as **exhausted** for this run.

## Workflow

### Step 1: Verify services and discover work groups

1. Health-check the orchestrator and Jira source via the CLI's `services`
   command. Abort with a clear message if either is down.
2. Fetch the work-group list with `list-jira-dimension --dimension
   workgroups`. The response carries `name` and `issueCount` per WG.
3. If the user supplied a work-group filter, intersect it with the returned
   list and warn about any names that didn't match.
4. Build the **active set** of work groups: those that pass the filter and
   have `issueCount > 0`. (The Jira-side count is total, not unprocessed —
   exhaustion is determined dynamically as draws return 404.)

### Step 2: Create output directories

For each active work group, create the cleaned-name subdirectory once:

```powershell
New-Item -ItemType Directory -Path "$OutputDir\$CleanedGroupName" -Force
```

### Step 3: In-flight tracking

Maintain in memory:

- `inFlight` — set of ticket keys currently assigned to a running sub-agent.
- `inFlightByWg` — count of in-flight tickets per work group (round-robin
  uses this to honor "one in-flight per WG per round").
- `exhaustedWgs` — set of work groups whose last draw returned 404.
- `completed` / `failed` counters for progress reporting.

Persist nothing locally; the Jira source's `ProcessedLocally` flag is the
durable state.

### Step 4: Round-robin draw and dispatch

Loop until either the in-flight set is empty AND every active WG is
exhausted, or the optional max-tickets cap is reached:

1. While `len(inFlight) < concurrency` and the round still has eligible WGs:
   1. Pick the next eligible work group (round-robin order, skipping
      exhausted WGs and — for round-robin mode — WGs that already have an
      in-flight ticket this round).
   2. Build the random-draw filter (WG + `processedLocally=false` + any
      user filters) and call `local-processing.get-random-ticket`.
   3. Handle the response:
      - **404 / empty** → mark the WG exhausted; skip.
      - **Ticket already in `inFlight`** → retry the draw up to **5 times**
        for this WG this round. If still colliding, skip the WG for this
        round (it will be retried in the next round). The collision case is
        rare but possible because `RANDOM()` is unaware of in-flight state.
      - **Fresh ticket** → add to `inFlight`, dispatch a sub-agent (Step 5).
2. Wait for the next sub-agent completion (Step 6) before continuing the
   outer loop.
3. When every active WG has been visited (or skipped) for the round, start
   the next round.

For **sequential** mode, the eligibility rule changes: keep drawing from the
current WG until it is exhausted, then move to the next WG. The in-flight
deduplication and 5-retry-on-collision rule still apply.

### Step 5: Dispatch a sub-agent

For each fresh ticket, launch a **general-purpose background agent** that
runs the `ticket-prep` skill. Use the same model as the orchestrator.

The sub-agent prompt:

````
Run the `ticket-prep` skill for the following ticket.

## Inputs

- **Ticket key:** {TICKET_KEY}
- **Work group:** {WORK_GROUP}
- **Output file:** {OUTPUT_DIR}\{CLEAN_GROUP}\{TICKET_KEY}.md
- **CLI path (if needed):** {CLI_PATH}

## Instructions

1. Follow the `ticket-prep` skill exactly, including all data-gathering
   steps, the repo-analysis briefing dependency, and the report format.
2. Save the completed report to the output file path above.
3. When finished, confirm success and state the full path of the saved file.
````

### Step 6: Handle completion and report back to Jira

When a sub-agent completes:

1. **Read the agent result** to confirm success and that the report file
   exists at the expected path.
2. **Mark the ticket processed locally** by calling
   `local-processing.set-processed` with `processedLocally: true`:

   ```bash
   fhir-augury-cli --json '{"command":"call","source":"jira","operation":"local-processing.set-processed","body":{"key":"FHIR-XXXXX","processedLocally":true}}'
   ```

   Only mark on **success**. On failure (Step 7), leave the flag unset so
   the ticket remains eligible for a future run.
3. **Remove the ticket from `inFlight`** and decrement `inFlightByWg`.
4. **Loop back to Step 4** to draw and dispatch the next ticket.

### Step 7: Error handling

- **Sub-agent failure** — log the ticket key and error; do **not** mark the
  ticket processed; remove it from `inFlight`; continue with the next
  draw. Do not retry the same ticket automatically in the same run (it can
  be re-drawn naturally on a future run).
- **Random-draw 404** — work-group is exhausted for the current filter set;
  mark exhausted and continue.
- **Random-draw collision with `inFlight`** — retry up to 5 times for the
  same WG; if still colliding, skip the WG for this round.
- **CLI unavailable** — fall back per the `fhir-augury-cli` skill's
  fallback order (MCP, then direct HTTP). Record which path was used in
  progress reports.
- **Service unhealthy mid-run** — pause new draws, wait for in-flight
  agents to complete, then surface the issue to the user before resuming.

### Step 8: Progress reporting

After each batch of completions, report to the user:

- Completed / failed / in-flight counts
- Per-WG completion totals so far this run
- Which WGs are exhausted vs. still active
- Any tickets currently in flight (key + WG)

## Resumability

There is **no local persistent state**. Resume is automatic:

- Already-processed tickets have `ProcessedLocallyAt` set in the Jira
  source and will not be drawn again.
- Re-invoking the skill with the same inputs simply continues from
  whatever the Jira source currently considers unprocessed.

If you need to clear processed flags (e.g., to re-run a batch from
scratch), use `local-processing.clear-all-processed` — but be aware that
this clears the flag for **every** ticket, not just the ones in your
current scope.

## Example Invocation

User: *"Prepare unprocessed tickets, saving reports to
C:\ai\git\fhir-augury-content\prepared\, round-robin, 4 concurrent agents."*

The orchestrator should:

1. Health-check services; list work groups via `list-jira-dimension`.
2. Create one cleaned-name subdirectory per active WG.
3. Round-robin draw a random unprocessed ticket per WG via
   `local-processing.get-random-ticket` (with `processedLocally:false`).
4. Maintain up to 4 concurrent `ticket-prep` sub-agents; on each success,
   call `local-processing.set-processed` with `processedLocally:true`.
5. Continue until every active WG returns 404 (or the optional max-tickets
   cap is reached); report progress as agents complete.

## Performance Notes

- Each ticket typically takes ~60–200 seconds depending on cross-reference
  density and Zulip search results.
- With 4 concurrent agents, expect ~1–2 tickets/minute throughput.
- The Jira source handles concurrent CLI requests well; the
  random-with-collision-retry pattern keeps duplicate dispatches negligible
  in practice.
