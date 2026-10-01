# ingestion-toggle

A small console utility that pauses or resumes ingestion for every source
service in one step by flipping the existing `IngestionPaused` setting in each
source project's local configuration override.

## Why

Pausing ingestion locally means editing `IngestionPaused` inside the source
section (`Jira`, `Zulip`, `Confluence`, `GitHub`, …) of several
`appsettings.local.json` files by hand. That is repetitive and easy to get
wrong. This tool applies one decision across all of them while leaving every
other setting untouched.

## Usage

Run from anywhere inside the repository (the tool locates the root by finding
`fhir-augury.slnx`):

```sh
dotnet run --project tools/ingestion-toggle -- --disable   # pause ingestion
dotnet run --project tools/ingestion-toggle -- --enable    # resume ingestion
```

| Flag | Effect |
|------|--------|
| `--enable` | Sets every existing `<Section>.IngestionPaused` to `false`. |
| `--disable` | Sets every existing `<Section>.IngestionPaused` to `true`. |
| `--help`, `-h`, `help` | Print usage and exit. |

Exactly one of `--enable` or `--disable` is required. The flags describe the
desired **ingestion** state, not the raw property value.

## Safe scope

- **Files:** only `src/*/appsettings.local.json`. Shared `appsettings.json`
  files, `appsettings.*.json` environment files, and anything outside `src/`
  are never read or written.
- **Properties:** only an `IngestionPaused` property that is a direct child of
  a top-level section object (e.g. `Jira.IngestionPaused`). Root-level,
  deeper-nested, or array-contained `IngestionPaused` keys are ignored.
  Matching is case-sensitive.
- **Never creates anything.** A missing `appsettings.local.json` or a missing
  `IngestionPaused` property is reported as skipped, not added.
- **Minimal edit.** Only the bytes of the boolean literal (`true`/`false`) are
  replaced. Formatting, comments, trailing commas, BOM, line endings, and all
  unrelated settings are preserved byte-for-byte. A file is written only when
  at least one value actually changes.

## Output

Each considered file is reported on its own line, followed by a summary:

```
CHANGED  src/FhirAugury.Source.Jira/appsettings.local.json | Jira.IngestionPaused=true
SKIPPED  src/FhirAugury.Source.Zulip/appsettings.local.json | Zulip.IngestionPaused already true.
SKIPPED  src/FhirAugury.Source.Fhir/appsettings.local.json | No <Section>.IngestionPaused setting found.
SKIPPED  src/FhirAugury.Orchestrator/appsettings.local.json | File does not exist.
FAILED   src/FhirAugury.Source.GitHub/appsettings.local.json | Malformed JSON: ...
Summary: 1 changed, 3 skipped, 1 failed
```

| Status | Meaning |
|--------|---------|
| `CHANGED` | At least one `IngestionPaused` value was flipped and the file was rewritten. |
| `SKIPPED` | Nothing to do: the value is already correct, the property is absent, or the project (one that has an `appsettings.json`) has no local override file. |
| `FAILED` | The file is malformed JSON, its root is not an object, an `IngestionPaused` value is not a boolean, or it could not be read or written. The file is left unchanged. |

`FAILED` lines go to standard error; everything else goes to standard output.
A failure in one file does not stop the remaining files from being processed.

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | Every file was changed or skipped. |
| `1` | At least one file failed, or the repository root / `src/` could not be found. |
| `2` | Invalid or missing command-line arguments. |

## Notes

- Running services read configuration at startup; restart them (or rely on
  configuration reload, where supported) for the new value to take effect.
- `appsettings.local.json` is gitignored, so this tool never produces a change
  to commit.
