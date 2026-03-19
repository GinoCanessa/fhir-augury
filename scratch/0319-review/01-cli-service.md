# Code Review: FhirAugury.Cli & FhirAugury.Service

**Reviewed:** 2026-03-19
**Projects:** `FhirAugury.Cli`, `FhirAugury.Service`

---

## Critical Findings

### 1. Blocking async call in HTTP endpoint handler
**File:** `IngestEndpoints.cs:210` | **Severity:** Critical | ✅ **FIXED**

**Resolution:** Changed `UpdateSchedule` method signature to `async Task<IResult>` and replaced `.GetAwaiter().GetResult()` with `await`.

`.GetAwaiter().GetResult()` on an ASP.NET Core request pipeline synchronously blocks a thread pool thread. This can cause thread pool starvation under load and potential deadlocks.

```csharp
var body = context.Request.ReadFromJsonAsync<UpdateScheduleRequest>().GetAwaiter().GetResult();
```

**Fix:** Make the method `async` and `await` the call:
```csharp
private static async Task<IResult> UpdateSchedule(...) {
    var body = await context.Request.ReadFromJsonAsync<UpdateScheduleRequest>(ct);
```

---

### 2. Race condition on `_nextRunTimes` dictionary
**File:** `ScheduledIngestionService.cs:15-16, 31, 57, 172` | **Severity:** Critical | ✅ **FIXED**

**Resolution:** Replaced `Dictionary<string, DateTimeOffset>` with `ConcurrentDictionary<string, DateTimeOffset>`.

`_nextRunTimes` is mutated by `ExecuteAsync` (background thread) and simultaneously by `UpdateSchedule` (HTTP request thread) and read by `GetSchedule`. `Dictionary<K,V>` is **not thread-safe**; concurrent read+write can corrupt the data structure.

```csharp
private readonly Dictionary<string, DateTimeOffset> _nextRunTimes = [];
public IReadOnlyDictionary<string, DateTimeOffset> NextRunTimes => _nextRunTimes;
```

**Fix:** Use `ConcurrentDictionary<string, DateTimeOffset>` or protect access with a lock.

---

### 3. HttpClient not disposed in `ServiceCommand.CreateClient`
**File:** `ServiceCommand.cs:188-193` | **Severity:** Critical

Every command invocation creates a new `HttpClient` that is never disposed. Over repeated calls this leaks socket handles (socket exhaustion). `ServiceClient` doesn't implement `IDisposable`.

```csharp
private static ServiceClient CreateClient(...) {
    var baseUrl = parseResult.GetValue(serviceOption)!;
    var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
    return new ServiceClient(httpClient);
}
```

**Fix:** Make `ServiceClient` implement `IDisposable` and dispose in a `using` block, or use a static/shared `HttpClient`.

---

## High Findings

### 4. HttpClient disposed prematurely via `using` inside `switch`
**File:** `IngestCommand.cs:66-93`, `DownloadCommand.cs:76-105`, `SyncCommand.cs:72-134` | **Severity:** High

The `using var httpClient` is scoped to the `case` block. When execution leaves the `switch`, the `HttpClient` has been disposed, but `dataSource` still holds a reference and tries to use it.

```csharp
case "jira": {
    using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
    dataSource = new JiraSource(jiraOptions, httpClient);
    break;  // httpClient disposed here
}
var result = await dataSource.IngestItemAsync(id, options, ct);  // uses disposed client
```

**Fix:** Move the `using` scope to encompass the actual usage.

---

### 5. URL parameters not encoded in `ServiceClient`
**File:** `ServiceClient.cs:13` | **Severity:** High

`source` path segment and `type` query parameter are injected directly without encoding. The `source` could contain path traversal characters.

```csharp
var url = $"/api/v1/ingest/{source}?type={type}";
```

**Fix:** Use `Uri.EscapeDataString(source)` and `Uri.EscapeDataString(type)`.

---

### 6. No input validation on `source` parameter in API endpoints
**File:** `IngestEndpoints.cs:25-54`, `XRefEndpoints.cs:16-41` | **Severity:** High

Source names from URL route parameters are passed directly to data access methods with no allowlist validation.

**Fix:** Validate `source` against known source names and return 400 for invalid values.

---

### 7. In-memory pagination loads entire dataset first
**File:** `JiraEndpoints.cs:32-60`, `ConfluenceEndpoints.cs:38-70`, `GitHubEndpoints.cs:39-76` | **Severity:** High

All records are loaded into memory, then sorted and paginated in C#. For large datasets, this wastes significant memory and CPU.

```csharp
issues = JiraIssueRecord.SelectList(conn);  // loads ALL records
var paged = issues.OrderByDescending(i => i.UpdatedAt).Skip(offset).Take(limit);
```

**Fix:** Push sort and pagination to SQL via `LIMIT`/`OFFSET` and `ORDER BY`.

---

### 8. No authentication/authorization on any API endpoint
**File:** `Program.cs` (Service), `AuguryApiExtensions.cs` | **Severity:** High

The service exposes ingestion trigger and schedule mutation endpoints with **zero authentication**. Any network-accessible client can trigger full data downloads or modify schedules.

**Fix:** Add at minimum an API key middleware or bearer token authentication for mutating endpoints.

---

### 9. No `limit` bounds checking on API endpoints
**File:** `SearchEndpoints.cs:29`, `JiraEndpoints.cs:24`, etc. | **Severity:** High

User can pass `limit=999999999` to force the server to return an enormous result set, causing OOM or DoS.

```csharp
var limit = int.TryParse(limitStr, out var l) ? l : 20;
```

**Fix:** Clamp `limit` to a sensible max (e.g., `Math.Min(Math.Max(l, 1), 500)`).

---

## Medium Findings

### 10. `ReadFromJsonAsync` can return `null` silently
**File:** `ServiceClient.cs:18, 25, 35, 45, 52, 60, 67, 74, 81` | **Severity:** Medium

If the response body is empty or invalid JSON, `ReadFromJsonAsync<JsonElement>` returns `default(JsonElement)` (Undefined kind), which may propagate unexpected behavior upstream.

---

### 11. Duplicate `JsonSerializerOptions` allocation
**File:** `ServiceClient.cs:9`, `OutputFormatter.cs:9`, `CacheCommand.cs:65` | **Severity:** Medium

Three separate `new JsonSerializerOptions { WriteIndented = true }` instances. Should be a shared static instance.

---

### 12. `switch` case ordering issue
**File:** `SyncCommand.cs:70-135` | **Severity:** Medium

`default` case appears before `confluence` and `github` cases. While C# handles this correctly, it's highly misleading.

**Fix:** Move `default` to the end of the switch.

---

### 13. `quietOption` declared but never used
**File:** `Program.cs:29-32, 37` | **Severity:** Medium

The `--quiet` option is added to the root command but never consumed by any command handler.

---

### 14. Empty catch blocks swallow exceptions
**File:** `ScheduledIngestionService.cs:107-109, 125-128` | **Severity:** Medium

Bare `catch` blocks with no logging. If the database is corrupt or inaccessible, this silently returns null, masking the root cause.

---

### 15. `DatabaseService` not disposed in CLI commands
**File:** `RelatedCommand.cs:32`, `SearchCommand.cs:33`, `SnapshotCommand.cs:29`, etc. | **Severity:** Medium

`DatabaseService` implements `IDisposable` but is never disposed in CLI commands.

---

### 16. CORS allows all origins in production
**File:** `appsettings.json:58` | **Severity:** Medium

```json
"CorsOrigins": [ "*" ]
```

Combined with no authentication, any website can make API requests to the service.

---

### 17. `GetCommand` only supports `jira` and `zulip`, not `confluence`/`github`
**File:** `GetCommand.cs:71-73` | **Severity:** Medium

The error message claims `confluence` and `github` are available, but the switch has no cases for them.

---

### 18. Zulip identifier parsing is fragile
**File:** `GetCommand.cs:52-53`, `SnapshotCommand.cs:56-57` | **Severity:** Medium

Uses first `:` as separator. If a stream name contains `:` (valid), the parsing is incorrect.

---

## Low Findings

| # | Finding | File |
|---|---------|------|
| 19 | `IngestionQueue` configured with `SingleReader = false` but only has one reader | `IngestionQueue.cs:15` |
| 20 | `PageSize` property is Confluence-specific but shared across all sources | `AuguryConfiguration.cs:38` |
| 21 | Missing `CancellationToken` propagation in scheduled service DB calls | `ScheduledIngestionService.cs:117-128` |
| 22 | `ActiveRequest` has no memory barrier for cross-thread visibility | `IngestionWorker.cs:25` |
| 23 | `SourceConfiguration` is a god class mixing all source configs | `AuguryConfiguration.cs:17-47` |

---

## Info Findings

| # | Finding | File |
|---|---------|------|
| 24 | `ProblemResponse` doesn't follow RFC 7807 (use built-in `ProblemDetails`) | `IngestEndpoints.cs:236` |
| 25 | No OpenAPI/Swagger registration | `Program.cs` |
| 26 | Version wildcard `2.0.*` in CLI package reference | `FhirAugury.Cli.csproj:13` |
| 27 | `SyncCommand.UpdateSyncState` duplicated from `IngestionWorker.UpdateSyncState` | `SyncCommand.cs:151-179` |
| 28 | `IndexCommand` methods are synchronous but return `Task.CompletedTask` | `IndexCommand.cs:19-50` |
| 29 | Database initialized multiple times per `rebuildAll` command | `IndexCommand.cs:63, 79, 93` |

---

## Summary

| Severity | Count |
|----------|-------|
| **Critical** | 3 |
| **High** | 6 |
| **Medium** | 9 |
| **Low** | 5 |
| **Info** | 6 |
| **Total** | **29** |

### Top Priorities
1. Fix blocking `.GetAwaiter().GetResult()` and `HttpClient` premature disposal (#1, #4)
2. Make `_nextRunTimes` thread-safe (#2)
3. Add authentication to mutating API endpoints and input validation/limit capping (#6, #8, #9)
