using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Service.Workers;
using Microsoft.Extensions.Options;

namespace FhirAugury.Service.Api;

/// <summary>Ingestion control endpoints.</summary>
public static class IngestEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var ingest = group.MapGroup("/ingest");

        ingest.MapPost("/{source}", TriggerIngestion);
        ingest.MapPost("/{source}/item", SubmitItem);
        ingest.MapPost("/sync", TriggerSyncAll);
        ingest.MapGet("/status", GetStatus);
        ingest.MapGet("/history", GetHistory);
        ingest.MapGet("/schedule", GetSchedule);
        ingest.MapPut("/{source}/schedule", UpdateSchedule);
    }

    private static async Task<IResult> TriggerIngestion(
        string source,
        IngestionQueue queue,
        HttpContext context,
        CancellationToken ct)
    {
        var typeStr = context.Request.Query["type"].FirstOrDefault() ?? "Incremental";
        if (!Enum.TryParse<IngestionType>(typeStr, ignoreCase: true, out var type))
        {
            return Results.BadRequest(new ProblemResponse("Invalid ingestion type", $"Unknown type: {typeStr}"));
        }

        var filter = context.Request.Query["filter"].FirstOrDefault();

        var request = new IngestionRequest
        {
            SourceName = source,
            Type = type,
            Filter = filter,
        };

        await queue.EnqueueAsync(request, ct);

        return Results.Accepted(value: new
        {
            request.RequestId,
            QueuePosition = queue.Count,
            Source = source,
            Type = type.ToString(),
        });
    }

    private static async Task<IResult> SubmitItem(
        string source,
        IngestionQueue queue,
        HttpContext context,
        CancellationToken ct)
    {
        var body = await context.Request.ReadFromJsonAsync<SubmitItemRequest>(ct);
        if (body is null || string.IsNullOrEmpty(body.Identifier))
        {
            return Results.BadRequest(new ProblemResponse("Missing identifier", "Body must include 'identifier' field."));
        }

        var request = new IngestionRequest
        {
            SourceName = source,
            Type = IngestionType.OnDemand,
            Identifier = body.Identifier,
        };

        await queue.EnqueueAsync(request, ct);

        return Results.Accepted(value: new
        {
            request.RequestId,
            QueuePosition = queue.Count,
            Source = source,
            Identifier = body.Identifier,
        });
    }

    private static async Task<IResult> TriggerSyncAll(
        IngestionQueue queue,
        IOptions<AuguryConfiguration> config,
        HttpContext context,
        CancellationToken ct)
    {
        var sourcesParam = context.Request.Query["sources"].FirstOrDefault();
        var cfg = config.Value;

        IEnumerable<string> sourcesToSync;
        if (!string.IsNullOrEmpty(sourcesParam))
        {
            sourcesToSync = sourcesParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else
        {
            sourcesToSync = cfg.Sources.Where(s => s.Value.Enabled).Select(s => s.Key);
        }

        var enqueued = new List<object>();
        foreach (var source in sourcesToSync)
        {
            var request = new IngestionRequest
            {
                SourceName = source,
                Type = IngestionType.Incremental,
            };
            await queue.EnqueueAsync(request, ct);
            enqueued.Add(new { request.RequestId, Source = source });
        }

        return Results.Accepted(value: new { Requests = enqueued });
    }

    private static IResult GetStatus(
        IngestionQueue queue,
        IngestionWorker worker,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var syncStates = SyncStateRecord.SelectList(conn);

        return Results.Ok(new
        {
            QueueDepth = queue.Count,
            ActiveIngestion = worker.ActiveRequest is { } active ? new
            {
                active.RequestId,
                active.SourceName,
                Type = active.Type.ToString(),
            } : null,
            Sources = syncStates.Select(s => new
            {
                s.SourceName,
                s.Status,
                LastSyncAt = s.LastSyncAt.ToString("o"),
                s.ItemsIngested,
                s.LastError,
            }),
        });
    }

    private static IResult GetHistory(
        DatabaseService dbService,
        HttpContext context)
    {
        var sourceFilter = context.Request.Query["source"].FirstOrDefault();
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 20;

        using var conn = dbService.OpenConnection();

        List<IngestionLogRecord> logs;
        if (!string.IsNullOrEmpty(sourceFilter))
        {
            logs = IngestionLogRecord.SelectList(conn, SourceName: sourceFilter);
        }
        else
        {
            logs = IngestionLogRecord.SelectList(conn);
        }

        var result = logs
            .OrderByDescending(l => l.StartedAt)
            .Take(limit)
            .Select(l => new
            {
                l.SourceName,
                l.RunType,
                StartedAt = l.StartedAt.ToString("o"),
                CompletedAt = l.CompletedAt?.ToString("o"),
                l.ItemsProcessed,
                l.ItemsNew,
                l.ItemsUpdated,
                l.ErrorMessage,
            });

        return Results.Ok(result);
    }

    private static IResult GetSchedule(
        ScheduledIngestionService scheduler,
        IOptions<AuguryConfiguration> config)
    {
        var cfg = config.Value;
        var nextRuns = scheduler.NextRunTimes;

        var schedules = cfg.Sources.Select(s => new
        {
            Source = s.Key,
            s.Value.Enabled,
            ConfiguredInterval = s.Value.SyncSchedule?.ToString(),
            NextRun = nextRuns.TryGetValue(s.Key, out var next) ? next.ToString("o") : null,
        });

        return Results.Ok(schedules);
    }

    private static IResult UpdateSchedule(
        string source,
        ScheduledIngestionService scheduler,
        HttpContext context)
    {
        var body = context.Request.ReadFromJsonAsync<UpdateScheduleRequest>().GetAwaiter().GetResult();
        if (body is null || string.IsNullOrEmpty(body.SyncInterval))
        {
            return Results.BadRequest(new ProblemResponse("Missing interval", "Body must include 'syncInterval' (e.g., '00:30:00')."));
        }

        if (!TimeSpan.TryParse(body.SyncInterval, out var interval))
        {
            return Results.BadRequest(new ProblemResponse("Invalid interval", $"Cannot parse '{body.SyncInterval}' as TimeSpan."));
        }

        scheduler.UpdateSchedule(source, interval);

        return Results.Ok(new
        {
            Source = source,
            SyncInterval = interval.ToString(),
            NextRun = scheduler.NextRunTimes.TryGetValue(source, out var next) ? next.ToString("o") : null,
        });
    }

    private record SubmitItemRequest(string Identifier);
    private record UpdateScheduleRequest(string SyncInterval);
}

/// <summary>Standard error response format.</summary>
public record ProblemResponse(string Title, string Detail);
