# Cache Directory Layout

The file-system cache stores raw API responses in a structured directory layout.
This document describes the directory structure, file naming conventions, and
how to pre-populate directories for cache-only (offline) ingestion.

## Directory Tree

```
{CacheRoot}/
├── _meta_jira.json                     # Jira sync cursor & metadata
├── _meta_confluence.json               # Confluence sync cursor & metadata
├── jira/
│   ├── DayOf_2026-03-18-000.json       # Daily batch with sequence number
│   ├── DayOf_2026-03-18-001.json       # Second batch for the same day
│   ├── DayOf_2025-11-05.xml            # Legacy daily batch (no sequence)
│   └── _WeekOf_2024-08-05.xml          # Legacy weekly batch
├── zulip/
│   ├── _meta_s270.json                 # Stream 270 cursor + name
│   ├── _meta_s412.json                 # Stream 412 cursor + name
│   ├── s270/                           # Stream directory (ID prefixed with 's')
│   │   ├── _WeekOf_2024-08-05-000.json # Weekly batch (initial download)
│   │   ├── DayOf_2026-03-18-000.json   # Daily batch (incremental)
│   │   └── ...
│   └── s412/
│       └── ...
└── confluence/
    └── pages/
        ├── 12345.json                  # One file per Confluence page
        └── 67890.json
```

## File Naming Conventions

### Jira

API responses are cached as **JSON** batch files with date-based names:

| Pattern | Description |
|---|---|
| `DayOf_yyyy-MM-dd-###.json` | Current format: daily batch with sequence number |
| `DayOf_yyyy-MM-dd.xml` | Legacy: daily batch without sequence (XML export) |
| `_WeekOf_yyyy-MM-dd.xml` | Legacy: weekly batch without sequence (XML export) |
| `_WeekOf_yyyy-MM-dd-###.json` | Weekly batch with sequence number |

Files are ingested in chronological order:
1. Ascending by date
2. Legacy (no sequence) before sequenced files
3. `_WeekOf_` before `DayOf_` for the same date
4. Ascending by sequence number

### Zulip

Each Zulip stream has its own subdirectory named `s{streamId}`:

| Pattern | Description |
|---|---|
| `_WeekOf_yyyy-MM-dd-###.json` | Weekly batch (initial full download) |
| `DayOf_yyyy-MM-dd-###.json` | Daily batch (incremental sync) |

The weekly date is normalized to the Monday of the week.

### Confluence

One file per page, stored under `pages/`:

| Pattern | Description |
|---|---|
| `pages/{pageId}.json` | Individual page JSON (Confluence content ID) |

## Metadata Files

Metadata files track sync cursors and are excluded from enumeration.

### `_meta_jira.json`

```json
{
  "lastSyncDate": "2026-03-18",
  "lastSyncTimestamp": "2026-03-18T15:30:00Z",
  "totalFiles": 542,
  "format": "json"
}
```

### `_meta_confluence.json`

```json
{
  "lastSyncDate": "2026-03-18",
  "lastSyncTimestamp": "2026-03-18T15:30:00Z",
  "totalFiles": 1203,
  "format": "json"
}
```

### `_meta_s{streamId}.json` (Zulip)

```json
{
  "streamId": 270,
  "streamName": "implementers",
  "lastSyncDate": "2026-03-18",
  "lastSyncTimestamp": "2026-03-18T15:30:00Z",
  "initialDownloadComplete": true
}
```

## Pre-Populating for Cache-Only Mode

To use `CacheOnly` mode without ever running a download:

### Jira

1. Place Jira XML export files in `{CacheRoot}/jira/`
2. Use any of the supported naming patterns
3. Run: `fhir-augury download --source jira --cache-mode CacheOnly --cache-path {CacheRoot}`

### Zulip

1. Create stream directories: `{CacheRoot}/zulip/s{streamId}/`
2. Place JSON files containing `{ "messages": [...] }` arrays
3. Optionally create `_meta_s{streamId}.json` with the stream name
4. Run: `fhir-augury download --source zulip --cache-mode CacheOnly --cache-path {CacheRoot}`

### Confluence

1. Create `{CacheRoot}/confluence/pages/`
2. Place individual page JSON files as `{pageId}.json`
3. Each file should be the raw Confluence API response for that page
4. Run: `fhir-augury download --source confluence --cache-mode CacheOnly --cache-path {CacheRoot}`
