using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class ThreadsController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("threads/{streamName}/{topic}")]
    public IActionResult GetThread([FromRoute] string streamName, [FromRoute] string topic, [FromQuery] int? limit)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 200, 1000);

        string sql = """
            SELECT ZulipMessageId, SenderName, ContentPlain, ContentHtml, Timestamp
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic
            ORDER BY Timestamp ASC
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        List<object> messages = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(new
            {
                id = reader.GetInt32(0),
                sender = reader.GetString(1),
                content = reader.IsDBNull(2) ? null : reader.GetString(2),
                contentHtml = reader.IsDBNull(3) ? null : reader.GetString(3),
                timestamp = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return Ok(new
        {
            stream = streamName,
            topic,
            total = messages.Count,
            url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
            messages,
        });
    }

    [HttpGet("threads/{streamName}/{topic}/snapshot")]
    public IActionResult GetThreadSnapshot([FromRoute] string streamName, [FromRoute] string topic)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();

        string md = ZulipUrlHelper.BuildThreadMarkdownSnapshot(connection, streamName, topic);

        return Ok(new SnapshotResponse(
            $"{streamName}:{topic}",
            SourceSystems.Zulip,
            md,
            $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
            null));
    }
}