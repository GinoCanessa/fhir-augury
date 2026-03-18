# Phase 3: Cross-Referencing & BM25

**Goal:** Cross-source linking via identifier extraction and advanced
relevance scoring with BM25 keywords for "find related" queries.

**Depends on:** Phase 2 (Zulip Integration)

---

## 3.1 — Cross-Reference Database Tables

### Objective

Define the cross-reference linking table.

### Files to Create in `FhirAugury.Database/`

#### 3.1.1 `Records/CrossRefLinkRecord.cs`

Fields: Id, SourceType, SourceId, TargetType, TargetId, LinkType, Context.

Indexes: (SourceType, SourceId), (TargetType, TargetId).

#### 3.1.2 Update `DatabaseService.InitializeDatabase()`

Add `xref_links` table creation.

### Acceptance Criteria

- [x] CRUD on `xref_links` works
- [x] Can query by source or target

---

## 3.2 — Cross-Reference Linker

### Objective

Scan text fields for identifiers from other sources and populate the
`xref_links` table.

### Files to Create in `FhirAugury.Indexing/`

#### 3.2.1 `CrossRefLinker.cs`

Pattern-based cross-reference extraction:

**Patterns to detect:**
| Pattern | Regex | Target |
|---------|-------|--------|
| Jira key | `\b(FHIR-\d+)\b` | `jira:{key}` |
| Jira URL | `https?://jira\.hl7\.org/browse/(FHIR-\d+)` | `jira:{key}` |
| Zulip URL | `https?://chat\.fhir\.org/#narrow/stream/(\d+)[^/]*/topic/(.+?)(?:\s\|$)` | `zulip:{stream}:{topic}` |
| GitHub issue URL | `https?://github\.com/(HL7/[^/]+)/issues/(\d+)` | `github:{repo}#{number}` |
| Confluence URL | `https?://confluence\.hl7\.org/.*?/(\d+)` | `confluence:{pageId}` |

**Methods:**
- `ExtractLinks(string text)` → `List<(string TargetType, string TargetId, string Context)>`
- `LinkNewItemsAsync(IngestionResult result, CancellationToken ct)` — processes all new/updated items
- `RebuildAllLinksAsync(connection, CancellationToken ct)` — full re-scan of all text fields
- `GetSurroundingText(string fullText, int matchIndex, int contextChars = 100)` — extracts context

**Implementation notes:**
- Compile all regex patterns with `RegexOptions.Compiled`
- Deduplicate links (same source→target pair) — keep first occurrence
- The context field stores ~100 chars around the match for display
- Avoid self-links (don't link an item to itself)

#### 3.2.2 `CrossRefQueryService.cs`

Query methods for cross-references:
- `GetRelatedItems(sourceType, sourceId)` → items that reference or are referenced by this item
- `GetMostReferenced(targetType, limit)` → most-referenced items (hotspots)
- `GetReferenceGraph(sourceType, sourceId, depth)` → multi-hop graph traversal

### Acceptance Criteria

- [x] Jira key pattern matches `FHIR-12345` in text
- [x] Jira URL pattern matches full URLs
- [x] Zulip URL pattern extracts stream and topic
- [x] Deduplication works (same link not inserted twice)
- [x] Context extraction provides meaningful surrounding text
- [x] Full rebuild scans all text fields in all source tables

---

## 3.3 — BM25 Keyword Scoring

### Objective

Implement the BM25 keyword extraction and scoring pipeline for "find similar"
queries.

### Files to Create in `FhirAugury.Database/`

#### 3.3.1 `Records/KeywordRecord.cs`

Fields: Id, SourceType, SourceId, Keyword, Count, KeywordType, Bm25Score.
Indexes: (SourceType, SourceId), Keyword, (Keyword, KeywordType).

#### 3.3.2 `Records/CorpusKeywordRecord.cs`

Fields: Id, Keyword, KeywordType, DocumentFrequency, Idf.
Index: (Keyword, KeywordType).

#### 3.3.3 `Records/DocStatsRecord.cs`

Fields: SourceType (PK), TotalDocuments, AverageDocLength.

### Files to Create in `FhirAugury.Indexing/`

#### 3.3.4 `Bm25/Tokenizer.cs`

Text tokenization:
- Split on whitespace and punctuation
- Preserve FHIR element paths (`Patient.name.given` → token + components)
- Preserve FHIR operations (`$validate`, `$expand`)
- Lowercase normalization
- Strip common noise (URLs, email addresses, code blocks)

#### 3.3.5 `Bm25/KeywordClassifier.cs`

Classify tokens:
- `word` — regular words
- `stop_word` — common English stop words (the, is, a, etc.)
- `fhir_path` — dotted paths matching FHIR resource/element names
- `fhir_operation` — `$`-prefixed operations

Uses a built-in stop word list and FHIR resource name list.

#### 3.3.6 `Bm25/Bm25Calculator.cs`

BM25 computation pipeline:

```
Input: collection of (SourceType, SourceId, text) tuples
  1. Tokenize each document
  2. Classify tokens, filter stop words
  3. Count per-document term frequency (TF)
  4. Store per-doc keyword records in index_keywords
  5. Compute corpus-level document frequency (DF) per keyword
  6. Compute IDF: log((N - df + 0.5) / (df + 0.5))
  7. Store corpus stats in index_corpus
  8. Compute BM25 per keyword per document:
     idf × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgDocLen))
  9. Update Bm25Score in index_keywords
  10. Store doc stats in index_doc_stats
```

Configurable parameters: `k1` (default 1.2), `b` (default 0.75).

**Methods:**
- `BuildFullIndexAsync(connection, CancellationToken ct)` — full corpus rebuild
- `UpdateIndexAsync(sourceType, items, CancellationToken ct)` — incremental update
  for newly ingested items (recomputes IDF for affected keywords)

#### 3.3.7 `Bm25/StopWords.cs`

Embedded stop word list (~150 common English words).

#### 3.3.8 `Bm25/FhirVocabulary.cs`

Known FHIR resource names and operations for token classification.
Can be loaded from embedded resource or static list.

### Acceptance Criteria

- [x] Tokenizer correctly splits text, preserves FHIR paths
- [x] Stop words are filtered from keyword index
- [x] BM25 scores are computed correctly (verify against known examples)
- [x] Full index build processes all Jira + Zulip documents
- [x] Incremental update handles new documents without full rebuild

---

## 3.4 — "Find Related" Feature

### Objective

Combine BM25 keyword similarity with explicit cross-references to find
related items across sources.

### Files to Create in `FhirAugury.Indexing/`

#### 3.4.1 `SimilaritySearchService.cs`

**Method: `FindRelatedAsync(sourceType, sourceId, limit)`**

1. Get top 10 keywords of the seed item (highest BM25 scores)
2. Find other items sharing those keywords, sum their BM25 scores
3. Get explicit cross-references for the seed item
4. Merge: cross-referenced items get a score boost
5. Deduplicate and sort by combined score
6. Enrich results with titles/snippets from source tables
7. Return top N results

### Acceptance Criteria

- [x] Given a Jira issue, returns related Zulip messages and vice versa
- [x] Explicit cross-references rank higher than keyword-only matches
- [x] Results span multiple sources
- [x] Performance acceptable for interactive use (<2s for a typical query)

---

## 3.5 — CLI Extensions

### Files to Update/Create

#### 3.5.1 `Commands/RelatedCommand.cs`

`fhir-augury related --source jira --id FHIR-43499 [--limit 20]`

- Calls `SimilaritySearchService.FindRelatedAsync()`
- Displays results in table/JSON/markdown format
- Shows source, ID, title, score, and relationship type

#### 3.5.2 Update `Commands/IndexCommand.cs`

Add subcommands:
- `fhir-augury index build-bm25` — build/rebuild BM25 keyword index
- `fhir-augury index build-xref` — build/rebuild cross-reference links
- `fhir-augury index rebuild-all` — rebuild FTS5 + BM25 + xref

#### 3.5.3 Update `Commands/SnapshotCommand.cs`

Add `--include-xref` flag to snapshot commands. When set, appends a
"Related Items" section showing cross-references from other sources.

### Acceptance Criteria

- [x] `fhir-augury related --source jira --id FHIR-12345` returns related items
- [x] `fhir-augury index build-bm25` builds keyword index with progress
- [x] `fhir-augury index build-xref` scans all text and builds links
- [x] `fhir-augury snapshot --source jira --id FHIR-12345 --include-xref` shows related

---

## 3.6 — Tests

### New Test Files

#### `tests/FhirAugury.Indexing.Tests/`

- `CrossRefLinkerTests.cs` — pattern matching for all identifier types,
  deduplication, context extraction
- `TokenizerTests.cs` — text splitting, FHIR path preservation
- `KeywordClassifierTests.cs` — stop word detection, FHIR path classification
- `Bm25CalculatorTests.cs` — BM25 computation with known inputs/outputs
- `SimilaritySearchTests.cs` — related item queries with mock keyword data

### Acceptance Criteria

- [x] All new tests pass
- [x] BM25 computation matches expected values for a small test corpus
- [x] Cross-reference patterns tested with positive and negative cases
