# FHIR Augury — Service & API

## Long-Running Service Architecture

The `FhirAugury.Service` project is the heart of the system. It runs as a .NET
hosted service (ASP.NET Minimal API + `BackgroundService`) that manages data
ingestion, indexing, and exposes an HTTP API for control and queries.

## Hosting Model

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: true);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);
builder.Configuration.AddEnvironmentVariables("FHIR_AUGURY_");

// Services
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<ScheduledIngestionService>();
builder.Services.AddScoped<IDataSource, ZulipSource>();
builder.Services.AddScoped<IDataSource, JiraSource>();
builder.Services.AddScoped<IDataSource, ConfluenceSource>();
builder.Services.AddScoped<IDataSource, GitHubSource>();
builder.Services.AddScoped<CrossRefLinker>();
builder.Services.AddScoped<IndexBuilder>();

var app = builder.Build();
app.MapAuguryApi();
app.Run();
```

## Ingestion Queue

Uses `System.Threading.Channels` for a bounded, thread-safe work queue:

```csharp
public class IngestionQueue
{
    private readonly Channel<IngestionRequest> _channel =
        Channel.CreateBounded<IngestionRequest>(
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

    public ValueTask EnqueueAsync(IngestionRequest request, CancellationToken ct)
        => _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<IngestionRequest> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

public record IngestionRequest(
    string SourceName,
    IngestionType Type,           // Full, Incremental, OnDemand
    string? Identifier = null,    // For on-demand: item identifier
    string? Filter = null         // Optional JQL, CQL, etc.
);
```

## Background Workers

### IngestionWorker

Continuously reads from the queue and dispatches to the appropriate source:

```csharp
public class IngestionWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var request in _queue.DequeueAllAsync(ct))
        {
            var source = _sources.First(s => s.SourceName == request.SourceName);
            var result = request.Type switch
            {
                IngestionType.Full => await source.DownloadAllAsync(...),
                IngestionType.Incremental => await source.DownloadIncrementalAsync(...),
                IngestionType.OnDemand => await source.IngestItemAsync(...),
                _ => throw new ArgumentException(...)
            };
            await _indexBuilder.UpdateIndexAsync(request.SourceName, result, ct);
            await _crossRefLinker.LinkNewItemsAsync(result, ct);
            _log.LogIngestionRun(request, result);
        }
    }
}
```

### ScheduledIngestionService

Manages per-source sync schedules. Each source has its own configurable
interval (e.g., hourly for Jira, daily for Confluence). The scheduler tracks
when each source last ran and only enqueues an incremental sync when the
source's interval has elapsed. This avoids a single global poll loop and
ensures high-frequency sources don't block low-frequency ones.

```csharp
public class ScheduledIngestionService : BackgroundService
{
    private readonly record struct ScheduleEntry(
        string SourceName,
        TimeSpan Interval,
        DateTimeOffset NextRunAt);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Build the initial schedule from per-source config
        var schedule = _config.Sources
            .Where(s => s.Enabled && s.SyncSchedule is not null)
            .Select(s => new ScheduleEntry(
                s.Name,
                s.SyncSchedule!.Value,
                DateTimeOffset.UtcNow))   // run immediately on startup
            .ToList();

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            for (int i = 0; i < schedule.Count; i++)
            {
                var entry = schedule[i];
                if (now < entry.NextRunAt)
                    continue;

                // Enqueue an incremental sync for this source
                await _queue.EnqueueAsync(new IngestionRequest(
                    entry.SourceName,
                    IngestionType.Incremental), ct);

                // Advance to next scheduled run
                schedule[i] = entry with
                {
                    NextRunAt = now + entry.Interval
                };
            }

            // Sleep until the next source is due (or 30s max to stay responsive)
            var nextDue = schedule.Min(e => e.NextRunAt);
            var delay = nextDue - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(
                    delay > TimeSpan.FromSeconds(30)
                        ? TimeSpan.FromSeconds(30) : delay,
                    ct);
        }
    }
}
```

## HTTP API Endpoints

```csharp
public static void MapAuguryApi(this WebApplication app)
{
    var api = app.MapGroup("/api/v1");

    // ── Ingestion Control ────────────────────────────────────
    api.MapPost("/ingest/{source}",         IngestEndpoints.TriggerIngestion);
    api.MapPost("/ingest/{source}/item",    IngestEndpoints.SubmitItem);
    api.MapPost("/ingest/sync",             IngestEndpoints.TriggerSyncAll);
    api.MapGet("/ingest/status",            IngestEndpoints.GetStatus);
    api.MapGet("/ingest/history",           IngestEndpoints.GetHistory);
    api.MapGet("/ingest/schedule",          IngestEndpoints.GetSchedule);
    api.MapPut("/ingest/{source}/schedule", IngestEndpoints.UpdateSchedule);

    // ── Search ───────────────────────────────────────────────
    api.MapGet("/search",                   SearchEndpoints.UnifiedSearch);
    api.MapGet("/search/{source}",          SearchEndpoints.SourceSearch);

    // ── Item Retrieval ───────────────────────────────────────
    api.MapGet("/zulip/streams",            ZulipEndpoints.ListStreams);
    api.MapGet("/zulip/messages",           ZulipEndpoints.SearchMessages);
    api.MapGet("/zulip/thread",             ZulipEndpoints.GetThread);

    api.MapGet("/jira/issues",              JiraEndpoints.ListIssues);
    api.MapGet("/jira/issues/{key}",        JiraEndpoints.GetIssue);
    api.MapGet("/jira/issues/{key}/comments", JiraEndpoints.GetComments);

    api.MapGet("/confluence/pages",         ConfluenceEndpoints.ListPages);
    api.MapGet("/confluence/pages/{id}",    ConfluenceEndpoints.GetPage);

    api.MapGet("/github/issues",            GitHubEndpoints.ListIssues);
    api.MapGet("/github/issues/{id}",       GitHubEndpoints.GetIssue);

    // ── Cross-References ─────────────────────────────────────
    api.MapGet("/xref/{source}/{id}",       XRefEndpoints.GetRelated);

    // ── Statistics ───────────────────────────────────────────
    api.MapGet("/stats",                    StatsEndpoints.GetOverview);
    api.MapGet("/stats/{source}",           StatsEndpoints.GetSourceStats);
}
```

### Key Endpoint Details

#### `POST /api/v1/ingest/{source}`

Trigger an ingestion run for a source. Defaults to `incremental` — fetches
all data updated since the source's last successful sync.

```
POST /api/v1/ingest/zulip
POST /api/v1/ingest/zulip?type=incremental
POST /api/v1/ingest/jira?type=full&filter=specification="FHIR Core"
```

**Response:**
```json
{
  "requestId": "abc-123",
  "source": "zulip",
  "type": "incremental",
  "status": "queued",
  "queuePosition": 3,
  "lastSyncAt": "2026-03-18T12:00:00Z"
}
```

#### `POST /api/v1/ingest/sync`

Trigger an incremental sync for **all enabled sources** at once. This is the
primary "update everything" endpoint — a client can call this to ensure all
sources are refreshed with data since their last poll.

```
POST /api/v1/ingest/sync
POST /api/v1/ingest/sync?sources=jira,zulip    # optional: limit to specific sources
```

**Response:**
```json
{
  "requests": [
    { "requestId": "abc-124", "source": "jira",       "lastSyncAt": "2026-03-18T14:00:00Z" },
    { "requestId": "abc-125", "source": "zulip",      "lastSyncAt": "2026-03-18T14:30:00Z" },
    { "requestId": "abc-126", "source": "confluence",  "lastSyncAt": "2026-03-17T08:00:00Z" },
    { "requestId": "abc-127", "source": "github",      "lastSyncAt": "2026-03-18T13:00:00Z" }
  ],
  "status": "queued"
}
```

#### `GET /api/v1/ingest/schedule`

View the current sync schedule for all sources — intervals, next run times,
and last successful sync timestamps.

**Response:**
```json
{
  "sources": [
    {
      "source": "jira",
      "syncInterval": "01:00:00",
      "lastSyncAt": "2026-03-18T14:00:00Z",
      "nextScheduledAt": "2026-03-18T15:00:00Z",
      "itemsSinceLastSync": null
    },
    {
      "source": "confluence",
      "syncInterval": "1.00:00:00",
      "lastSyncAt": "2026-03-17T08:00:00Z",
      "nextScheduledAt": "2026-03-18T08:00:00Z",
      "itemsSinceLastSync": null
    }
  ]
}
```

#### `PUT /api/v1/ingest/{source}/schedule`

Update a source's sync interval at runtime (persisted to config).

```
PUT /api/v1/ingest/jira/schedule
Body: { "syncInterval": "00:30:00" }
```

#### `POST /api/v1/ingest/{source}/item`

Submit a single item for ingestion.

```
POST /api/v1/ingest/jira/item
Body: { "identifier": "FHIR-43499" }

POST /api/v1/ingest/zulip/item
Body: { "identifier": "implementers:FHIRPath questions" }
```

#### `GET /api/v1/search?q={query}`

Unified cross-source full-text search.

```
GET /api/v1/search?q=FHIRPath+normative&sources=zulip,jira&limit=20
```

**Response:**
```json
{
  "query": "FHIRPath normative",
  "totalResults": 47,
  "results": [
    {
      "source": "jira",
      "id": "FHIR-43499",
      "title": "FHIRPath normative readiness review",
      "snippet": "...FHIRPath needs normative ballot...",
      "score": 12.5,
      "url": "https://jira.hl7.org/browse/FHIR-43499",
      "updatedAt": "2026-02-15T10:30:00Z"
    },
    {
      "source": "zulip",
      "id": "msg-987654",
      "title": "implementers > FHIRPath normative discussion",
      "snippet": "...discussing normative readiness...",
      "score": 11.2,
      "url": "https://chat.fhir.org/#narrow/stream/...",
      "updatedAt": "2026-03-01T14:00:00Z"
    }
  ]
}
```

## Configuration

```json
{
  "FhirAugury": {
    "DatabasePath": "fhir-augury.db",
    "Sources": {
      "Zulip": {
        "Enabled": true,
        "SyncSchedule": "04:00:00",
        "BaseUrl": "https://chat.fhir.org",
        "CredentialFile": "~/.zuliprc"
      },
      "Jira": {
        "Enabled": true,
        "SyncSchedule": "01:00:00",
        "BaseUrl": "https://jira.hl7.org",
        "AuthMode": "cookie"
      },
      "Confluence": {
        "Enabled": true,
        "SyncSchedule": "1.00:00:00",
        "BaseUrl": "https://confluence.hl7.org",
        "Spaces": ["FHIR", "FHIRI"],
        "AuthMode": "basic"
      },
      "GitHub": {
        "Enabled": true,
        "SyncSchedule": "02:00:00",
        "Repositories": ["HL7/fhir", "HL7/fhir-ig-publisher"]
      }
    },
    "Bm25": {
      "K1": 1.2,
      "B": 0.75
    },
    "Api": {
      "Port": 5150,
      "AllowedOrigins": ["http://localhost:*"]
    }
  }
}
```

### Configuration Notes

- **`SyncSchedule`** — `TimeSpan`-format string per source. Controls how often
  the scheduler triggers an incremental sync. Set to `null` or omit to disable
  automatic sync (the source can still be synced on-demand via API/CLI).
  Recommended defaults:
  - **Jira:** `01:00:00` (hourly) — issues change frequently during ballot/WGM
  - **Zulip:** `04:00:00` (every 4 hours) — high volume but messages are append-only
  - **Confluence:** `1.00:00:00` (daily) — pages change infrequently
  - **GitHub:** `02:00:00` (every 2 hours) — moderate activity on core repos
- **`Enabled`** — master switch per source. When `false`, the source is
  excluded from scheduled sync and API-triggered sync-all requests.
- Intervals can be changed at runtime via `PUT /api/v1/ingest/{source}/schedule`
  and are persisted back to the config file.
