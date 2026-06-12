# fhir-spec-review

A read-only fhir-augury tool that runs FMG-style content-quality checks over the
**current** HL7/fhir build and emits a per-workgroup static HTML report. It is a
faithful functional port of the standalone `fmg-r6-review` CLI, re-sourced to
fhir-augury's GitHub source cache.

It has two verbs:

- **`process`** — reads the current build (GitHub source cache: clone tree +
  indexed structure definitions / elements / canonical artifacts / workgroups),
  the published baseline vocabulary (`fhir-spec.db`), a published baseline site,
  and a dictionary (`dictionary.db`); runs the per-page and per-artifact checks;
  and writes results to a review SQLite DB.
- **`report`** — reads the review DB and emits a per-workgroup local static HTML
  site.

## Usage

```
fhir-spec-review process \
    --github-db ./data/github.db \
    --github-cache ./cache \
    --repo HL7/fhir \
    --fhir-spec-db ./cache/fhir-spec.db \
    --baseline-release R5 \
    --baseline-site C:\ai\support\fhir-r5 \
    --dictionary-db ./cache/dictionary.db \
    --review-db ./cache/fhir-spec-review.db \
    --drop-tables

fhir-spec-review report \
    --review-db ./cache/fhir-spec-review.db \
    --out ./cache/fhir-spec-review-site \
    --force
```

### `process` options

| Flag | Default | Notes |
|------|---------|-------|
| `--github-db` | `./data/github.db` | GitHub source SQLite DB (read-only). |
| `--github-cache` | `./cache` | GitHub source cache root (clone tree lives under `github/repos/<owner>_<repo>/clone/`). |
| `--repo` | `HL7/fhir` | Repository under review. |
| `--fhir-spec-db` | `./cache/fhir-spec.db` | External baseline vocabulary DB (read-only). |
| `--baseline-release` | `R5` | Baseline FHIR release selecting the `fhir-spec.db` package row. |
| `--baseline-site` | *(required)* | Published baseline site folder, for presence tracking. |
| `--dictionary-db` | `./cache/dictionary.db` | External dictionary DB (read-only). |
| `--review-db` | `./cache/fhir-spec-review.db` | Output review SQLite DB. |
| `--drop-tables` | off | Drop and recreate the review schema first. |

### `report` options

| Flag | Default | Notes |
|------|---------|-------|
| `--review-db` | `./cache/fhir-spec-review.db` | Review SQLite DB to read. |
| `--out` | `./cache/fhir-spec-review-site` | Output directory for the static site. |
| `--force` | off | Overwrite an existing output directory. |

## Required external inputs

- `fhir-spec.db` and `dictionary.db` are upstream-produced read-only reference
  DBs (gitignored, live under `cache/`).
- `--baseline-site` is a rendered, per-artifact published-release site (e.g. a
  local `fhir-r5` site). It is required and drives baseline **presence**
  tracking (pages/artifacts removed since the baseline).

## v1 limitations

- FMG feedback-sheet fields (disposition / voted-by / comments) are **not**
  ingested.
- No Confluence publishing and no markdown summary — the report is local static
  HTML only (the index tables are the summary view).
- Value-set / code-system / expanded-code / operation-name **matching** is not
  exercised (the data exists in `fhir-spec.db` but the legacy checks for it are
  not active).
- The legacy `create-dict-db` verb is dropped; the dictionary is consumed from
  `cache/dictionary.db`.
