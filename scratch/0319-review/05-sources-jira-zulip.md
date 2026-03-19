# Code Review: FhirAugury.Sources.Jira & FhirAugury.Sources.Zulip

**Reviewed:** 2026-03-19
**Projects:** `FhirAugury.Sources.Jira`, `FhirAugury.Sources.Zulip`

---

## Critical Findings

### 1. XML External Entity (XXE) Vulnerability
**File:** `JiraXmlParser.cs:9, 29` | **Severity:** Critical | ✅ **FIXED**

**Resolution:** Added `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }` and replaced direct `Serializer.Deserialize(stream)` with `Serializer.Deserialize(XmlReader.Create(stream, settings))`.

`XmlSerializer.Deserialize` with default settings can process DTDs. If Jira XML exports include a malicious DTD, this enables XXE attacks (file disclosure, SSRF, DoS).

```csharp
private static readonly XmlSerializer Serializer = new(typeof(JiraRss));
var rss = (JiraRss?)Serializer.Deserialize(stream);
```

**Fix:**
```csharp
var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
using var reader = XmlReader.Create(stream, settings);
var rss = (JiraRss?)Serializer.Deserialize(reader);
```

---

## High Findings

### 2. Duplicated auth logic — JiraAuthHandler.cs
**Lines 22–44 and 47–68** — Both `ConfigureHttpClient` and `SendAsync` apply auth headers. Credentials applied twice — once via `DefaultRequestHeaders` and once per-request.

**Fix:** Remove auth from one path and rely on the other exclusively.

---

### 3. Duplicated auth logic — ZulipAuthHandler.cs
**Lines 23–34 and 37–46** — Identical duplication issue as Jira.

---

### 4. Massive code duplication between DownloadAllAsync and DownloadIncrementalAsync
- **JiraSource.cs:** Lines 15–144 vs 146–274 (~90% shared)
- **ZulipSource.cs:** Lines 15–194 vs 196–354 (similarly duplicated)

The only difference is the filter construction. Extract a shared `FetchAndProcessAsync()`.

---

### 5. HttpClient created per-call with `new HttpClientHandler()`
**JiraAuthHandler.cs:12, ZulipAuthHandler.cs:13** — If called frequently, causes socket exhaustion. Should be long-lived or created via `IHttpClientFactory`.

---

### 6. Credential file path traversal — ZulipAuthHandler.cs
**Line 51** — No validation that `CredentialFile` is a safe path. A value like `../../etc/passwd` would be read.

**Fix:** Add path canonicalization/validation.

---

## Medium Findings

### 7. Duplicated custom field map
**JiraFieldMapper.cs:9–23 vs JiraXmlParser.cs:11–25** — Identical dictionaries. Must be updated in both places.

**Fix:** Extract to a shared constant.

---

### 8. Duplicated switch-on-property-name (12 cases × 2 files)
**JiraFieldMapper.cs:72–86, JiraXmlParser.cs:88–102** — Same mapping logic duplicated.

**Fix:** Use a delegate map or reflection.

---

### 9. Duplicated ParseDate/ParseNullableDate — 3 copies
- `JiraFieldMapper.cs:219–223`
- `JiraCommentParser.cs:37–38`
- `JiraXmlParser.cs:112–116`

**Fix:** Extract to a shared utility.

---

### 10. JQL injection risk
**JiraSource.cs:153–154** — `baseJql` comes from `ingestionOptions.Filter` which could be user-controlled, interpolated directly into JQL.

---

### 11. Incomplete JSON string escaping
**ZulipSource.cs:667–668**

```csharp
private static string EscapeJsonString(string value) =>
    value.Replace("\\", "\\\\").Replace("\"", "\\\"");
```

Doesn't escape control characters (`\n`, `\r`, `\t`, `\0`).

**Fix:** Use `JsonSerializer.Serialize(value)` for proper escaping.

---

### 12. Silent failure when auth credentials are missing
**JiraAuthHandler.cs:31–43, ZulipAuthHandler.cs:30–34** — Silently sends unauthenticated requests when credentials are null/empty.

**Fix:** Log a warning or throw early.

---

### 13. `IngestItemAsync` uses hardcoded `streamDbId: 0`
**ZulipSource.cs:502** — `StreamId` will always be 0 for single-item ingestion. May break queries joining on `StreamId`.

---

### 14. Stream DB ID may not match actual DB assignment
**ZulipSource.DownloadAllAsync** — Doesn't look up actual DB ID for newly inserted streams before passing to `ProcessMessage`.

---

## Low Findings

| # | Finding | File |
|---|---------|------|
| 15 | `key.Split('-')[0]` edge case for malformed keys | `JiraFieldMapper.cs:34` |
| 16 | `DateTimeOffset.MinValue` sentinel for missing dates | Multiple files |
| 17 | 5/10 minute HTTP timeouts not configurable | `JiraAuthHandler.cs:15`, `ZulipAuthHandler.cs:16` |
| 18 | `JiraCommentParser.ParseJsonComments` is a thin one-line delegator | `JiraCommentParser.cs` |
| 19 | `prop.ToString()` for non-string JSON values returns raw JSON | `JiraFieldMapper.cs:126` |
| 20 | SearchableTextFields may contain null entries | `ZulipSource.cs:614` |

---

## Info Findings

| # | Finding | File |
|---|---------|------|
| 21 | No nullable annotations at project level | Both `.csproj` |
| 22 | Static `GetIndex()` thread-safety unknown | Both projects |
| 23 | XML serialization classes nested inside `JiraXmlParser` (~150 lines) | `JiraXmlParser.cs:118-274` |
| 24 | Floating version references in `.csproj` | Both `.csproj` |

---

## Summary

| Severity | Count |
|----------|-------|
| **Critical** | 1 |
| **High** | 5 |
| **Medium** | 8 |
| **Low** | 6 |
| **Info** | 4 |
| **Total** | **24** |

### Top Priorities
1. **Fix XXE vulnerability** in `JiraXmlParser.cs` — use `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }`
2. **Deduplicate** `DownloadAllAsync`/`DownloadIncrementalAsync` in both sources and auth handler logic
3. **Fix `EscapeJsonString`** in `ZulipSource.cs` to properly handle JSON control characters
4. **Add path validation** to `ZulipAuthHandler` credential file handling
