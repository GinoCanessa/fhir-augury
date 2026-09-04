# notes-site

`notes-site` renders the BallotNotes review SPA from a trusted immutable
processor snapshot and its descriptor.

## Usage

```powershell
dotnet run --project tools\notes-site -- report `
  --snapshot-db <snapshot.db> `
  --snapshot-descriptor <snapshot.descriptor.json> `
  --out cache\notes-site
```

Options:

| Option | Description |
|-|-|
| `--snapshot-db <path>` | BallotNotes review snapshot. Required. |
| `--snapshot-descriptor <path>` | Trusted descriptor paired with the snapshot. Required. |
| `--out <dir>` | Output directory. Defaults to `cache\notes-site`. |
| `--title <text>` | Site title. Defaults to `FHIR Ballot Notes`. |
| `--force` | Permit replacement of an owned compatible output. |

The tool copies the input once to private immutable storage, verifies the
descriptor checksum, filename, processor kind, schema, provenance, counts,
and forbidden state, then publishes a self-contained site through the staged
directory publisher. Snapshot sequence and build identity prevent stale or
incompatible promotion.
