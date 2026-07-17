using System.Text.Json;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Database.Records;
using zulip_cs_lib.Models;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>Maps Zulip API JSON responses and library model types to database records.</summary>
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
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Maps a <see cref="StreamObject"/> to a ZulipStreamRecord.</summary>
    public static ZulipStreamRecord MapStream(StreamObject streamObj)
    {
        return new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = streamObj.StreamId,
            Name = streamObj.Name ?? string.Empty,
            Description = streamObj.Description,
            IsWebPublic = streamObj.IsWebPublic ?? false,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Maps a Zulip message JSON element to a ZulipMessageRecord.</summary>
    public static ZulipMessageRecord MapMessage(JsonElement messageJson, string streamName, int streamDbId)
    {
        string? contentHtml = GetStringOrNull(messageJson, "content");
        string contentPlain = TextSanitizer.StripHtml(contentHtml) ?? string.Empty;

        long timestamp = messageJson.GetProperty("timestamp").GetInt64();
        DateTimeOffset messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        string? reactions = null;
        if (messageJson.TryGetProperty("reactions", out JsonElement reactionsEl) &&
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

    /// <summary>Maps a <see cref="MessageObject"/> to a ZulipMessageRecord.</summary>
    public static ZulipMessageRecord MapMessage(MessageObject msgObj, string streamName, int streamDbId)
    {
        string contentHtml = msgObj.Content ?? string.Empty;
        string contentPlain = TextSanitizer.StripHtml(contentHtml) ?? string.Empty;
        DateTimeOffset messageTimestamp = DateTimeOffset.FromUnixTimeSeconds(msgObj.Timestamp);

        return new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = (int)msgObj.Id,
            StreamId = streamDbId,
            StreamName = streamName,
            Topic = msgObj.Subject ?? string.Empty,
            SenderId = msgObj.SenderId,
            SenderName = msgObj.SenderFullName ?? "Unknown",
            SenderEmail = msgObj.SenderEmail,
            ContentHtml = contentHtml,
            ContentPlain = contentPlain,
            Timestamp = messageTimestamp,
            CreatedAt = messageTimestamp,
            Reactions = null,
        };
    }

    private static string? GetStringOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.GetString();
    }

    private static bool GetBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement prop))
            return false;
        return prop.ValueKind == JsonValueKind.True;
    }

    private static int GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement prop))
            return 0;
        return prop.GetInt32();
    }
}
