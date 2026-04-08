# Implementation Plan: StructureDefinition Element Indexing (Feature 03)

## 1. Prerequisites

### Hard Prerequisites

1. **Feature 02 (StructureDefinition Source Indexing) must be complete**, specifically:
   - `github_structure_definitions` table exists and is populated
   - `GitHubStructureDefinitionRecord` record class with CsLightDbGen attributes
   - `StructureDefinitionIndexer` service exists and inserts SD records
   - `FhirAugury.Parsing.Fhir` project is functional with `FhirContentParser.TryParseStructureDefinition()` returning `StructureDefinitionInfo` including populated `DifferentialElements`

2. **`StructureDefinitionInfo.DifferentialElements`** returns valid `ElementInfo` records — this must be verified by feature 02 / proposal 08 parser tests.

3. **All existing tests pass**: `dotnet test fhir-augury.slnx`

### Verification Before Starting

Run these checks:
```bash
dotnet build fhir-augury.slnx
dotnet test fhir-augury.slnx
```

Verify `GitHubStructureDefinitionRecord` exists at `src/FhirAugury.Source.GitHub/Database/Records/GitHubStructureDefinitionRecord.cs`.

Verify `StructureDefinitionIndexer` exists at `src/FhirAugury.Source.GitHub/Ingestion/StructureDefinitionIndexer.cs`.

---

## 2. Implementation Phases

```
Phase 1: Database Record
Phase 2: Element Extraction Integration
Phase 3: BM25 Integration
Phase 4: API / Query Enhancement
Phase 5: Testing & Verification
```

---

## 3. Phase 1: Database Record ✅

### Step 1.1: Create `GitHubSdElementRecord`

**Create**: `src/FhirAugury.Source.GitHub/Database/Records/GitHubSdElementRecord.cs`

```csharp
using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Element-level data from StructureDefinition differentials.</summary>
[LdgSQLiteTable("github_sd_elements")]
[LdgSQLiteIndex(nameof(StructureDefinitionId))]
[LdgSQLiteIndex(nameof(Path))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Path))]
[LdgSQLiteIndex(nameof(BindingValueSet))]
public partial record class GitHubSdElementRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>FK to github_structure_definitions.Id</summary>
    public required int StructureDefinitionId { get; set; }

    /// <summary>e.g., Patient.contact.relationship</summary>
    public required string ElementId { get; set; }

    /// <summary>e.g., Patient.contact.relationship</summary>
    public required string Path { get; set; }

    /// <summary>Last segment of path, e.g., relationship</summary>
    public required string Name { get; set; }

    /// <summary>Brief description</summary>
    public string? Short { get; set; }

    /// <summary>Full definition text</summary>
    public string? Definition { get; set; }

    /// <summary>Additional notes</summary>
    public string? Comment { get; set; }

    public int? MinCardinality { get; set; }

    /// <summary>"0", "1", or "*"</summary>
    public string? MaxCardinality { get; set; }

    /// <summary>Semicolon-joined type codes</summary>
    public string? Types { get; set; }

    /// <summary>Semicolon-joined profile canonical URLs</summary>
    public string? TypeProfiles { get; set; }

    /// <summary>Semicolon-joined target profile URLs (for Reference types)</summary>
    public string? TargetProfiles { get; set; }

    /// <summary>required / extensible / preferred / example</summary>
    public string? BindingStrength { get; set; }

    /// <summary>ValueSet canonical URL</summary>
    public string? BindingValueSet { get; set; }

    public string? SliceName { get; set; }

    /// <summary>0 or 1</summary>
    public int? IsModifier { get; set; }

    /// <summary>0 or 1</summary>
    public int? IsSummary { get; set; }

    /// <summary>Fixed value as string</summary>
    public string? FixedValue { get; set; }

    /// <summary>Pattern value as string</summary>
    public string? PatternValue { get; set; }

    /// <summary>Sequential order within the differential</summary>
    public required int FieldOrder { get; set; }
}
```

### Step 1.2: Update `GitHubDatabase.InitializeSchema()`

**File**: `src/FhirAugury.Source.GitHub/Database/GitHubDatabase.cs`

Add after the `GitHubStructureDefinitionRecord.CreateTable(connection);` line (added by feature 02):

```csharp
GitHubSdElementRecord.CreateTable(connection);
```

### Step 1.3: Update `GitHubDatabase.ResetDatabase()`

**File**: `src/FhirAugury.Source.GitHub/Database/GitHubDatabase.cs`

Add to the drop list **before** `github_structure_definitions` (element table must be dropped first due to FK relationship):

```sql
DROP TABLE IF EXISTS github_sd_elements;
```

### Step 1.4: Build and Verify

```bash
dotnet build fhir-augury.slnx
```

Verify the CsLightDbGen source generator produces the expected methods on `GitHubSdElementRecord`:
- `CreateTable(SqliteConnection)`
- `Insert(SqliteConnection, GitHubSdElementRecord, bool ignoreDuplicates)`
- `SelectList(SqliteConnection, ...)`
- `GetIndex()`

---

## 4. Phase 2: Element Extraction Integration ✅

### Step 2.1: Extend `StructureDefinitionIndexer`

**File**: `src/FhirAugury.Source.GitHub/Ingestion/StructureDefinitionIndexer.cs`

The existing `IndexStructureDefinitions()` method (created by feature 02) inserts `GitHubStructureDefinitionRecord` entries. Extend it to also insert `GitHubSdElementRecord` entries for each SD's differential elements.

**After** the SD record insert, add element insertion:

```csharp
// Inside the per-file loop, after inserting the SD record:

int sdId = sdRecord.Id; // The ID from the just-inserted GitHubStructureDefinitionRecord

foreach (ElementInfo element in sdInfo.DifferentialElements)
{
    ct.ThrowIfCancellationRequested();

    string types = string.Join(";", element.Types.Select(t => t.Code));
    string typeProfiles = string.Join(";", element.Types.SelectMany(t => t.Profiles ?? []));
    string targetProfiles = string.Join(";", element.Types.SelectMany(t => t.TargetProfiles ?? []));

    GitHubSdElementRecord elementRecord = new()
    {
        Id = GitHubSdElementRecord.GetIndex(),
        RepoFullName = repoFullName,
        StructureDefinitionId = sdId,
        ElementId = element.ElementId,
        Path = element.Path,
        Name = element.Name,
        Short = element.Short,
        Definition = element.Definition,
        Comment = element.Comment,
        MinCardinality = element.MinCardinality,
        MaxCardinality = element.MaxCardinality,
        Types = string.IsNullOrEmpty(types) ? null : types,
        TypeProfiles = string.IsNullOrEmpty(typeProfiles) ? null : typeProfiles,
        TargetProfiles = string.IsNullOrEmpty(targetProfiles) ? null : targetProfiles,
        BindingStrength = element.BindingStrength,
        BindingValueSet = element.BindingValueSet,
        SliceName = element.SliceName,
        IsModifier = element.IsModifier switch { true => 1, false => 0, null => null },
        IsSummary = element.IsSummary switch { true => 1, false => 0, null => null },
        FixedValue = element.FixedValue,
        PatternValue = element.PatternValue,
        FieldOrder = element.FieldOrder,
    };

    GitHubSdElementRecord.Insert(connection, elementRecord, ignoreDuplicates: true);
}
```

### Step 2.2: Update Repo Cleanup in `StructureDefinitionIndexer`

The existing cleanup at the start of `IndexStructureDefinitions()` deletes SD records for the repo. Elements must be deleted **first**:

```csharp
// At the start of IndexStructureDefinitions(), before deleting SDs:
using (SqliteCommand cmd = connection.CreateCommand())
{
    cmd.CommandText = "DELETE FROM github_sd_elements WHERE RepoFullName = @repo";
    cmd.Parameters.AddWithValue("@repo", repoFullName);
    cmd.ExecuteNonQuery();
}

// Then delete SDs (existing code from feature 02):
using (SqliteCommand cmd = connection.CreateCommand())
{
    cmd.CommandText = "DELETE FROM github_structure_definitions WHERE RepoFullName = @repo";
    cmd.Parameters.AddWithValue("@repo", repoFullName);
    cmd.ExecuteNonQuery();
}
```

### Step 2.3: Add Logging

Add element count logging at the end of the indexing method:

```csharp
logger.LogInformation(
    "Indexed {SdCount} StructureDefinitions with {ElementCount} elements for {Repo}",
    sdCount, elementCount, repoFullName);
```

### Step 2.4: Build

```bash
dotnet build fhir-augury.slnx
```

---

## 5. Phase 3: BM25 Integration ✅

### Step 3.1: Add `ContentTypes.Element`

**File**: `src/FhirAugury.Source.GitHub/ContentTypes.cs`

Add:

```csharp
public const string Element = "element";
```

The full class becomes:

```csharp
public static class ContentTypes
{
    public const string Issue = "issue";
    public const string Comment = "comment";
    public const string Commit = "commit";
    public const string File = "file";
    public const string StructureDefinition = "structuredefinition"; // added by feature 02
    public const string Element = "element";                         // added by feature 03
}
```

### Step 3.2: Update `GitHubIndexer.CollectDocuments()`

**File**: `src/FhirAugury.Source.GitHub/Indexing/GitHubIndexer.cs`

After the SD collection loop (added by feature 02), add element collection:

```csharp
List<GitHubSdElementRecord> elements = GitHubSdElementRecord.SelectList(connection);
foreach (GitHubSdElementRecord element in elements)
{
    ct.ThrowIfCancellationRequested();
    string text = string.Join(" ",
        new[] { element.Name, element.Short, element.Definition, element.Comment }
            .Where(s => !string.IsNullOrEmpty(s)));

    if (!string.IsNullOrWhiteSpace(text))
    {
        documents.Add(new()
        {
            ContentType = ContentTypes.Element,
            SourceId = $"{element.RepoFullName}:{element.Path}",
            Text = text,
        });
    }
}
```

### Step 3.3: Update Document List Capacity

Update the `List<IndexContent>` initial capacity in `CollectDocuments()` to account for element records:

```csharp
List<IndexContent> documents = new(
    issues.Count + comments.Count + commits.Count + fileContents.Count + sds.Count + elements.Count);
```

### Step 3.4: Build

```bash
dotnet build fhir-augury.slnx
```

---

## 6. Phase 4: API / Query Enhancement ✅

### Step 4.1: Enhance `ArtifactFileMapper.ResolveFilePaths()`

**File**: `src/FhirAugury.Source.GitHub/Indexing/ArtifactFileMapper.cs`

Replace the existing `elementPath` resolution (lines 58–67) with a query that uses `github_sd_elements`:

```csharp
if (!string.IsNullOrEmpty(elementPath))
{
    // Precise resolution via github_sd_elements
    using SqliteCommand cmd = new SqliteCommand(
        @"SELECT DISTINCT sd.FilePath
          FROM github_sd_elements e
          JOIN github_structure_definitions sd ON e.StructureDefinitionId = sd.Id
          WHERE sd.RepoFullName = @repo AND e.Path = @path",
        connection);
    cmd.Parameters.AddWithValue("@repo", repoFullName);
    cmd.Parameters.AddWithValue("@path", elementPath);
    using SqliteDataReader reader = cmd.ExecuteReader();
    while (reader.Read())
        paths.Add(reader.GetString(0));

    // Fallback to LIKE-based search if no precise match
    if (paths.Count == 0)
    {
        using SqliteCommand fallbackCmd = new SqliteCommand(
            "SELECT FilePath FROM github_spec_file_map WHERE RepoFullName = @repo AND FilePath LIKE @pattern",
            connection);
        fallbackCmd.Parameters.AddWithValue("@repo", repoFullName);
        fallbackCmd.Parameters.AddWithValue("@pattern", $"%{elementPath}%");
        using SqliteDataReader fallbackReader = fallbackCmd.ExecuteReader();
        while (fallbackReader.Read())
            paths.Add(fallbackReader.GetString(0));
    }
}
```

### Step 4.2: Build

```bash
dotnet build fhir-augury.slnx
```

---

## 7. Phase 5: Testing & Verification ✅

### Step 5.1: Unit Tests for Element Record

**In**: `tests/FhirAugury.Source.GitHub.Tests/`

Create or update a test class for `GitHubSdElementRecord`:

```csharp
[Fact]
public void GitHubSdElementRecord_RoundTrip()
{
    // Create in-memory SQLite database
    // Call GitHubSdElementRecord.CreateTable()
    // Insert a record with all fields populated
    // Read back via SelectList
    // Assert all fields match
}

[Fact]
public void GitHubSdElementRecord_SelectByStructureDefinitionId()
{
    // Insert parent GitHubStructureDefinitionRecord
    // Insert multiple GitHubSdElementRecord with same StructureDefinitionId
    // Query by StructureDefinitionId
    // Assert correct count and field values
}

[Fact]
public void GitHubSdElementRecord_SelectByPath()
{
    // Insert elements with different paths
    // Query by specific path
    // Assert correct element returned
}

[Fact]
public void GitHubSdElementRecord_BindingValueSetIndex()
{
    // Insert elements with various BindingValueSet values
    // Query by BindingValueSet
    // Assert correct elements returned
}
```

### Step 5.2: Unit Tests for Element Extraction in Parser

**In**: `tests/FhirAugury.Parsing.Fhir.Tests/`

These may already exist from feature 02 / proposal 08. Verify or add:

```csharp
[Fact]
public void TryParseStructureDefinition_Resource_HasDifferentialElements()
{
    // Parse a Resource SD (e.g., Patient)
    // Assert DifferentialElements is not empty
    // Assert FieldOrder is sequential (0, 1, 2, ...)
    // Assert first element path starts with resource name (e.g., "Patient")
}

[Fact]
public void TryParseStructureDefinition_Extension_HasValueElement()
{
    // Parse an Extension SD
    // Assert DifferentialElements contains Extension.value[x] or Extension.url
}

[Fact]
public void ElementInfo_MultiType_TypesSemicolonJoined()
{
    // Parse an SD with a value[x] element
    // Assert Types list has multiple ElementTypeInfo entries
}

[Fact]
public void ElementInfo_Binding_Extracted()
{
    // Parse an SD with bound elements
    // Assert BindingStrength and BindingValueSet are populated
}

[Fact]
public void ElementInfo_EmptyDifferential_ReturnsEmptyList()
{
    // Parse an SD with no <differential> section
    // Assert DifferentialElements is empty list, not null
}

[Fact]
public void ElementInfo_Cardinality_Extracted()
{
    // Parse an SD with explicit cardinality
    // Assert MinCardinality and MaxCardinality are populated
}

[Fact]
public void ElementInfo_IsModifier_BoolToInt()
{
    // Parse an SD with isModifier=true elements
    // Assert IsModifier converts correctly
}
```

### Step 5.3: Integration Tests for StructureDefinitionIndexer

**In**: `tests/FhirAugury.Source.GitHub.Tests/`

```csharp
[Fact]
public void IndexStructureDefinitions_InsertsElementsWithSdId()
{
    // Set up in-memory database with schema
    // Create sample SD XML files in a temp directory
    // Call IndexStructureDefinitions
    // Verify:
    //   - GitHubStructureDefinitionRecord exists
    //   - GitHubSdElementRecord entries exist with correct StructureDefinitionId
    //   - Element count matches expected differential element count
    //   - FieldOrder is sequential
}

[Fact]
public void IndexStructureDefinitions_ReIndex_ClearsExistingElements()
{
    // Insert SD + elements for repo
    // Call IndexStructureDefinitions again for same repo
    // Verify no duplicate elements exist
    // Verify element count matches (not doubled)
}

[Fact]
public void IndexStructureDefinitions_ElementDeletedBeforeSd()
{
    // Insert SD + elements
    // Re-index repo
    // Verify elements are deleted before SDs (FK integrity)
}
```

### Step 5.4: BM25 Integration Test

```csharp
[Fact]
public void CollectDocuments_IncludesElementDocuments()
{
    // Set up database with SD + element records
    // Call CollectDocuments
    // Assert documents contain ContentTypes.Element entries
    // Assert SourceId format is "{repoFullName}:{path}"
    // Assert Text contains element name + short + definition
}
```

### Step 5.5: ArtifactFileMapper Test

```csharp
[Fact]
public void ResolveFilePaths_ElementPath_UsesGitHubSdElements()
{
    // Insert SD record with FilePath = "source/patient/structuredefinition-Patient.xml"
    // Insert element record with Path = "Patient.contact.relationship"
    // Call ResolveFilePaths with elementPath = "Patient.contact.relationship"
    // Assert returns "source/patient/structuredefinition-Patient.xml"
}
```

### Step 5.6: Run All Tests

```bash
dotnet test fhir-augury.slnx
```

---

## 8. Verification Criteria

The feature is complete when:

1. **`dotnet build fhir-augury.slnx`** succeeds with no errors
2. **`dotnet test fhir-augury.slnx`** passes all tests (existing + new)
3. **`github_sd_elements`** table is created by `InitializeSchema()` after `github_structure_definitions`
4. **Element records** are inserted for each SD's differential elements during indexing
5. **FK relationship** is maintained: `StructureDefinitionId` references a valid `github_structure_definitions.Id`
6. **Cascading deletes** work: elements are deleted before SDs during repo re-indexing
7. **BM25 index** includes `ContentTypes.Element` documents with element name/short/definition/comment text
8. **ArtifactFileMapper** resolves element paths (e.g., `Patient.contact.relationship`) to source files via `github_sd_elements` JOIN `github_structure_definitions`
9. **Type mapping** correctly joins multiple `ElementTypeInfo` entries into semicolon-separated strings
10. **Bool-to-int mapping** correctly converts `IsModifier` and `IsSummary` from `bool?` to `int?` (0/1/null)
11. **FieldOrder** is sequential starting from 0 for each SD's differential

### Data Verification Queries

After a successful ingestion, these queries should return expected results:

```sql
-- Count elements per repo
SELECT RepoFullName, COUNT(*) as ElementCount
FROM github_sd_elements
GROUP BY RepoFullName;

-- Verify FK integrity (should return 0)
SELECT COUNT(*)
FROM github_sd_elements e
LEFT JOIN github_structure_definitions sd ON e.StructureDefinitionId = sd.Id
WHERE sd.Id IS NULL;

-- Verify FieldOrder is sequential per SD
SELECT StructureDefinitionId, MIN(FieldOrder), MAX(FieldOrder), COUNT(*)
FROM github_sd_elements
GROUP BY StructureDefinitionId
HAVING MIN(FieldOrder) != 0 OR MAX(FieldOrder) != COUNT(*) - 1;
-- (should return 0 rows)

-- Find elements with bindings
SELECT e.Path, e.BindingStrength, e.BindingValueSet
FROM github_sd_elements e
WHERE e.BindingValueSet IS NOT NULL
ORDER BY e.Path;

-- Cross-reference elements to source files
SELECT sd.FilePath, e.Path, e.Short
FROM github_sd_elements e
JOIN github_structure_definitions sd ON e.StructureDefinitionId = sd.Id
WHERE e.Path LIKE 'Patient.%'
ORDER BY e.FieldOrder;
```

---

## 9. Rollback Plan

### To Undo Feature 03 While Keeping Feature 02

1. Delete `src/FhirAugury.Source.GitHub/Database/Records/GitHubSdElementRecord.cs`
2. Revert `GitHubDatabase.cs`:
   - Remove `GitHubSdElementRecord.CreateTable(connection);` from `InitializeSchema()`
   - Remove `DROP TABLE IF EXISTS github_sd_elements;` from `ResetDatabase()`
3. Revert `StructureDefinitionIndexer.cs`:
   - Remove element insertion loop
   - Remove element deletion from cleanup
   - Remove element count logging
4. Revert `ContentTypes.cs`:
   - Remove `Element` constant
5. Revert `GitHubIndexer.cs`:
   - Remove element collection from `CollectDocuments()`
6. Revert `ArtifactFileMapper.cs`:
   - Restore original `elementPath` LIKE-based resolution
7. Delete element-specific test files/methods
8. Run `dotnet build fhir-augury.slnx && dotnet test fhir-augury.slnx`
9. Drop orphaned table: `DROP TABLE IF EXISTS github_sd_elements;`

### To Undo Both Features 02 and 03

Follow the rollback plan from `22-plan-structuredefinition-source-indexing.md`, which includes removing the element table.

**Critical**: Always drop `github_sd_elements` **before** dropping `github_structure_definitions` to maintain referential integrity during manual cleanup.
