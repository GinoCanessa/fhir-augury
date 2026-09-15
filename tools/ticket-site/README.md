# ticket-site

`ticket-site` is the offline, network-independent command-line adapter over
`FhirAugury.Publishing.Tickets`. It renders a static review site from one
supplied, verified Preparer or Planner snapshot pair. It never opens a live
processor database, downloads a snapshot, starts a publication refresh,
contacts the Preparer, Planner, Orchestrator, or Jira, or owns processor
completion state.

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

Preparer snapshot schema and Discussion renderer schema are independent
contracts. The current safe public processor output is **Preparer snapshot
schema v3**. The adapter validates that immutable input, then creates a
publication-owned **Discussion renderer schema v2** browser database. A
renderer version never changes the supplied snapshot in place.

For a provenance-complete schema-v2 or schema-v3 Preparer snapshot, the
discussion display title appends the latest successful upstream Jira refresh
represented anywhere in the retained corpus, for example
`Tickets for Discussion - Built September 08, 2026`. Every retained ticket in
either schema must resolve to exactly one accepted authoring coordinate. Zero
or multiple matches are invalid snapshot structure and abort publication; they
are not treated as unavailable freshness. Once that structure is valid, the
publisher requires both run provenance and parent-ticket hydration to carry
stable Jira freshness coordinates and uses the latest complete value. `Built`
therefore describes Jira input freshness, not authoring completion, snapshot
creation, or site publication time.

Legacy Preparer schema-v1 and schema-v2 snapshots remain readable
compatibility inputs, but every generated publication reports degraded
readiness and exposes no trusted people from them. Schema v2 can still qualify
the `Built` date when its ordinary provenance is complete; schema v1 and
otherwise incomplete provenance use the unsuffixed base title. The legacy
pair is never upgraded or edited. An explicit `--title` remains the stable
base; the freshness suffix and then any filter suffix are added only to the
display title. Applying remains unchanged: Planner snapshots stay on schema
v1, receive no freshness suffix, and retain `Ticket Site` as the omitted-title
CLI default.

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

Discussion publication accepts immutable Preparer snapshot schemas v1, v2,
and v3 and projects each into a newly created **renderer schema v2** SQLite
database. Its `ReadinessJson` and the manifest's structured
`discussionReadiness` carry the same `isReady`, `evidence`, optional source
revision and people-policy coordinates, and machine-readable reasons. Legacy
input includes
`legacy-snapshot-schema` and `missing-people-policy-proof`; missing ordinary
provenance or an invalid refresh proof adds its corresponding reason. Reporter
and Assignee are rendered with that unavailability reason when policy proof is
missing. `Not provided` is reserved for a policy-qualified v3 value that is
legitimately absent. The browser never queries processor persistence tables.
Applying continues to consume Planner snapshot schema v1 through its existing
path.

The output contains the selected sub-site, an embedded SQLite database, the
exact manifest name `site-manifest.json`, ownership metadata, and a chooser
page when either sub-site exists. For Discussion, manifest `title` remains the
stable base used by workflow validation, while `displayTitle`,
`jiraSourceLastSuccessfulRefreshAt`, `rendererSchemaVersion`, and
`discussionReadiness` carry the presentation contract. `tableCounts`
describes the embedded renderer database, not the canonical Preparer snapshot.
The remaining fields record source snapshot identity and digest, included
counts, filters, renderer assets version, build identity, and output path.

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

Publication repair happens before this adapter is invoked. Use the Dev UI,
typed CLI, or HTTP API to create and complete a linked Preparer
`publication-refresh` run, download that new run's verified pair, and then
supply the pair to `ticket-site`. The adapter renders exactly that pair and
publishes files locally; it does not call the Preparer, Orchestrator, or Jira
to validate, refresh, or supplement its contents. Legacy loose-file arguments
remain a compatibility entry point only because the adapter first captures
them into an immutable, service-bound verified pair.

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
