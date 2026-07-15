# fhir-xver-element-diff

A read-only fhir-augury tool that diffs the **element trees** of the FHIR core
releases across each pairwise increment — **R4 → R4B → R5 → R6** — and emits one
markdown change-report per increment. Every changed element is flagged (added /
removed / renamed / cardinality / type / target) and **attributed** to the FHIR
Jira ticket(s) — or, failing that, the commit(s) — that produced it.

The element facts come entirely from the two published spec SQLite DBs (never
from parsing the running build); the git clone and Jira DB are used only to
*attribute* changes, never to compute them. Attribution is best-effort
enrichment layered on top of the exact change tables — a missing clone or Jira
DB degrades the change record to blank, it never changes which elements are
reported.

## What it produces

For each increment it writes `<slug>.md` (`r4-r4b.md`, `r4b-r5.md`,
`r5-r6.md`) containing:

- A resolved header block — both releases' versions + build dates, the git
  window `(since, until)` anchors, the clone `HEAD`, and (for R5→R6) the
  ballot4 snapshot note.
- `## Mapped` / `## Removed` / `## Added` sections, each split into
  `### Primitive types` / `### Complex types` / `### Resources`.
- One `####` heading per changed structure (rename-aware, e.g.
  *Ingredient (renamed from MedicinalProductIngredient)*), then a 10-column
  table: source/target element, the six change flags (`Y` / `Y?` for a
  suspected rename), a human summary, and the **Change record** (ticket / commit
  links). Structures with no element changes are omitted.

## Data sources

| Input | Default path | Role |
|-------|--------------|------|
| R4 / R4B / R5 spec DB | `./cache/fhir-spec.db` | Element facts for the three published releases (package rows R4=`4.0.1`, R4B=`4.3.0`, R5=`5.0.0`). Read-only. |
| R6 spec DB | `./cache/fhir-r6.db` | Element facts for R6 **6.0.0-ballot4** (a separate same-schema DB; R6 is *not* in `fhir-spec.db`). Read-only. |
| HL7/fhir clone | `./cache/github/repos/HL7_fhir/clone` | Git window walk for attribution (`git log`/`ls-tree`). Best-effort. |
| Jira DB | `./cache/jira.db` | The `FHIR-N` key allowlist (`jira_issues` where `ProjectKey='FHIR'`) used to reject bogus ticket tokens. Best-effort. |

All four live under `cache/` and are gitignored, upstream-produced reference
data.

## Usage

The tool is built by path (it is **not** a member of `fhir-augury.slnx`):

```
dotnet run --project .\tools\fhir-xver-element-diff\fhir-xver-element-diff.csproj -c Release -- \
    --increment all --out .\scratch\reports
```

Single increment with an overridden git window (overrides apply only when a
single `--increment` is selected):

```
dotnet run --project .\tools\fhir-xver-element-diff\fhir-xver-element-diff.csproj -- \
    --increment r5-r6 --since eca054db --until 94dbe68f --out .\scratch\reports
```

Change tables only, skipping the git/Jira attribution pass:

```
dotnet run --project .\tools\fhir-xver-element-diff\fhir-xver-element-diff.csproj -- \
    --increment all --no-attribution --out .\scratch\reports
```

Per-release count smoke command (no reports written):

```
dotnet run --project .\tools\fhir-xver-element-diff\fhir-xver-element-diff.csproj -- --dump R6
```

### Options

| Flag | Default | Notes |
|------|---------|-------|
| `--dump <release>` | — | Print structure/element counts for one release (`R4`/`R4B`/`R5`/`R6`) and exit. |
| `--increment <sel>` | `all` | `all` \| `r4-r4b` \| `r4b-r5` \| `r5-r6`. |
| `--out <dir>` | `./scratch/0714-03/reports` | Output directory for the `<slug>.md` reports. |
| `--no-attribution` | off | Emit the change tables only; skip the git/Jira attribution pass. |
| `--since <sha>` | *(per increment)* | Override the git window start. Single-increment only. |
| `--until <sha>` | *(per increment)* | Override the git window end. Single-increment only. |
| `--fhir-spec-db <path>` | `./cache/fhir-spec.db` | R4/R4B/R5 spec DB. |
| `--fhir-r6-db <path>` | `./cache/fhir-r6.db` | R6 spec DB. |
| `--jira-db <path>` | `./cache/jira.db` | Jira DB for the `FHIR-N` allowlist. |
| `--clone <path>` | `./cache/github/repos/HL7_fhir/clone` | HL7/fhir clone. |

### Default git windows

The default `(since, until)` anchors track the **first-parent `master`** line of
the clone (release tags do not cover R4/R4B, and `v5.0.0` sits off first-parent —
see the plan's anchor table):

| Increment | since | until |
|-----------|-------|-------|
| `r4-r4b` | `b6357157` (R4 GA anchor) | `d685d85` (R4B GA) |
| `r4b-r5` | `959acd13` | `eca054db` (R5 GA, `v5.0.0`) |
| `r5-r6` | `eca054db` | `94dbe68f` (clone `HEAD`) |

## Rename detection

Structure and element renames are resolved in layers so a renamed resource or
field diffs correctly instead of surfacing as an unrelated remove + add:

- **Structure renames** — a curated map plus a resolved-ticket signal produce a
  *confirmed* rename (e.g. `DeviceUseStatement → DeviceUsage`); weaker matches
  render as *suspected* (`⚠ suspected`, `Y?`). Elements are then diffed by a
  normalized, root-relative, choice-`[x]`-folded key, not the raw path.
- **Element renames** — residual per-structure add/remove leftovers are paired
  into element renames and choice split/merge notes.

## Attribution

Each changed row's **Change record** is filled in two tiers:

1. **Structure window (default).** For each changed structure, the tool walks
   `git log <since>..<until>` over that structure's `source/` file(s) and
   extracts the FHIR tickets its authoring commits cite — prefixed `FHIR-N`,
   `J#N` / `FHIR#N` aliases, `/browse/` URLs, and the HL7-specific bare `#N`
   form — each validated against the Jira allowlist. When authoring commits cite
   nothing, the enclosing PR-merge subject / `Branch_<n>` is harvested; failing
   that, the newest few commit short-hashes are used. This shared record is the
   default for every one of the structure's rows.
2. **Per-element (hybrid) refinement.** The same commits' diffs are parsed, and
   when a single commit cleanly isolates one element **and** changes a
   parseable facet, that element's row is attributed to *that* commit's
   ticket(s) instead — strictly sharpening precision over the shared record.

Tickets are always preferred over commit hashes, and a per-element refinement
only ever **replaces a window ticket with a more specific ticket**, never with a
bare hash — so precision improves without ever losing ticket information.

### Precision & limitations

- **Facet scope of the per-element tier.** Only **cardinality** (`<min>`/`<max>`)
  and **structural** add/remove (a changed `<path>` line) are refined per
  element — these are reliably tied to their element via the in-context
  `<path>` line. **Type/target-only** rows keep the structure-window record
  (isolating a nested `<type>`/`<targetProfile>` change from a raw hunk is too
  fuzzy to attribute safely). The `<base>` and `<slicing>` sub-blocks (which
  carry their own path/min/max) are skipped.
- **Isolation guard.** A commit touching more than four distinct element paths
  is treated as a broad sweep and is not used for per-element attribution (it
  still contributes to the structure-window record). The newest qualifying
  commit wins.
- **R6 = ballot4, and the moving `until`.** The R6 change tables reflect the
  frozen **6.0.0-ballot4** DB, while the R5→R6 `until` is the clone `HEAD`,
  which runs *past* the ballot4 snapshot commit (~2026-06-24). Per-element
  cardinality attribution is therefore verified against the DB's target value:
  a commit that over-wrote the element **after** the snapshot is rejected, and
  the newest commit at/under the snapshot wins.
- **Pre-migration spreadsheet form.** Structures whose source is still the
  legacy `<name>-spreadsheet.xml` (rather than `structuredefinition-<Name>.xml`)
  match none of the per-element diff patterns, so those rows fall back to the
  structure-window record.
- **R4B side-branch.** R4B GA (`d685d85`) sits on a side branch off the R4-era
  line; a handful of structures removed before the R4 anchor (the
  `MedicinalProduct*` family) have no in-window source history and so render a
  blank change record. This is expected — attribution never gates the change
  tables.

## Tests

```
dotnet test .\tests\FhirAugury.Tools.FhirXverElementDiff.Tests\FhirAugury.Tools.FhirXverElementDiff.Tests.csproj
```

Structure/element diffing, rename detection, markdown rendering, the ticket
extraction rules, and the per-element patch parser + isolation/snapshot-gate
selection are all covered by fast unit tests (no DB or git required).
`[Trait("Category","LiveDb")]` smoke tests exercise the real cache DBs and clone
when present and skip cleanly when they are absent.
