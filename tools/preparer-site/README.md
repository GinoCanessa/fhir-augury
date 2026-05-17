# preparer-site

A small `dotnet`-run utility that reads a Jira preparer SQLite
database (produced by `FhirAugury.Processor.Jira.Fhir.Preparer`) and
emits a self-contained static HTML site for human review. The site
loads the database into [sql.js](https://sql.js.org/) in the browser
and renders list, per-ticket, and a few cross-cut views.

## What this is

A zero-server review surface for an entire preparer run. One
`index.html`, one `assets/` folder of vendored JS/CSS/wasm, no
network at runtime. Opens straight from `file://` in Chromium-family
browsers. A reviewer (or workgroup co-chair) can zip the output
folder and hand it around without any infrastructure on the
receiving side.

### Hydration

The inlined DB always carries the `prepared_*_hydration` and
`prepared_ticket_jira_xref` tables alongside the agent-authored
`prepared_tickets*` tables. Those hydration rows are populated
automatically by `FhirAugury.Processor.Jira.Fhir.Preparer` as the
ticket-prep handler completes each ticket; the site renders directly
from them with no additional fetch. There is one canonical DB shape;
no opt-in slimming is offered.

## What this is not

Not a server, not a daemon, not a long-running process. Not a
search index — just substring filtering on Key / Title /
RequestSummary. Not incremental — every run overwrites `<out>/`.
Not a planner or ballot-notes site (each of those would be a
sibling tool under `tools/`).

## Quick start

```bash
dotnet run --project tools/preparer-site -- \
  --db ./cache/jira-preparer.db \
  --out ./cache/jira-preparer-site \
  --title "Preparer Report — May 2026"
```

Open `cache/jira-preparer-site/index.html` in a Chromium-family
browser. You should see a landing page titled
*"Preparer Report — May 2026"* with `N prepared tickets in this run.`
and three summary tables (workgroup, recommendation, impact). Each
row in the summary tables links into a filtered list view; each row
in the list view links into the per-ticket page.

## Flags

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | *required* | Path to the preparer SQLite DB. |
| `--out <path>` | `./cache/jira-preparer-site` | Output directory; **overwritten** if it exists. |
| `--title <string>` | `"Preparer Report"` | Threads through to `<title>` and the landing-page `<h1>` (HTML-encoded). |
| `--help` | — | Print usage and exit non-zero. |

## Output size

`index.html` carries the entire preparer DB (including hydration
tables and inlined `DescriptionPlain` per ticket) as a single
base64-inlined blob, so the file size is roughly

```
indexHtml ≈ dbSize × 4 / 3 + ~100 KB chrome
```

At today's volume (~3,900 prepared tickets) the hydrated DB lands
in the 70–90 MB range. Chromium and Firefox handle that file size
fine; Safari may struggle (see [Browser compatibility](#browser-compatibility)).
For distribution, zip the output folder.

## Browser compatibility

Tested in Chromium-family browsers (Chrome, Edge, Brave) opened
directly from `file://`. Safari may refuse to load WebAssembly from
`file://`; if you hit that, serve the output directory and open
`http://localhost:8000/` instead:

```bash
cd ./cache/jira-preparer-site
python3 -m http.server
```

## Vendored assets

The site relies on a vendored copy of
[sql.js](https://github.com/sql-js/sql.js) to load the embedded
SQLite database in the browser. The bytes ship as embedded
resources in the C# project and are copied into `<out>/assets/` on
each run.

- Release: [`sql-js/sql.js` v1.10.3](https://github.com/sql-js/sql.js/releases/tag/v1.10.3)
- Asset: `sqljs-wasm.zip` (contains `sql-wasm.js` + `sql-wasm.wasm`)
- License: MIT
- SHA-256:
  - `sql-wasm.js`&nbsp;&nbsp; `558a72c3ab3415d0e6d243cfd23f9d61543600d59054b4b7b8da3cd65f6b9fd4`
  - `sql-wasm.wasm` `d7e61b828523001f26ce0b3f88dabcf6c12e5e6edf80eb4f08b26ac7b946ff88`

To refresh:

1. Download `sqljs-wasm.zip` from the chosen `sql-js/sql.js` release.
2. Extract `sql-wasm.js` and `sql-wasm.wasm` into
   `tools/preparer-site/web-assets/`.
3. Update the SHA-256 values above (`shasum -a 256 sql-wasm.*`).
4. Rebuild (`dotnet build tools/preparer-site/preparer-site.csproj`).

No automated update path is planned.

## Known limitations

- **No incremental mode.** Every run rewrites `<out>/` from scratch.
  If `<out>` exists it is deleted before the new site is written.
- **Substring filter only.** The list view does a debounced 150 ms
  case-insensitive substring match against
  `Key + Title + RequestSummary`. There is no full-text index, no
  fuzzy match, no field-targeted search.
- **No link to legacy `cache/jira-preparer-reports/*.md`.** Per-
  ticket pages do not reference the legacy markdown files. The site
  is the canonical deliverable; the markdown pipeline is untouched
  but unlinked.
- **Single-run only.** No diff between runs, no two-DB comparison.
- **No theming / dark-mode toggle** beyond honoring
  `prefers-color-scheme`.
