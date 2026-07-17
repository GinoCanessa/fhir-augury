# dictionary-build

A small one-shot console utility that rebuilds the spell-check database
`cache/dictionary.db` from the source word lists and typo maps under
`dictionary/`.

## Why

The application reads `cache/dictionary.db` (a SQLite database), but the
editable source of truth is the text files under `dictionary/`
(`*.words.txt`, `*.typo.txt`). Edits to those files only take effect after the
database is rebuilt. This tool performs that rebuild explicitly, so you don't
have to rely on a service auto-building it on startup.

`cache/dictionary.db` is gitignored — each contributor rebuilds it locally.

## Usage

Run from the repository root:

```sh
dotnet run --project tools/dictionary-build
```

This performs a **full, deterministic rebuild**, overwriting
`cache/dictionary.db`. On success it prints the resolved output path and the
loaded word/typo counts.

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `--source <dir>` | `./dictionary` | Directory containing the `*.words.txt` / `*.typo.txt` source files. |
| `--out <path>` | `./cache/dictionary.db` | Output SQLite database path. |
| `--force` | — | No-op alias. The tool **always** performs a full rebuild; accepted only so a documented invocation that includes `--force` works verbatim. |
| `--help`, `-h`, `help` | — | Print usage and exit. |

```sh
dotnet run --project tools/dictionary-build -- --source ./dictionary --out ./cache/dictionary.db
```

## Notes

- The relative defaults resolve against the current working directory, so run
  the tool from the repository root (where `dictionary/` lives). If the source
  directory or its `*.words.txt` / `*.typo.txt` files cannot be found, the tool
  exits non-zero with a clear message rather than silently producing nothing.
- If a running service or tool holds `cache/dictionary.db` open (Windows file
  lock), the rebuild can fail on the final move; close it and retry.
- This is a full rebuild every run — there is no incremental mode.
