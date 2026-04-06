using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class MessagesController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("messages/{id:int}")]
    public IActionResult GetMessage([FromRoute] int id)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: id);
        if (message is null)
            return NotFound(new { error = $"Message {id} not found" });

        return Ok(new
        {
            message.ZulipMessageId,
            message.StreamName,
            message.Topic,
            message.SenderName,
            message.SenderEmail,
            message.ContentPlain,
            message.ContentHtml,
            message.Timestamp,
            url = ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, message.ZulipMessageId),
        });
    }

    [HttpGet("messages")]
    public IActionResult ListMessages([FromQuery] int? limit, [FromQuery] int? offset)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp FROM zulip_messages ORDER BY Timestamp DESC LIMIT @limit OFFSET @offset";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<object> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);
            items.Add(new
            {
                messageId = msgId,
                title = $"[{streamName}] {topic}",
                sender = reader.IsDBNull(3) ? null : reader.GetString(3),
                timestamp = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return Ok(new { total = items.Count, items });
    }

    [HttpGet("messages/by-user/{user}")]
    public IActionResult GetMessagesByUser([FromRoute] string user, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        // user can be a sender name or a numeric sender ID
        string sql;
        SqliteCommand cmd;

        if (int.TryParse(user, out int senderId))
        {
            sql = """
                SELECT ZulipMessageId, StreamName, Topic, SenderName, substr(ContentPlain, 1, 200), Timestamp
                FROM zulip_messages
                WHERE SenderId = @senderId
                ORDER BY Timestamp DESC
                LIMIT @limit OFFSET @offset
                """;
            cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@senderId", senderId);
        }
        else
        {
            sql = """
                SELECT ZulipMessageId, StreamName, Topic, SenderName, substr(ContentPlain, 1, 200), Timestamp
                FROM zulip_messages
                WHERE SenderName = @senderName
                ORDER BY Timestamp DESC
                LIMIT @limit OFFSET @offset
                """;
            cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@senderName", user);
        }

        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<object> results = [];
        using (cmd)
        {
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int msgId = reader.GetInt32(0);
                string streamName = reader.GetString(1);
                string topic = reader.GetString(2);
                results.Add(new
                {
                    id = msgId,
                    streamName,
                    topic,
                    senderName = reader.GetString(3),
                    snippet = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    timestamp = reader.IsDBNull(5) ? null : reader.GetString(5),
                    url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, msgId),
                });
            }
        }

        return Ok(new { total = results.Count, messages = results });
    }
}