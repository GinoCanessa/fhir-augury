using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Service.Api;

/// <summary>Zulip-specific data access endpoints.</summary>
public static class ZulipEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var zulip = group.MapGroup("/zulip");

        zulip.MapGet("/streams", ListStreams);
        zulip.MapGet("/messages", SearchMessages);
        zulip.MapGet("/thread", GetThread);
    }

    private static IResult ListStreams(DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var streams = ZulipStreamRecord.SelectList(conn);

        return Results.Ok(streams.Select(s => new
        {
            s.ZulipStreamId,
            s.Name,
            s.Description,
            s.IsWebPublic,
            s.MessageCount,
            LastFetchedAt = s.LastFetchedAt.ToString("o"),
        }));
    }

    private static IResult SearchMessages(
        DatabaseService dbService,
        HttpContext context)
    {
        var query = context.Request.Query["q"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new ProblemResponse("Missing query", "Provide ?q= parameter."));
        }

        var stream = context.Request.Query["stream"].FirstOrDefault();
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 20;

        using var conn = dbService.OpenConnection();
        var results = FtsSearchService.SearchZulipMessages(conn, query, limit, stream);

        return Results.Ok(results);
    }

    private static IResult GetThread(
        DatabaseService dbService,
        HttpContext context)
    {
        var stream = context.Request.Query["stream"].FirstOrDefault();
        var topic = context.Request.Query["topic"].FirstOrDefault();

        if (string.IsNullOrEmpty(stream) || string.IsNullOrEmpty(topic))
        {
            return Results.BadRequest(new ProblemResponse("Missing parameters", "Provide ?stream= and ?topic= parameters."));
        }

        using var conn = dbService.OpenConnection();
        var messages = ZulipMessageRecord.SelectList(conn, StreamName: stream, Topic: topic);

        if (messages.Count == 0)
        {
            return Results.NotFound(new ProblemResponse("Not found", $"No messages for '{stream} > {topic}'."));
        }

        return Results.Ok(new
        {
            Stream = stream,
            Topic = topic,
            MessageCount = messages.Count,
            Messages = messages
                .OrderBy(m => m.Timestamp)
                .Select(m => new
                {
                    m.ZulipMessageId,
                    m.SenderName,
                    m.ContentPlain,
                    Timestamp = m.Timestamp.ToString("o"),
                    m.Reactions,
                }),
        });
    }
}
