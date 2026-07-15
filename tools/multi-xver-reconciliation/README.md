# Multi-source FHIR cross-version reconciliation

A stdlib-Python ETL that **consolidates eight independent, mutually-inconsistent
sources** of FHIR cross-version element-change data into a single SQLite database
so their discrepancies can be reconciled.

- **Tool location:** `tools/multi-xver-reconciliation/` (this directory; committed)
- **Output database:** `cache/multi-xver-analysis.db` (git-ignored; ~16 MB)
- **Intermediate staging:** `tools/multi-xver-reconciliation/staging/*.jsonl` (git-ignored)
- **Runtime:** Python 3.13+ standard library only — no third-party packages
- **Scope:** 3 adjacent version pairs — **R4→R4B**, **R4B→R5**, **R5→R6** (6.0.0-ballot4)
- **Change dimensions:** Added, Removed, Renamed, Cardinality, Type, Target (+ auxiliary Binding)
- **Constraints honored:** every input is read **read-only**; nothing outside the
  output DB and this tool's `staging/` is written; no network access.

> The **tool scripts** are committed; the **output DB and `staging/`** are
> git-ignored generated artifacts, reproducible at any time by re-running the ETL.
>
> **Origin:** promoted from the one-time prototype in `scratch/0714-03/` (see that
> slot's `plan.md` Progress Log), which was itself a layered request on top of the
> `tools/fhir-xver-element-diff` C# tool.

---

## The eight sources

| Source (`Sources.SourceName`) | What it is | Location | Applies to pairs |
|---|---|---|---|
| `Report`       | xver-report markdown change tables (built earlier this session) | `scratch/xver-report/*.md` | R4→R4B, R4B→R5, R5→R6 |
| `ElementMap`   | ConceptMap element-name maps | `C:\git\fhir-cross-version\input\elements\ConceptMap-elements-*.json` | R4→R4B, R4B→R5 |
| `ResourceMap`  | ConceptMap resource-name maps | `…\input\resources\ConceptMap-resources-*.json` | R4→R4B, R4B→R5 |
| `TypeMap`      | ConceptMap datatype-name maps | `…\input\types\ConceptMap-types-*.json` | R4→R4B, R4B→R5 |
| `Fml`          | FHIR Mapping Language StructureMaps | `…\input\R4BtoR5\*.fml` | R4B→R5 |
| `FhirIni`      | `fhir.ini` `[r5-r6-changes]` section | `C:\specs\fhir\source\fhir.ini` | R5→R6 |
| `DiffJson`     | published-guide diff files | `C:\ai\support\fhir-r4b\*.diff.json`, `…\fhir-r5\*.r4b.diff.json` | R4→R4B, R4B→R5 |
| `ComparisonDb` | local xver-analysis pipeline output | `C:\git\fhir-cross-version\temp\fhir-comparison.sqlite` (opened `mode=ro&immutable=1`) | R4→R4B, R4B→R5 |

**Applicability matters.** A source is *applicable* to a pair only if it has data
for it. `R5→R6` is only covered by `Report` and `FhirIni` — no ConceptMaps, FML,
diff.json, or comparison DB exist for R6. This drives the **tri-state** indicators
below (a source that is *not applicable* to a pair reads `n/a`, not "missed it").

---

## Running the ETL

### Prerequisites

- Python 3.13+ (standard library only — no `pip install` needed).
- The read-only source inputs present on disk (see the source table above). Each
  root can be relocated via an `MXVER_*` environment variable (see "Configuration"
  below) — otherwise the canonical local defaults in `contract.py` are used.
- Windows PowerShell. **Set UTF-8 output** so the console can print the `→`
  characters that appear in the data:

  ```powershell
  $env:PYTHONIOENCODING = "utf-8"
  ```

All scripts live in this directory and import the shared `contract.py`, so run
them **from `tools/multi-xver-reconciliation/`**.

### Run order

```powershell
cd C:\ai\git\fhir-augury\tools\multi-xver-reconciliation
$env:PYTHONIOENCODING = "utf-8"

# 1. (Re)create the output DB schema  (drops & recreates ONLY cache/multi-xver-analysis.db)
python create_schema.py

# 2. Extract every source to staging/*.jsonl  (order among these does not matter)
python extract_report.py          # -> staging/Report.jsonl
python extract_conceptmaps.py     # -> staging/ElementMap.jsonl, ResourceMap.jsonl, TypeMap.jsonl
python extract_fml.py             # -> staging/Fml.jsonl
python extract_fhirini.py         # -> staging/FhirIni.jsonl
python extract_diffjson.py        # -> staging/DiffJson.jsonl
python extract_comparisondb.py    # -> staging/ComparisonDb.jsonl

# 3. Integrate: load staging -> correspondences -> spine -> signals
python build_spine.py

# 4. (Re)create the reconciliation views  (safe to re-run any time)
python create_views.py
```

### Optional helpers

```powershell
# Per-source invariant checks (run with no arg to check all sources)
python validate_staging.py            # or: python validate_staging.py report

# Final reconciliation snapshot: counts, tri-state sanity, coverage %, conflict tallies
python query_summary.py
```

### Configuration (path overrides)

All paths resolve from `contract.py`. The tool's own location, the output DB, and
`staging/` are derived automatically (no editing needed). The read-only source
roots default to their canonical local locations but each accepts an environment
override, so the tool runs on another checkout/machine without code changes:

| Env var | Default | Backs source |
|---|---|---|
| `MXVER_OUT_DB` | `<repo>/cache/multi-xver-analysis.db` | output database |
| `MXVER_STAGING_DIR` | `<tool>/staging` | intermediate JSONL |
| `MXVER_XVER_REPORT_DIR` | `<repo>/scratch/xver-report` | `Report` |
| `MXVER_XVER_INPUT` | `C:\git\fhir-cross-version\input` | `ElementMap`/`ResourceMap`/`TypeMap`/`Fml` |
| `MXVER_FHIR_INI` | `C:\specs\fhir\source\fhir.ini` | `FhirIni` |
| `MXVER_SUPPORT_R4` / `_R4B` / `_R5` | `C:\ai\support\fhir-r4` / `-r4b` / `-r5` | `DiffJson` |
| `MXVER_COMPARISON_DB` | `C:\git\fhir-cross-version\temp\fhir-comparison.sqlite` | `ComparisonDb` |

`<repo>` and `<tool>` are derived from `contract.py`'s own location
(`<repo>/tools/multi-xver-reconciliation/`).

### Pipeline stages

```
  sources (read-only)                 staging/                 cache/multi-xver-analysis.db
 ─────────────────────   extract_*   ───────────   build_spine  ────────────────────────────
  .md / .json / .fml     ─────────►  <Source>.jsonl  ─────────►  RawAssertions
  fhir.ini / .sqlite                 (one atomic                  → Correspondences (union-find)
                                      assertion per line)         → ElementChanges (the spine)
                                                                  → ChangeSignals
                                                     create_views ► 8 reconciliation views
```

Each extractor emits **normalized assertion records** (the `contract.rec()` shape:
`source, pair, structure, earlier_path, later_path, change_type, raw_old, raw_new,
relationship, detail_file, detail_ref, notes`) — so every source is reduced to one
common vocabulary before integration.

### Idempotency / re-running

- `create_schema.py` deletes and rebuilds the output DB (and its `-wal`/`-shm`).
  It never touches any input.
- Extractors overwrite their own `staging/*.jsonl`.
- `build_spine.py` clears and repopulates the spine tables from **all** staging files.
- `create_views.py` drops and recreates only the 8 views — cheap, run it whenever
  you tweak a view definition.

A full rebuild is `create_schema → all extracts → build_spine → create_views`.

---

## Database schema

### Reference tables

| Table | Rows | Meaning |
|---|---|---|
| `Sources` | 8 | one row per source, with a description |
| `VersionPairs` | 3 | `R4->R4B`, `R4B->R5`, `R5->R6` with package versions |
| `SourceApplicability` | 15 | which (source, pair) combinations are applicable, and the backing files |

### Data tables

| Table | ~Rows | Meaning |
|---|---|---|
| `RawAssertions` | 26,590 | **every atomic claim from every source** — the per-source detail store. Back-links to its spine row via `ChangeKey`. |
| `Correspondences` | 6,821 | union-find components: **element identity across a pair** (which earlier paths ↔ which later paths are "the same element"). |
| `ElementChanges` | 7,884 | **the spine** — one row per `(pair, earlierPath, laterPath)` correspondence edge, with change flags + per-source indicators. |
| `ChangeSignals` | 13,556 | normalized `(spineRow × source × changeType)` evidence with each source's `raw_old`/`raw_new` — the value-level reconciliation layer. |

### `ElementChanges` — the spine

Row identity is the tuple **`(PairKey, EarlierPath, LaterPath)`**. Columns:

- **Change flags** (0/1): `IsAdded`, `IsRemoved`, `IsRenamed`,
  `IsCardinalityChanged`, `IsTypeChanged`, `IsTargetChanged`, `IsBindingChanged`.
  These are the union of what *any* source asserted for the element.
- **Per-source indicators (TRI-STATE):** `InReport`, `InElementMap`,
  `InResourceMap`, `InTypeMap`, `InFml`, `InFhirIni`, `InDiffJson`,
  `InComparisonDb`:

  | Value | Meaning |
  |---|---|
  | `NULL` | source is **not applicable** to this pair (reads `n/a` in views) |
  | `0` | source **is applicable but did not assert** this element change |
  | `1` | source **asserts** this element change |

- **Roll-ups:** `PresentSourceCount` (how many applicable sources = 1),
  `ApplicableSourceCount` (how many applicable to the pair), `DisagreementFlag`
  (1 when applicable sources disagree, i.e. `Present < Applicable`).

---

## How the reconciliation is modeled (read this before interpreting)

Three modeling decisions determine what the numbers *mean*:

1. **Only *real changes* create spine rows and set `InX = 1`.**
   `REAL_CHANGE_TYPES = {Added, Removed, Renamed, Cardinality, Type, Target, Binding, NoMap}`.
   A source saying an element is `Mapped` (equivalent / unchanged) or attaching a
   free-text `Comment` **does not** count as "catching a change" — so
   `ComparisonDb`'s thousands of "equivalent" rows do **not** inflate its
   `InComparisonDb=1` count. Those Mapped/Comment assertions are still **retained
   in `ChangeSignals`** as queryable **counter-evidence** ("source X explicitly
   said *no change* here"). `NoMap` implies `IsRemoved` (a distinct `NoMap` signal
   is kept).

2. **Correspondence via union-find.** Nodes are side-tagged paths
   (`E\x1f<earlier>`, `L\x1f<later>`); any assertion carrying both paths unions
   them. Each resulting component gets a `Kind`:

   | `Correspondences.Kind` | Meaning |
   |---|---|
   | `InPlace` | one earlier path == one later path (same name, changed facets) |
   | `Renamed` | one earlier ↔ one different later |
   | `Added` | later-only element |
   | `Removed` | earlier-only element |
   | `FanOut` | one earlier ↔ many later |
   | `FanIn` | many earlier ↔ one later |
   | `Complex` | many ↔ many |

   Disagreements about an element's *fate* (one source renames it, another removes
   it) land in the same component and surface via `StructuralConflicts`.

3. **`ChangeSignals` is the value layer.** For value-level questions ("did two
   sources agree the new cardinality is `0..*`?"), join `ChangeSignals` on
   `ChangeKey` + `ChangeType`. Raw values are preserved verbatim (honest), which is
   why some are annotated (e.g. diff.json emits `?` for a dimension it didn't
   report; Report may suffix `⚠ suspected`).

---

## Interpreting the results — the views

### Per-source detail views (raw assertions, unfiltered)

`ReportChanges`, `ElementMaps`, `ResourceMaps`, `TypeMaps`, `FmlMappings`,
`FhirIniChanges`, `DiffJsonChanges`, `ComparisonElements` — each is just
`RawAssertions` filtered to one source (plus `PairName`/`SourceName`). Use these to
see exactly what a single source claimed, including its `Mapped`/`Comment` rows.

### Reconciliation views (the consolidated surface)

| View | Answers | How to read it |
|---|---|---|
| **`ChangeMatrix`** | *For every spine element, what changed and which sources caught it?* | One row per element; `Added…Binding` show the union change flags; `Report…ComparisonDb` columns show `Y` / `-` / `n/a` per source; `Present`/`Applicable` roll-ups. **The primary consolidated view.** |
| **`ChangeMatrixChanges`** | Same, but only rows that assert ≥1 real change (drops no-change anchors). | Start here for "what actually changed." |
| **`SourceReliability`** | *How complete is each source per pair?* | `PresentChangeRows / ApplicableChangeRows` = coverage. High = comprehensive; low = sparse. |
| **`StructuralConflicts`** | *Same element identity, sources disagree on its fate.* | Grouped by correspondence; `AddRows`/`RemoveRows`/`RenameRows` > 0 in conflicting combinations (e.g. renamed by one, removed by another). |
| **`ValueConflicts`** | *Same element + change type, two sources give different values.* | Raw catch-all (`SourceA/OldA/NewA` vs `SourceB/OldB/NewB`). **Noisy** — values are un-normalized across heterogeneous formats. |
| **`CardinalityConflicts`** | *High-signal subset of `ValueConflicts`:* firm `min..max` on both sides (excludes `?` wildcards). | If non-empty, sources genuinely disagree on a cardinality. Currently **0** (see findings). |
| **`UniqueCatch`** | *Changes only ONE applicable source caught* (`Present=1, Applicable>1`). | The other applicable sources missed it — candidates for "who's right?" |
| **`CoverageGaps`** | *Most caught it, ≥1 applicable source missed it* (`2 ≤ Present < Applicable`). | Likely gaps in the missing source(s). |

### Worked reading of a `ChangeMatrix` row

```
Pair=R4->R4B  Structure=MarketingStatus  Element=MarketingStatus.country
Cardinality=Y   Report=Y  DiffJson=-  ComparisonDb=Y  Present=2  Applicable=6
```

Interpretation: `MarketingStatus.country` had a cardinality change; `Report` and
`ComparisonDb` caught it, **`DiffJson` did not** (`-` = applicable but silent), and
the ConceptMap sources are `n/a`/silent — `Present=2` of `Applicable=6`, so
`DisagreementFlag=1`. That's a concrete DiffJson coverage gap.

---

## Example queries

```sql
-- All real changes for one resource, with the source matrix
SELECT * FROM ChangeMatrixChanges
WHERE Structure = 'AdverseEvent' AND Pair = 'R4B->R5'
ORDER BY EarlierPath;

-- Source completeness per pair (coverage %)
SELECT Pair, Source, PresentChangeRows, ApplicableChangeRows,
       ROUND(100.0*PresentChangeRows/ApplicableChangeRows, 1) AS CoveragePct
FROM SourceReliability
ORDER BY Pair, CoveragePct DESC;

-- Changes only a single applicable source noticed
SELECT Pair, Structure, EarlierPath, LaterPath,
       Report, ElementMap, ResourceMap, TypeMap, Fml, FhirIni, DiffJson, ComparisonDb
FROM UniqueCatch
ORDER BY Pair, Structure;

-- Elements where sources disagree on rename vs. remove
SELECT * FROM StructuralConflicts ORDER BY Pair, Kind;

-- Drill from a spine element to every source's raw claim
SELECT s.SourceName, sig.ChangeType, sig.RawOld, sig.RawNew, sig.Relationship, sig.Notes
FROM ChangeSignals sig
JOIN Sources s ON s.SourceKey = sig.SourceKey
JOIN ElementChanges ec ON ec.ChangeKey = sig.ChangeKey
WHERE ec.EarlierPath = 'MarketingStatus.country' AND ec.PairKey =
      (SELECT PairKey FROM VersionPairs WHERE PairName = 'R4->R4B');
```

---

## Key findings (from the built DB)

- **Source completeness (coverage %):** `Report` most complete (61–95%),
  `DiffJson` 36–57%, `ComparisonDb` 29–43%, ConceptMaps ~0–3%. `R5→R6` is covered
  only by `Report` (95%) and `FhirIni` (12%).
- **`CardinalityConflicts = 0`** — wherever ≥2 sources assert a *parseable*
  cardinality change, they **agree** on the new cardinality (the least-ambiguous
  dimension). Earlier apparent conflicts were `Report` packing type/target prose
  into the cardinality cell; that is normalized at extraction. A genuine `1..1`
  vs `0..*` disagreement *would* still surface here.
- **`StructuralConflicts ≈ 377`**, **`UniqueCatch ≈ 4,807`**,
  **`CoverageGaps ≈ 2,887`** — the bulk of the reconciliation work.
- **`ValueConflicts` is intentionally noisy** — a raw catch-all over
  heterogeneous, un-normalized value formats. Prefer `CardinalityConflicts` for a
  clean signal; treat `ValueConflicts` as leads to investigate.
- **`ComparisonDb` is source-driven**, so target-only "Added" elements are absent
  from it (an expected under-coverage pattern, not a bug).

---

## Caveats

- Numbers above are from the last full build; a rebuild reproduces them from the
  same read-only inputs.
- The **tool scripts** (`*.py`, this `README.md`) are committed. The **output DB**
  (`cache/multi-xver-analysis.db`) and the **`staging/`** JSONL are git-ignored
  generated artifacts — do not commit them; regenerate with the run order above.
- The `→` (U+2192) and `⚠` characters occur in source-derived text; keep
  `PYTHONIOENCODING=utf-8` set when running the scripts or querying with Python.
