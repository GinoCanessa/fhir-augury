using System.Globalization;
using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Queries;

/// <summary>Read-only resolution against indexed context, with no upstream fetch or ingestion.</summary>
public sealed class ZulipReferenceResolver(
    ZulipDatabase db,
    IOptions<ZulipServiceOptions> optsAccessor,
    ILogger<ZulipReferenceResolver> logger)
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public ZulipReferenceResolutionResponse Resolve(string? reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference))
            return Failure(reference, ZulipReferenceLookupOutcome.InvalidReference);

        bool isMessage = ZulipReferenceContract.TryGetMessageId(reference, out int messageId);
        if (!isMessage && !reference.Contains(':', StringComparison.Ordinal))
        {
            string numeric = reference.Trim().TrimStart('+', '-');
            return Failure(reference, numeric.Length > 0 && numeric.All(char.IsAsciiDigit)
                ? ZulipReferenceLookupOutcome.InvalidReference
                : ZulipReferenceLookupOutcome.UnsupportedReference);
        }

        try
        {
            using SqliteConnection connection = db.OpenConnection();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            return ResolveIndexed(connection, transaction, reference, isMessage ? messageId : null, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 3 or 5 or 6 or 8 or 10 or 11 or 13 or 14 or 26)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("Zulip reference source unavailable (SQLite {SqliteErrorCode})", ex.SqliteErrorCode);
            return Failure(reference, ZulipReferenceLookupOutcome.SourceUnavailable);
        }
    }

    internal ZulipReferenceResolutionResponse ResolveIndexed(
        SqliteConnection connection, SqliteTransaction transaction, string reference, int? messageId, CancellationToken ct)
    {
        ThreadLocation? location = null;
        if (messageId is { } id)
        {
            using SqliteCommand cmd = new("""
                SELECT m.StreamId, s.ZulipStreamId, s.Name, m.Topic
                FROM zulip_messages m
                LEFT JOIN zulip_streams s ON s.Id = m.StreamId
                WHERE m.ZulipMessageId = @messageId
                """, connection, transaction);
            cmd.Parameters.AddWithValue("@messageId", id);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Failure(reference, ZulipReferenceLookupOutcome.NotFound);
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
                return Failure(reference, ZulipReferenceLookupOutcome.InvalidSourceContext,
                    ZulipReferenceDiagnosticCode.MissingStreamContext);

            location = new ThreadLocation(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
        }
        else
        {
            bool hasDelimiter = false;
            for (int separator = reference.IndexOf(':'); separator >= 0; separator = reference.IndexOf(':', separator + 1))
            {
                ct.ThrowIfCancellationRequested();
                string streamName = reference[..separator];
                string topic = reference[(separator + 1)..];
                if (string.IsNullOrWhiteSpace(streamName) || string.IsNullOrWhiteSpace(topic))
                    continue;
                hasDelimiter = true;

                using SqliteCommand cmd = new("""
                    SELECT s.Id, s.ZulipStreamId, s.Name
                    FROM zulip_streams s
                    WHERE s.Name = @streamName
                      AND EXISTS (SELECT 1 FROM zulip_messages m WHERE m.StreamId = s.Id AND m.Topic = @topic)
                    LIMIT 2
                    """, connection, transaction);
                cmd.Parameters.AddWithValue("@streamName", streamName);
                cmd.Parameters.AddWithValue("@topic", topic);
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (location is not null)
                        return Failure(reference, ZulipReferenceLookupOutcome.AmbiguousReference);
                    location = new ThreadLocation(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), topic);
                }
            }

            if (location is null)
                return Failure(reference, hasDelimiter
                    ? ZulipReferenceLookupOutcome.NotFound
                    : ZulipReferenceLookupOutcome.InvalidReference);
        }

        ct.ThrowIfCancellationRequested();
        if (location.StreamId <= 0 || string.IsNullOrWhiteSpace(location.StreamName) || string.IsNullOrWhiteSpace(location.Topic))
            return Failure(reference, ZulipReferenceLookupOutcome.InvalidSourceContext,
                ZulipReferenceDiagnosticCode.MissingStreamContext);

        ZulipThreadContext context = ReadThreadContext(
            connection, transaction, location.StreamName, location.Topic, location.LocalStreamId, ct);
        string url = messageId is { } nearId
            ? BuildMessageUrl(optsAccessor.Value, location.StreamName, location.Topic, nearId)
            : BuildThreadUrl(optsAccessor.Value, location.StreamName, location.Topic);
        if (!ZulipReferenceContract.IsSafeUrl(url))
            return Failure(reference, ZulipReferenceLookupOutcome.InvalidSourceContext,
                ZulipReferenceDiagnosticCode.InvalidSourceUrl);

        return new ZulipReferenceResolutionResponse
        {
            Reference = reference,
            Outcome = ZulipReferenceLookupOutcome.Resolved,
            Kind = messageId is null ? ZulipReferenceKind.Thread : ZulipReferenceKind.Message,
            MessageId = messageId,
            StreamId = location.StreamId,
            StreamName = location.StreamName,
            Topic = location.Topic,
            Url = url,
            MessageCount = context.MessageCount,
            FirstMessageAt = context.FirstMessageAt,
            LastMessageAt = context.LastMessageAt,
            FirstMessageExcerpt = context.FirstMessageExcerpt,
            Diagnostics = context.Diagnostics,
        };
    }

    internal static ZulipThreadContext ReadThreadContext(
        SqliteConnection connection, SqliteTransaction transaction, string streamName, string topic,
        int? localStreamId = null, CancellationToken ct = default)
    {
        // The legacy content endpoint groups by its requested name. Certified
        // resolutions instead use the owning local FK, including renamed streams.
        string predicate = localStreamId is null ? "StreamName = @stream" : "StreamId = @stream";
        using SqliteCommand cmd = new($"""
            SELECT Timestamp, ContentPlain FROM zulip_messages
            WHERE {predicate} AND Topic = @topic
            ORDER BY Timestamp ASC
            """, connection, transaction);
        cmd.Parameters.AddWithValue("@stream", (object?)localStreamId ?? streamName);
        cmd.Parameters.AddWithValue("@topic", topic);
        using SqliteDataReader reader = cmd.ExecuteReader();
        int count = 0;
        bool invalidTimestamp = false;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        string? excerpt = null;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            count++;
            DateTimeOffset? timestamp = ParseTimestamp(reader.IsDBNull(0) ? null : reader.GetString(0));
            if (timestamp is null)
                invalidTimestamp = true;
            else
            {
                if (first is null || timestamp < first) first = timestamp;
                if (last is null || timestamp > last) last = timestamp;
            }
            if (excerpt is null && !reader.IsDBNull(1))
                excerpt = TruncateExcerpt(reader.GetString(1), 240);
        }

        // Unknown instants prevent certifying either temporal extremum, not the link.
        return new ZulipThreadContext(count, invalidTimestamp ? null : first, invalidTimestamp ? null : last, excerpt,
            invalidTimestamp ? [ZulipReferenceDiagnosticCode.InvalidTimestamp] : []);
    }

    internal static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp)
                ? timestamp
                : null;

    internal static string BuildThreadUrl(ZulipServiceOptions options, string streamName, string topic) =>
        $"{options.BaseUrl.TrimEnd('/')}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}";

    internal static string BuildMessageUrl(ZulipServiceOptions options, string streamName, string topic, int messageId) =>
        $"{BuildThreadUrl(options, streamName, topic)}/near/{messageId.ToString(CultureInfo.InvariantCulture)}";

    internal static string? TruncateExcerpt(string? source, int maxLen)
    {
        if (string.IsNullOrEmpty(source) || source.Length <= maxLen) return source;
        int cut = source.LastIndexOf(' ', Math.Min(maxLen, source.Length - 1));
        if (cut <= 0) cut = maxLen;
        return source[..cut] + "…";
    }

    private static ZulipReferenceResolutionResponse Failure(
        string? reference, ZulipReferenceLookupOutcome outcome, params ZulipReferenceDiagnosticCode[] diagnostics) =>
        new() { Reference = reference, Outcome = outcome, Diagnostics = diagnostics };

    private sealed record ThreadLocation(int LocalStreamId, int StreamId, string StreamName, string Topic);
}

internal sealed record ZulipThreadContext(
    int MessageCount,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? LastMessageAt,
    string? FirstMessageExcerpt,
    IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics);
