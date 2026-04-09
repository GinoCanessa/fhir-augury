using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class StreamsController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("streams")]
    public IActionResult ListStreams()
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        List<ZulipStreamRecord> streams = ZulipStreamRecord.SelectList(connection);

        return Ok(new
        {
            total = streams.Count,
            streams = streams.Select(s => new
            {
                s.ZulipStreamId,
                s.Name,
                s.Description,
                s.MessageCount,
                s.IsWebPublic,
                s.IncludeStream,
                s.BaselineValue,
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(s.Name)}",
            }),
        });
    }

    [HttpGet("streams/{zulipStreamId:int}")]
    public IActionResult GetStream([FromRoute] int zulipStreamId)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: zulipStreamId);
        if (stream is null)
            return NotFound(new { error = $"Stream with Zulip ID {zulipStreamId} not found" });

        return Ok(new
        {
            stream.Id,
            stream.ZulipStreamId,
            stream.Name,
            stream.Description,
            stream.MessageCount,
            stream.IsWebPublic,
            stream.IncludeStream,
            stream.BaselineValue,
            url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(stream.Name)}",
        });
    }

    [HttpPut("streams/{zulipStreamId:int}")]
    public IActionResult UpdateStream([FromRoute] int zulipStreamId, [FromBody] ZulipStreamUpdateRequest body)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: zulipStreamId);
        if (stream is null)
            return NotFound(new { error = $"Stream with Zulip ID {zulipStreamId} not found" });

        stream.IncludeStream = body.IncludeStream;
        if (body.BaselineValue.HasValue)
            stream.BaselineValue = Math.Clamp(body.BaselineValue.Value, 0, 10);
        ZulipStreamRecord.Update(connection, stream);

        return Ok(new
        {
            stream.Id,
            stream.ZulipStreamId,
            stream.Name,
            stream.Description,
            stream.MessageCount,
            stream.IsWebPublic,
            stream.IncludeStream,
            stream.BaselineValue,
            url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(stream.Name)}",
        });
    }

    [HttpGet("streams/{streamName}/topics")]
    public IActionResult GetStreamTopics([FromRoute] string streamName, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = """
            SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMsgAt
            FROM zulip_messages
            WHERE StreamName = @streamName
            GROUP BY Topic
            ORDER BY LastMsgAt DESC
            LIMIT @limit OFFSET @offset
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<object> topics = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string topic = reader.GetString(0);
            topics.Add(new
            {
                topic,
                messageCount = reader.GetInt32(1),
                lastMessageAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
            });
        }

        return Ok(new { stream = streamName, total = topics.Count, topics });
    }
}