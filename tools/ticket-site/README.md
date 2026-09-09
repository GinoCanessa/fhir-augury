# ticket-site

`ticket-site` renders a static review site from one trusted processor
snapshot. It never opens a live processor database or contacts Jira while
building.

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
processor kind, schema, counts, provenance, and monotonic sequence are
validated before publication. Rendering uses an immutable private copy and
publishes through the staged directory publisher, so a failed or older build
cannot replace a valid newer site.

The output contains the selected sub-site, an embedded filtered SQLite
snapshot, `site-build-manifest.json`, ownership metadata, and a chooser page
when both discussion and applying sites exist.

## Static hosting

Upload the complete generated output directory, including every `assets`
folder. Generated HTML adds the renderer version to asset URLs so a CDN or
browser cannot combine a newly generated embedded database with stale
JavaScript or WebAssembly.

For a deployment created before asset versioning was added, purge the CDN
cache once after replacing the complete output. A stale discussion `app.js`
does not inflate the current gzipped database and reports
`Failed to load database: file is not a database`.
