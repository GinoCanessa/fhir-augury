# Code Review: FhirAugury.Models & FhirAugury.Mcp

**Reviewed:** 2026-03-19
**Projects:** `FhirAugury.Models`, `FhirAugury.Mcp`

---

## FhirAugury.Models

### Clean Files ✅
- `IDataSource.cs` — Well-designed interface, appropriate `CancellationToken` usage
- `IngestedItem.cs` — Good use of `record`, `required`, `IReadOnlyList<string>`
- `IngestionOptions.cs` — Clean options record
- `IngestionType.cs` — Simple enum
- `SearchResult.cs` — Well-structured, proper nullable usage
- `Caching/CacheMode.cs` — Clean
- `Caching/CacheStats.cs` — Clean
- `Caching/CacheMetadata.cs` — Simple DTOs with JSON attributes
- `Caching/IResponseCache.cs` — Well-documented interface
- `Caching/NullResponseCache.cs` — Proper null-object pattern with singleton

---

### HttpRetryHelper.cs

#### [Medium] `new Random()` per call — use `Random.Shared`
**Line 61.** In .NET 6+, `Random.Shared` is thread-safe and produces better distribution.

```csharp
var random = new Random();
```

---

#### [Medium] Unnecessary async wrapper causes extra state machine allocation
**Lines 46-48.**

```csharp
return await ExecuteWithRetryAsync(
    async token => await httpClient.GetAsync(url, token),  // unnecessary wrapper
    ct, maxRetries, sourceName);
```

**Fix:** `token => httpClient.GetAsync(url, token)` — remove the async/await wrapper.

---

#### [Low] HttpResponseMessage leaks on auth failure path
**Lines 87-93.** The `response` is not disposed before throwing.

```csharp
if (AuthFailureCodes.Contains(response.StatusCode))
{
    throw new HttpRequestException(msg, null, response.StatusCode);
    // response never disposed
}
```

**Fix:** `response.Dispose()` before throwing, or use `using`.

---

#### [Low] Doc comment omits 504 from listed status codes
**Line 20.** Says "429, 500, 502, 503" but 504 (GatewayTimeout) is also in the retry set.

---

### IngestionResult.cs

#### [Low] `IngestionError` holds `Exception?` which impacts serialization
**Line 32.** `System.Text.Json` cannot round-trip exceptions, and the full exception graph can be enormous. Consider `[JsonIgnore]` or storing `Exception?.ToString()`.

---

### Caching/CacheConfiguration.cs

#### [Low] No validation on `RootPath`
**Line 7.** Accepts any string including empty/whitespace. An empty path would resolve to the current directory.

---

### Caching/FileSystemResponseCache.cs

#### [High] Path traversal protection bypass — ✅ **FIXED**
**Lines 161-168.** 

**Resolution:** Fixed `ResolvePath` to append directory separator before `StartsWith` check. Also added `ResolveSourcePath` helper and applied traversal protection to `EnumerateKeys` and `Clear` methods which were completely unprotected.

`StartsWith` can be tricked by similar prefixes — `C:\cache` would match `C:\cache2\evil`.

```csharp
if (!resolved.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException(...)
```

**Fix:** Ensure `_rootPath` ends with the directory separator:
```csharp
var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar)
    ? _rootPath : _rootPath + Path.DirectorySeparatorChar;
if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
    && resolved != _rootPath)
```

---

#### [Medium] TOCTOU race: File.Exists → new FileStream
**Lines 19-28.** Between `File.Exists` and `new FileStream`, another process could delete the file.

**Fix:** Use `FileMode.Open` without the `Exists` check and catch `FileNotFoundException`.

---

#### [Medium] Temp-file cleanup in catch block can mask original exception
**Lines 38-52.** `File.Delete(tempPath)` in `catch` could throw and mask the original exception.

**Fix:** Wrap cleanup in a nested `try/catch`:
```csharp
catch
{
    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
    throw;
}
```

---

#### [Low] `TryGet` returns open FileStream that can be leaked
**Lines 19-30.** The `TryGet` pattern pushes disposal responsibility entirely to callers, which is fragile.

---

#### [Low] `ClearAll` has TOCTOU with concurrent modifications
**Lines 89-95.** Entries could be deleted between enumeration and deletion.

---

### Caching/CacheMetadataService.cs

#### [Medium] Synchronous `File.ReadAllText` in otherwise async-capable class
**Line 20.** `ReadMetadata` is synchronous while `WriteMetadataAsync` is async. Inconsistent.

---

#### [Low] Temp file cleanup can mask original exception (same pattern)
**Lines 42-43.** Same issue as `FileSystemResponseCache`.

---

## FhirAugury.Mcp

### Program.cs

#### [High] No validation on `dbPath` before use
**Lines 7-9.**

```csharp
var dbPath = args.Length > 1 && args[0] == "--db"
    ? args[1]
    : Environment.GetEnvironmentVariable("FHIR_AUGURY_DB") ?? "fhir-augury.db";
```

- No check that the file actually exists (read-only mode — missing file = crash)
- No path sanitization
- `args[0] == "--db"` is case-sensitive
- No `--help` or usage info

---

### Tools/ListingTools.cs

#### [Medium] `limit` and `offset` have no upper bound validation
**Lines 21-22.** An LLM could pass `limit = 1000000`, pulling the entire database into a giant markdown string.

**Fix:** `limit = Math.Clamp(limit, 1, 200)`

---

#### [Medium] Raw SQL bypasses record layer abstraction
**Lines 83-93.** `ListZulipTopics` uses hardcoded SQL. If the table schema changes, this breaks silently.

---

#### [Low] Reader assumes `MAX(Timestamp)` is non-null
**Line 97.** If a stream has null timestamps, `GetString(2)` throws `InvalidCastException`.

---

### Tools/SearchTools.cs

#### [Medium] No limit validation (same issue)
**Line 20.** No upper bound on `limit`. LLM could request `limit=999999`.

---

### Tools/RetrievalTools.cs

#### [Medium] `GetZulipThread` returns ALL messages with no limit
**Lines 91-123.** Some topics have thousands of messages. This will produce massive markdown strings that may exceed MCP transport limits.

**Fix:** Add a `limit` parameter with a sensible default.

---

#### [Medium] `GetGithubIssue` eagerly loads ALL comments
**Lines 214-232.** Popular issues can have hundreds of comments. No pagination or limit.

---

### Tools/SnapshotTools.cs

#### [Medium] Snapshot methods build potentially enormous strings
**Lines 14-109.** Loads issue + all comments + all cross-references. No size guard.

---

### FhirAugury.Mcp.csproj

#### [Low] Wildcard package versions
```xml
<PackageReference Include="ModelContextProtocol" Version="1.0.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.*" />
```

Can cause non-reproducible builds.

---

## Summary

| Severity | Count |
|----------|-------|
| **High** | 2 |
| **Medium** | 9 |
| **Low** | 9 |
| **Info** | 0 |
| **Total** | **20** |

### Top Priorities
1. **Fix path traversal in `FileSystemResponseCache.ResolvePath`** — append directory separator before `StartsWith` check
2. **Dispose `HttpResponseMessage` on auth failure** in `HttpRetryHelper`
3. **Cap `limit` parameters** across all MCP tools — prevent unbounded results
4. **Add `limit` to `GetZulipThread` and `GetGithubIssue`** — prevent multi-megabyte MCP responses
5. **Validate DB path exists in MCP Program.cs** — fail fast with clear error
