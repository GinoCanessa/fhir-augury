# ingestion-toggle

A small .NET console utility that sets existing boolean settings across source
projects' local configuration overrides. The original ingestion pause/resume
commands remain available, but the tool now supports other boolean properties
without adding a flag catalog to the code.

## Why

Settings such as `IngestionPaused`, `RunIngestionOnStartupOnly`, and
`ReloadFromCacheOnStartup` live inside each source's configuration section.
This tool applies the same boolean values across sources while leaving every
unselected setting untouched.

## Usage

From the repository root:

```powershell
dotnet run --project tools\ingestion-toggle -- --ingestion-paused true
dotnet run --project tools\ingestion-toggle -- --ingestion-paused false --ingest-on-startup-only true
dotnet run --project tools\ingestion-toggle -- --reload-from-cache-on-startup false
```

On macOS/Linux, use forward slashes in the project path. The tool locates the
repository root by walking up from the current directory to `fhir-augury.slnx`;
when invoking it from a subdirectory, adjust the project path accordingly.

Use the property's **kebab-case name**, followed by an explicit `true` or
`false`. Multiple settings can be supplied in one command; `--setting=true`
is also accepted. Values set the actual property, so `--ingestion-paused true`
**pauses** ingestion.

| Example flag | Existing property |
|------|--------|
| `--ingestion-paused true` | `IngestionPaused` |
| `--run-ingestion-on-startup-only true` | `RunIngestionOnStartupOnly` |
| `--reload-from-cache-on-startup false` | `ReloadFromCacheOnStartup` |
| `--reindex-tickets-on-startup true` | `ReindexTicketsOnStartup` |
| `--rebuild-fts-on-startup false` | `RebuildFtsOnStartup` |
| `--custom-boolean-setting true` | Any existing `CustomBooleanSetting` |

Only sources that already define the requested property are affected. These
examples are not an allowlist: property names are converted using .NET's
kebab-case naming policy, including ordinary PascalCase and camelCase names.
Flags use lowercase kebab-case. A misspelled flag that matches no existing
property produces reported skips, not a new property.

### Aliases and compatibility

| Flag | Effect |
|------|--------|
| `--ingest-on-startup-only true/false` | Alias for `--run-ingestion-on-startup-only true/false`. |
| `--enable` | Sets `IngestionPaused` to `false` (resume ingestion). |
| `--disable` | Sets `IngestionPaused` to `true` (pause ingestion). |
| `--help`, `-h`, `help` | Print usage and exit; use alone. |

The legacy `--enable` and `--disable` flags retain their original **ingestion**
meaning. They may be combined with other settings, but specifying a setting
more than once, including through an alias, is an error. Missing or invalid
boolean values and unexpected trailing arguments are rejected before any
configuration is read or changed.

## Safe scope

- **Files:** only `appsettings.local.json` immediately inside
  `src\FhirAugury.Source.*` directories. Processor, Orchestrator, and other
  non-source projects are excluded, as are shared `appsettings.json` files,
  other environment files, and nested directories.
- **Properties:** only direct children of the source's own top-level section.
  For example, `FhirAugury.Source.Jira` updates properties inside `Jira`, not
  another section. The section name is matched case-insensitively. Root-level,
  deeper-nested, and array-contained properties are ignored.
- **Never creates local files or properties.** Missing settings are skipped.
  When some requested properties exist and others do not, only the existing
  ones are updated.
- **Minimal edit.** Only the bytes of the boolean literal (`true`/`false`) are
  replaced. Formatting, comments, trailing commas, BOM, line endings, and all
  unrelated settings are preserved byte-for-byte in UTF-8 files, with or
  without a BOM. A file is replaced only when at least one value changes.
- **Per-file replacement.** The complete candidate is written to a temporary
  file in the same directory before replacing the original. Temporary files
  are cleaned up, and Unix file permissions are preserved. A detected change
  to the original before replacement fails rather than overwriting that edit.
- **Invalid or ambiguous targets fail.** Non-boolean requested properties,
  duplicate matching properties or source sections, malformed JSON, and
  non-object source sections are rejected without applying any of that
  file's changes. Linked files/directories and read-only files requiring
  changes are rejected.

## Output

Each considered file is reported on its own line, followed by a summary:

```
CHANGED  src/FhirAugury.Source.Jira/appsettings.local.json | Jira.IngestionPaused=true, Jira.RunIngestionOnStartupOnly=false
SKIPPED  src/FhirAugury.Source.Zulip/appsettings.local.json | Already set: Zulip.IngestionPaused=true.
SKIPPED  src/FhirAugury.Source.Fhir/appsettings.local.json | No requested setting found under Fhir: --ingestion-paused, --run-ingestion-on-startup-only.
SKIPPED  src/FhirAugury.Source.Confluence/appsettings.local.json | File does not exist.
FAILED   src/FhirAugury.Source.GitHub/appsettings.local.json | Malformed JSON (line 2, byte 8).
Summary: 1 changed, 3 skipped, 1 failed
```

| Status | Meaning |
|--------|---------|
| `CHANGED` | At least one selected value changed and the file was replaced. |
| `SKIPPED` | All matching values are already correct, none of the requested properties exist, or the source has no local override file. |
| `FAILED` | The file or selected configuration is invalid, ambiguous, inaccessible, or could not be safely replaced. No changes to that file are applied. |

`FAILED` lines go to standard error; everything else goes to standard output.
A failure in one file does not stop the remaining files from being processed.
A multi-file run is not transactional: successful updates are kept even if
another file fails.

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | Every file was changed or skipped. |
| `1` | At least one file failed, or the repository root / `src/` could not be found. |
| `2` | Invalid or missing command-line arguments. |

## Notes

- Running services read configuration at startup; restart them (or rely on
  configuration reload, where supported) for the new value to take effect.
- `appsettings.local.json` is gitignored. The tool does not change committed
  configuration or contact running services.
- Reversing a command cannot restore different original values across sources;
  retain your own backups if that is required.
