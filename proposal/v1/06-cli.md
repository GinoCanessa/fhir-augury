# FHIR Augury — CLI

## Overview

The CLI (`FhirAugury.Cli`) is a standalone console application built with
`System.CommandLine`. It can operate in two modes:

1. **Direct mode** — operates directly against the SQLite database (for batch
   operations, initial setup, offline use).
2. **Client mode** — communicates with the running `FhirAugury.Service` API
   (for live operations like on-demand ingestion).

## Command Structure

```
fhir-augury
├── download          # Full download from a source
│   ├── --source      # zulip | jira | confluence | github
│   ├── --db          # Database file path
│   ├── --filter      # Source-specific filter (JQL, CQL, repo list)
│   └── (source-specific auth options)
│
├── sync              # Incremental update from a source
│   ├── --source      # zulip | jira | confluence | github | all
│   ├── --db          # Database file path
│   └── --since       # Override: sync from specific date
│
├── ingest            # Submit a single item for ingestion
│   ├── --source      # zulip | jira | confluence | github
│   ├── --id          # Item identifier (FHIR-12345, stream:topic, etc.)
│   ├── --db          # Database file path (direct mode)
│   └── --service     # Service URL (client mode)
│
├── index             # Build/rebuild search indexes
│   ├── build-fts     # Populate FTS5 tables
│   ├── build-bm25    # Extract keywords and compute BM25 scores
│   ├── build-xref    # Run cross-reference linker
│   ├── rebuild-all   # Full rebuild of all indexes
│   └── --db          # Database file path
│
├── search            # Search the knowledge base
│   ├── --query, -q   # Search query text
│   ├── --source, -s  # Filter to source(s): zulip,jira,confluence,github
│   ├── --limit, -n   # Max results (default: 20)
│   ├── --format, -f  # Output format: table | json | markdown
│   ├── --db          # Database file path
│   └── --service     # Service URL (client mode, uses API instead of DB)
│
├── get               # Retrieve a specific item
│   ├── --source      # zulip | jira | confluence | github
│   ├── --id          # Item identifier
│   ├── --format      # table | json | markdown
│   └── --db          # Database file path
│
├── snapshot           # Render a detailed view of an item
│   ├── --source       # Source type
│   ├── --id           # Item identifier
│   ├── --include-xref # Include cross-references from other sources
│   └── --db           # Database file path
│
├── related           # Find related items across sources
│   ├── --source      # Source type of the seed item
│   ├── --id          # Seed item identifier
│   ├── --limit       # Max results
│   └── --db          # Database file path
│
├── stats             # Database statistics
│   ├── --source      # Optional: filter to one source
│   └── --db          # Database file path
│
├── service           # Service management (client mode)
│   ├── status        # Check service health
│   ├── trigger       # Trigger ingestion via API
│   └── --service     # Service URL
│
└── (global options)
    ├── --db           # Default database path
    ├── --service      # Default service URL
    ├── --config       # Config file path
    ├── --verbose      # Verbose output
    └── --json         # Force JSON output
```

## Usage Examples

### Initial Setup

```bash
# Full download of all Zulip messages
fhir-augury download --source zulip --db fhir-augury.db \
  --zulip-email user@example.com --zulip-api-key abc123

# Full download of Jira issues
fhir-augury download --source jira --db fhir-augury.db \
  --jira-cookie "JSESSIONID=abc; seraph.rememberme.cookie=xyz"

# Full download of Confluence FHIR space
fhir-augury download --source confluence --db fhir-augury.db \
  --confluence-user user@example.com --confluence-token abc123 \
  --confluence-spaces FHIR,FHIRI

# Full download of GitHub repos
fhir-augury download --source github --db fhir-augury.db \
  --github-token ghp_abc123 --github-repos HL7/fhir,HL7/fhir-ig-publisher

# Build all indexes
fhir-augury index rebuild-all --db fhir-augury.db
```

### Daily Use

```bash
# Incremental sync of all sources
fhir-augury sync --source all --db fhir-augury.db

# Search across all sources
fhir-augury search -q "FHIRPath normative ballot" -n 10

# Search only Zulip and Jira
fhir-augury search -q "Bundle signature" -s zulip,jira

# Get a specific Jira issue with full details
fhir-augury snapshot --source jira --id FHIR-43499

# Find items related to a specific issue (across all sources)
fhir-augury related --source jira --id FHIR-43499 --limit 20

# Submit a single item for on-demand indexing via the service
fhir-augury ingest --source jira --id FHIR-44000 --service http://localhost:5150

# Check service status
fhir-augury service status --service http://localhost:5150
```

### Output Formats

**Table (default):**
```
Source      ID            Title                                Score   Updated
─────────  ────────────  ───────────────────────────────────  ──────  ──────────
jira       FHIR-43499    FHIRPath normative readiness          12.5   2026-02-15
zulip      msg-987654    [implementers] FHIRPath normative     11.2   2026-03-01
github     HL7/fhir#823  FHIRPath spec clarification            8.1   2026-01-20
confluence page-45678    FHIRPath Working Group Notes            6.4   2026-02-28
```

**JSON:** Full structured output for scripting and piping.

**Markdown:** Formatted for rendering in terminals with markdown support
or piping into documents.
