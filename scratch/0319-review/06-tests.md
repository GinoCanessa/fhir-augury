# Code Review: Test Suite

**Reviewed:** 2026-03-19
**Scope:** All 5 test projects (~218 test methods, 42 files)

---

## Critical Findings

### 1. SearchEndpointTests never cleans up temp database files
**File:** `SearchEndpointTests.cs`

Implements `IClassFixture<SearchTestFactory>` but never implements `IDisposable`, so temp database files accumulate on disk indefinitely.

```csharp
// SearchEndpointTests.cs:11 — no IDisposable
public class SearchEndpointTests : IClassFixture<SearchEndpointTests.SearchTestFactory>
{
    // ...no Dispose() method...
}

// Compare with IngestEndpointTests.cs:12 — correct
public class IngestEndpointTests : IClassFixture<IngestTestFactory>, IDisposable
{
    public void Dispose() => _factory.Cleanup();
}
```

**Fix:** Add `IDisposable` with `Dispose()` calling `_factory.Cleanup()`.

---

### 2. JiraFieldMapperTests leaks JsonDocument on every test method
**File:** `JiraFieldMapperTests.cs:8-12` | ✅ **FIXED**

**Resolution:** Changed `LoadSampleIssue()` to use `.RootElement.Clone()` which detaches from the pooled `JsonDocument`, allowing it to be garbage collected safely.

`LoadSampleIssue()` calls `JsonDocument.Parse()` and returns only the `RootElement`. The `JsonDocument` is never disposed, leaking unmanaged memory. Called **13 times**.

```csharp
private static JsonElement LoadSampleIssue()
{
    var json = File.ReadAllText(Path.Combine("TestData", "sample-jira-issue.json"));
    return JsonDocument.Parse(json).RootElement; // JsonDocument leaked!
}
```

**Fix:** Store `JsonDocument` as a field and implement `IDisposable`, or refactor tests to use `using var doc = ...`.

---

### 3. ZulipMessageMapperTests also leaks JsonDocument
**File:** `ZulipMessageMapperTests.cs:10-14` | ✅ **FIXED**

**Resolution:** Changed `LoadTestData()` to use `.RootElement.Clone()` — same fix as JiraFieldMapperTests.

Same pattern as above. `LoadTestData()` returns `JsonElement` from an undisposed `JsonDocument`, called 11 times.

---

## High Findings

### 4. Massively duplicated `CreateInMemoryDb()` — 6 independent copies
Code duplication / maintenance burden across:

| Location | Tables initialized |
|---|---|
| `Database.Tests/TestHelper.cs:9` | Standard tables + FTS |
| `Indexing.Tests/Bm25CalculatorTests.cs:12` | + CrossRefLink, Keyword, CorpusKeyword, DocStats |
| `Indexing.Tests/SimilaritySearchTests.cs:12` | Same as Bm25CalculatorTests |
| `Indexing.Tests/FourSourceSearchTests.cs:11` | Standard tables + FTS (no indexing tables) |
| `Indexing.Tests/UnifiedSearchTests.cs:10` | Standard tables + FTS (no indexing tables) |
| `Mcp.Tests/McpTestHelper.cs:12` | All tables including indexing tables |

**Fix:** Create a single shared `TestDatabaseHelper.CreateInMemoryDb()` parameterized by table sets.

---

### 5. Duplicated TestHelper / McpTestHelper factory methods
Nearly identical factory methods for creating sample records. Will diverge over time.

**Fix:** Extract into a shared test data builder in a common test utilities assembly.

---

### 6. Almost no error path coverage (2 out of ~218 tests)
Only 2 tests use `Assert.Throws` / `Assert.ThrowsAsync`. Missing error path tests include:
- Duplicate `Insert` with `ignoreDuplicates: false`
- Closed/corrupt DB connection
- FTS search with SQL injection–like input
- `JiraFieldMapper.MapIssue` with malformed JSON
- `ConfluenceContentParser.ToPlainText` with extremely large input
- `Bm25Calculator.BuildFullIndex` on empty DB
- `SimilaritySearchService.FindRelated` with invalid sourceType

---

### 7. No tests for data source classes
The four core `*Source` classes have **zero test coverage**:
- `JiraSource.cs`, `ZulipSource.cs`, `ConfluenceSource.cs`, `GitHubSource.cs`

These are the most complex classes in the system, handling HTTP calls, pagination, caching, error recovery.

---

### 8. No tests for auth handlers
Three auth handler classes have zero coverage:
- `JiraAuthHandler.cs`, `ZulipAuthHandler.cs`, `ConfluenceAuthHandler.cs`

Only `GitHubRateLimiterTests` partially tests one handler (3 tests on `CreateHttpClient`, not actual delegation).

---

## Medium Findings

### 9. JiraXmlParserTests re-parses same XML file 9 times
Every test calls `LoadSampleXml()` → `File.OpenRead()` → `JiraXmlParser.ParseExport(stream).ToList()`.

**Fix:** Use `IClassFixture<T>` or `static Lazy<>` to parse once and share.

---

### 10. Database delete tests use raw SQL instead of generated Delete methods
**JiraIssueRecordTests.cs:48-56, JiraCommentRecordTests.cs:63-68, ZulipMessageRecordTests.cs:55-58**

```csharp
// Comment: "Use SQL delete directly as generated Delete may have issues with nullable params"
```

This comment suggests a **known bug** in the generated Delete method. Either fix it or add explicit tests verifying the failure.

---

### 11. GitHubIssueMapperTests reads same file 6 times
Same redundant parsing pattern.

---

### 12. No negative/boundary tests for Tokenizer
Missing: very long strings, only stop words, only URLs/emails, Unicode, only punctuation, single-character tokens.

---

### 13. No tests for CrossRefQueryService
The query side of cross-references has zero direct tests. Only extraction logic is tested.

---

### 14. No tests for FtsSearchService directly
Only tested indirectly. Missing: `SearchConfluencePages`, `SearchGitHubIssues`, empty queries, special characters, very long queries.

---

### 15. Integration tests only check HTTP status codes
Most verify only the HTTP status code without validating response shape or content:

```csharp
var response = await client.GetAsync("/api/v1/search/jira?q=patient");
Assert.Equal(HttpStatusCode.OK, response.StatusCode);
// No body validation
```

---

### 16. No tests for DatabaseService class
Connection pooling, `InitializeDatabase`, `OpenConnection`, `Dispose`, `CreateInMemory`, batch size — all untested directly.

---

## Low Findings

| # | Finding | Details |
|---|---------|---------|
| 17 | No tests for CLI project | 11 command files + ServiceClient + OutputFormatter = 0 tests |
| 18 | No tests for background workers | `IngestionWorker` and `ScheduledIngestionService` untested |
| 19 | 6 of 8 API endpoint classes untested | Only `IngestEndpoints` and `SearchEndpoints` partially tested |
| 20 | `HttpRetryHelper` has no tests | Critical retry/backoff infrastructure untested |
| 21 | Inconsistent test naming conventions | Mix of `Method_Scenario_Result` and `Method_DoesVerb` |
| 22 | MCP tests use temp files instead of in-memory DBs | Performance risk and cleanup burden |
| 23 | Weak FTS snippet assertion | `Assert.NotNull(result.Snippet)` — should verify content |
| 24 | ScoreNormalizerTests assumes stable ordering | Should find items by ID, not index |

---

## Test Coverage Gap Summary

| Component | Coverage |
|-----------|----------|
| Data source classes (4) | ❌ None |
| Auth handlers (3) | ❌ None |
| CLI commands (11) | ❌ None |
| Background workers (2) | ❌ None |
| HttpRetryHelper | ❌ None |
| DatabaseService | ❌ None |
| CrossRefQueryService | ❌ None |
| FtsSearchService (direct) | ❌ None |
| 6/8 API endpoints | ❌ None |
| Error/exception paths | ⚠️ 2/218 tests |
| Record CRUD operations | ✅ Good |
| Mappers/parsers | ✅ Good |
| Indexing (BM25, tokenizer) | ✅ Good |
| MCP tools | ✅ Good |
| FTS triggers | ✅ Good |

---

## Good Practices Observed ✅
- Test isolation: each DB test creates own in-memory DB
- AAA pattern followed consistently
- `[Theory]`/`[InlineData]` used appropriately
- Edge cases in TextSanitizer, ConfluenceContentParser, CrossRefLinker, Tokenizer
- `IDisposable` cleanup in MCP test classes
- Realistic test data files (JSON/XML)
- Concurrent access test for `IngestionQueue`
- Security test for path traversal in `FileSystemResponseCache`
- FTS trigger verification (INSERT/UPDATE/DELETE)
- BM25 IDF invariant verification

---

## Summary

| Severity | Count |
|----------|-------|
| **Critical** | 3 |
| **High** | 5 |
| **Medium** | 8 |
| **Low** | 8 |
| **Total** | **24** |

### Top Priorities
1. **Fix the 3 critical resource leaks** (JsonDocument disposals + SearchEndpointTests cleanup)
2. **Consolidate 6 duplicated `CreateInMemoryDb` methods** into shared test utility
3. **Add error/exception path tests** — near-zero negative testing across 218 tests
4. **Add tests for data source classes** with mocked HTTP handlers
