# notes-site

A read-only `dotnet`-run utility that turns hydrated FHIR **ballot notes** into a
self-contained, searchable, sortable static HTML SPA for human review. It is the
ballot-notes sibling of `ticket-site` and `fhir-spec-review`: it reads the notes
SQLite database **owned by the BallotNotes processor**
(`FhirAugury.Processor.GitHub.Fhir.BallotNotes`) via a shared `.Persistence`
project reference, and emits a single `index.html` + `assets/` folder that opens
straight from `file://` with no server and no network.

It has one verb:

- **`report`** — reads the notes DB and emits a single self-contained report:
  an `index.html` plus an `assets/` folder. The notes DB is inlined as base64
  and loaded in-browser via [sql.js](https://sql.js.org/) (no network). The
  landing view is a type-to-filter, click-to-sort index table with an optional
  **Group by workgroup** toggle; every row links to a per-note **detail view**.

## What this is

A zero-server review surface for a tranche of ballot notes. One `<out>/index.html`,
one `<out>/assets/` folder of vendored JS/CSS/wasm, no network at runtime. A
workgroup co-chair can zip the output folder and hand it around without any
infrastructure on the receiving side.

The landing index table columns mirror the `index-notes` skill's README index:

| Column | Content |
|--------|---------|
| Name | The unit name (artifact / page / datatype), linking to the detail view. |
| Type | `Artifact`, `Page`, or `DataType`. |
| Workgroup | The owning work group. |
| Repo | `owner/name`. |
| Commits | Commits in the since-commit window. |
| Tickets | Jira tickets attributed. |
| Needs note | `yes` / `no` / `unknown` (color-coded badge). |

The per-note **detail view** renders the full report: the proposed ballot note
(sanitized HTML), the current ballot note at HEAD (collapsed), the after-applied
roll-up summary and notes-for-reviewer (authored Markdown), and the supporting
source-file / commit / ticket evidence tables. A top-right **📋 Copy for AI**
button copies a clean Markdown serialization of the note to the clipboard for
pasting into an LLM (works offline from `file://`).

## Usage

The notes DB is produced by the BallotNotes processor (hydration writes the
evidence; the `notes-*` skills `PUT` the authored prose). Once it exists, emit
the static review site:

```bash
dotnet run --project tools/notes-site -- report \
    --db ./cache/ballot-notes.db \
    --out ./cache/notes-site \
    --title "Ballot Notes — May 2026" \
    --force
```

### `report` options

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | `./cache/ballot-notes.db` | Notes SQLite DB to read (owned by the BallotNotes processor). |
| `--out <dir>` | `./cache/notes-site` | Output directory; receives `index.html` + `assets/`. Existing contents are removed on emit. |
| `--title <text>` | `FHIR Ballot Notes` | Site title (header + `<title>`). |
| `--force` | off | Overwrite an existing report output directory. |

## Persistence

The notes database is **owned by the BallotNotes processor**, not this tool. Each
unit's business key is a slug of `repoOwner-repoName-type-name`; the processor's
hydration upserts the evidence half (window, source files, commits, tickets,
current ballot-note HTML) and the `notes-artifact` / `notes-page` /
`notes-datatype` skills write back the prose half (proposed note, roll-up,
notes-for-reviewer, needs-note) via `PUT /api/v1/ballot-notes/{slug}/note`. The
`notes_runs` table tracks the repo + since-commit window; the most recent row
drives the SPA's provenance header. This tool only **reads** that database and
renders it.

## Security

The SPA renders every DB-derived value via `textContent` / `createElement` —
never `innerHTML` — with two deliberate, sanitizer-gated exceptions:

- **Proposed / current ballot-note HTML** is authored content; it is passed
  through [DOMPurify](https://github.com/cure53/DOMPurify) before display.
- **Roll-up summary / notes-for-reviewer Markdown** is rendered with
  [marked](https://marked.js.org/) and then sanitized with DOMPurify.

When either library is unavailable the content degrades to escaped text.

## What this is not

Not a server, not a daemon, not a long-running process. Not a drafting engine
and **not a persistence layer** — the ballot-note evidence is hydrated and the
prose authored upstream (the BallotNotes processor + the `notes-*` skills); this
tool only **reads and renders**. Not incremental at the site level — every
`report` run overwrites `<out>/`. Not a ticket or spec-review site (each of
those is a sibling tool under `tools/`).
