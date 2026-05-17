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
