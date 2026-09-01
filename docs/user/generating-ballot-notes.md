# Generating Ballot Notes

This guide walks you from a cold **BallotNotes** processor to an opened
`notes-site` static site showing **proposed** ballot notes. It calls out the one
step users most often skip — **authoring** — which is why a freshly hydrated site
can come up with no proposed notes.

## What you'll produce

A self-contained `notes-site` static SPA (`index.html` + an `assets/` folder)
under `./cache/notes-site`, fed from the BallotNotes database
`./cache/ballot-notes.db`. The site is type-to-filter and click-to-sort, and is
meant to be handed to reviewers / co-chairs for ballot review.

## Prerequisites

- The shared sources are reachable: `source-jira` (`:5160`), `source-github`
  (`:5190`), and the `orchestrator` (`:5150`).
- The target repo is already cloned on disk at
  `./cache/github/repos/<owner>_<name>/clone` (for example
  `./cache/github/repos/HL7_fhir/clone`). A missing clone **or** an unresolvable
  `sinceSha` makes hydration fail with `503`.
- **Optional but recommended:** the GitHub source has ingested the target repo
  into `github.db`. Hydration reads the on-disk clone directly and does **not**
  require the index or gate on its coverage, but the commit→PR→ticket *gap-fill*
  attribution (for window commits whose message names no ticket) and work-group
  lookups draw on `github.db`; if it is stale or absent, hydration still succeeds
  and simply attributes fewer tickets to those message-less commits.
- The `processor-github-fhir-ballotnotes` resource is started (see Step 1).

Confirm the processor is listening:

```bash
curl http://localhost:5174/api/v1/ballot-notes?limit=1
# → 200 with a (possibly empty) JSON array
```

> **Note:** on a cold processor that has never hydrated,
> `GET /api/v1/ballot-notes/hydrate/status` returns `404 No hydration run`. That
> still proves the service is listening — it is **not** an error.

## Steps

### 1. Start the BallotNotes processor

`processor-github-fhir-ballotnotes` is a `WithExplicitStart()` resource, so it
does **not** auto-start with the rest of the stack. Either click **Start** on
that resource in the Aspire dashboard, or run it standalone:

```bash
dotnet run --project src/FhirAugury.Processor.GitHub.Fhir.BallotNotes
```

**Verify:** the `curl` from Prerequisites returns `200`.

### 2. Hydrate evidence

Hydration walks the commit window and gathers source files, attributed commits,
and the Jira tickets they applied — but it writes **evidence only**, not notes.

```bash
curl -X POST http://localhost:5174/api/v1/ballot-notes/hydrate \
    -H "Content-Type: application/json" \
    -d '{"repoOwner":"HL7","repoName":"fhir","sinceSha":"<sha>"}'
# → 202 Accepted, with a runKey
```

**Verify:** you get `202 Accepted` and a `runKey` in the response.

### 3. Poll until hydration completes

```bash
curl http://localhost:5174/api/v1/ballot-notes/hydrate/status
# → { "status": "completed", "unitsTotal": …, "unitsHydrated": …,
#     "commitsInWindow": …, "ticketsAttributed": … }
```

**Verify:** `status` is `completed` (it passes through `running`; `failed` means
the run errored). Note `unitsTotal` / `unitsHydrated` — these are the units you
must author in the next step.

### 4. Author the notes — the step users skip

> **This is the step that is easy to skip and produces an empty-looking site.**
> Hydration (Step 2) writes evidence only. Until you author the units, the
> rendered site shows **no proposed notes**.

Run the **`orchestrate-notes`** skill against the same repo and `sinceSha`. It
fans out per-unit authoring sub-agents — `notes-artifact`, `notes-page`, and
`notes-datatype` — each of which writes prose back to the processor via
`PUT /api/v1/ballot-notes/{slug}/note`.

**Verify:** re-read one unit's note and confirm the **proposed-note** prose is
non-empty:

```bash
curl http://localhost:5174/api/v1/ballot-notes/<slug>
# → the unit's proposedNoteHtml field is now populated
```

### 5. Render the static site

```bash
dotnet run --project tools/notes-site -- report \
    --db ./cache/ballot-notes.db \
    --out ./cache/notes-site \
    --title "Ballot Notes — May 2026" \
    --force
```

**Verify:** `./cache/notes-site/index.html` and `./cache/notes-site/assets/`
exist.

### 6. Open the site

Open `./cache/notes-site/index.html` in a Chromium-family browser (the database
is inlined as base64 and loaded in-browser via sql.js, so no server is needed).

**Verify:** the index table lists units, and opening a unit's detail view shows
its proposed note.

## Did I miss a step?

- **The site renders but the proposed notes are blank ⇒ you skipped authoring
  (Step 4, `orchestrate-notes`).** Hydration alone never writes notes. This is the
  single most common mistake.
- **Hydrate returns `503` ⇒** the repo is not cloned at
  `./cache/github/repos/<owner>_<name>/clone`, or the `sinceSha` could not be
  resolved. Clone the repo / fix the SHA and retry.
- **Most blank _current_ notes are expected.** A unit with no ballot note at HEAD
  shows an empty **current** note — that is the normal state, not an error. The
  field that matters after authoring is the **proposed** note.

## Reference

- [Processors runbook — BallotNotes](../technical/processors.md) — the operator
  reference and source of truth for endpoints, ports, and lifecycle.
- [`notes-site` tool README](../../tools/notes-site/README.md) — render command
  and all `report` options.
- [`orchestrate-notes` skill](../../.github/skills/orchestrate-notes/SKILL.md) —
  the bulk authoring orchestrator, plus the per-unit
  [`notes-artifact`](../../.github/skills/notes-artifact/SKILL.md),
  [`notes-page`](../../.github/skills/notes-page/SKILL.md), and
  [`notes-datatype`](../../.github/skills/notes-datatype/SKILL.md) skills.
- [BallotNotes processor configuration](../configuration.md#ballotnotes-processor-service)
  — environment variables and defaults.

## See also

- [Generating Discussion Tickets](generating-discussion-tickets.md)
- [Generating Application Tickets](generating-application-tickets.md)
