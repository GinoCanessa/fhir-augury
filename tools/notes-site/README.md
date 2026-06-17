# notes-site

A read-only `dotnet`-run utility that turns drafted FHIR **ballot notes** into a
self-contained, searchable, sortable static HTML SPA for human review. It is the
ballot-notes sibling of `ticket-site` and `fhir-spec-review`: it owns its own
notes SQLite database, written one unit at a time by the ballot-note drafting
skills, and emits a single `index.html` + `assets/` folder that opens straight
from `file://` with no server and no network.

It has two verbs:

- **`write`** — persists **one** drafted ballot note (a `NoteWritePayload` JSON
  document supplied via `--in <file>` or stdin) into the notes DB. Re-writing
  the same unit (same repo + type + name) replaces the prior row and its
  children, so the verb is idempotent. This is the deterministic counterpart to
  the LLM drafting done by the `notes-artifact`, `notes-page`, and
  `notes-datatype` skills (typically orchestrated by `orchestrate-notes`); each
  per-unit skill emits one payload and calls `notes-site write` once.
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
source-file / commit / ticket evidence tables.

## Usage

```bash
# 1) Write one note (repeat per unit; the drafting skills do this).
dotnet run --project tools/notes-site -- write \
    --db ./cache/notes.db \
    --in ./payloads/observation.json \
    --drop-tables          # first write of a fresh batch only

dotnet run --project tools/notes-site -- write \
    --db ./cache/notes.db \
    --in ./payloads/security.json

# 2) Emit the static review site.
dotnet run --project tools/notes-site -- report \
    --db ./cache/notes.db \
    --out ./cache/notes-site \
    --title "Ballot Notes — May 2026" \
    --force
```

A payload can also be piped on stdin:

```bash
cat ./payloads/observation.json | dotnet run --project tools/notes-site -- write --db ./cache/notes.db
```

### `write` options

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | `./cache/notes.db` | Notes SQLite DB; created (with its parent dir) if absent. |
| `--in <path>` | *(stdin)* | JSON payload file. When omitted, the payload is read from stdin. |
| `--drop-tables` | off | Drop and recreate the notes schema first (clean re-run / new batch). |

### `report` options

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | `./cache/notes.db` | Notes SQLite DB to read. |
| `--out <dir>` | `./cache/notes-site` | Output directory; receives `index.html` + `assets/`. Existing contents are removed on emit. |
| `--title <text>` | `FHIR Ballot Notes` | Site title (header + `<title>`). |
| `--force` | off | Overwrite an existing report output directory. |

## Payload shape (`NoteWritePayload`)

The `write` verb accepts a single JSON object. All keys are case-insensitive.
Required: `type` (`Artifact` \| `Page` \| `DataType`), `name`, `repoOwner`,
`repoName`. Everything else is optional and defaults to empty / zero / `unknown`.

```jsonc
{
  "type": "Artifact",
  "name": "Observation",
  "repoOwner": "HL7",
  "repoName": "fhir",
  "repoCategory": "FhirCore",
  "workGroup": "Orders and Observations (OO)",
  "workGroupCode": "OO",
  "sinceSha": "1a2b3c4d…",
  "sinceShortSha": "1a2b3c4d5e6f",
  "headSha": "9f8e7d6c…",
  "headShortSha": "9f8e7d6c5b4a",
  "commitsInWindow": 3,
  "ticketsAttributed": 2,
  "needsNote": "yes",
  "currentBallotNoteHtml": "<blockquote class=\"ballot-note\" id=\"bn1\">…</blockquote>",
  "proposedBallotNoteHtml": "<blockquote class=\"ballot-note\" id=\"bn1\">…</blockquote>",
  "rollupSummaryMarkdown": "## After-applied summary\n\n- …",
  "notesForReviewerMarkdown": "…",
  "sourceFilesNote": "Pattern `…` produced no match.",
  "generatedAt": "2026-06-15T12:00:00Z",
  "sourceFiles": [
    { "path": "source/observation/structuredefinition-observation.xml", "role": "StructureDefinition", "touchedInWindow": true }
  ],
  "commits": [
    { "sha": "…", "shortSha": "…", "authorName": "Jane Dev", "authorDate": "2026-06-10T09:00:00Z",
      "subject": "FHIR-12345 …", "webUrl": "https://github.com/HL7/fhir/commit/…", "ticketKeys": ["FHIR-12345"] }
  ],
  "tickets": [
    { "key": "FHIR-12345", "title": "…", "resolution": "Persuasive",
      "workGroup": "Orders and Observations (OO)", "specification": "FHIR Core (FHIR)",
      "url": "https://jira.hl7.org/browse/FHIR-12345", "commitCount": 1 }
  ]
}
```

### Identity & idempotence

The business key for a note is a slug of `repoOwner-repoName-type-name`. Writing
the same unit twice replaces the earlier row and all of its child rows (source
files, commits, tickets). The `notes_runs` table tracks the repo + since-commit
window; the most recent row drives the SPA's provenance header.

## Security

The SPA renders every DB-derived value via `textContent` / `createElement` —
never `innerHTML` — with two deliberate, sanitizer-gated exceptions:

- **Proposed / current ballot-note HTML** is authored content; it is passed
  through [DOMPurify](https://github.com/cure53/DOMPurify) before display.
- **Roll-up summary / notes-for-reviewer Markdown** is rendered with
  [marked](https://marked.js.org/) and then sanitized with DOMPurify.

When either library is unavailable the content degrades to escaped text.

## What this is not

Not a server, not a daemon, not a long-running process. Not a drafting engine —
the ballot-note prose is authored upstream by the `notes-*` skills; this tool
only persists and renders it. Not incremental at the site level — every
`report` run overwrites `<out>/`. Not a ticket or spec-review site (each of
those is a sibling tool under `tools/`).
