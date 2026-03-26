# Section 8: Tests

**Goal:** Comprehensive test coverage for the cache layer, file naming logic,
source integration, and end-to-end cache-only ingestion.

**Dependencies:** All previous sections

---

## 8.1 — CacheFileNaming Unit Tests

### Objective

Thoroughly test the shared file naming/parsing/sorting logic since it is the
foundation of correct cache behaviour for both Jira and Zulip.

### File to Create: `tests/FhirAugury.Sources.Tests/Caching/CacheFileNamingTests.cs`

### Test Cases

#### Parsing (`TryParse`)

| Test Name | Input | Expected |
|-----------|-------|----------|
| `Parse_WeeklyLegacy_NoSequence` | `_WeekOf_2024-08-05.xml` | WeekOf, 2024-08-05, null |
| `Parse_DailyLegacy_NoSequence` | `DayOf_2025-11-05.xml` | DayOf, 2025-11-05, null |
| `Parse_DailyCurrent_WithSequence` | `DayOf_2026-03-18-000.xml` | DayOf, 2026-03-18, 0 |
| `Parse_DailyCurrent_HighSequence` | `DayOf_2026-03-18-042.json` | DayOf, 2026-03-18, 42 |
| `Parse_WeeklyWithSequence` | `_WeekOf_2024-08-05-003.json` | WeekOf, 2024-08-05, 3 |
| `Parse_InvalidFormat_ReturnsFalse` | `random-file.xml` | false |
| `Parse_EmptyString_ReturnsFalse` | `""` | false |
| `Parse_InvalidDate_ReturnsFalse` | `DayOf_2026-13-45.xml` | false |
| `Parse_NoExtension_ReturnsFalse` | `DayOf_2026-03-18-000` | false |

#### Sorting (`SortForIngestion`)

| Test Name | Input Files | Expected Order |
|-----------|-------------|----------------|
| `Sort_DateAscending` | `DayOf_2026-03-20-000.xml`, `DayOf_2026-03-18-000.xml` | 18 → 20 |
| `Sort_WeeklyBeforeDaily_SameDate` | `DayOf_2024-08-05.xml`, `_WeekOf_2024-08-05.xml` | WeekOf → DayOf |
| `Sort_LegacyBeforeSequenced_SameDate` | `DayOf_2026-03-18.xml`, `DayOf_2026-03-18-000.xml` | legacy → 000 |
| `Sort_SequenceAscending` | `DayOf_2026-03-18-002.xml`, `DayOf_2026-03-18-000.xml`, `DayOf_2026-03-18-001.xml` | 000 → 001 → 002 |
| `Sort_MixedFormats` | All patterns from proposal | Correct chronological order |
| `Sort_EmptyList` | `[]` | `[]` |

#### Generation

| Test Name | Input | Expected |
|-----------|-------|----------|
| `GenerateDaily_NoExisting` | date=2026-03-18, ext=xml, existing=[] | `DayOf_2026-03-18-000.xml` |
| `GenerateDaily_WithExisting` | date=2026-03-18, existing=[...-000, ...-001] | `DayOf_2026-03-18-002.xml` |
| `GenerateWeekly_NormalizesToMonday` | date=2024-08-07 (Wed), ext=json | `_WeekOf_2024-08-05-000.json` |
| `GenerateWeekly_WithExisting` | date=2024-08-05, existing=[...-000] | `_WeekOf_2024-08-05-001.json` |

### Acceptance Criteria

- [ ] All parsing tests pass for all four file name patterns
- [ ] All sorting tests validate the proposal's ordering rules
- [ ] All generation tests verify auto-incrementing sequence numbers
- [ ] Edge cases (empty input, invalid dates, missing extensions) are covered

---

## 8.2 — FileSystemResponseCache Unit Tests

### Objective

Test the concrete cache implementation's file I/O operations.

### File to Create: `tests/FhirAugury.Sources.Tests/Caching/FileSystemResponseCacheTests.cs`

### Test Setup

Use a temporary directory (via `Path.GetTempPath()` + `Guid`) that is
cleaned up in `Dispose`.

### Test Cases

| Test Name | Description |
|-----------|-------------|
| `PutThenGet_RoundTrips` | Write content, read it back, verify bytes match |
| `TryGet_Missing_ReturnsFalse` | Get a non-existent key returns false |
| `PutAsync_CreatesDirectories` | Put with `"zulip/s270/file.json"` creates nested dirs |
| `PutAsync_AtomicWrite` | Verify no partial files on cancellation |
| `Remove_DeletesFile` | Put then remove, verify TryGet returns false |
| `EnumerateKeys_ReturnsAllFiles` | Put several files, enumerate, verify all returned |
| `EnumerateKeys_ExcludesMetaFiles` | Put `_meta_jira.json`, verify it's not enumerated |
| `EnumerateKeys_SortsBatchFiles` | Put mixed date files, verify chronological order |
| `EnumerateKeys_WithSubPath` | Enumerate only files in a specific subdirectory |
| `Clear_RemovesSourceDirectory` | Clear one source, verify files gone, other source intact |
| `ClearAll_RemovesEverything` | ClearAll, verify all sources' files gone |
| `GetStats_ReturnsCorrectCounts` | Put files, verify count and byte totals |
| `PathTraversal_Rejected` | Key containing `..` throws or is rejected |

### Acceptance Criteria

- [ ] All I/O operations tested against real temp directory
- [ ] Metadata file exclusion verified
- [ ] Sort order verified for batch files
- [ ] Cleanup runs even on test failure (IDisposable)

---

## 8.3 — CacheMetadata Unit Tests

### Objective

Test metadata serialization round-trips and file operations.

### File to Create: `tests/FhirAugury.Sources.Tests/Caching/CacheMetadataTests.cs`

### Test Cases

| Test Name | Description |
|-----------|-------------|
| `JiraMetadata_RoundTrip` | Serialize → deserialize `JiraCacheMetadata` |
| `ZulipStreamMetadata_RoundTrip` | Serialize → deserialize `ZulipStreamCacheMetadata` |
| `ConfluenceMetadata_RoundTrip` | Serialize → deserialize `ConfluenceCacheMetadata` |
| `ReadMetadata_MissingFile_ReturnsNull` | Read from non-existent path returns null |
| `WriteMetadata_CreatesDirectories` | Write to nested path creates parents |

### Acceptance Criteria

- [ ] All metadata types round-trip correctly via System.Text.Json
- [ ] Missing file handling is graceful

---

## 8.4 — NullResponseCache Tests

### Objective

Verify the no-op cache behaves correctly.

### File to Create: `tests/FhirAugury.Sources.Tests/Caching/NullResponseCacheTests.cs`

### Test Cases

| Test Name | Description |
|-----------|-------------|
| `TryGet_AlwaysReturnsFalse` | Any source/key combination returns false |
| `PutAsync_DoesNotThrow` | Write succeeds silently |
| `EnumerateKeys_ReturnsEmpty` | No keys ever returned |
| `GetStats_ReturnsZeros` | File count and bytes are zero |

### Acceptance Criteria

- [ ] All methods are no-ops that don't throw

---

## 8.5 — Jira Cache Integration Test

### Objective

End-to-end test: pre-populate a directory with Jira XML files in various
naming patterns → run `CacheOnly` download → verify records in the database
reflect the newest version of each ticket.

### File to Create: `tests/FhirAugury.Integration.Tests/JiraCacheIntegrationTests.cs`

### Test Setup

1. Create a temp directory with the following pre-seeded files:
   - `_WeekOf_2024-08-05.xml` — contains ticket FHIR-100 (version A)
   - `DayOf_2024-08-06.xml` — contains ticket FHIR-100 (version B, updated)
   - `DayOf_2024-08-07-000.xml` — contains ticket FHIR-101 (new)

2. Use the existing `sample-jira-export.xml` test data as a template for
   generating these files, modifying dates and content.

### Test Scenarios

| Test Name | Verify |
|-----------|--------|
| `CacheOnly_LoadsAllFormats` | All three file patterns are processed |
| `CacheOnly_OldestToNewest` | Processing order is chronological |
| `CacheOnly_UpsertResolvesNewest` | FHIR-100 in DB has version B (from later file) |
| `CacheOnly_NoNetworkCalls` | No HttpClient calls made (use mock handler) |
| `CacheOnly_EmptyDirectory_ReturnsZeroItems` | Empty cache → zero items processed |

### Acceptance Criteria

- [ ] All file naming patterns ingested
- [ ] Upsert semantics produce correct final state
- [ ] Zero network activity verified
- [ ] Existing test data (`sample-jira-export.xml`) reused where possible

---

## 8.6 — Zulip Cache Integration Test

### Objective

End-to-end test: pre-populate a Zulip cache directory with per-stream weekly
and daily batch files → run `CacheOnly` → verify messages resolve correctly.

### File to Create: `tests/FhirAugury.Integration.Tests/ZulipCacheIntegrationTests.cs`

### Test Setup

1. Create a temp directory:
   ```
   zulip/
   ├── _meta_s1.json
   ├── s1/
   │   ├── _WeekOf_2024-08-05-000.json   (message ID 100, version A)
   │   ├── DayOf_2024-08-06-000.json     (message ID 100, version B)
   │   └── DayOf_2024-08-07-000.json     (message ID 200, new)
   ```

2. Use `sample-zulip-messages.json` test data as template.

### Test Scenarios

| Test Name | Verify |
|-----------|--------|
| `CacheOnly_DiscoversStreamsFromDirectories` | Stream s1 discovered |
| `CacheOnly_LoadsWeeklyAndDailyBatches` | Both prefixes processed |
| `CacheOnly_UpsertResolvesNewest` | Message 100 has version B |
| `CacheOnly_ReadsStreamNameFromMetadata` | Stream record has name from meta file |
| `CacheOnly_MultipleStreams` | Multiple `s{id}` directories all processed |

### Acceptance Criteria

- [ ] Stream discovery from directory names works
- [ ] Weekly and daily batches mixed correctly
- [ ] Metadata-sourced stream names applied
- [ ] Duplicate message resolution via upsert verified

---

## 8.7 — Test Data Files

### Objective

Create test data files for cache integration tests.

### Files to Create in `tests/TestData/cache/`

| File | Purpose |
|------|---------|
| `jira/_WeekOf_2024-08-05.xml` | Jira weekly batch (legacy format) |
| `jira/DayOf_2024-08-06.xml` | Jira daily batch (legacy, no sequence) |
| `jira/DayOf_2024-08-07-000.xml` | Jira daily batch (current format) |
| `zulip/s1/_WeekOf_2024-08-05-000.json` | Zulip weekly batch |
| `zulip/s1/DayOf_2024-08-06-000.json` | Zulip daily batch |
| `confluence/pages/12345.json` | Confluence page cache |

### Acceptance Criteria

- [ ] Test data files match the expected cache directory structure
- [ ] Files contain valid, parseable content
- [ ] Content includes overlapping records for upsert testing
