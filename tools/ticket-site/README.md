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
| `--title <text>` | Site title. |
| `--spec <value>` | Filter by specification using snapshot-resident data. |
| `--project <value>` | Filter by project using snapshot-resident data. |
| `--wg <value>` | Filter by workgroup using the snapshot workgroup catalog. |
| `--force` | Permit replacement of an owned compatible output. |

Exactly one snapshot option is required. The descriptor filename, checksum,
processor kind, service/workflow binding, schema, counts, provenance, and
monotonic sequence are validated before publication.

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

The output contains the selected sub-site, an embedded filtered SQLite
snapshot, the exact manifest name `site-manifest.json`, ownership metadata,
and a chooser page when both discussion and applying sites exist. The manifest
records source snapshot identity and digest, included counts, filters, title,
renderer assets version, build identity, and output path.

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
JavaScript or WebAssembly.

For a deployment created before asset versioning was added, purge the CDN
cache once after replacing the complete output. A stale discussion `app.js`
does not inflate the current gzipped database and reports
`Failed to load database: file is not a database`.
