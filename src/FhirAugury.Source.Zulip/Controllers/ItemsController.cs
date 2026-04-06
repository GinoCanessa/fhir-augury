using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class ItemsController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("items/{id}")]
    public IActionResult GetItem([FromRoute] string id, [FromQuery] bool? includeContent)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (!int.TryParse(id, out int msgId))
            return BadRequest(new { error = "ID must be a Zulip message ID integer" });

        using SqliteConnection connection = db.OpenConnection();
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return NotFound(new { error = $"Message {id} not found" });

        Dictionary<string, string> metadata = new()
        {
            ["stream_name"] = message.StreamName,
            ["topic"] = message.Topic,
            ["sender_name"] = message.SenderName,
            ["sender_id"] = message.SenderId.ToString(),
        };
        if (message.SenderEmail is not null) metadata["sender_email"] = message.SenderEmail;

        return Ok(new ItemResponse
        {
            Source = SourceSystems.Zulip,
            Id = message.ZulipMessageId.ToString(),
            Title = $"[{message.StreamName}] {message.Topic}",
            Content = (includeContent ?? false) ? (message.ContentPlain ?? "") : null,
            Url = ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, message.ZulipMessageId),
            CreatedAt = message.Timestamp,
            UpdatedAt = message.Timestamp,
            Metadata = metadata,
        });
    }

    [HttpGet("items")]
    public IActionResult ListItems([FromQuery] int? limit, [FromQuery] int? offset)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp FROM zulip_messages ORDER BY Timestamp DESC LIMIT @limit OFFSET @offset";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<ItemSummary> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);
            items.Add(new ItemSummary
            {
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, msgId),
                UpdatedAt = ZulipUrlHelper.ParseTimestamp(reader, 4),
                Metadata = new Dictionary<string, string>
                {
                    ["sender_name"] = reader.GetString(3),
                    ["stream_name"] = streamName,
                    ["topic"] = topic,
                },
            });
        }

        return Ok(new ItemListResponse(items.Count, items));
    }

    [HttpGet("items/{id}/related")]
    public IActionResult GetRelatedItems([FromRoute] string id, [FromQuery] int? limit, [FromQuery] string? seedSource, [FromQuery] string? seedId)
    {
        ZulipServiceOptions options = optsAccessor.Value;

        // Cross-source related: use seed source/id if provided
        if (!string.IsNullOrEmpty(seedSource) && !string.Equals(seedSource, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
        {
            SearchResponse response = ZulipUrlHelper.GetCrossSourceRelated(seedSource, seedId ?? id, limit, db, options);
            return Ok(response);
        }

        if (!int.TryParse(id, out int msgId))
            return Ok(new SearchResponse("", 0, [], null));

        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 10, 50);

        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return Ok(new SearchResponse("", 0, [], null));

        string sql = """
            SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp, substr(ContentPlain, 1, 200)
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic AND ZulipMessageId != @msgId
            ORDER BY Timestamp DESC
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", message.StreamName);
        cmd.Parameters.AddWithValue("@topic", message.Topic);
        cmd.Parameters.AddWithValue("@msgId", msgId);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        List<SearchResult> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int relMsgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);
            results.Add(new SearchResult
            {
                Source = SourceSystems.Zulip,
                Id = relMsgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = reader.IsDBNull(5) ? null : reader.GetString(5),
                Score = 0,
                Url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, relMsgId),
                UpdatedAt = ZulipUrlHelper.ParseTimestamp(reader, 4),
            });
        }

        return Ok(new SearchResponse("", results.Count, results, null));
    }

    [HttpGet("items/{id}/snapshot")]
    public IActionResult GetItemSnapshot([FromRoute] string id)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (!int.TryParse(id, out int msgId))
            return BadRequest(new { error = "ID must be a Zulip message ID integer" });

        using SqliteConnection connection = db.OpenConnection();
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return NotFound(new { error = $"Message {id} not found" });

        string md = ZulipUrlHelper.BuildThreadMarkdownSnapshot(connection, message.StreamName, message.Topic);

        return Ok(new SnapshotResponse(
            id,
            SourceSystems.Zulip,
            md,
            ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId),
            null));
    }

    [HttpGet("items/{id}/content")]
    public IActionResult GetItemContent([FromRoute] string id, [FromQuery] string? format)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (!int.TryParse(id, out int msgId))
            return BadRequest(new { error = "ID must be a Zulip message ID integer" });

        using SqliteConnection connection = db.OpenConnection();
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return NotFound(new { error = $"Message {id} not found" });

        string content = format?.Equals("html", StringComparison.OrdinalIgnoreCase) == true
            ? (message.ContentHtml ?? "")
            : (message.ContentPlain ?? "");

        return Ok(new ContentResponse(
            id,
            SourceSystems.Zulip,
            content,
            string.IsNullOrEmpty(format) ? "text" : format,
            ZulipUrlHelper.BuildMessageUrl(options, message.StreamName, message.Topic, msgId),
            null, null));
    }
}