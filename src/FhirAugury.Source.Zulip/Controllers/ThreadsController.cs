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
    [HttpGet("threads")]
    public IActionResult GetThread([FromQuery] string? streamName, [FromQuery] string? topic, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(streamName) || string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { error = "streamName and topic query parameters are required" });

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
        string? firstContentPlain = null;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string? contentPlain = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (firstContentPlain is null && contentPlain is not null) firstContentPlain = contentPlain;
                messages.Add(new
                {
                    id = reader.GetInt32(0),
                    sender = reader.GetString(1),
                    content = contentPlain,
                    contentHtml = reader.IsDBNull(3) ? null : reader.GetString(3),
                    timestamp = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        int? messageCount = null;
        string? firstMessageAt = null;
        string? lastMessageAt = null;
        using (SqliteCommand aggCmd = new SqliteCommand(
            "SELECT COUNT(*), MIN(Timestamp), MAX(Timestamp) FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic",
            connection))
        {
            aggCmd.Parameters.AddWithValue("@streamName", streamName);
            aggCmd.Parameters.AddWithValue("@topic", topic);
            using SqliteDataReader reader = aggCmd.ExecuteReader();
            if (reader.Read())
            {
                messageCount = reader.GetInt32(0);
                firstMessageAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                lastMessageAt = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        int? streamId = null;
        using (SqliteCommand streamCmd = new SqliteCommand(
            "SELECT ZulipStreamId FROM zulip_streams WHERE Name = @streamName LIMIT 1",
            connection))
        {
            streamCmd.Parameters.AddWithValue("@streamName", streamName);
            object? value = streamCmd.ExecuteScalar();
            if (value is not null && value is not DBNull) streamId = Convert.ToInt32(value);
        }

        string? firstMessageExcerpt = TruncateExcerpt(firstContentPlain, 240);

        return Ok(new
        {
            stream = streamName,
            streamId,
            topic,
            total = messages.Count,
            url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
            messageCount,
            firstMessageAt,
            lastMessageAt,
            firstMessageExcerpt,
            messages,
        });
    }

    private static string? TruncateExcerpt(string? source, int maxLen)
    {
        if (string.IsNullOrEmpty(source)) return source;
        if (source.Length <= maxLen) return source;
        int cut = source.LastIndexOf(' ', Math.Min(maxLen, source.Length - 1));
        if (cut <= 0) cut = maxLen;
        return source.Substring(0, cut) + "…";
    }

    [HttpGet("threads/snapshot")]
    public IActionResult GetThreadSnapshot([FromQuery] string? streamName, [FromQuery] string? topic)
    {
        if (string.IsNullOrWhiteSpace(streamName) || string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { error = "streamName and topic query parameters are required" });

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