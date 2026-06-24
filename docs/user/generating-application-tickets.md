# Generating Application Tickets

This guide walks you from a cold **Planner** processor to an opened
**"Tickets for Applying"** sub-site, and explains the full **Applier**
apply-and-push flow inline. It calls out the easy-to-miss **planner
topic-grouping** step, without which the site has no topic list.

> The rendered site labels this output **"Tickets for Applying"**.

## What you'll produce

The `ticket-site` **applying** sub-site under `<out>/applying/`, rendered from the
Planner database `./data/processor.jira.fhir.planner.db`; plus, optionally,
**applied commits** produced by the Applier and pushed to the upstream remote on
demand.

## Prerequisites

- The **Planner** (`:5172`) needs `source-jira` (`:5160`), `source-github`
  (`:5190`), and the `orchestrator` (`:5150`).
- The **Applier** (`:5173`) additionally needs **completed plans** in the Planner
  DB (`./data/processor.jira.fhir.planner.db`).
- Both `processor-jira-fhir-planner` and `processor-jira-fhir-applier` resources
  are started (see Step 1).

Confirm the processors are listening:

```bash
curl http://localhost:5172/status   # Planner
curl http://localhost:5173/status   # Applier
```

## Steps

### 1. Start the Planner (and Applier) processors

Both are `WithExplicitStart()` resources. Either click **Start** on each in the
Aspire dashboard, or run them standalone:

```bash
dotnet run --project src/FhirAugury.Processor.Jira.Fhir.Planner
dotnet run --project src/FhirAugury.Processor.Jira.Fhir.Applier
```

**Verify:** both `/status` calls return `200`.

### 2. Plan tickets

A started Planner **auto-processes** via its sync worker
(`MaxConcurrentProcessingThreads` is `3`). You can also drive it explicitly, or
do a bulk agent run with the **`orchestrate-plan`** skill.

```bash
# explicit control (optional)
curl -X POST http://localhost:5172/processing/start
curl -X POST http://localhost:5172/processing/stop

# check progress
curl http://localhost:5172/processing/queue
```

**Verify:** completed plans accumulate in `processing/queue` (processed > 0).

### 3. Run the planner topic-grouping pass — the easy-to-miss step

> **Without this pass the applying sub-site greys out `Show Topic List →` and
> the `#/topics` route renders an empty-state message.**

Run the **`orchestrate-planner-topic-groupings`** skill to populate the
`planned_ticket_topics*` and `planned_ticket_topic_repos` tables.

**Verify:** after rendering (Step 4), the applying landing page shows a live
**`Show Topic List →`** affordance. Still greyed-out means the populator was not
run.

### 4. Render the applying sub-site

```bash
dotnet run --project tools/ticket-site -- \
    --planner-db ./data/processor.jira.fhir.planner.db \
    --out ./cache/jira-ticket-site \
    --title "Applying — May 2026"
```

**Verify:** open `./cache/jira-ticket-site/index.html` and choose the **Tickets
for Applying** card; `<out>/applying/` exists.

### 5. Apply and push (the Applier flow, inline)

The Applier has **no per-ticket enqueue trigger** and there is **no
`orchestrate-applier` skill.** Once started, it **auto-discovers** completed
plans via its `PlannerWorkQueue` and processes them itself:

- It applies each planned change in a per-(ticket, repo) git worktree under
  `./data/applier-workspaces`, writes surviving build-output diffs under
  `./out/applier`, and **commits locally**.
- Applied-ticket state lives in `./data/processor.jira.fhir.applier.db`.

Pushing a ticket's successful local commits to the upstream remote is the one
operator-facing HTTP action, **on demand**:

```bash
curl -X POST http://localhost:5173/api/v1/applied-tickets/FHIR-12345/push
# → 200 with a per-repo result summary
#   404 if the ticket has no applied record
#   409 if no repo has a successful local commit yet
```

**Verify:** the push returns `200` with a per-repo summary.

## Did I miss a step?

- **Greyed-out topic list ⇒** the planner topic populator (Step 3,
  `orchestrate-planner-topic-groupings`) was not run.
- **Nothing to apply ⇒** there are no completed plans in the Planner DB; run the
  Planner (Step 2) first.
- **Push returns `404` ⇒** there is no applied record for that ticket key.
- **Push returns `409` ⇒** no repo has a successful local commit yet (the Applier
  has not finished applying that ticket).

## Reference

- [Processors runbook — Planner & Applier](../technical/processors.md) — the
  operator reference for endpoints, ports, and lifecycle.
- [`ticket-site` tool README](../../tools/ticket-site/README.md) — sub-site
  emission, flags, and topic-table sources.
- [`orchestrate-plan` skill](../../.github/skills/orchestrate-plan/SKILL.md) —
  bulk ticket planning.
- [`orchestrate-planner-topic-groupings` skill](../../.github/skills/orchestrate-planner-topic-groupings/SKILL.md)
  — the planner topic-grouping pass.
- [Configuration reference](../configuration.md) — environment variables and
  defaults.

## See also

- [Generating Ballot Notes](generating-ballot-notes.md)
- [Generating Discussion Tickets](generating-discussion-tickets.md)
