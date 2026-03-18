using System.Text.Json;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Sources.Zulip;

/// <summary>Maps Zulip API JSON responses to database records.</summary>
public static class ZulipMessageMapper
{
    /// <summary>Maps a Zulip stream JSON element to a ZulipStreamRecord.</summary>
    public static ZulipStreamRecord MapStream(JsonElement streamJson)
    {
        return new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = streamJson.GetProperty("stream_id").GetInt32(),
            Name = streamJson.GetProperty("name").GetString() ?? string.Empty,
            Description = GetStringOrNull(streamJson, "description"),
            IsWebPublic = GetBool(streamJson, "is_web_public"),
            MessageCount = 0,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Maps a Zulip message JSON element to a ZulipMessageRecord.</summary>
    public static ZulipMessageRecord MapMessage(JsonElement messageJson, string streamName, int streamDbId)
    {
        var contentHtml = GetStringOrNull(messageJson, "content");
        var contentPlain = TextSanitizer.StripHtml(contentHtml);

        var timestamp = messageJson.GetProperty("timestamp").GetInt64();
        var messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        string? reactions = null;
        if (messageJson.TryGetProperty("reactions", out var reactionsEl) &&
            reactionsEl.ValueKind == JsonValueKind.Array &&
            reactionsEl.GetArrayLength() > 0)
        {
            reactions = reactionsEl.GetRawText();
        }

        return new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = messageJson.GetProperty("id").GetInt32(),
            StreamId = streamDbId,
            StreamName = streamName,
            Topic = messageJson.GetProperty("subject").GetString() ?? string.Empty,
            SenderId = GetInt(messageJson, "sender_id"),
            SenderName = messageJson.GetProperty("sender_full_name").GetString() ?? "Unknown",
            SenderEmail = GetStringOrNull(messageJson, "sender_email"),
            ContentHtml = contentHtml,
            ContentPlain = contentPlain,
            Timestamp = messageTimestamp,
            CreatedAt = messageTimestamp,
            Reactions = reactions,
        };
    }

    private static string? GetStringOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.GetString();
    }

    private static bool GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
            return false;
        return prop.ValueKind == JsonValueKind.True;
    }

    private static int GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
            return 0;
        return prop.GetInt32();
    }
}
