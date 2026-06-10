# ticket-site

A small `dotnet`-run utility that turns a Jira-FHIR processor SQLite
database (preparer or planner) into a self-contained static HTML
sub-site for human review. A chooser landing page links to whichever
sub-site(s) have been built into the same output root.

- `--preparer-db <path>` → emits the **Tickets for Discussion**
  (discussion) sub-site under `<out>/discussion/`, with the
  preparer-side SPA (list, per-ticket, crosscut, topic views).
- `--planner-db <path>` → emits the **Tickets for Applying**
  (applying) sub-site under `<out>/applying/`, with the
  planner-side SPA (list, per-ticket, topics with `SpannedRepos`).
- After either run, `<out>/index.html` is regenerated as a static
  chooser landing page. Sub-sites that haven't been built into this
  `<out>` show greyed cards.

Exactly one of `--preparer-db` / `--planner-db` is required;
supplying both (or neither) fails with exit code 2. Both pages load
the SQLite database into [sql.js](https://sql.js.org/) in the browser
and require no network at runtime.

## What this is

A zero-server review surface for an entire preparer or planner run.
One `<out>/index.html` chooser, one `<out>/<sub-site>/index.html` per
sub-site, one `<out>/<sub-site>/assets/` folder of vendored
JS/CSS/wasm per sub-site, no network at runtime. Opens straight from
`file://` in Chromium-family browsers. A reviewer (or workgroup
co-chair) can zip the output folder and hand it around without any
infrastructure on the receiving side.

### Hydration

Both processor services (`FhirAugury.Processor.Jira.Fhir.Preparer`
and `FhirAugury.Processor.Jira.Fhir.Planner`) own the hydration
surface for their respective DBs. The inlined DB always carries the
processor-side `*_hydration` and `*_jira_xref` tables alongside the
agent-authored `prepared_tickets*` / `planned_tickets*` tables.

Hydration is populated per-ticket as each ticket is processed, plus
a full sweep at service startup and on demand via
`POST /api/v1/admin/hydration/backfill` against either service.
**`ticket-site` is a pure consumer of an already-hydrated DB** —
when building the discussion sub-site, if `prepared_ticket_hydration`
is missing or empty the tool fails fast with an actionable error
pointing operators at the preparer service. The applying sub-site
runs through `PlannerDbTrimmer` which self-migrates legacy planner
DBs through `PlannerDatabase.EnsureSchema`, so missing planner-side
hydration tables are created (empty) rather than blocking the emit;
the SPA renders `(unknown)` placeholders where hydration columns
would be.

### Related-artifact / related-page surface (discussion sub-site only)

To support the discussion SPA's "By artifact" and "By page" crosscut
columns the emitted preparer DB carries two normalized child tables:

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
columns. The applying sub-site does not consume these tables.

### Prerequisite: hydrated DB

`ticket-site` no longer hydrates anything itself. Each processor
service owns the full hydration surface for its own DB:

- **Discussion sub-site (`--preparer-db`)**: the preparer service
  (`FhirAugury.Processor.Jira.Fhir.Preparer`) must have hydrated the
  DB. Per-ticket hydration runs as each ticket is prepared; a full
  sweep runs at service startup and on demand via
  `POST /api/v1/admin/hydration/backfill`.
- **Applying sub-site (`--planner-db`)**: the planner service
  (`FhirAugury.Processor.Jira.Fhir.Planner`) hydrates the same way
  (planner has the same admin endpoint and the same startup-sweep
  behavior; per-ticket hydration runs after each `ticket-plan` agent
  invocation).

If a preparer DB has no `prepared_ticket_hydration` rows when
`ticket-site` opens it, the tool exits non-zero with:

```
Database '<path>' is not hydrated. Run FhirAugury.Processor.Jira.Fhir.Preparer
against it first (the service hydrates on startup, or POST
/api/v1/admin/hydration/backfill on a running service).
```

The applying sub-site does not fail fast on a missing
`planned_ticket_hydration` table (it self-migrates the schema and
emits a usable site with `(unknown)` placeholders where hydration
columns would surface). Running the planner service's
`POST /api/v1/admin/hydration/backfill` against the DB first is
still strongly recommended for a useful reviewer experience.

### Topic surface

Both sub-sites carry their respective agent / orchestrator topic
layers:

- **Discussion sub-site**: `prepared_ticket_topics`,
  `prepared_ticket_topic_groups`, `prepared_ticket_topic_members`
  (no spanned-repo concept). Topics are written by the preparer-side
  `orchestrate-topic-groupings` skill.
- **Applying sub-site**: `planned_ticket_topics`,
  `planned_ticket_topic_groups`, `planned_ticket_topic_members`,
  and a new first-class `planned_ticket_topic_repos` table that
  captures each topic's coordinated `SpannedRepos` set (e.g.
  `HL7/fhir` + `HL7/fhir-extensions` + `HL7/UTG` for a
  cross-spec change). The planner topic populator
  (`orchestrate-planner-topic-groupings`) is follow-on work; until
  it lands, the applying sub-site greys out `Show Topic List →`
  with a tooltip and the `#/topics` route renders an empty-state
  message rather than a list.

When at least one topic row survives the trim, the landing page
renders a `Show Topic List →` affordance next to `Show Ticket List →`.
The topic list views are sortable on the natural columns; both link
into a per-topic detail (`#/topic/<id>`). The applying topic detail
adds a `Spanned repos` section above the member-ticket list.

When the inlined DB has zero topic rows (older preparer DBs, or a
trim that removed every topic's members), the affordance renders
as a greyed-out span with a `title="No topics in this run."`
tooltip rather than a live link. The orphan removal that keeps
this honest runs inside the same trim transaction, so a filtered
run never ships topics whose only members were trimmed away.

Per-ticket detail pages render one `Member of topic: <ShortDescription>`
line per topic membership (sorted by short description). Tickets
that aren't in any topic show no such line.

## What this is not

Not a server, not a daemon, not a long-running process. Not a
search index — just substring filtering on Key / Title /
RequestSummary. Not incremental — every run overwrites `<out>/`.
Not a planner or ballot-notes site (each of those would be a
sibling tool under `tools/`).

## Quick start

```bash
dotnet run --project tools/ticket-site -- \
  --preparer-db ./cache/jira-preparer.db \
  --out ./cache/jira-ticket-site \
  --spec 'FHIR Core (FHIR)' \
  --title "Discussion — May 2026"
```

Or, for the planner-side applying sub-site:

```bash
dotnet run --project tools/ticket-site -- \
  --planner-db ./cache/jira-planner.db \
  --out ./cache/jira-ticket-site \
  --title "Applying — May 2026"
```


Building both into the same `--out` is the expected workflow — the
chooser at `<out>/index.html` will then surface both cards as live.

`--spec` matches the hydrated `Specification` value (Jira
`customfield_11302`, e.g. `'FHIR Core (FHIR)'` or `fhir-extensions`);
`--project` matches the Jira project key (e.g. `FHIR`). The two are
distinct: a ticket in the `FHIR` Jira project may have any
`Specification` value, including blank.

Open `cache/jira-ticket-site/index.html` in a Chromium-family
browser. You should see the chooser landing page with a
**Tickets for Discussion** card and a **Tickets for Applying** card;
cards whose sub-site hasn't been built into this `<out>` render
greyed-out and are unclickable. Live cards link into the respective
sub-site at `<out>/discussion/index.html` or
`<out>/applying/index.html`.

Each sub-site is its own self-contained SPA:

- **Discussion sub-site landing** shows `N prepared tickets in this
  run.` plus `Show Ticket List →` (and `Show Topic List →` when the
  run carries any topic rows), followed by a grid of crosscut summary
  tables (workgroup, type, artifact, page, impact, specification).
- **Applying sub-site landing** shows the planned-ticket count, a
  `Show Ticket List →` shortcut, a tickets-by-repo summary, and a
  topics-by-spanned-repo summary (when topics exist). Per-ticket
  pages render one section per repo with that repo's changes,
  impacts, change validations, testing considerations, and open
  questions; per-topic pages render the spanned-repo list and the
  member tickets (grouped or remaining).

Both sub-sites honor the same chip composition (`wg:`, `spec:`,
`project:` from the generation-time flags) and link to per-ticket
pages from row clicks. **No cross-sub-site links** are emitted on
per-ticket pages — that's an intentional `[decided]` hard boundary
in the feature request.

## Filter chips

The landing page, the list view, and crosscut sub-views in each
sub-site share a single filter banner. Each active filter is a chip
with the shape `dim: value`:

- **Generation chips** (`spec:`, `project:`, `wg:`) come from the
  build-time flags and are baked into the trimmed DB. They render
  without an `×` button — the underlying data is already trimmed so
  the chip cannot be removed.
- **In-page chips** (`wg:`, `artifact:`, `page:`, `spec:`, `impact:`,
  `type:`) come from clicking a row in a filterable crosscut column
  on the **discussion** sub-site. They render with an `×` button
  that drops just that one chip and re-renders the view. `type:` is
  in-page-only — there is no `--type` generation-time flag. The
  applying sub-site does not currently surface in-page chips beyond
  the build-time generation chips.

## Ticket list views

### Discussion sub-site (`<out>/discussion/`)

The `#/list` view (and any crosscut redirect into it) renders a
seven-column table: `Key`, `Title`, `Workgroup`, `Status`, `Type`,
`Impact A`, `Impact B`. The per-ticket `Recommendation` prose and the
`SavedAt` timestamp are intentionally not surfaced here — both remain
visible on the per-ticket detail page.

Each column header is clickable (or activatable with Enter/Space) to
sort the post-filter row set by that column. The first click sorts
ascending; clicking the active column again toggles to descending. The
active column shows a `▲` / `▼` glyph and an `aria-sort` attribute.
The `Key` column uses a numeric-aware compare so `FHIR-5079` sorts
before `FHIR-50710`. Sort state is per-mount: leaving and re-entering
the list view resets the default sort to `Key` ascending.

Chips compose with logical AND, encoded in the URL hash as a
`?dim=value&dim2=value` suffix
(e.g., `#/list?wg=Patient%20Administration&artifact=Observation`).
This encoding is internal to the SPA and not a stable external link
contract.

Filterable crosscut columns auto-hide on the landing grid when their
own dimension is already pinned by a chip OR has only one distinct
non-`(unknown)` value in the post-chip data set. The `(unknown)`
pseudo-value on a crosscut row toggles a chip with literal value
`(unknown)`, which matches no tickets and is mainly useful for
confirming a column has been auto-hidden because all non-unknown
values were already pinned.

### Applying sub-site (`<out>/applying/`)

The applying `#/list` view renders a six-column table: `Key`,
`Title`, `Workgroup`, `Spec`, `Repos`, `Changes`. `Title` /
`Workgroup` / `Spec` come from the self-Jira hydration row
(`planned_jira_hydration` where `JiraKey = IssueKey`) and display
`(unknown)` when the row is missing. `Repos` is the
comma-separated `planned_ticket_repos.RepoKey` set per ticket;
`Changes` is the `planned_ticket_repo_changes` count. Each `Key`
links into the per-ticket detail (`#/ticket/<key>`), which groups
content by repo (changes, impacts, validations, testing
considerations, open questions).

## Flags

| Flag | Default | Notes |
|------|---------|-------|
| `--preparer-db <path>` | `./cache/jira-preparer.db` (only applied if the flag is supplied) | Builds the **discussion** sub-site under `<out>/discussion/`. Must already be hydrated (the preparer service handles that — see [Prerequisite: hydrated DB](#prerequisite-hydrated-db)). Mutually exclusive with `--planner-db`. |
| `--planner-db <path>` | `./cache/jira-planner.db` (only applied if the flag is supplied) | Builds the **applying** sub-site under `<out>/applying/`. Older planner DBs self-migrate during the trim step. Mutually exclusive with `--preparer-db`. |
| `--out <path>` | `./cache/jira-ticket-site` | Output root. Contains the chooser `index.html` plus whichever sub-site folders have been built. Sub-site delete is per-folder, so building one sub-site never wipes the other. |
| `--title <string>` | `"Ticket Site"` | Threads through to each sub-site's `<title>` and landing `<h1>` (HTML-encoded). When any filter is active, an automatic ` (filtered: …)` suffix is appended. |
| `--spec <name>` | — | Filter to tickets whose hydrated `Specification` matches (case-insensitive). On the discussion side this matches `prepared_ticket_hydration.Specification`; on the applying side it matches the self-Jira `planned_jira_hydration.Specification`. |
| `--project <key>` | — | Filter to tickets in the given Jira project key (case-insensitive). |
| `--wg <name\|code>` | — | Filter to tickets in the given workgroup. Matches the workgroup `Name` recorded on the processor-side ticket first; on miss, resolves the input as a workgroup code or clean name via the Jira source service (HTTP `--jira-source`, then `--jira-source-db`). |
| `--jira-source <url>` | `http://localhost:5160` | Base URL of the Jira source service for `--wg` code/clean-name resolution. |
| `--jira-source-db <path>` | — | Jira source SQLite DB. Fallback for `--wg` resolution when the HTTP service is unreachable, and the source for the discussion sub-site's "By artifact" and "By page" crosscut columns (planner side does not consume these tables); without it those discussion-side tables are present but empty. |
| `--force` | `false` | Overwrite a sub-site directory whose recorded filter set differs from the current run's (see "Output directory safety rail"). |
| `--help` | — | Print usage and exit 0. |

### Active filters

```bash
# Workgroup by code, falling back through the default Jira source service.
dotnet run --project tools/ticket-site -- \
  --preparer-db ./cache/jira-preparer.db \
  --out ./cache/jira-ticket-site-fhir-i \
  --wg fhir-i

# Specification filter only.
dotnet run --project tools/ticket-site -- \
  --preparer-db ./cache/jira-preparer.db \
  --out ./cache/jira-ticket-site-fhir-extensions \
  --spec fhir-extensions

# Spec filter against the planner DB.
dotnet run --project tools/ticket-site -- \
  --planner-db ./cache/jira-planner.db \
  --out ./cache/jira-ticket-site-fhir-core \
  --spec FHIR
```

When a filter flag is supplied the inlined DB is trimmed to just the
surviving tickets and their related rows; the active filter set is
also surfaced in the page `<title>` and as a non-removable
generation chip in the banner on every view except the per-ticket
detail page. If the filter set ANDs to zero rows the site is still
emitted and the landing view shows `0 prepared tickets match this
filter.` instead of the usual count line.

## Output size

Each sub-site's `index.html` carries its full processor DB
(including hydration tables and inlined `DescriptionPlain` per
ticket on the discussion side) as a single base64-inlined blob, so
the file size is roughly

```
indexHtml ≈ dbSize × 4 / 3 + ~100 KB chrome
```

At today's volume (~3,900 prepared tickets) the hydrated preparer
DB lands in the 70–90 MB range. The planner DB is typically smaller
since it carries no ticket-level prose. Chromium and Firefox handle
both file sizes fine; Safari may struggle (see
[Browser compatibility](#browser-compatibility)). For distribution,
zip the output folder. With one or more filter flags supplied, the
inlined DB shrinks roughly in proportion to the surviving ticket
count (both `PreparerDbTrimmer` and `PlannerDbTrimmer` trim the
core ticket table and every per-ticket child table and run
`VACUUM` before the bytes are inlined).

The chooser at `<out>/index.html` is plain HTML (a couple KB) and
carries no DB blob.

## Browser compatibility

Tested in Chromium-family browsers (Chrome, Edge, Brave) opened
directly from `file://`. Safari may refuse to load WebAssembly from
`file://`; if you hit that, serve the output directory and open
`http://localhost:8000/` instead:

```bash
cd ./cache/jira-ticket-site
python3 -m http.server
```

## Vendored assets

Both sub-sites rely on a vendored copy of
[sql.js](https://github.com/sql-js/sql.js) to load the embedded
SQLite database in the browser. The bytes ship as embedded
resources in the C# project under `web-assets/shared/` and are
copied into **each sub-site's** `<out>/<sub-site>/assets/` folder
on emit so each sub-site is fully self-contained.

- Release: [`sql-js/sql.js` v1.10.3](https://github.com/sql-js/sql.js/releases/tag/v1.10.3)
- Asset: `sqljs-wasm.zip` (contains `sql-wasm.js` + `sql-wasm.wasm`)
- License: MIT
- SHA-256:
  - `sql-wasm.js`&nbsp;&nbsp; `558a72c3ab3415d0e6d243cfd23f9d61543600d59054b4b7b8da3cd65f6b9fd4`
  - `sql-wasm.wasm` `d7e61b828523001f26ce0b3f88dabcf6c12e5e6edf80eb4f08b26ac7b946ff88`

To refresh:

1. Download `sqljs-wasm.zip` from the chosen `sql-js/sql.js` release.
2. Extract `sql-wasm.js` and `sql-wasm.wasm` into
   `tools/ticket-site/web-assets/shared/`.
3. Update the SHA-256 values above (`shasum -a 256 sql-wasm.*`).
4. Rebuild (`dotnet build tools/ticket-site/ticket-site.csproj`).

No automated update path is planned.

### Markdown rendering (applying sub-site)

The applying (planned) sub-site renders long-form prose fields
(Resolution summary, Feature proposal, Design rationale, and per-repo
prose) as Markdown. Two more libraries ship the same way — embedded
under `web-assets/shared/` and copied into **each sub-site's**
`assets/` folder on emit:

- [`marked`](https://github.com/markedjs/marked) **18.0.5** —
  vendored as `marked.min.js` (the package's `lib/marked.umd.js`
  UMD/global build; marked 18 ships no separate minified browser
  build). Parses Markdown to HTML. License: MIT.
  - SHA-256: `2dc4769dfde29f51c7aca1a539c6407c789c8ea644cf8b7d01ded28a9c1d800b`
- [`DOMPurify`](https://github.com/cure53/DOMPurify) **3.4.8** —
  vendored as `purify.min.js` (`dist/purify.min.js` UMD build).
  Sanitizes the rendered HTML before it reaches `innerHTML`; it is
  the single sanitization layer for untrusted ticket prose.
  License: Apache-2.0 / MPL-2.0.
  - SHA-256: `b656113abe5f9b5f2c30c8b10462b7b4e947e10a49f561058822bc48e9601b4a`

Identifiers, keys, titles, file paths and code blocks stay HTML-escaped;
only authored prose is routed through `marked` + `DOMPurify`. To refresh,
re-download the pinned versions into `web-assets/shared/`, update the
hashes above, and rebuild.

## Known limitations

- **No incremental mode for a single sub-site.** Every run rewrites
  its `<out>/<sub-site>/` from scratch, subject to the safety rail
  (see below). Building the *other* sub-site never touches the
  already-emitted sub-site folder.
- **Output directory safety rail.** Every emitted sub-site drops a
  `.ticket-site.meta` JSON marker inside its sub-site folder. The
  marker records the canonical filter set and a `kind` field
  (`preparer` or `planner`) for defense-in-depth integrity checking.
  A subsequent run against the same sub-site folder whose filter
  set differs (e.g., re-running a `--project FHIR` build into a
  folder that previously held a `--wg CDS` build) refuses with a
  stderr diagnostic; pass `--force` to overwrite anyway. Pre-existing
  sub-site directories with no marker (i.e., produced by an earlier
  version of the tool) are overwritten without `--force`. The
  chooser at `<out>/index.html` has no marker — it is a derived
  artifact and is unconditionally regenerated every run.
- **Substring filter only.** The discussion sub-site's list view
  does a debounced 150 ms case-insensitive substring match against
  `Key + Title + RequestSummary`. There is no full-text index, no
  fuzzy match, no field-targeted search. The applying sub-site does
  not currently surface in-page search.
- **No cross-sub-site links.** Per-ticket pages do not link from one
  sub-site to the other, even when the same ticket key is present in
  both DBs. This is a `[decided]` hard boundary (reviewers open two
  tabs).
- **No diff between runs**, no two-DB comparison.
- **No theming / dark-mode toggle** beyond honoring
  `prefers-color-scheme`.
- **Planner topic populator is follow-on work.** Until
  `orchestrate-planner-topic-groupings` ships, the applying
  sub-site greys out `Show Topic List →` (the schema + write
  endpoint + reviewer UI are all in place; only the producer is
  missing).

