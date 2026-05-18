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
`prepared_tickets*` tables. Those hydration rows are populated by
`FhirAugury.Processor.Jira.Fhir.Preparer`: per-ticket as each ticket is
prepared, plus a full sweep at service startup and on demand via
`POST /api/v1/admin/hydration/backfill`. **`preparer-site` is a pure
consumer of an already-hydrated DB** — if `prepared_ticket_hydration`
is missing or empty the tool fails fast with an actionable error
pointing operators at the service.

### Related-artifact / related-page surface

To support the SPA's "By artifact" and "By page" crosscut columns the
emitted DB carries two normalized child tables:

| Table | Columns | Source |
|-------|---------|--------|
| `prepared_ticket_artifacts` | `TicketKey TEXT, Value TEXT, PRIMARY KEY (TicketKey, Value)` | `jira_issues.RelatedArtifacts` + `jira_baldef.RelatedArtifacts` |
| `prepared_ticket_pages` | `TicketKey TEXT, Value TEXT, PRIMARY KEY (TicketKey, Value)` | `jira_baldef.RelatedPages` |

Values are comma-split, trimmed, and case-insensitively de-duplicated
per ticket with first-seen casing preserved (matching the
`index-planned` skill's "Related artifact normalization" rule). The
tables are always created in the inlined DB; rows are only populated
when `--jira-source-db <path>` is provided (the upstream HTTP DTO does
not currently carry these fields). Without `--jira-source-db` the
tables are present but empty and the SPA auto-hides the corresponding
columns.

### Prerequisite: hydrated DB

`preparer-site` no longer hydrates anything itself. The preparer
service (`FhirAugury.Processor.Jira.Fhir.Preparer`) owns the full
hydration surface: per-ticket hydration as each ticket is prepared,
plus a full sweep at service startup and the on-demand
`POST /api/v1/admin/hydration/backfill` admin endpoint. Run the
preparer service against your DB once before invoking `preparer-site`.

If the DB has no `prepared_ticket_hydration` rows when `preparer-site`
opens it, the tool exits non-zero with:

```
Database '<path>' is not hydrated. Run FhirAugury.Processor.Jira.Fhir.Preparer
against it first (the service hydrates on startup, or POST
/api/v1/admin/hydration/backfill on a running service).
```

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
  --spec 'FHIR Core (FHIR)' \
  --title "Preparer Report — May 2026"
```

`--spec` matches the hydrated `Specification` value (Jira
`customfield_11302`, e.g. `'FHIR Core (FHIR)'` or `fhir-extensions`);
`--project` matches the Jira project key (e.g. `FHIR`). The two are
distinct: a ticket in the `FHIR` Jira project may have any
`Specification` value, including blank.

Open `cache/jira-preparer-site/index.html` in a Chromium-family
browser. You should see a landing page titled
*"Preparer Report — May 2026"* with `N prepared tickets in this run.`
on the left and a `Show Ticket List →` shortcut on the right of the
same row, followed by a grid of crosscut summary tables (workgroup,
artifact, page, impact, specification, GitHub item state, hydration
status). Each filterable row in the summary tables toggles a chip on
the current view; each row in the list view links into the per-ticket
page.

## Filter chips

The landing page, the list view, and crosscut sub-views share a
single filter banner. Each active filter is a chip with the shape
`dim: value`:

- **Generation chips** (`spec:`, `project:`, `wg:`) come from the
  build-time flags and are baked into the trimmed DB. They render
  without an `×` button — the underlying data is already trimmed so
  the chip cannot be removed.
- **In-page chips** (`wg:`, `artifact:`, `page:`, `spec:`) come from
  clicking a row in a filterable crosscut column. They render with an
  `×` button that drops just that one chip and re-renders the view.

Chips compose with logical AND, encoded in the URL hash as a
`?dim=value&dim2=value` suffix
(e.g., `#/list?wg=Patient%20Administration&artifact=Observation`).
This encoding is internal to the SPA and not a stable external link
contract.

Filterable crosscut columns auto-hide on the landing grid when their
own dimension is already pinned by a chip OR has only one distinct
non-`(unknown)` value in the post-chip data set. The non-filterable
columns (impact, GitHub state, hydration status) always render when
they have data. The `(unknown)` pseudo-value on a crosscut row toggles
a chip with literal value `(unknown)`, which matches no tickets and is
mainly useful for confirming a column has been auto-hidden because all
non-unknown values were already pinned.

## Flags

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | *required* | Path to the preparer SQLite DB. Must already be hydrated (the preparer service handles that — see [Prerequisite: hydrated DB](#prerequisite-hydrated-db)). |
| `--out <path>` | `./cache/jira-preparer-site` | Output directory; overwritten subject to the safety rail (see below). |
| `--title <string>` | `"Preparer Report"` | Threads through to `<title>` and the landing-page `<h1>` (HTML-encoded). When any filter is active, an automatic ` (filtered: …)` suffix is appended. |
| `--spec <name>` | — | Filter to tickets whose hydrated `Specification` (Jira `customfield_11302`) matches (case-insensitive). |
| `--project <key>` | — | Filter to tickets in the given Jira project key (case-insensitive). |
| `--wg <name\|code>` | — | Filter to tickets in the given workgroup. Matches the workgroup `Name` recorded on the preparer-side ticket first; on miss, resolves the input as a workgroup code or clean name via the Jira source service (HTTP `--jira-source`, then `--jira-source-db`). |
| `--jira-source <url>` | `http://localhost:5160` | Base URL of the Jira source service for `--wg` code/clean-name resolution. |
| `--jira-source-db <path>` | — | Jira source SQLite DB. Fallback for `--wg` resolution when the HTTP service is unreachable, and the source for the SPA's "By artifact" and "By page" crosscut columns; without it those tables are present but empty. |
| `--force` | `false` | Overwrite an output directory whose recorded filter set differs from the current run's (see "Output directory safety rail"). |
| `--help` | — | Print usage and exit non-zero. |

### Active filters

```bash
# Workgroup by code, falling back through the default Jira source service.
dotnet run --project tools/preparer-site -- \
  --db ./cache/jira-preparer.db \
  --out ./cache/jira-preparer-site-fhir-i \
  --wg fhir-i

# Specification filter only.
dotnet run --project tools/preparer-site -- \
  --db ./cache/jira-preparer.db \
  --out ./cache/jira-preparer-site-fhir-extensions \
  --spec fhir-extensions
```

When a filter flag is supplied the inlined DB is trimmed to just the
surviving tickets and their related rows; the active filter set is
also surfaced in the page `<title>` and as a non-removable
generation chip in the banner on every view except the per-ticket
detail page. If the filter set ANDs to zero rows the site is still
emitted and the landing view shows `0 prepared tickets match this
filter.` instead of the usual count line.

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
For distribution, zip the output folder. With one or more filter
flags supplied, the inlined DB shrinks roughly in proportion to the
surviving ticket count (`prepared_tickets` and all per-ticket child
tables are trimmed and the file is `VACUUM`ed before it is
inlined).

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

- **No incremental mode.** Every run rewrites `<out>/` from scratch,
  subject to the safety rail (see below). If `<out>` exists and its
  recorded filter set matches the current run, it is deleted before
  the new site is written.
- **Output directory safety rail.** Every emitted site drops a
  `.preparer-site.meta` JSON marker recording the canonical filter
  set. A subsequent run against the same `--out` whose filter set
  differs (e.g., re-running a `--project FHIR` build into a folder
  that previously held a `--wg CDS` build) refuses with a stderr
  diagnostic; pass `--force` to overwrite anyway. Pre-existing
  directories with no marker (i.e., produced by an earlier version of
  the tool) are overwritten without `--force`.
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
