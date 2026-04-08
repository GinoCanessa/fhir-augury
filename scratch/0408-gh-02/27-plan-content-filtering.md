# Implementation Plan: Content Filtering (Proposal 07)

## Prerequisites

1. **Strategy implementations (proposal 06)** — Each strategy must define meaningful `GetPriorityPaths()` and `GetAdditionalIgnorePatterns()` return values. Content filtering is only effective when strategies return non-null priority paths.
2. **Indexer behavior understanding** — The existing `GitHubFileContentIndexer.IndexRepositoryFiles()` already treats `effectiveIncludeOnlyPaths` as a hard filter (lines 83-101). This plan verifies and documents that behavior rather than implementing new filtering logic.

---

## Phase 1: Strategy Method Updates ✅

### Goal

Implement `GetPriorityPaths()` and `GetAdditionalIgnorePatterns()` for each strategy so that the file content indexer restricts indexing to artifact directories only.

### 1.1 FhirCoreStrategy

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/FhirCoreStrategy.cs`

`GetPriorityPaths()` — **No change needed.** Already returns `["source/"]` (line 63).

`GetAdditionalIgnorePatterns()` — **Update from `[]` to:**

```csharp
public List<string> GetAdditionalIgnorePatterns()
{
    return
    [
        "source/**/list-*.xml",
        "source/**/*.txt",
        "source/**/*.json",
        "source/**/implementationguide-*.xml",
    ];
}
```

**Rationale:** Within `source/`, these files are metadata lists, example payloads, JSON edge-case samples, and IG metadata — none are canonical FHIR artifacts. All non-`source/` directories (`qa/`, `tests/`, `tools/`, `implementations/`, `schema/`, `plugin/`, `gradle/`, `images/`, `health-cards-dev-tools/`) are already excluded by the priority path filter.

### 1.2 UtgStrategy

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/UtgStrategy.cs`

`GetPriorityPaths()` — **Change from `null` to:**

```csharp
public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
{
    return ["input/sourceOfTruth/", "input/resources/"];
}
```

`GetAdditionalIgnorePatterns()` — **Change from `[]` to:**

```csharp
public List<string> GetAdditionalIgnorePatterns()
{
    return
    [
        "input/sourceOfTruth/history/",
        "input/sourceOfTruth/control-manifests/",
        "input/sourceOfTruth/release-tracking/",
    ];
}
```

**Rationale:** `input/sourceOfTruth/` contains the canonical terminology artifacts. `input/resources/` contains 9 MIF extension StructureDefinitions. The three excluded subdirectories contain historical tracking data, build configuration, and release management files that aren't artifacts. All other directories (`input/pagecontent/`, `input/includes/`, `input/input-cache/`) are excluded by priority path filtering.

### 1.3 FhirExtensionsPackStrategy

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/FhirExtensionsPackStrategy.cs`

`GetPriorityPaths()` — **Change from `null` to:**

```csharp
public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
{
    return ["input/definitions/"];
}
```

`GetAdditionalIgnorePatterns()` — **No change needed.** Returns `[]` because the `definitions/` directory is clean — it contains only FHIR artifact files organized by target resource.

### 1.4 IncubatorStrategy

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/IncubatorStrategy.cs`

`GetPriorityPaths()` — **Change from `null` to:**

```csharp
public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
{
    return ["input/resources/", "input/fsh/"];
}
```

`GetAdditionalIgnorePatterns()` — **Change from `[]` to:**

```csharp
public List<string> GetAdditionalIgnorePatterns()
{
    return
    [
        "input/fsh/fsh-index.txt",
        "fixme/",
    ];
}
```

**Rationale:** `input/resources/` contains XML FHIR artifacts. `input/fsh/` contains FSH source definitions. `fsh-index.txt` is a SUSHI-generated index file. `fixme/` contains temporary files (seen in cg-incubator). All other directories (`input/pagecontent/`, `input/images/`) are excluded by priority path filtering.

### 1.5 IgStrategy (Deferred)

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/IgStrategy.cs`

No changes in this proposal. IG repository support is deferred (not covered in this proposal set). The IgStrategy stub continues to return `null` for `GetPriorityPaths()` and `[]` for `GetAdditionalIgnorePatterns()`, meaning IG repos continue with full-tree indexing.

---

## Phase 2: Indexer Behavior Verification ✅

### Goal

Confirm that `GitHubFileContentIndexer` already implements the hard-filter semantics needed, and document any edge cases or gaps.

### 2.1 Verify IndexRepositoryFiles() Filtering

**File:** `src/FhirAugury.Source.GitHub/Ingestion/GitHubFileContentIndexer.cs`

The critical code path (lines 54-101) already implements hard filtering:

```csharp
// Line 55-57: Merge priority paths with global IncludeOnlyPaths
List<string> effectiveIncludeOnlyPaths = priorityPaths is not null
    ? [.. _config.IncludeOnlyPaths, .. priorityPaths]
    : _config.IncludeOnlyPaths;

// Lines 83-101: Check include-only paths (hard filter)
if (effectiveIncludeOnlyPaths.Count > 0)
{
    bool included = false;
    foreach (string includePath in effectiveIncludeOnlyPaths)
    {
        string normalizedInclude = includePath.Replace('\\', '/').TrimEnd('/');
        if (relativePath.StartsWith(normalizedInclude + "/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals(normalizedInclude, StringComparison.OrdinalIgnoreCase))
        {
            included = true;
            break;
        }
    }
    if (!included)
    {
        skippedByPattern++;
        continue;
    }
}
```

**Verification:** When `priorityPaths` is non-null (which it will now be for all strategies), `effectiveIncludeOnlyPaths` has entries, and files not matching any include path are skipped. **No code change needed.**

### 2.2 Verify Pipeline Wiring

**File:** `src/FhirAugury.Source.GitHub/Ingestion/GitHubIngestionPipeline.cs`

The pipeline correctly passes strategy methods to the indexer (lines 166-172):

```csharp
IRepoCategoryStrategy? strategy = _strategyMap.GetValueOrDefault(category);
List<string>? priorityPaths = strategy?.GetPriorityPaths(repo, clonePath);
List<string>? additionalIgnorePatterns = strategy?.GetAdditionalIgnorePatterns();

fileContentIndexer.IndexRepositoryFiles(repo, clonePath, ct,
    priorityPaths,
    additionalIgnorePatterns is { Count: > 0 } ? additionalIgnorePatterns : null);
```

**Verification:** Priority paths and ignore patterns from each strategy are already passed through to the indexer. **No code change needed.**

### 2.3 Verify IncrementalUpdate() Gap

**File:** `src/FhirAugury.Source.GitHub/Ingestion/GitHubFileContentIndexer.cs`

The `IncrementalUpdate()` method (line 174) does NOT check `effectiveIncludeOnlyPaths`. It only checks:
- File existence
- `ignoreMatcher.IsExcluded()` (ignore patterns only — no include-only check)
- `FileTypeClassifier.Classify()` (extension-based type check)
- File size limit

**Assessment:** This is a minor gap. Files changed outside priority paths could be re-indexed during incremental updates. However, the full `IndexRepositoryFiles()` runs on every sync (lines 169-172 of the pipeline), which correctly filters. The incremental path is only triggered for explicit incremental-only updates.

**Recommended fix (optional):** Add priority path filtering to `IncrementalUpdate()`:

```csharp
public void IncrementalUpdate(
    string repoFullName, string clonePath,
    IReadOnlyList<string> changedFiles,
    CancellationToken ct = default,
    List<string>? priorityPaths = null)
{
    // ... existing setup ...

    List<string> effectiveIncludeOnlyPaths = priorityPaths is not null
        ? [.. _config.IncludeOnlyPaths, .. priorityPaths]
        : _config.IncludeOnlyPaths;

    foreach (string relativePath in changedFiles)
    {
        // ... existing checks ...

        // Add: Check include-only paths
        if (effectiveIncludeOnlyPaths.Count > 0)
        {
            bool included = effectiveIncludeOnlyPaths.Any(p =>
                normalizedPath.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            if (!included)
            {
                DeleteFileRecord(connection, repoFullName, normalizedPath);
                removed++;
                continue;
            }
        }
    }
}
```

This fix is low priority but improves consistency.

---

## Phase 3: Database Cleanup ✅

### Goal

Remove stale `github_file_contents` records for files that were indexed before content filtering was active. These records exist because the old strategy stubs returned `null` for `GetPriorityPaths()`, allowing full-tree indexing.

### 3.1 Strategy-Level Cleanup in BuildArtifactMappings()

Each strategy's `BuildArtifactMappings()` should include a cleanup step. Since `BuildArtifactMappings()` runs after `IndexRepositoryFiles()` in the pipeline, stale rows from before filtering was active will still exist.

Add cleanup at the beginning of each strategy's `BuildArtifactMappings()`:

**FhirCoreStrategy:**
```csharp
// Remove non-artifact file content records
using (SqliteCommand cleanupCmd = connection.CreateCommand())
{
    cleanupCmd.CommandText = """
        DELETE FROM github_file_contents
        WHERE RepoFullName = @repo
        AND FilePath NOT LIKE 'source/%'
        """;
    cleanupCmd.Parameters.AddWithValue("@repo", repoFullName);
    int removed = cleanupCmd.ExecuteNonQuery();
    if (removed > 0)
        logger.LogInformation("Cleaned up {Count} non-artifact file content records for {Repo}", removed, repoFullName);
}
```

**UtgStrategy:**
```csharp
cleanupCmd.CommandText = """
    DELETE FROM github_file_contents
    WHERE RepoFullName = @repo
    AND FilePath NOT LIKE 'input/sourceOfTruth/%'
    AND FilePath NOT LIKE 'input/resources/%'
    """;
```

**FhirExtensionsPackStrategy:**
```csharp
cleanupCmd.CommandText = """
    DELETE FROM github_file_contents
    WHERE RepoFullName = @repo
    AND FilePath NOT LIKE 'input/definitions/%'
    """;
```

**IncubatorStrategy:**
```csharp
cleanupCmd.CommandText = """
    DELETE FROM github_file_contents
    WHERE RepoFullName = @repo
    AND FilePath NOT LIKE 'input/resources/%'
    AND FilePath NOT LIKE 'input/fsh/%'
    """;
```

### 3.2 Alternative: Pipeline-Level Generic Cleanup

Instead of per-strategy cleanup, add a generic cleanup method to `GitHubIngestionPipeline.PostIngestionAsync()` that runs after file indexing:

```csharp
// After IndexRepositoryFiles, clean up any stale records outside priority paths
if (priorityPaths is { Count: > 0 })
{
    using SqliteConnection connection = database.OpenConnection();
    CleanupStaleFileContents(connection, repo, priorityPaths);
}
```

**Pros:** DRY — no duplicated cleanup logic across strategies.
**Cons:** Couples pipeline-level code to strategy path semantics. Slightly less clear where cleanup responsibility lives.

**Recommendation:** Use the pipeline-level approach for cleaner separation. The cleanup logic is generic (delete rows where `FilePath NOT LIKE` any priority path prefix).

### 3.3 Cleanup Timing

The cleanup must happen **before** the BM25 index rebuild at line 212 of `GitHubIngestionPipeline.cs`:

```csharp
// Current order in PostIngestionAsync:
// 1. Clone repos
// 2. Extract commits
// 3. Index file contents (with new priority path filtering)
// 4. Apply tags
// 5. Build artifact mappings ← cleanup could go here
// 6. Extract cross-refs
// 7. Rebuild BM25 index ← must be AFTER cleanup
```

If using the pipeline-level approach, add the cleanup call between file indexing (step 3) and BM25 rebuild (step 7). The natural place is right after `fileContentIndexer.IndexRepositoryFiles()` returns.

---

## Phase 4: Documentation Update ✅

### Goal

Update the `GetPriorityPaths()` doc comment to clearly communicate its hard-filter semantics.

### 4.1 Update IRepoCategoryStrategy.cs

**File:** `src/FhirAugury.Source.GitHub/Ingestion/Categories/IRepoCategoryStrategy.cs`

Change the doc comment from:

```csharp
/// <summary>
/// Returns priority paths to focus file content indexing on, or null for default behavior.
/// </summary>
List<string>? GetPriorityPaths(string repoFullName, string clonePath);
```

To:

```csharp
/// <summary>
/// Returns the only paths that should be included in file content indexing.
/// Files outside these paths are completely excluded from the <c>github_file_contents</c> table.
/// Returns null to index all files (not recommended for structured repos).
/// Paths are relative to the clone root and should use forward slashes with a trailing slash
/// (e.g., <c>"source/"</c>, <c>"input/definitions/"</c>).
/// </summary>
List<string>? GetPriorityPaths(string repoFullName, string clonePath);
```

### 4.2 Update GetAdditionalIgnorePatterns() Doc

Also clarify the ignore patterns doc:

```csharp
/// <summary>
/// Returns additional gitignore-style patterns to exclude from file content indexing.
/// These are merged with global <see cref="FileContentIndexingOptions.IgnorePatterns"/>
/// and any <c>.augury-index-ignore</c> file in the repository root.
/// Applied after <see cref="GetPriorityPaths"/> filtering (only files within
/// priority paths are candidates for ignore pattern matching).
/// </summary>
List<string> GetAdditionalIgnorePatterns();
```

---

## Phase 5: BM25 Quality Verification ✅

### Goal

Verify that the filtered corpus produces better search results than the unfiltered corpus.

### 5.1 Baseline Capture

Before deploying content filtering, capture BM25 search results for representative queries:

| Query | Expected improvement |
|---|---|
| `"Patient.birthDate"` | Should return Patient SD XML, not build scripts |
| `"administrative-gender"` | Should return CodeSystem/ValueSet, not Java code |
| `"observation vital signs"` | Should return Observation profiles, not test fixtures |
| `"data-absent-reason"` | Should return CodeSystem definition, not example payloads |

### 5.2 Post-Filtering Verification

After deploying, re-run the same queries and verify:
- Artifact files rank higher (shorter distance from result to definition)
- Non-artifact files are absent from results
- No relevant artifacts are missing (false exclusion check)

### 5.3 Index Size Metrics

Capture `github_file_contents` row counts before and after:

```sql
SELECT RepoFullName, COUNT(*) as FileCount
FROM github_file_contents
GROUP BY RepoFullName;
```

---

## Testing Strategy ✅

### Unit Tests

**Location:** `tests/FhirAugury.Source.GitHub.Tests/`

| Test | Purpose |
|---|---|
| `FhirCoreStrategy_GetAdditionalIgnorePatterns_ReturnsExpectedPatterns` | Verify list-*.xml, *.txt, *.json, implementationguide-*.xml patterns |
| `UtgStrategy_GetPriorityPaths_ReturnsSourceOfTruthAndResources` | Verify non-null return with both paths |
| `UtgStrategy_GetAdditionalIgnorePatterns_ReturnsHistoryControlRelease` | Verify 3 directory exclusion patterns |
| `FhirExtensionsPackStrategy_GetPriorityPaths_ReturnsDefinitions` | Verify single path return |
| `IncubatorStrategy_GetPriorityPaths_ReturnsResourcesAndFsh` | Verify both paths |
| `IncubatorStrategy_GetAdditionalIgnorePatterns_ReturnsFshIndexAndFixme` | Verify 2 patterns |

### Integration Tests

| Test | Purpose |
|---|---|
| `IndexRepositoryFiles_WithPriorityPaths_OnlyIndexesMatchingFiles` | Verify hard-filter behavior with mock file tree |
| `IndexRepositoryFiles_WithIgnorePatterns_ExcludesMatchedFiles` | Verify ignore patterns are applied within priority paths |
| `BuildArtifactMappings_CleansUpStaleFileContents` | Verify stale row removal |

### Before/After Comparison Tests

| Test | Purpose |
|---|---|
| `FhirCore_FileCount_MatchesExpectedReduction` | ~5,000 → ~2,000 |
| `Utg_FileCount_MatchesExpectedReduction` | ~5,000 → ~4,300 |
| `ExtensionsPack_FileCount_MatchesExpectedReduction` | ~1,500 → ~740 |
| `Incubator_FileCount_MatchesExpectedReduction` | ~200-500 → ~30-80 |

---

## Verification Criteria

### File Count Reduction Metrics

| Repo Type | Before (Total Files) | After (Indexed Files) | Expected Reduction |
|---|---|---|---|
| FhirCore (`HL7/fhir`) | ~5,000+ | ~2,000 | ~60% |
| UTG (`HL7/UTG`) | ~5,000+ | ~4,300 | ~15% |
| ExtensionsPack (`HL7/fhir-extensions`) | ~1,500+ | ~740 | ~50% |
| Incubator (each) | ~200-500 | ~30-80 | ~70-85% |

### Functional Checks

1. Every strategy returns non-null from `GetPriorityPaths()` (except IgStrategy, which is deferred)
2. `GitHubFileContentIndexer.IndexRepositoryFiles()` produces `FileIndexingResult` with `SkippedByPattern` count reflecting filtered files
3. No `github_file_contents` rows exist for files outside priority paths after a full sync
4. BM25 search results for artifact-related queries return only artifact files
5. Issue, comment, and commit indexing remains unaffected (same counts before and after)
6. Stale `github_file_contents` rows from pre-filtering syncs are cleaned up
7. `IgnorePatternMatcher` correctly handles strategy-specific patterns (verify with existing `IgnorePatternMatcher` tests)
8. Build succeeds: `dotnet build fhir-augury.slnx`
9. All tests pass: `dotnet test fhir-augury.slnx`
