# preparer-site

A small `dotnet`-run utility that reads a Jira preparer SQLite database
(produced by `FhirAugury.Processor.Jira.Fhir.Preparer`) and emits a
self-contained static HTML site for human review. The site loads the
DB into [sql.js](https://sql.js.org/) in the browser and renders list,
per-ticket, and a few cross-cut views. No server, no per-ticket
pre-render, opens straight from `file://`.

## Quick start

```bash
dotnet run --project tools/preparer-site -- \
  --db ./cache/jira-preparer.db \
  --out ./cache/jira-preparer-site \
  --title "Preparer Report — May 2026"
```

Then open `cache/jira-preparer-site/index.html` in a Chromium-family
browser.

## Flags

| Flag | Default | Notes |
|------|---------|-------|
| `--db <path>` | *required* | Path to the preparer SQLite DB. |
| `--out <path>` | `./cache/jira-preparer-site` | Output directory (overwritten if it exists; implemented in Phase 2). |
| `--title <string>` | `"Preparer Report"` | Threads through to `<title>` and `<h1>`. |
| `--prune` | off | Inline a slimmed copy of the DB (drops fields the SPA never queries). Implemented in Phase 6. |
| `--help` | — | Print usage and exit non-zero. |

## Vendored assets

The site relies on a vendored copy of [sql.js](https://github.com/sql-js/sql.js)
to load the embedded SQLite database in the browser. The bytes ship
as embedded resources in the C# project and are copied into
`<out>/assets/` on each run.

- Release: [`sql-js/sql.js` v1.10.3](https://github.com/sql-js/sql.js/releases/tag/v1.10.3)
- Asset: `sqljs-wasm.zip` (contains `sql-wasm.js` + `sql-wasm.wasm`)
- License: MIT
- SHA-256:
  - `sql-wasm.js` &nbsp; `558a72c3ab3415d0e6d243cfd23f9d61543600d59054b4b7b8da3cd65f6b9fd4`
  - `sql-wasm.wasm` `d7e61b828523001f26ce0b3f88dabcf6c12e5e6edf80eb4f08b26ac7b946ff88`

To refresh: download `sqljs-wasm.zip` from the chosen release,
extract `sql-wasm.js` and `sql-wasm.wasm` into
`tools/preparer-site/web-assets/`, update the SHA-256 values above,
and rebuild.

## Browser compatibility

Tested in Chromium-family browsers opened directly from `file://`.
Safari may refuse to load WebAssembly from `file://`; if you hit that,
serve the output directory over `python3 -m http.server` and open
`http://localhost:8000/` instead.
