# FHIR Augury — Indexing & Search

## Full-Text Search (FTS5)

### Strategy

Each source has a dedicated FTS5 virtual table that indexes the most
searchable text fields. The unified search combines results from all
FTS5 tables, ranked by FTS5's built-in `rank` function (BM25-based).

### FTS5 Table Definitions

| Source | FTS5 Table | Indexed Fields |
|--------|-----------|----------------|
| Zulip | `zulip_messages_fts` | stream_name, topic, sender_name, content_plain |
| Jira | `jira_issues_fts` | key, title, description, summary, resolution_description, labels, specification, work_group, related_artifacts |
| Jira | `jira_comments_fts` | issue_key, author, body |
| Confluence | `confluence_pages_fts` | title, body_plain, labels |
| GitHub | `github_issues_fts` | title, body, labels |
| GitHub | `github_comments_fts` | body |

### FTS5 Configuration

All FTS5 tables use content-synced mode for efficient incremental updates:

```sql
CREATE VIRTUAL TABLE zulip_messages_fts USING fts5(
    stream_name, topic, sender_name, content_plain,
    content='zulip_messages',
    content_rowid='Id'
);

-- Triggers for automatic sync
CREATE TRIGGER zulip_messages_ai AFTER INSERT ON zulip_messages BEGIN
    INSERT INTO zulip_messages_fts(rowid, stream_name, topic, sender_name, content_plain)
    VALUES (new.Id, new.StreamName, new.Topic, new.SenderName, new.ContentPlain);
END;

CREATE TRIGGER zulip_messages_ad AFTER DELETE ON zulip_messages BEGIN
    INSERT INTO zulip_messages_fts(zulip_messages_fts, rowid, stream_name, topic, sender_name, content_plain)
    VALUES ('delete', old.Id, old.StreamName, old.Topic, old.SenderName, old.ContentPlain);
END;

CREATE TRIGGER zulip_messages_au AFTER UPDATE ON zulip_messages BEGIN
    INSERT INTO zulip_messages_fts(zulip_messages_fts, rowid, stream_name, topic, sender_name, content_plain)
    VALUES ('delete', old.Id, old.StreamName, old.Topic, old.SenderName, old.ContentPlain);
    INSERT INTO zulip_messages_fts(rowid, stream_name, topic, sender_name, content_plain)
    VALUES (new.Id, new.StreamName, new.Topic, new.SenderName, new.ContentPlain);
END;
```

These triggers ensure the FTS5 index is always up-to-date as the service
inserts or updates records — no separate rebuild step needed for incremental
operations.

### Text Sanitization

Before indexing, text is sanitized:

1. **HTML stripping** — Zulip content, Confluence storage format, and GitHub
   markdown are stripped of HTML/XML tags.
2. **Markdown stripping** — Optional removal of markdown syntax.
3. **Unicode normalization** — NFC normalization for consistent matching.
4. **FHIR-aware tokenization** — Dotted paths like `Patient.name.given` are
   preserved as tokens alongside their components.

---

## BM25 Keyword Scoring

### Purpose

While FTS5 provides good relevance ranking for text queries, pre-computed
BM25 scores enable fast "find similar items" queries without re-running the
full-text search engine. This is the approach used successfully in
JiraFhirUtils.

### Pipeline

```
┌────────────┐    ┌──────────────┐    ┌─────────────┐    ┌──────────────┐
│  Source     │───▶│  Tokenize &  │───▶│  Count      │───▶│  Compute     │
│  Text       │    │  Lemmatize   │    │  per-doc TF  │    │  IDF & BM25  │
└────────────┘    └──────────────┘    └─────────────┘    └──────────────┘
```

1. **Tokenize** — split text into words, preserving FHIR element paths and
   operation names as special token types.
2. **Classify** — mark each token as `word`, `stop_word`, `fhir_path`, or
   `fhir_operation` using a reference dictionary.
3. **Lemmatize** — reduce inflected words to their base form using a lemma
   dictionary (optional, from auxiliary DB).
4. **Count** — compute per-document term frequency (TF) for each keyword.
5. **Compute IDF** — across the entire corpus: `log((N - df + 0.5) / (df + 0.5))`
6. **Compute BM25** — per keyword per document:
   `idf × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgDocLen))`
7. **Store** — persist keyword records with BM25 scores in `index_keywords`.

### "Find Similar" Query

Given a seed item, find related items across all sources:

```csharp
public async Task<IReadOnlyList<SearchResult>> FindSimilarAsync(
    string sourceType, string sourceId, int limit = 20)
{
    // 1. Get top keywords of the seed item (by BM25 score)
    var seedKeywords = KeywordRecord.SelectList(conn,
        SourceType: sourceType, SourceId: sourceId)
        .OrderByDescending(k => k.Bm25Score)
        .Take(10)
        .ToList();

    // 2. Find other items sharing those keywords, sum their scores
    var sql = @"
        SELECT SourceType, SourceId, SUM(Bm25Score) as TotalScore
        FROM index_keywords
        WHERE Keyword IN ({keywords})
          AND NOT (SourceType = @seedType AND SourceId = @seedId)
        GROUP BY SourceType, SourceId
        ORDER BY TotalScore DESC
        LIMIT @limit";

    // 3. Enrich with titles/snippets from source tables
    return EnrichResults(conn, rawResults);
}
```

---

## Cross-Source Linking

### Identifier Patterns

The cross-reference linker scans text fields for known identifiers:

| Pattern | Source | Example |
|---------|--------|---------|
| `FHIR-\d+` | Jira | "See FHIR-43499 for details" |
| `#\d+` (in GitHub context) | GitHub | "Fixes #823" |
| `https://jira.hl7.org/browse/FHIR-\d+` | Jira (URL) | Full Jira link |
| `https://chat.fhir.org/#narrow/stream/...` | Zulip (URL) | Full Zulip link |
| `https://confluence.hl7.org/.../\d+` | Confluence (URL) | Full Confluence link |
| `https://github.com/HL7/.+/issues/\d+` | GitHub (URL) | Full GitHub link |

### Linking Process

```csharp
public class CrossRefLinker
{
    private static readonly Regex JiraKeyPattern =
        new(@"\b(FHIR-\d+)\b", RegexOptions.Compiled);

    private static readonly Regex JiraUrlPattern =
        new(@"https?://jira\.hl7\.org/browse/(FHIR-\d+)", RegexOptions.Compiled);

    private static readonly Regex ZulipUrlPattern =
        new(@"https?://chat\.fhir\.org/#narrow/stream/(\d+)[^/]*/topic/(.+?)(?:\s|$)",
            RegexOptions.Compiled);

    // ... more patterns

    public async Task LinkNewItemsAsync(IngestionResult result, CancellationToken ct)
    {
        foreach (var item in result.NewAndUpdatedItems)
        {
            var textFields = item.GetSearchableText();
            var links = ExtractLinks(textFields);

            foreach (var link in links)
            {
                CrossRefLinkRecord.Insert(conn, new CrossRefLinkRecord
                {
                    SourceType = item.SourceType,
                    SourceId = item.SourceId,
                    TargetType = link.TargetType,
                    TargetId = link.TargetId,
                    LinkType = "mentions",
                    Context = link.SurroundingText,
                });
            }
        }
    }
}
```

### Cross-Reference Queries

```sql
-- Find everything that mentions FHIR-43499
SELECT * FROM xref_links
WHERE TargetType = 'jira' AND TargetId = 'FHIR-43499';

-- Find all cross-references FROM a Zulip message
SELECT * FROM xref_links
WHERE SourceType = 'zulip' AND SourceId = '987654';

-- Find the most-referenced Jira issues (hotspots)
SELECT TargetId, COUNT(*) as ref_count
FROM xref_links
WHERE TargetType = 'jira'
GROUP BY TargetId
ORDER BY ref_count DESC
LIMIT 20;
```

---

## Unified Search Architecture

The unified search merges results from all FTS5 tables using a scored union:

```csharp
public IReadOnlyList<UnifiedSearchResult> UnifiedSearch(
    string query, IReadOnlySet<string>? sources, int limit)
{
    var allResults = new List<UnifiedSearchResult>();

    if (sources is null or { Count: 0 } || sources.Contains("jira"))
        allResults.AddRange(SearchJiraFts(conn, query, limit));

    if (sources is null or { Count: 0 } || sources.Contains("zulip"))
        allResults.AddRange(SearchZulipFts(conn, query, limit));

    if (sources is null or { Count: 0 } || sources.Contains("confluence"))
        allResults.AddRange(SearchConfluenceFts(conn, query, limit));

    if (sources is null or { Count: 0 } || sources.Contains("github"))
        allResults.AddRange(SearchGitHubFts(conn, query, limit));

    // Normalize scores across sources (FTS5 ranks aren't directly comparable)
    NormalizeScores(allResults);

    return allResults
        .OrderByDescending(r => r.NormalizedScore)
        .Take(limit)
        .ToList();
}
```

Score normalization uses min-max scaling within each source to make cross-source
ranking fair.
