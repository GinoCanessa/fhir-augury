using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Queries;
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
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        int maxResults = Math.Min(limit ?? 200, 1000);

        string sql = """
            SELECT ZulipMessageId, SenderName, ContentPlain, ContentHtml, Timestamp
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic
            ORDER BY Timestamp ASC
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        List<ZulipThreadMessage> messages = [];
        string? firstContentPlain = null;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                string? contentPlain = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (firstContentPlain is null && contentPlain is not null) firstContentPlain = contentPlain;
                DateTimeOffset? timestamp = ZulipReferenceResolver.ParseTimestamp(reader.IsDBNull(4) ? null : reader.GetString(4));
                messages.Add(new ZulipThreadMessage(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    contentPlain,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    timestamp,
                    timestamp is null ? [ZulipReferenceDiagnosticCode.InvalidTimestamp] : []));
            }
        }

        ZulipThreadContext context = ZulipReferenceResolver.ReadThreadContext(connection, transaction, streamName, topic);

        int? streamId = null;
        using (SqliteCommand streamCmd = new SqliteCommand(
            "SELECT ZulipStreamId FROM zulip_streams WHERE Name = @streamName LIMIT 1",
            connection, transaction))
        {
            streamCmd.Parameters.AddWithValue("@streamName", streamName);
            object? value = streamCmd.ExecuteScalar();
            if (value is not null && value is not DBNull) streamId = Convert.ToInt32(value);
        }

        string? firstMessageExcerpt = ZulipReferenceResolver.TruncateExcerpt(firstContentPlain, 240);

        return Ok(new ZulipThreadResponse(
            streamName,
            streamId,
            topic,
            messages.Count,
            ZulipReferenceResolver.BuildThreadUrl(options, streamName, topic),
            context.MessageCount,
            context.FirstMessageAt,
            context.LastMessageAt,
            firstMessageExcerpt,
            messages,
            context.Diagnostics));
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