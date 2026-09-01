# Processors runbook

Operator-facing guide for **kicking off a processing run** on each of the four
FHIR Augury processors. Every processor section leads with the **raw HTTP
trigger** (curl-able), then covers prerequisites, how to monitor a run, where the
output lands, and finally the `orchestrate-*` agent skill that wraps it for bulk
runs.

> Audience: a developer/operator running the stack locally (via the Aspire
> AppHost or standalone) who has a processor up and listening but needs to know
> the trigger. For config reference see [configuration.md](../configuration.md);
> for the project layout see [project-structure.md](project-structure.md); for
> the overall design see [architecture.md](architecture.md).

## Overview

A **source service** (`source-jira`, `source-zulip`, `source-confluence`,
`source-github`, `source-fhir`) ingests and serves upstream data. A
**processor** consumes that data and produces derived artifacts (prepared
tickets, implementation plans, applied commits, ballot notes). There are four:

| Processor | Project | Port | Role |
|-----------|---------|------|------|
| Preparer | `FhirAugury.Processor.Jira.Fhir.Preparer` | 5171 | Queues triaged Jira tickets, runs `ticket-prep`, persists prepared output |
| Planner | `FhirAugury.Processor.Jira.Fhir.Planner` | 5172 | Queues resolved change-required tickets, runs `ticket-plan`, persists plans |
| Applier | `FhirAugury.Processor.Jira.Fhir.Applier` | 5173 | Auto-discovers completed plans, applies each in a git worktree, push on demand |
| BallotNotes | `FhirAugury.Processor.GitHub.Fhir.BallotNotes` | 5174 | Hydrates ballot-note evidence for a repo + since-commit window |

The Jira processors form a chain — **Preparer → Planner → Applier** — while
**BallotNotes** is standalone (GitHub-source driven). There are two ways to drive
any of them:

1. **Raw HTTP trigger** (this page leads with these) — the primitive an operator
   or an automation hits directly.
2. **`orchestrate-*` agent skills** — higher-level bulk wrappers
   (`orchestrate-prep`, `orchestrate-plan`, `orchestrate-notes`). There is **no**
   `orchestrate-applier` skill.

All four processors are registered in the AppHost with `WithExplicitStart()`, so
an operator must **start the resource first** — click "Start" on the resource in
the Aspire dashboard, or run the project standalone (`dotnet run`) — before any
trigger below will reach a listening service.

### Lifecycle vs. trigger

The three Jira processors share a uniform processing lifecycle and queue model:

- `StartProcessingOnStartup` defaults to **`true`**, so a *started* processor
  begins processing on boot.
- `POST /processing/start` and `POST /processing/stop` toggle processing at
  runtime.
- A background sync worker feeds the local queue automatically on the
  `SyncSchedule` cadence; the single-ticket HTTP enqueue endpoint is an **ad-hoc**
  path, not the bulk mechanism.

BallotNotes does **not** use this lifecycle model — it is purely on-demand: each
`POST .../hydrate` validates and runs one commit-window hydration.

Every lifecycle route below is mapped at both the bare path and an `/api/v1`
prefix (e.g. `/processing/start` **and** `/api/v1/processing/start`).

---

## Preparer (`processor-jira-fhir-preparer`, :5171)

**Purpose:** Queue triaged/submitted Jira FHIR tickets, run the `ticket-prep`
agent, and persist structured prepared-ticket output for the `ticket-site`
renderer.

**Prerequisites:** `source-jira` (:5160) and `orchestrator` (:5150) reachable
(the AppHost `WaitFor`s both). The resource must be started.

**Kick-off:**

Because `StartProcessingOnStartup` defaults to `true` and a
`JiraTicketSyncWorker` feeds the queue from the Jira source on the `SyncSchedule`
cadence (`00:01:00` in the shipped `appsettings.json`), a started Preparer begins
working automatically — no trigger call is required for the bulk path. To control
processing explicitly:

```bash
# Begin (or resume) processing
curl -X POST http://localhost:5171/processing/start

# Pause processing (in-flight items drain)
curl -X POST http://localhost:5171/processing/stop
```

To enqueue or reset a **single** ticket ad hoc (not the bulk path):

```bash
curl -X POST http://localhost:5171/processing/tickets/FHIR-12345
```

For a **bulk** run driven by an agent, use the `orchestrate-prep` skill. Note:
that skill draws tickets from the **Jira source** (via the `jira-local-processing`
surface) and dispatches `ticket-prep` agents itself — it does **not** route
through this processor's `/processing` queue.

**Monitor:**

```bash
curl http://localhost:5171/status            # running/paused, SyncSchedule, StartProcessingOnStartup
curl http://localhost:5171/processing/queue  # processed / remaining / in-flight / error counts
```

**Output:** `./data/processor.jira.fhir.preparer.db` → rendered by the
`ticket-site` tool.

**Config:** Detailed reference lives in the project's `appsettings.json` under
the `Processing` section. Key defaults: DB path
`./data/processor.jira.fhir.preparer.db`, `SyncSchedule` `00:01:00`,
`MaxConcurrentProcessingThreads` `8`, `StartProcessingOnStartup` `true`.

---

## Planner (`processor-jira-fhir-planner`, :5172)

**Purpose:** Queue resolved change-required Jira FHIR tickets, run the
`ticket-plan` agent, and persist structured implementation-plan output.

**Prerequisites:** `source-jira` (:5160), `source-github` (:5190), and
`orchestrator` (:5150) reachable. The resource must be started.

**Kick-off:** Same lifecycle model as the Preparer — a started Planner processes
automatically via its sync worker:

```bash
curl -X POST http://localhost:5172/processing/start
curl -X POST http://localhost:5172/processing/stop

# Ad-hoc single-ticket enqueue/reset:
curl -X POST http://localhost:5172/processing/tickets/FHIR-12345
```

For a bulk agent-driven run, use the `orchestrate-plan` skill (same honesty note
as the Preparer: it draws from the Jira source, not the processor's queue).

**Monitor:**

```bash
curl http://localhost:5172/status
curl http://localhost:5172/processing/queue
```

**Output:** `./data/processor.jira.fhir.planner.db` → rendered by the
`ticket-site` tool; also the input the Applier auto-discovers.

**Config:** `appsettings.json` `Processing` section. Key defaults: DB path
`./data/processor.jira.fhir.planner.db`, `SyncSchedule` `00:01:00`,
`MaxConcurrentProcessingThreads` `3`, `StartProcessingOnStartup` `true`.

---

## Applier (`processor-jira-fhir-applier`, :5173)

**Purpose:** Consume completed plans from the Planner database and run an agent in
a per-(ticket, repo) git worktree to actually apply each planned change, then
locally commit the result. Successful commits are pushed to the upstream remote
**on demand**.

**Prerequisites:** `source-jira` (:5160), `orchestrator` (:5150), and the Planner
(:5172) reachable; completed plans must exist in the Planner DB
(`./data/processor.jira.fhir.planner.db`). The resource must be started.

**Kick-off:** The Applier has **no per-ticket HTTP enqueue trigger**. Once
started, it **auto-discovers** completed plans by polling the Planner DB via its
`PlannerWorkQueue` on the `SyncSchedule` cadence and processes them itself:

```bash
curl -X POST http://localhost:5173/processing/start
curl -X POST http://localhost:5173/processing/stop
```

The operator-facing HTTP action is the **push** API, which moves a ticket's
successful local commits to the upstream remote:

```bash
curl -X POST http://localhost:5173/api/v1/applied-tickets/FHIR-12345/push
```

The push returns `200` with a per-repo result summary, `404` if the ticket has no
applied record, or `409` if no repo has a successful local commit yet. There is
**no** `orchestrate-applier` skill.

**Monitor:**

```bash
curl http://localhost:5173/status
curl http://localhost:5173/processing/queue
```

**Output:** Per-(ticket, repo) commits in worktrees under
`./data/applier-workspaces`; surviving build-output diffs under `./out/applier`;
applied-ticket state in `./data/processor.jira.fhir.applier.db`. Pushes go to the
configured upstream remote on demand.

**Config:** `appsettings.json` `Processing` section. Key defaults: DB path
`./data/processor.jira.fhir.applier.db`, `SyncSchedule` `00:05:00`,
`MaxConcurrentProcessingThreads` `1`, `StartProcessingOnStartup` `true`;
`Applier.WorkingDirectory` `./data/applier-workspaces`, `Applier.OutputDirectory`
`./out/applier`, `Applier.PlannerDatabasePath`
`./data/processor.jira.fhir.planner.db`.

---

## BallotNotes (`processor-github-fhir-ballotnotes`, :5174)

**Purpose:** Hydrate ballot-note evidence for a GitHub repo across a
since-commit → HEAD window (commit-window walk, ticket attribution, source-file
resolution, current-note capture) so the `notes-*` skills can author updated
ballot notes.

**Prerequisites:** The GitHub source must have **cloned** the target repo —
BallotNotes expects a clone at `<CloneRoot>/<owner>_<name>/clone` (default
`CloneRoot` is `./cache/github/repos`). A missing clone or an unresolvable
since-commit returns `503`. `source-jira` (:5160), `source-github` (:5190), and
`orchestrator` (:5150) back attribution. The resource must be started.

**Kick-off:** Unlike the Jira processors, BallotNotes is purely on-demand. Trigger
a hydration with the repo and the since-commit to walk from:

```bash
curl -X POST http://localhost:5174/api/v1/ballot-notes/hydrate \
  -H "Content-Type: application/json" \
  -d '{"repoOwner":"HL7","repoName":"fhir","sinceSha":"<sha>"}'
```

The body requires `repoOwner`, `repoName`, and `sinceSha`; `repoCategory` and
`workGroupHint` are optional. The call validates the clone + since-commit
synchronously (`503` if either is missing/unresolvable, `400` on a malformed
body), then returns `202 Accepted` with a `runKey` and fires the walk
fire-and-forget.

**Monitor:** Poll the dedicated status endpoint (BallotNotes does **not** expose
`/status` or `/processing/queue`). The `runKey` contains `/`, so it is a query
parameter — omit it to read the latest run:

```bash
# Latest run:
curl "http://localhost:5174/api/v1/ballot-notes/hydrate/status"

# A specific run:
curl "http://localhost:5174/api/v1/ballot-notes/hydrate/status?runKey=<runKey>"
```

The status response reports `status` (`running` / `completed` / `failed`),
`unitsTotal`, `unitsHydrated`, `commitsInWindow`, `ticketsAttributed`,
`startedAt`, `completedAt`, and `error`. The run is done when `status` is
`completed` or `failed`.

For a bulk run that hydrates and then authors notes across the whole window, use
the `orchestrate-notes` skill, which hydrates via this same endpoint, polls the
status endpoint until the run completes, and dispatches the `notes-artifact` /
`notes-page` / `notes-datatype` authoring agents.

**Output:** `./cache/ballot-notes.db` → rendered by the `notes-site` tool.

**Config:** See the [BallotNotes Processor Service](../configuration.md#ballotnotes-processor-service)
section of `configuration.md` for the full appsettings/env reference. Key
defaults: DB path `./cache/ballot-notes.db`, `Hydration.CloneRoot`
`./cache/github/repos`, `Hydration.MaxParallelism` `4`.

**Hydration internals (performance):** The hydrator is tuned to minimize
per-unit git-process and network churn while producing byte-for-byte identical
ballot-note output:

- **Per-unit commit walk stays on `git log` (by design).** Each unit's window
  commit list comes from a per-unit pathspec walk
  (`git log <since>..HEAD --no-merges --name-status … -- <paths>`), **not** from
  the `github_commit_files` index. Git applies *history simplification*
  (TREESAME pruning) to a pathspec `git log`: it omits a non-merge commit that
  genuinely changed a path when that change is also reachable via a simpler route
  through the DAG. The raw commit-file index records every commit's changes with
  no such simplification, so an index-driven walk would over-include pruned
  commits, feed a different commit set into ticket attribution, and change the
  emitted note. Because the pruning depends on the commit graph at query time, it
  cannot be reconstructed from the index — so the walk deliberately remains a live
  `git log`.
- **Batched blob reads.** Every unit's candidate current-note intro at `HEAD`,
  and both the since- and head-side of every changed StructureDefinition, are read
  with a single `git cat-file --batch` pass each, replacing a `git show` spawn per
  blob. A leading UTF-8 BOM is stripped from decoded blob text so the batched read
  matches git's own `StreamReader` decoding byte-for-byte (git strips a detected
  BOM preamble; a raw UTF-8 decode would not).
- **In-run memoization.** The structural-diff pass parses each distinct
  `(format, blob)` at most once per run. Attribution cross-references each distinct
  commit SHA once, and fetches each distinct ticket's enrichment once, per run —
  shared across the parallel unit fan-out via best-effort concurrent memos, so a
  commit or ticket touched by many units still costs a single upstream call.
- **Validation-only index infrastructure.** A `WindowIndexReader` and the
  `github_commit_files.BlobSha` covering index (see
  [database schema](database-schema.md#github_commit_files--files-changed-in-commits))
  exist as a coverage oracle for the ingested window; they are **not** on the
  output-critical hydration path and do not gate a run.

---

## Output destinations at a glance

| Processor | Output store | Downstream renderer / next stage |
|-----------|--------------|----------------------------------|
| Preparer | `./data/processor.jira.fhir.preparer.db` | `ticket-site` tool |
| Planner | `./data/processor.jira.fhir.planner.db` | `ticket-site` tool; input for the Applier |
| Applier | per-ticket worktree commits + `./out/applier` + `./data/processor.jira.fhir.applier.db` | on-demand push to upstream remote |
| BallotNotes | `./cache/ballot-notes.db` | `notes-site` tool |
