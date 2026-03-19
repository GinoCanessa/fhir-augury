# Indexing and Search

This document describes the search and indexing internals of FHIR Augury,
including FTS5 full-text search, BM25 keyword scoring, cross-reference linking,
and FHIR-aware tokenization.

## FTS5 Full-Text Search

FHIR Augury uses [SQLite FTS5](https://www.sqlite.org/fts5.html) virtual tables
for fast text search across all data sources.

### FTS5 Tables

Each data source has its own FTS5 virtual table indexing the most relevant text
fields:

| FTS5 Table | Source Table | Indexed Fields |
|------------|-------------|----------------|
| `jira_issues_fts` | `jira_issues` | Key, Title, Description, Summary, ResolutionDescription, Labels, Specification, WorkGroup, RelatedArtifacts |
| `jira_comments_fts` | `jira_comments` | IssueKey, Author, Body |
| `zulip_messages_fts` | `zulip_messages` | StreamName, Topic, SenderName, ContentPlain |
| `confluence_pages_fts` | `confluence_pages` | Title, BodyPlain, Labels |
| `github_issues_fts` | `github_issues` | Title, Body, Labels |
| `github_comments_fts` | `github_comments` | Body |

### Content-Synced Triggers

FTS5 tables use `content='<table_name>'` and `content_rowid='Id'` to shadow
the real content tables. Three triggers per table keep them in sync:

- **AFTER INSERT** — Inserts the new row into the FTS table
- **AFTER DELETE** — Removes the row from the FTS table using the FTS5 `'delete'`
  command
- **AFTER UPDATE** — Deletes the old content, then inserts the new content

This means the FTS index is always up-to-date without any explicit rebuild step
during normal operations. A manual rebuild is available via
`INSERT INTO <fts_table>(<fts_table>) VALUES ('rebuild')`.

### Query Processing

The `FtsSearchService` handles search queries:

1. **Sanitization** (`SanitizeFtsQuery`) — Splits the raw query on whitespace,
   double-quote-escapes each term, wraps in `"..."` so FTS5 treats each as a
   literal phrase token
2. **Execution** — Each source's FTS table is queried with `MATCH @query` and
   `ORDER BY <table>.rank`
3. **Snippet extraction** — The FTS5 `snippet()` function extracts context with
   `<b>`/`</b>` highlighting, up to 20 tokens
4. **Score normalization** — FTS5 returns negative rank values (lower is better);
   these are negated, then per-source min-max normalization scales scores to
   `[0, 1]`
5. **Merging** — Results from all sources are interleaved by normalized score

### Source Filters

Each source-specific search method supports optional filters:

| Source | Filter | Description |
|--------|--------|-------------|
| Jira | `statusFilter` | Filter by issue status |
| Zulip | `streamFilter` | Filter by stream name |
| Confluence | `spaceFilter` | Filter by space key |
| GitHub | `repoFilter`, `stateFilter` | Filter by repository or state |

## BM25 Keyword Scoring

Beyond FTS5's built-in ranking, FHIR Augury maintains a pre-computed BM25
keyword index for similarity search.

### Storage Tables

| Table | Purpose |
|-------|---------|
| `index_keywords` | Per-document, per-keyword records with count, type, and BM25 score |
| `index_corpus` | Corpus-level records with keyword, document frequency, and IDF |
| `index_doc_stats` | Per-source-type total document count and average document length |

### BM25 Formula

```
score = IDF × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgDocLen))
```

Where:

- **IDF** = `ln(1 + (N - df + 0.5) / (df + 0.5))`
- **tf** = term frequency in the document
- **k1** = term frequency saturation (default: `1.2`)
- **b** = document length normalization (default: `0.75`)
- **N** = total documents in the source type
- **df** = number of documents containing the term
- **docLen** = number of tokens in this document
- **avgDocLen** = average document length for this source type

The k1 and b parameters are configurable via `Bm25Configuration`.

### Index Build Pipeline

The `Bm25Calculator` builds the index in these steps:

1. **Collect documents** — Concatenate all `SearchableTextFields` for each item
2. **Tokenize** — FHIR-aware tokenization (see below)
3. **Classify** — Determine keyword type; filter stop words
4. **Count** — Compute term frequencies per document
5. **Insert** — Store `KeywordRecord` rows in `index_keywords`
6. **Corpus stats** — Compute document frequencies, IDF, average doc lengths
7. **BM25 scores** — Bulk SQL UPDATE computes all BM25 scores using subqueries

Both full rebuild (`BuildFullIndex`) and incremental update (`UpdateIndex`) are
supported. Incremental updates recompute only the affected documents but
refresh corpus-wide statistics.

## FHIR-Aware Tokenization

The `Tokenizer` extracts tokens with special handling for FHIR-specific content.

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

The `KeywordClassifier` assigns each token to one of four types:

| Type | Criteria | Example |
|------|----------|---------|
| `fhir_operation` | Starts with `$` | `$validate` |
| `fhir_path` | Contains `.` and first segment is a known FHIR resource, or token is a resource name | `Patient.name.given` |
| `stop_word` | In the stop-word list (~170 common English words) | `the`, `and`, `also` |
| `word` | Everything else | `terminology` |

Stop words are filtered out entirely and not indexed.

### FHIR Vocabulary

`FhirVocabulary` contains 120+ FHIR R4 resource names (Patient, Observation,
MedicationRequest, etc.) and 30+ FHIR operations ($validate, $expand,
$everything, etc.). All matching is case-insensitive.

## Cross-Reference Linking

The `CrossRefLinker` scans text fields for cross-source identifiers and creates
bidirectional links between items.

### Detection Patterns

| Pattern | Target Type | Example Match |
|---------|-------------|---------------|
| `\b(FHIR-\d+)\b` | `jira` | `FHIR-43499` |
| `https?://jira.hl7.org/browse/(FHIR-\d+)` | `jira` | Jira issue URLs |
| `https?://chat.fhir.org/#narrow/stream/...` | `zulip` | Zulip topic URLs |
| `https?://github.com/(HL7/[^/]+)/issues/(\d+)` | `github` | GitHub issue URLs |
| `https?://confluence.hl7.org/.*?/(\d+)` | `confluence` | Confluence page URLs |

### Link Storage

Each link is stored in the `xref_links` table as a `CrossRefLinkRecord`:

- **SourceType/SourceId** — The item containing the reference
- **TargetType/TargetId** — The referenced item
- **LinkType** — Always `"mention"` currently
- **Context** — ~100 characters of surrounding text

Links are deduplicated. Self-links are excluded. Jira URLs are checked before
bare Jira keys to avoid double-matching.

### Rebuild Modes

- **Incremental** (`LinkNewItems`) — Process only newly ingested items; delete
  existing links for those items first
- **Full** (`RebuildAllLinks`) — Clear all links and rescan all content tables

### Query Services

`CrossRefQueryService` provides:

- `GetRelatedItems()` — Bidirectional: finds both outgoing links (where item is
  source) and incoming links (where item is target)
- `GetMostReferenced()` — Aggregates reference counts across all sources
- `GetReferenceGraph()` — Multi-hop BFS traversal (default depth 2) expanding
  through both incoming and outgoing links

## Similarity Search

The `SimilaritySearchService` combines BM25 keyword overlap with cross-reference
boosting to find related items.

### Algorithm

1. Get top 10 keywords (by BM25 score) for the seed item
2. Find all other items sharing those keywords; sum their BM25 scores per
   candidate
3. Query explicit cross-references via `CrossRefQueryService`
4. **Boost** cross-referenced items by 2× (`XrefBoost = 2.0`); items found
   only via xref get a base score of 2.0
5. Sort by combined score, return top results
6. Enrich results with titles from source tables

## Score Normalization

The `ScoreNormalizer` applies min-max normalization to map raw scores to
`[0, 1]`:

```
normalized = (score - min) / (max - min)
```

When combining results across sources, normalization is applied per-source
first to ensure fair cross-source ranking. Optional per-source weight
multipliers can be applied for custom ranking preferences.

## Key Classes

| Class | File | Responsibility |
|-------|------|---------------|
| `FtsSearchService` | `FtsSearchService.cs` | FTS5 MATCH queries, query sanitization, score normalization |
| `Bm25Calculator` | `Bm25/Bm25Calculator.cs` | Full/incremental BM25 index builds, corpus stats, bulk score updates |
| `SimilaritySearchService` | `SimilaritySearchService.cs` | BM25 keyword overlap + xref boost for related items |
| `CrossRefLinker` | `CrossRefLinker.cs` | Regex-based cross-source identifier extraction |
| `CrossRefQueryService` | `CrossRefQueryService.cs` | Bidirectional xref queries, most-referenced items, graph traversal |
| `Tokenizer` | `Bm25/Tokenizer.cs` | FHIR-aware tokenization |
| `KeywordClassifier` | `Bm25/KeywordClassifier.cs` | Token classification (fhir_path, fhir_operation, stop_word, word) |
| `FhirVocabulary` | `Bm25/FhirVocabulary.cs` | FHIR resource names and operations dictionary |
| `StopWords` | `Bm25/StopWords.cs` | English stop-word list |
| `ScoreNormalizer` | `ScoreNormalizer.cs` | Min-max normalization with optional source weights |
| `TextSanitizer` | `TextSanitizer.cs` | HTML/Markdown stripping, Unicode normalization |
