# ticket-site

`ticket-site` is the thin command-line adapter over
`FhirAugury.Publishing.Tickets`. It renders a static review site from one
verified Preparer or Planner snapshot pair. It never opens a live processor
database, contacts Jira, or owns processor completion state.

## Usage

Choose exactly one processor snapshot and provide its descriptor:

```powershell
dotnet run --project tools\ticket-site -- `
  --preparer-snapshot <snapshot.db> `
  --snapshot-descriptor <snapshot.descriptor.json> `
  --out cache\jira-ticket-site
```

```powershell
dotnet run --project tools\ticket-site -- `
  --planner-snapshot <snapshot.db> `
  --snapshot-descriptor <snapshot.descriptor.json> `
  --out cache\jira-ticket-site
```

Options:

| Option | Description |
|-|-|
| `--preparer-snapshot <path>` | Preparer review snapshot for the discussion site. |
| `--planner-snapshot <path>` | Planner review snapshot for the applying site. |
| `--snapshot-descriptor <path>` | Trusted descriptor paired with the selected snapshot. |
| `--out <dir>` | Root output directory. Defaults to `cache\jira-ticket-site`. |
| `--title <text>` | Base site title. When omitted, Preparer uses `Tickets for Discussion` and Planner uses the existing `Ticket Site`. |
| `--spec <value>` | Filter by specification using snapshot-resident data. |
| `--project <value>` | Filter by project using snapshot-resident data. |
| `--wg <value>` | Filter by workgroup using the snapshot workgroup catalog. |
| `--force` | Permit replacement of an owned compatible output. |

Exactly one snapshot option is required. The descriptor filename, checksum,
processor kind, service/workflow binding, schema, counts, provenance, and
monotonic sequence are validated before publication.

For a provenance-complete schema-v2 Preparer snapshot, the discussion display
title appends the latest successful upstream Jira refresh represented anywhere
in the retained corpus, for example
`Tickets for Discussion - Built September 08, 2026`. In schema v2, every
retained ticket must resolve to exactly one accepted authoring coordinate.
Zero or multiple matches are invalid snapshot structure and abort publication;
they are not treated as unavailable freshness. Once that structure is valid,
the publisher requires both run provenance and parent-ticket hydration to
carry stable Jira freshness coordinates and uses the latest complete value.
`Built` therefore describes Jira input freshness, not the authoring
completion, snapshot creation, or site publication time.

An old schema-v1 snapshot or a structurally valid v2 corpus with missing, null,
unstable, or partially bound freshness coordinates deliberately produces the
unsuffixed base title. An explicit `--title` remains that stable base; the
discussion freshness suffix and then any existing filter suffix are added only
to the display title. Applying remains unchanged: Planner snapshots stay on
schema v1, receive no freshness suffix, and retain `Ticket Site` as the
omitted-title CLI default.

When the two paths name files in a durable pair directory, the adapter verifies
the directory's `verified-pair.json`. For legacy loose-file input, it captures
an immutable private copy and constructs a temporary service-bound pair before
calling the publisher. Loose files are compatibility input, not durable
publisher state. A ready pair contains exactly:

- the descriptor named by `descriptorFileName`;
- the SQLite database named by `databaseFileName`; and
- `verified-pair.json`, written last and binding the service, run, snapshot,
  format version, filenames, size, and descriptor/database SHA-256 digests.

The shared publisher re-verifies that pair, validates the canonical public
snapshot schema, renders from an immutable private copy, and publishes through
the staged directory publisher. A failed or older build cannot replace a valid
newer site. The Dev UI invokes this same publisher in-process; it does not
spawn `ticket-site`.

Discussion publication accepts immutable Preparer snapshot schemas v1 and v2
and projects either into a newly created **renderer schema v1** SQLite
database. Old v1 input maps new people and freshness data to unavailable; the
browser never queries processor persistence tables. Applying continues to
consume Planner snapshot schema v1 through its existing path.

The output contains the selected sub-site, an embedded SQLite database, the
exact manifest name `site-manifest.json`, ownership metadata, and a chooser
page when either sub-site exists. For Discussion, manifest `title` remains the
stable base used by workflow validation, while optional `displayTitle`,
`jiraSourceLastSuccessfulRefreshAt`, and `rendererSchemaVersion` fields carry
the presentation contract. `tableCounts` describes the embedded renderer
database, not the canonical Preparer snapshot. The remaining fields record
source snapshot identity and digest, included counts, filters, renderer assets
version, build identity, and output path.

The command-line default remains `cache\jira-ticket-site`. The Dev UI instead
uses one output root per workflow/run:

```text
cache\devui-review-sites\prepare\{runId}\discussion\
cache\devui-review-sites\plan\{runId}\applying\
```

Those sites are served at
`/review-sites/{prepare|plan}/{runId}/{discussion|applying}/` on the trusted
local Dev UI origin. Their corresponding verified pairs stay under the private
`cache\devui-authoring-snapshots\` root. A publication failure does not alter
processor state or accepted receipts; retain the pair and rerun only
publication.

## Static hosting

Upload the complete generated output directory, including every `assets`
folder. Generated HTML adds the renderer version to asset URLs so a CDN or
browser cannot combine a newly generated embedded database with stale
JavaScript or WebAssembly. The completed site is self-contained: its browser
reads only the embedded renderer database and static assets and does not call
Jira, the Orchestrator, the Preparer, or any other live service. It can be
opened directly from disk or served from a static host. Discussion HTML embeds
the trusted SQL.js WebAssembly bytes for direct `file://` initialization while
also retaining the standalone WebAssembly asset for hosted compatibility.

For a deployment created before asset versioning was added, purge the CDN
cache once after replacing the complete output. A stale discussion `app.js`
does not inflate the current gzipped database and reports
`Failed to load database: file is not a database`.
