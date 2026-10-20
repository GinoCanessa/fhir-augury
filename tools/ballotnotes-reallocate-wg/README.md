# ballotnotes-reallocate-wg

This maintenance tool recalculates BallotNotes workgroup ownership from a
read-only evidence database and a matching repository checkout. Every write
is submitted as one expected-revision batch to the BallotNotes processor,
which owns the mutation fence and transaction.

## Usage

Preview without writing:

```powershell
dotnet run --project tools\ballotnotes-reallocate-wg -- reallocate `
  --db cache\ballot-notes.db `
  --clone cache\github\repos\HL7_fhir\clone `
  --repo HL7/fhir `
  --dry-run
```

Apply through the processor:

```powershell
dotnet run --project tools\ballotnotes-reallocate-wg -- reallocate `
  --db cache\ballot-notes.db `
  --clone cache\github\repos\HL7_fhir\clone `
  --repo HL7/fhir `
  --processor http://localhost:5174
```

| Option | Description |
|-|-|
| `--clone <path>` | Matching repository checkout. Required. |
| `--db <path>` | Read-only BallotNotes evidence database. |
| `--processor <url>` | BallotNotes processor URL. Required unless `--dry-run` is used. |
| `--repo <owner/name>` | Restrict processing to one repository. |
| `--dry-run` | Print the proposed changes without submitting them. |
| `--github-db <path>` | Read-only GitHub registry database. |
| `--fhir-r6-db <path>` | Read-only R6 specification database. |
| `--fhir-spec-db <path>` | Read-only fallback specification database. |
| `--work-group-hint <value>` | Optional resolver hint. |
| `--allow-stale-clone` | Permit a checkout whose HEAD differs from persisted evidence. |
| `--allow-mixed-heads` | Permit notes hydrated from multiple HEAD revisions. |

The submitted batch carries every note's observed evidence revision. A stale
revision rejects the complete batch without partial updates. Regenerate the
review site from the next trusted BallotNotes snapshot after maintenance.
