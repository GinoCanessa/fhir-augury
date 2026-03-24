# Indexing and Search

This document describes the search and indexing internals of FHIR Augury v2,
including per-service FTS5 full-text search, Orchestrator search aggregation,
BM25 keyword scoring, cross-reference linking, and FHIR-aware tokenization.

## Architecture Overview

In v2, search is **distributed across microservices**:

1. Each **source service** (Jira, Zulip, Confluence, GitHub) maintains its own
   FTS5 indexes and BM25 keyword index in its local SQLite database
2. The **Orchestrator** fans out search requests to all enabled sources via
   gRPC, then aggregates, normalizes, boosts, and ranks the combined results
3. Cross-references are managed by the Orchestrator's `CrossRefLinker`, which
   streams searchable text from each source via the `StreamSearchableText` gRPC

```
User Query → Orchestrator
              ├── gRPC Search → Source.Jira    (local FTS5 MATCH)
              ├── gRPC Search → Source.Zulip   (local FTS5 MATCH)
              ├── gRPC Search → Source.Confluence (local FTS5 MATCH)
              └── gRPC Search → Source.GitHub  (local FTS5 MATCH)
              ↓
         Score Normalization (per-source min-max)
              ↓
         Cross-Ref Boost (CrossRefBoostFactor = 0.5)
              ↓
         Freshness Decay (per-source weights)
              ↓
         Sort by Final Score → Return Results
```

## Per-Source FTS5 Full-Text Search

Each source service uses [SQLite FTS5](https://www.sqlite.org/fts5.html)
virtual tables for fast text search within its local database.

### FTS5 Tables

| Service | FTS5 Table | Content Table | Indexed Fields |
|---------|------------|-------------|----------------|
| Jira | `jira_issues_fts` | `jira_issues` | Title, DescriptionPlain, ResolutionDescriptionPlain |
| Jira | `jira_comments_fts` | `jira_comments` | BodyPlain |
| Zulip | `zulip_messages_fts` | `zulip_messages` | ContentPlain, Topic |
| Confluence | `confluence_pages_fts` | `confluence_pages` | Title, BodyPlain, Labels |
| GitHub | `github_issues_fts` | `github_issues` | Title, Body, Labels |
| GitHub | `github_comments_fts` | `github_comments` | Body |

### FTS5 Table Creation

FTS5 tables are created by `SourceDatabase.CreateFts5Table()` from
`FhirAugury.Common`. This method:

1. Creates the FTS5 virtual table with `content='<table_name>'` and
   `content_rowid='Id'`
2. Auto-generates three content-sync triggers:
   - **AFTER INSERT** — Inserts the new row into the FTS table
   - **AFTER DELETE** — Removes the row using the FTS5 `'delete'` command
   - **AFTER UPDATE** — Deletes the old content, then inserts the new content

This keeps the FTS index always in sync without any explicit rebuild step during
normal operations. A manual rebuild is available via
`SourceDatabase.RebuildFts5()` or
`INSERT INTO <fts_table>(<fts_table>) VALUES ('rebuild')`.

### Per-Source Query Processing

Each source service handles search queries locally:

1. **Query sanitization** — Splits the raw query on whitespace, wraps each term
   in double quotes (escaping internal quotes) so FTS5 treats each as a literal
   phrase token
2. **FTS5 MATCH execution** — Queries with `MATCH @query` and
   `ORDER BY <table>.rank` (BM25 scoring)
3. **Score conversion** — FTS5 rank is negated (more negative = more relevant)
   and stored as a positive value
4. **Snippet extraction** — The FTS5 `snippet()` function extracts context with
   `<b>`/`</b>` highlighting, up to 20 tokens of surrounding context
5. **Return via gRPC** — Scored results with snippets are returned to the
   Orchestrator

### Source Filters

Each source service supports optional filters in its search:

| Source | Filter | Description |
|--------|--------|-------------|
| Jira | `statusFilter` | Filter by issue status |
| Zulip | `streamFilter` | Filter by stream name |
| Confluence | `spaceFilter` | Filter by space key |
| GitHub | `repoFilter`, `stateFilter` | Filter by repository or state |

## Orchestrator Search Pipeline

The Orchestrator coordinates search across all source services:

### 1. Fan-Out

Search requests are sent to all enabled source services **in parallel** via
gRPC `Search` RPCs.

### 2. Score Normalization

Raw scores from each source are normalized using **per-source-group min-max
normalization**:

```
normalized = (score - min) / (max - min)
```

Normalization is applied per source group (e.g., all Jira results together)
before merging, ensuring fair cross-source ranking.

### 3. Cross-Reference Boost

Items that have cross-references (links to/from other source items) get
boosted. The `CrossRefBoostFactor` is `0.5` — items with cross-references
receive a score increase proportional to their reference count.

### 4. Freshness Decay

Scores are adjusted by recency with configurable per-source weight multipliers.
Sources with more time-sensitive content (like chat messages) are weighted
higher:

| Source | Default Weight |
|--------|---------------|
| Zulip | 2.0 |
| GitHub | 1.0 |
| Confluence | 1.0 |
| Jira | 0.5 |

### 5. Final Ranking

Results are sorted by final score (after normalization, cross-ref boost, and
freshness decay) and truncated to the requested limit.

## BM25 Keyword Scoring

Each source service maintains a pre-computed BM25 keyword index in its local
database for similarity search and keyword-based ranking.

### Storage Tables (per-service)

| Table | Purpose |
|-------|---------|
| `index_keywords` | Per-document, per-keyword records with TF count, keyword type, and BM25 score |
| `index_corpus` | Corpus-level records with keyword, document frequency (DF), and IDF |
| `index_doc_stats` | Per-source-type total document count and average document length |

### BM25 Formula

Parameters: **k1 = 1.2**, **b = 0.75**

```
score = IDF × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgDocLen))
```

Where:

- **IDF** = `log(1 + (N - df + 0.5) / (df + 0.5))`
- **tf** = term frequency in the document
- **k1** = term frequency saturation (`1.2`)
- **b** = document length normalization (`0.75`)
- **N** = total documents in the source type
- **df** = number of documents containing the term
- **docLen** = number of tokens in this document
- **avgDocLen** = average document length for this source type

### Index Build Pipeline (per-service)

Each source service builds its BM25 index locally:

1. **Collect documents** — Tokenize all content from the source's content tables
2. **Classify keywords** — Determine keyword type using `KeywordClassifier`
3. **Filter stop words** — Remove stop words (200+ English words)
4. **Count TF** — Compute term frequencies per document
5. **Insert keywords** — Store rows in `index_keywords`
6. **Corpus stats** — Compute document frequencies, IDF, and average doc lengths
   in `index_corpus` and `index_doc_stats`
7. **BM25 scores** — Bulk SQL UPDATE computes all BM25 scores using subqueries

Both full rebuild and incremental update are supported. Incremental updates
recompute only the affected documents but refresh corpus-wide statistics.

## FHIR-Aware Tokenization

The `Tokenizer` in `FhirAugury.Common.Text` extracts tokens with special
handling for FHIR-specific content. This tokenizer is used by all source
services for both FTS5 indexing and BM25 keyword extraction.

### Token Extraction Order

1. **FHIR operations** — Regex `\$[a-zA-Z][a-zA-Z0-9-]*` matches operations
   like `$validate`, `$expand`, `$everything`
2. **FHIR paths** — Regex `[A-Z][a-zA-Z]+(\.[a-zA-Z][a-zA-Z0-9]*)+` matches
   dotted paths like `Patient.name.given`. Both the full path **and** each
   component are added as separate tokens
3. **Noise removal** — URLs, email addresses, and Markdown code blocks are
   stripped from the text
4. **Regular words** — Regex `[a-zA-Z0-9]+` extracts remaining tokens

### Keyword Classification

The `KeywordClassifier` (in `FhirAugury.Common.Text`) assigns each token to one
of four types:

| Type | Criteria | Example |
|------|----------|---------|
| `fhir_operation` | Starts with `$` | `$validate` |
| `fhir_path` | Contains `.` and first segment is a known FHIR resource, or token is a resource name | `Patient.name.given` |
| `stop_word` | In the stop-word list (200+ common English words) | `the`, `and`, `also` |
| `word` | Everything else | `terminology` |

Stop words are filtered out entirely and not indexed.

### FHIR Vocabulary

`FhirVocabulary` (in `FhirAugury.Common.Text`) contains 100+ FHIR R4 resource
names (Patient, Observation, MedicationRequest, etc.) and 30+ FHIR operations
($validate, $expand, $everything, etc.). All matching is case-insensitive.

## Cross-Reference Linking

Cross-references are managed by the **Orchestrator's `CrossRefLinker`**, which
discovers mentions of items from one source within the text of another source.

### How It Works

1. The Orchestrator streams searchable text from each source service via the
   gRPC `StreamSearchableText` RPC (server-streaming)
2. `CrossRefLinker` applies `CrossRefPatterns` (from `FhirAugury.Common.Text`)
   to extract cross-source identifiers
3. Discovered links are stored in the Orchestrator's `cross_ref_links` table
4. Incremental scanning is tracked via `xref_scan_state` (cursor/timestamp-based)
   so only new or updated content is processed

### Detection Patterns (`CrossRefPatterns`)

| Pattern | Target Type | Example Match |
|---------|-------------|---------------|
| `\b(FHIR-\d+)\b` | `jira` | `FHIR-43499` |
| `https?://jira.hl7.org/browse/(FHIR-\d+)` | `jira` | Jira issue URLs |
| `https?://chat.fhir.org/#narrow/stream/...` | `zulip` | Zulip topic URLs |
| `https?://github.com/(HL7/[^/]+)/issues/(\d+)` | `github` | GitHub issue URLs |
| `https?://github.com/(HL7/[^/]+)/pull/(\d+)` | `github` | GitHub PR URLs |
| `\b(HL7/[a-zA-Z0-9._-]+#\d+)\b` | `github` | GitHub short refs (HL7/repo#123) |
| `https?://confluence.hl7.org/.*?/(\d+)` | `confluence` | Confluence page URLs |

### Link Storage

Each link is stored in the Orchestrator's `cross_ref_links` table:

- **SourceType/SourceId** — The item containing the reference
- **TargetType/TargetId** — The referenced item
- **LinkType** — Always `"mention"` currently
- **Context** — ~100 characters of surrounding text

Links are deduplicated. Self-links are excluded. Jira URLs are checked before
bare Jira keys to avoid double-matching.

### Scanning Modes

- **Incremental** — Process only new/updated items since the last scan cursor;
  tracked per source via `xref_scan_state`
- **Full rebuild** — Clear all links and rescan all content from all sources

## Related Items

The Orchestrator's `RelatedItemFinder` uses a **4-signal ranking** approach to
find items related to a given seed item:

1. **Explicit cross-references** (weight 10.0) — Items the seed directly mentions
2. **Reverse cross-references** (weight 8.0) — Items that mention the seed
3. **BM25 text similarity** (weight 3.0) — Items sharing high-value keywords,
   extracted using `Tokenizer` and `StopWords` from `FhirAugury.Common.Text`
4. **Shared metadata** (weight 2.0) — Items sharing work groups, specifications,
   or other structured fields

## Score Normalization

Score normalization maps raw scores to `[0, 1]` using min-max normalization:

```
normalized = (score - min) / (max - min)
```

Normalization is applied **per-source group** before merging results across
services. Per-source weight multipliers can be applied for custom ranking
preferences (e.g., weighting Zulip results higher for recency-sensitive
queries).

## Key Components

| Component | Location | Responsibility |
|-----------|----------|---------------|
| `SourceDatabase.CreateFts5Table()` | `FhirAugury.Common` | Creates FTS5 virtual tables with auto-generated content-sync triggers |
| `SourceDatabase.RebuildFts5()` | `FhirAugury.Common` | Rebuilds FTS5 index from content table |
| `Tokenizer` | `FhirAugury.Common.Text` | FHIR-aware tokenization (operations, paths, words) |
| `KeywordClassifier` | `FhirAugury.Common.Text` | Token classification (fhir_path, fhir_operation, stop_word, word) |
| `FhirVocabulary` | `FhirAugury.Common.Text` | 100+ FHIR resource names and 30+ operations |
| `StopWords` | `FhirAugury.Common.Text` | 200+ English stop words |
| `CrossRefPatterns` | `FhirAugury.Common.Text` | Regex patterns for cross-source identifier extraction |
| `TextSanitizer` | `FhirAugury.Common.Text` | HTML/Markdown stripping, NFC Unicode normalization |
| `CrossRefLinker` | Orchestrator | Streams text from sources, extracts cross-references, stores in `cross_ref_links` |
| `RelatedItemFinder` | Orchestrator | 4-signal related item ranking (xref + BM25 + boost + diversity) |
| Per-source search impl | Each source service | FTS5 MATCH queries, BM25 index builds, snippet extraction |
