# Generating Discussion Tickets

This guide walks you from a cold **Preparer** processor to an opened
**"Tickets for Discussion"** sub-site. It calls out the easy-to-miss
**topic-grouping** step, without which the site has no topic list.

## What you'll produce

The `ticket-site` **discussion** sub-site under `<out>/discussion/` (with a
chooser landing page at `<out>/index.html`), rendered from the Preparer database
`./data/processor.jira.fhir.preparer.db`. The site is labelled
**Tickets for Discussion**.

## Prerequisites

- `source-jira` (`:5160`) and the `orchestrator` (`:5150`) are reachable.
- The `processor-jira-fhir-preparer` resource is started (see Step 1).

Confirm the processor is listening:

```bash
curl http://localhost:5171/status
# → running/paused, SyncSchedule, StartProcessingOnStartup
```

## Steps

### 1. Start the Preparer processor

`processor-jira-fhir-preparer` is a `WithExplicitStart()` resource. Either click
**Start** on that resource in the Aspire dashboard, or run it standalone:

```bash
dotnet run --project src/FhirAugury.Processor.Jira.Fhir.Preparer
```

**Verify:** `curl http://localhost:5171/status` returns `200`.

### 2. Process tickets

A started Preparer **auto-processes** via its sync worker
(`StartProcessingOnStartup` is `true`, `SyncSchedule` is `00:01:00`). You can
also drive it explicitly, or do a bulk agent run with the **`orchestrate-prep`**
skill.

```bash
# explicit control (optional)
curl -X POST http://localhost:5171/processing/start
curl -X POST http://localhost:5171/processing/stop

# check progress
curl http://localhost:5171/processing/queue
# → processed / remaining / in-flight / error counts
```

**Verify:** `processed` is greater than `0`.

### 3. Run the topic-grouping pass — the easy-to-miss step

> **Without this pass the rendered site has no topic list.** Topic rows are
> written by the topic-grouping pass, not by ticket processing.

Run the **`orchestrate-topic-groupings`** skill to populate the
`prepared_ticket_topics*` tables. If you need to refresh hydration on demand,
you can also backfill:

```bash
curl -X POST http://localhost:5171/api/v1/admin/hydration/backfill
```

**Verify:** after rendering (Step 4), the discussion landing page shows a live
**`Show Topic List →`** affordance. A greyed-out affordance means no topic rows
survived — the topic-grouping pass did not run (or trimmed every topic away).

### 4. Render the discussion sub-site

```bash
dotnet run --project tools/ticket-site -- \
    --preparer-db ./data/processor.jira.fhir.preparer.db \
    --out ./cache/jira-ticket-site \
    --title "Discussion — May 2026"
```

Optional filters: `--spec`, `--project`, `--jira-source-db`.

**Verify:** `./cache/jira-ticket-site/index.html` and
`./cache/jira-ticket-site/discussion/` exist.

### 5. Open the site

Open `./cache/jira-ticket-site/index.html` and choose the **Tickets for
Discussion** card.

**Verify:** the discussion sub-site loads and lists tickets; if topics were
grouped, **`Show Topic List →`** is a live link.

## Did I miss a step?

- **`ticket-site` fails fast with `Database '<path>' is not hydrated…` ⇒** you
  rendered against a DB the Preparer never processed. Run the Preparer against
  that DB first (the startup sweep, or
  `POST /api/v1/admin/hydration/backfill` on a running service), then re-render.
- **Greyed-out `Show Topic List →` / empty topic list ⇒** the topic-grouping
  pass (Step 3, `orchestrate-topic-groupings`) was not run, or every topic's
  members were trimmed away.

## Reference

- [Processors runbook — Preparer](../technical/processors.md) — the operator
  reference for endpoints, ports, and lifecycle.
- [`ticket-site` tool README](../../tools/ticket-site/README.md) — sub-site
  emission, flags, and topic-table sources.
- [`orchestrate-prep` skill](../../.github/skills/orchestrate-prep/SKILL.md) —
  bulk ticket preparation.
- [`orchestrate-topic-groupings` skill](../../.github/skills/orchestrate-topic-groupings/SKILL.md)
  — the topic-grouping pass.
- [Configuration reference](../configuration.md) — environment variables and
  defaults.

## See also

- [Generating Ballot Notes](generating-ballot-notes.md)
- [Generating Application Tickets](generating-application-tickets.md)
