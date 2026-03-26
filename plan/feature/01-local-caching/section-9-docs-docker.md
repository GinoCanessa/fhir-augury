# Section 9: Documentation & Docker

**Goal:** Update documentation to cover cache configuration, CLI commands, and
Docker volume setup. Add Dockerfile and docker-compose support for cache
persistence.

**Dependencies:** All previous sections

---

## 9.1 — Update Configuration Documentation

### Objective

Add cache configuration reference to the existing docs.

### File to Modify: `docs/configuration.md`

### Content to Add

#### Cache Configuration Section

Document all cache settings with a complete reference table:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Cache.RootPath` | `string` | `"./cache"` | Root directory for all source caches |
| `Cache.DefaultMode` | `CacheMode` | `WriteThrough` | Default cache mode for all sources |
| `Sources.{name}.Cache.Mode` | `CacheMode?` | `null` (inherit) | Per-source cache mode override |
| `Sources.{name}.Cache.Path` | `string?` | `null` | Override cache subdirectory for this source |

#### CacheMode Values

| Value | Behaviour |
|-------|-----------|
| `Disabled` | No caching — always fetch from API |
| `WriteThrough` | Read from cache if available → otherwise fetch from API → write to cache |
| `CacheOnly` | Read from cache only — no network calls, no credentials required |
| `WriteOnly` | Always fetch from API → write to cache (populate cache without using it) |

#### Example Configurations

**Minimal (write-through caching with defaults):**
```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "./cache"
    }
  }
}
```

**Cache-only for Jira (offline development):**
```json
{
  "FhirAugury": {
    "Cache": {
      "RootPath": "/data/fhir-cache",
      "DefaultMode": "WriteThrough",
      "Sources": {
        "jira": { "Mode": "CacheOnly" }
      }
    }
  }
}
```

**Environment variable overrides:**
```bash
export FHIR_AUGURY_Cache__RootPath=/data/cache
export FHIR_AUGURY_Cache__DefaultMode=WriteThrough
export FHIR_AUGURY_Sources__jira__Cache__Mode=CacheOnly
```

### Acceptance Criteria

- [ ] All cache properties documented with types and defaults
- [ ] All CacheMode values explained
- [ ] Example configurations for common use cases
- [ ] Environment variable mapping documented

---

## 9.2 — Update CLI Reference Documentation

### Objective

Document the new CLI options and commands.

### File to Modify: `docs/cli-reference.md`

### Content to Add

#### New Global Options on `download` and `sync`

```
--cache-path <path>     Override the cache root directory (default: ./cache)
--cache-mode <mode>     Cache mode: Disabled, WriteThrough, CacheOnly, WriteOnly
```

#### Cache Commands

```
fhir-augury cache stats [--cache-path <path>]
    Show cache size per source (files, bytes, sub-paths).

fhir-augury cache clear [--source <name>] [--cache-path <path>]
    Clear cached responses. Use --source to clear only one source.
```

#### Usage Examples

```bash
# Download Jira with caching enabled (default)
fhir-augury download jira --jira-cookie "..." --cache-mode WriteThrough

# Ingest from pre-downloaded Jira XML files (no network needed)
fhir-augury download jira --cache-mode CacheOnly --cache-path /data/jira-exports

# Ingest from pre-downloaded Zulip archives
fhir-augury download zulip --cache-mode CacheOnly --cache-path /data/zulip-archives

# Check cache usage
fhir-augury cache stats

# Clear only Jira cache
fhir-augury cache clear --source jira

# Clear all caches
fhir-augury cache clear
```

### Acceptance Criteria

- [ ] New options documented with descriptions and defaults
- [ ] `cache stats` and `cache clear` commands documented
- [ ] Usage examples cover common workflows

---

## 9.3 — Cache Directory Layout Documentation

### Objective

Document the cache directory structure so users understand what's on disk and
can pre-populate directories for cache-only ingestion.

### File to Create: `docs/cache-layout.md`

### Content

Document the full directory tree with explanations:

```
{CacheRoot}/
├── _meta_jira.json                     # Jira sync cursor & metadata
├── _meta_confluence.json               # Confluence sync cursor & metadata
├── jira/
│   ├── DayOf_2026-03-18-000.xml        # Daily batch with sequence number
│   ├── DayOf_2026-03-18-001.xml        # Second batch for the same day
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

Include:
- File naming conventions for each source
- How to pre-populate for cache-only mode
- Metadata file schemas

### Acceptance Criteria

- [ ] Directory tree clearly documented
- [ ] All file naming patterns explained
- [ ] Pre-population guide included
- [ ] Metadata JSON schemas documented

---

## 9.4 — Docker Support

### Objective

Add Dockerfile and docker-compose.yml changes so the cache directory can be
mounted as a persistent volume.

### Files to Create/Modify

#### `Dockerfile` (create if not exists, or modify)

Add cache volume declaration:

```dockerfile
# After existing VOLUME declarations
VOLUME ["/data/cache"]

# Set default cache path via environment variable
ENV FHIR_AUGURY_Cache__RootPath=/data/cache
```

#### `docker-compose.yml` (create if not exists, or modify)

```yaml
services:
  fhir-augury:
    build: .
    volumes:
      - ./local-cache:/data/cache     # Cache survives rebuilds
      - fhir-augury-db:/data/db       # Database persistence
    environment:
      - FHIR_AUGURY_Cache__RootPath=/data/cache
      - FHIR_AUGURY_DatabasePath=/data/db/fhir-augury.db

volumes:
  fhir-augury-db:
```

### Design Notes

- The cache volume is a bind mount by default (`./local-cache`) so users can
  easily inspect, pre-populate, and manage cached files from the host.
- The database volume is a named volume for simplicity.
- `docker compose down && docker compose build && docker compose up` preserves
  the cache because the bind mount is host-managed.

### Acceptance Criteria

- [ ] Dockerfile declares `/data/cache` volume
- [ ] docker-compose.yml maps cache as bind mount
- [ ] Cache survives `docker compose down && up`
- [ ] Environment variable sets cache root path
