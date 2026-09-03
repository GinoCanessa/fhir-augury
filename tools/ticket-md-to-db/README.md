# `ticket-md-to-db`

> [!WARNING]
> This is a temporary, migration-only recovery tool for rebuilding a preparer
> database from the historical Markdown corpus. It is not a supported ingestion
> API and must not become part of normal ticket preparation. Remove the tool and
> its stale-path documentation after the recovery artifact has been accepted.

The tool compiles ticket-review Markdown into a deterministic manifest, writes
the records through `PreparerDatabase`, hydrates them through the Orchestrator's
HTTP API, certifies the complete persisted state, and promotes a closed SQLite
database together with a digest-bound audit.

## Prerequisites

- .NET 10.
- The Jira source service and Orchestrator running and reachable for write mode.
- A quiescent destination and its consumers when replacing a database.
- The destination, audit, staging, and backup locations on one volume.

The tool never opens the Jira source database. Hydration goes through the
Orchestrator, and the local `jira_processing_source_tickets` projection is
written only into the candidate preparer database.

## Usage

```powershell
dotnet run --project tools\ticket-md-to-db -- `
  --input C:\path\to\prep `
  --db C:\path\to\recovery\prepared.db `
  --orchestrator http://localhost:5150 `
  --expected-count 2391 `
  --audit C:\path\to\recovery\prepared.db.import-audit.json `
  --override-template C:\path\to\recovery\prepared.db.override-template.json `
  --dry-run
```

Run a successful full-corpus dry run before removing `--dry-run`. Review and,
where necessary, fill an override file generated from the template:

```powershell
dotnet run --project tools\ticket-md-to-db -- `
  --input C:\path\to\prep `
  --db C:\path\to\recovery\prepared.db `
  --orchestrator http://localhost:5150 `
  --expected-count 2391 `
  --overrides C:\path\to\recovery\overrides.json
```

### Options

| Option | Meaning |
|-|-|
| `--input <root>` | Root recursively searched for `FHIR-*.md`. Required. |
| `--db <path>` | Final preparer database path. Required, including dry run. |
| `--orchestrator <url>` | Explicit HTTP(S) Orchestrator base URI. Required. |
| `--expected-count <n>` | Exact report count. A mismatch blocks the run. |
| `--overrides <json>` | Optional fingerprint-bound scalar overrides. |
| `--audit <json>` | Final audit path. Defaults to `<db>.import-audit.json`. |
| `--override-template <json>` | Missing-field review template. Defaults to `<db>.override-template.json`. |
| `--dry-run` | Compile and write JSON evidence without creating the database. |
| `--replace-existing` | Replace a complete destination instead of refusing it. Write mode only. |
| `--accept-unresolved-hydration` | Promote only otherwise-valid unresolved/incomplete self hydration as `degraded-accepted`. Write mode only. |

`--replace-existing` never merges rows. A destination either remains untouched
or is replaced as a whole.

## Path safety

Paths are normalized with `Path.GetFullPath`, compared case-insensitively on
Windows, and resolved through existing symbolic-link or junction targets where
available. In write mode, every database, SQLite sidecar, audit, template,
staging, backup, candidate-audit, recovery, and promotion-lock path must:

- be distinct from every other writable path;
- be outside the Markdown source tree;
- not equal the overrides file or any source report; and
- keep the destination, audit candidates, staging, and backups on one volume.

The staging name is `<db>.<run-id>.staging`. It and its `-wal`/`-shm` files must
not already exist; crash residue is never reopened. Dry runs retain the
historical ability to place JSON evidence below an isolated fixture root, but
still reject exact collisions with source Markdown or the overrides input.
Operators should keep all real-corpus evidence outside the source tree in both
modes.

## Override schema

Overrides may change named scalar prepared-ticket fields only. They cannot
change a Jira key, `SavedAt`, or child collection. Every ticket entry requires
the source report SHA-256 and a human review reason:

```json
{
  "tickets": {
    "FHIR-12345": {
      "sourceSha256": "64-lowercase-hex-characters",
      "reviewReason": "Confirmed against the ballot disposition",
      "fields": {
        "Recommendation": "B",
        "ProposalAImpact": "Not assessed"
      }
    }
  }
}
```

Duplicate properties, unknown fields, stale fingerprints, missing review
reasons, and invalid impact/recommendation categories are blocking.

## Markdown dialects and evidence

Normal reports use `canonical-v1`. Six bounded adapters preserve known
historical layouts:

- `legacy-fhir-10333`
- `legacy-fhir-10654`
- `legacy-fhir-12563`
- `legacy-fhir-13634`
- `legacy-fhir-16662`
- `legacy-fhir-17156`

Every report must match exactly one dialect. The manifest records original file
bytes by SHA-256, scalar source state and exact Markdown spans, child-reference
evidence and merged justifications, applied overrides, and a non-overlapping
classification of the entire source document. Unknown layouts, ambiguous
layouts, unaccounted ranges, and multiply consumed ranges block the run.

`Not assessed` is used only when historical Markdown omitted a Proposal A or B
impact. Required prose without source text uses
`Not present in source Markdown`. A missing or ambiguous recommendation always
requires a reviewed override.

## Deterministic manifest and run envelope

The manifest contains only deterministic compile facts:

- parser schema version and manifest ID;
- sorted source files and source hashes;
- sorted normalized payloads;
- scalar/child provenance and source-range dispositions; and
- sorted diagnostics.

Equivalent source bytes and overrides produce the same manifest ID regardless
of root directory or run time.

The run envelope intentionally contains nondeterministic operational facts:
absolute paths, run ID and times, `importedAt`, hydration outcomes, readiness
failures, database digest, promotion classification, and retained recovery
paths. Whole audit files are therefore not expected to be byte-identical.

## Write and readiness rules

Write mode recompiles immediately before reserving staging, then:

1. creates a run-unique candidate and initializes the current preparer schema;
2. applies one `importedAt` to every payload and saves Jira keys in order;
3. reads every prepared scalar and child back through a pooling-disabled,
   read-only connection and compares it to the manifest;
4. hydrates each ticket serially through the Orchestrator;
5. projects only usable resolved self-Jira rows into the supported local source
   columns and marks them complete;
6. compares every persisted hydration and local-source field with the returned
   batches;
7. verifies valid impact/recommendation categories, generated identities,
   cleaned workgroups, all workgroup clustering views and partitions,
   `PRAGMA integrity_check`, and `PRAGMA foreign_key_check`; and
8. checkpoints WAL, leaves a sidecar-free closed main file, and hashes it.

`ready` requires resolved parent and self hydration, a parent specification,
non-empty self title/status/type/workgroup/specification, and one of `Comment`,
`Question`, `Technical Correction`, or `Change Request` for every ticket.

`--accept-unresolved-hydration` waives only unresolved/not-found self readiness,
missing required parent/self metadata, and the corresponding absent local
source row. It does not waive structural, validation, deduplication,
deep-readback, key-set, category, integrity, path, or promotion failures. The
result is labeled `degraded-accepted`, prints a warning, and is not
downstream-ready.

## Exit codes and statuses

| Exit | Audit status | Meaning |
|-|-|-|
| `0` | `dry-run-valid` | Compile-only validation succeeded. |
| `0` | `ready` | Certified database and matching final audit were promoted. |
| `0` | `degraded-accepted` | Only explicitly accepted hydration readiness failures remain. |
| `1` | `dry-run-failed` or `failed` | Compilation, certification, hydration, path, cleanup, or promotion failed. |
| `2` | no run | CLI syntax or option validation failed. |

A database is ready only when the final audit exists, has status `ready` (or
explicitly `degraded-accepted`), and its database SHA-256 equals the closed
destination bytes.

## Sidecars, backups, and recovery

Run-specific paths use the following forms:

| Artifact | Path |
|-|-|
| Promotion lock | `<db>.promotion.lock` |
| Staging database | `<db>.<run-id>.staging` |
| Candidate audit | `<audit>.<run-id>.candidate` |
| Failed-run audit | `<audit>.<run-id>.failed` |
| Replaced database backup | `<db>.<run-id>.backup` |
| Digest-matched audit backup | `<audit>.<run-id>.backup` |
| Dry-run, malformed, or mismatched prior audit evidence | `<audit>.<run-id>.prior` |
| Candidate retained during rollback | `<db>.<run-id>.recovery-candidate` |

Promotion holds the zero-state lock with exclusive sharing, rechecks the
destination and audit observed at preflight, and refuses a concurrent run that
would otherwise replace a newly changed destination.

For replacement, the old database is checkpointed and closed first. A prior
audit is paired with the database backup only when its recorded digest matches
the checkpointed old database. Dry-run, malformed, orphaned, or digest-mismatched
audits are retained as unpaired `.prior` evidence.

Database replacement and audit movement are individually atomic; the two files
cannot be process-crash atomic as a pair. Crash interpretation is therefore
digest based:

- database plus matching final audit: usable according to the recorded status;
- database with no final audit or a digest mismatch: **not ready**;
- matched `.backup` database/audit pair: stop consumers, copy both back to their
  final names, then verify the digest before use;
- `.recovery-candidate` plus `.candidate` audit: verify their digest match,
  preserve copies, then move both to the final names while consumers are
  stopped; and
- unpaired `.prior` audit: evidence only, never proof that a database is ready.

Never delete candidate, backup, or prior-audit evidence until the intended
database/audit pair has been restored and independently hashed.

## Recovery artifact lifecycle

Keep the certified database and matching audit immutable. Copy the database to
a separate working path before starting mutable preparer or topic-grouping
workflows, and record the certified SHA-256 from which the copy was made.
`ticket-site` may read the hydrated database directly. Once grouping or another
processor mutates a working copy, the original recovery audit no longer
describes that copy.
