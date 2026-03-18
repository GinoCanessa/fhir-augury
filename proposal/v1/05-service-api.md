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

Periodically enqueues incremental sync requests per source:

```csharp
public class ScheduledIngestionService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var source in _config.Sources.Where(s => s.AutoSync))
            {
                await _queue.EnqueueAsync(new IngestionRequest(
                    source.Name,
                    IngestionType.Incremental), ct);
            }
            await Task.Delay(_config.SyncInterval, ct);
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
    api.MapGet("/ingest/status",            IngestEndpoints.GetStatus);
    api.MapGet("/ingest/history",           IngestEndpoints.GetHistory);

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

Trigger an ingestion run for a source.

```
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
  "queuePosition": 3
}
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
    "SyncIntervalMinutes": 60,
    "Sources": {
      "Zulip": {
        "Enabled": true,
        "AutoSync": true,
        "BaseUrl": "https://chat.fhir.org",
        "CredentialFile": "~/.zuliprc"
      },
      "Jira": {
        "Enabled": true,
        "AutoSync": true,
        "BaseUrl": "https://jira.hl7.org",
        "AuthMode": "cookie"
      },
      "Confluence": {
        "Enabled": true,
        "AutoSync": true,
        "BaseUrl": "https://confluence.hl7.org",
        "Spaces": ["FHIR", "FHIRI"],
        "AuthMode": "basic"
      },
      "GitHub": {
        "Enabled": true,
        "AutoSync": true,
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
