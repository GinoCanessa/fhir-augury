# Phase 4: Service Layer

**Goal:** Long-running background service with ingestion queue, scheduled
sync, and HTTP API.

**Depends on:** Phase 3 (Cross-Referencing & BM25)

---

## 4.1 — Service Project Setup

### Objective

Create the `FhirAugury.Service` project with ASP.NET Minimal API hosting.

### Tasks

#### 4.1.1 Create project

Create `src/FhirAugury.Service/` as an ASP.NET web app.

NuGet references:
```xml
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.*" />
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.*" />
```

Project references: all `FhirAugury.*` libraries.

#### 4.1.2 `Program.cs`

Host builder setup:
- Configuration: `appsettings.json`, `appsettings.local.json`, env vars
  (`FHIR_AUGURY_*`)
- Register `DatabaseService` as singleton
- Register `IngestionQueue` as singleton
- Register `IngestionWorker` as hosted service
- Register `ScheduledIngestionService` as hosted service
- Register all `IDataSource` implementations as scoped
- Register `CrossRefLinker`, `IndexBuilder`, search services as scoped
- Map API endpoints via `app.MapAuguryApi()`

#### 4.1.3 `appsettings.json`

Default configuration per proposal §05 — database path, per-source
settings (enabled, sync schedule, base URL, auth mode), BM25 parameters,
API port and CORS.

#### 4.1.4 `AuguryConfiguration.cs`

Strongly-typed options class that binds to the `FhirAugury` config section:
- `string DatabasePath`
- `Dictionary<string, SourceConfiguration> Sources`
- `Bm25Configuration Bm25`
- `ApiConfiguration Api`

Where `SourceConfiguration` contains:
- `bool Enabled`
- `TimeSpan? SyncSchedule`
- `string BaseUrl`
- `string? AuthMode`
- Source-specific properties

#### 4.1.5 Add to solution file

Update `fhir-augury.slnx`.

### Acceptance Criteria

- [ ] `dotnet run --project src/FhirAugury.Service` starts and listens on configured port
- [ ] Configuration loads from `appsettings.json` and environment variables
- [ ] All services resolve correctly from DI

---

## 4.2 — Ingestion Queue

### Objective

Implement the `System.Threading.Channels`-based ingestion queue.

### Files to Create in `FhirAugury.Service/`

#### 4.2.1 `IngestionQueue.cs`

Bounded channel with capacity 100:
- `EnqueueAsync(IngestionRequest request, CancellationToken ct)`
- `DequeueAllAsync(CancellationToken ct)` → `IAsyncEnumerable<IngestionRequest>`
- `Count` — current queue depth (for status API)

#### 4.2.2 `IngestionRequest.cs` (if not already in Models)

Record: `SourceName`, `IngestionType Type`, `string? Identifier`,
`string? Filter`, `string RequestId` (auto-generated GUID).

### Acceptance Criteria

- [ ] Enqueue/dequeue works correctly
- [ ] Bounded capacity blocks writers when full
- [ ] Multiple concurrent readers can process items

---

## 4.3 — Background Workers

### Objective

Implement the ingestion worker and scheduled sync service.

### Files to Create in `FhirAugury.Service/`

#### 4.3.1 `Workers/IngestionWorker.cs`

`BackgroundService` that continuously reads from the queue:
1. Resolve the appropriate `IDataSource` by `request.SourceName`
2. Read `sync_state` for the source's last sync time
3. Dispatch to `DownloadAllAsync`, `DownloadIncrementalAsync`, or
   `IngestItemAsync` based on `request.Type`
4. After ingestion: update FTS5 (via triggers, automatic), update BM25
   index, run cross-reference linker on new/updated items
5. Log ingestion run to `ingestion_log`
6. Update `sync_state` with new cursor and timestamp
7. Error handling: catch exceptions, log to `ingestion_log`, update
   `sync_state.Status` to "failed" with error message

#### 4.3.2 `Workers/ScheduledIngestionService.cs`

`BackgroundService` with per-source scheduling:
1. On startup, build schedule from config (each source has its own interval)
2. Main loop: check each source, enqueue incremental sync if interval elapsed
3. Sleep until next source is due (max 30s to stay responsive)
4. Support runtime schedule updates (reload from `sync_state` or config)
5. Skip sources with `Enabled = false`
6. Log schedule decisions (next run time, skipped sources)

### Acceptance Criteria

- [ ] Worker processes queued requests sequentially
- [ ] Scheduler enqueues incremental syncs at configured intervals
- [ ] Failed ingestion is logged and doesn't crash the service
- [ ] Schedule can be updated at runtime
- [ ] Graceful shutdown on cancellation

---

## 4.4 — HTTP API Endpoints

### Objective

Implement all REST API endpoints for ingestion control, search, and data access.

### Files to Create in `FhirAugury.Service/`

#### 4.4.1 `Api/AuguryApiExtensions.cs`

Extension method `MapAuguryApi(this WebApplication app)` that registers
all endpoint groups.

#### 4.4.2 `Api/IngestEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `POST /api/v1/ingest/{source}` | `TriggerIngestion` | Enqueue an ingestion (default: incremental). Query params: `type`, `filter`. Response: request ID, queue position. |
| `POST /api/v1/ingest/{source}/item` | `SubmitItem` | Submit single item. Body: `{ "identifier": "..." }`. |
| `POST /api/v1/ingest/sync` | `TriggerSyncAll` | Enqueue incremental sync for all enabled sources. Optional: `?sources=jira,zulip`. |
| `GET /api/v1/ingest/status` | `GetStatus` | Current queue depth, active ingestion, per-source last sync. |
| `GET /api/v1/ingest/history` | `GetHistory` | Recent ingestion log entries. Query: `?source=`, `?limit=`. |
| `GET /api/v1/ingest/schedule` | `GetSchedule` | Per-source sync intervals, next run times. |
| `PUT /api/v1/ingest/{source}/schedule` | `UpdateSchedule` | Update a source's sync interval. Body: `{ "syncInterval": "00:30:00" }`. |

#### 4.4.3 `Api/SearchEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GET /api/v1/search` | `UnifiedSearch` | Cross-source FTS5 search. Query: `?q=`, `?sources=`, `?limit=`. |
| `GET /api/v1/search/{source}` | `SourceSearch` | Single-source search with source-specific filters. |

#### 4.4.4 `Api/JiraEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GET /api/v1/jira/issues` | `ListIssues` | Paginated issue list with filters (work_group, status, etc.). |
| `GET /api/v1/jira/issues/{key}` | `GetIssue` | Full issue details by key. |
| `GET /api/v1/jira/issues/{key}/comments` | `GetComments` | Issue comments. |

#### 4.4.5 `Api/ZulipEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GET /api/v1/zulip/streams` | `ListStreams` | All indexed streams. |
| `GET /api/v1/zulip/messages` | `SearchMessages` | Search messages with optional stream filter. |
| `GET /api/v1/zulip/thread` | `GetThread` | Full topic thread. Query: `?stream=`, `?topic=`. |

#### 4.4.6 `Api/XRefEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GET /api/v1/xref/{source}/{id}` | `GetRelated` | Cross-references and related items for an item. |

#### 4.4.7 `Api/StatsEndpoints.cs`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `GET /api/v1/stats` | `GetOverview` | Database-wide statistics. |
| `GET /api/v1/stats/{source}` | `GetSourceStats` | Per-source statistics. |

### Acceptance Criteria

- [ ] All endpoints return correct HTTP status codes
- [ ] `POST /ingest/{source}` enqueues a request and returns request ID
- [ ] `POST /ingest/sync` enqueues sync for all enabled sources
- [ ] `GET /search?q=test` returns ranked results from available sources
- [ ] `GET /stats` returns correct counts
- [ ] Error responses use consistent format (problem details)

---

## 4.5 — CLI Client Mode

### Objective

Add `--service` flag to CLI commands so they can use the HTTP API instead
of direct database access.

### Files to Create/Update

#### 4.5.1 `ServiceClient.cs` (in `FhirAugury.Cli/`)

HTTP client wrapper for the service API:
- `TriggerIngestionAsync(source, type, filter?)`
- `SubmitItemAsync(source, identifier)`
- `TriggerSyncAllAsync(sources?)`
- `SearchAsync(query, sources?, limit)`
- `GetStatusAsync()`
- `GetScheduleAsync()`
- `UpdateScheduleAsync(source, interval)`

#### 4.5.2 Update CLI commands

When `--service` is provided, use `ServiceClient` instead of direct DB:
- `download` → not applicable (always direct)
- `sync` → `POST /ingest/{source}` or `POST /ingest/sync`
- `ingest` → `POST /ingest/{source}/item`
- `search` → `GET /search`
- `stats` → `GET /stats`
- `service status` → `GET /ingest/status`
- `service schedule` → `GET /ingest/schedule`

#### 4.5.3 `Commands/ServiceCommand.cs`

New command group:
- `fhir-augury service status --service URL` — check service health
- `fhir-augury service trigger --source SOURCE --service URL` — trigger sync
- `fhir-augury service schedule [--source SOURCE] --service URL` — view schedule
- `fhir-augury service schedule --source SOURCE --interval HH:MM:SS --service URL` — update

### Acceptance Criteria

- [ ] CLI works in both direct and client mode
- [ ] `--service` flag routes requests through HTTP API
- [ ] `fhir-augury service status` shows running service info
- [ ] `fhir-augury service schedule` shows per-source schedule

---

## 4.6 — Tests

### New Test Files

#### `tests/FhirAugury.Integration.Tests/`

Create project with xUnit dependencies plus:
```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.*" />
```

- `IngestionQueueTests.cs` — enqueue/dequeue, bounded capacity, concurrent access
- `IngestEndpointTests.cs` — API endpoint tests using `WebApplicationFactory`
- `SearchEndpointTests.cs` — search API with pre-populated test data
- `ScheduledIngestionTests.cs` — scheduler fires at correct intervals (time abstraction)

### Acceptance Criteria

- [ ] All tests pass
- [ ] Integration tests use `WebApplicationFactory` for in-process API testing
- [ ] Queue tests verify thread safety
- [ ] No tests require external services (Jira, Zulip, etc.)
