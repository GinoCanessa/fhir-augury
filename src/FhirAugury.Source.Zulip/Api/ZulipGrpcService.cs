using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Api;

/// <summary>
/// Implements the SourceService gRPC contract for the Zulip source.
/// </summary>
public class ZulipGrpcService(
    ZulipDatabase database,
    ZulipIngestionPipeline pipeline,
    IResponseCache cache,
    FhirAugury.Common.Ingestion.IngestionWorkQueue workQueue,
    IOptions<ZulipServiceOptions> optionsAccessor)
    : SourceService.SourceServiceBase
{
    private readonly ZulipServiceOptions options = optionsAccessor.Value;
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    // ── SourceService RPCs ────────────────────────────────────────

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        var limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        var sql = """
            SELECT zm.ZulipMessageId, zm.Topic, zm.StreamName,
                   snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank,
                   zm.SenderName, zm.Timestamp
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            WHERE zulip_messages_fts MATCH @query
            ORDER BY zulip_messages_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        var response = new SearchResponse { Query = request.Query };
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var msgId = reader.GetInt32(0);
            var topic = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var streamName = reader.IsDBNull(2) ? "" : reader.GetString(2);

            response.Results.Add(new SearchResultItem
            {
                Source = "zulip",
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Score = -reader.GetDouble(4),
                Url = BuildMessageUrl(streamName, topic, msgId),
                UpdatedAt = ParseTimestamp(reader, 6),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out var msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        var message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        var response = new ItemResponse
        {
            Source = "zulip",
            Id = message.ZulipMessageId.ToString(),
            Title = $"[{message.StreamName}] {message.Topic}",
            Content = request.IncludeContent ? (message.ContentPlain ?? "") : "",
            Url = BuildMessageUrl(message.StreamName, message.Topic, message.ZulipMessageId),
            CreatedAt = Timestamp.FromDateTimeOffset(message.Timestamp),
            UpdatedAt = Timestamp.FromDateTimeOffset(message.Timestamp),
        };

        response.Metadata.Add("stream_name", message.StreamName);
        response.Metadata.Add("topic", message.Topic);
        response.Metadata.Add("sender_name", message.SenderName);
        response.Metadata.Add("sender_id", message.SenderId.ToString());
        if (message.SenderEmail is not null) response.Metadata.Add("sender_email", message.SenderEmail);

        return Task.FromResult(response);
    }

    public override async Task ListItems(ListItemsRequest request, IServerStreamWriter<ItemSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        var sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp FROM zulip_messages ORDER BY Timestamp DESC LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var msgId = reader.GetInt32(0);
            var streamName = reader.GetString(1);
            var topic = reader.GetString(2);

            var summary = new ItemSummary
            {
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Url = BuildMessageUrl(streamName, topic, msgId),
                UpdatedAt = ParseTimestamp(reader, 4),
            };
            summary.Metadata.Add("sender_name", reader.GetString(3));
            summary.Metadata.Add("stream_name", streamName);
            summary.Metadata.Add("topic", topic);

            await responseStream.WriteAsync(summary);
        }
    }

    public override Task<SearchResponse> GetRelated(GetRelatedRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        if (!int.TryParse(request.Id, out var msgId))
            return Task.FromResult(new SearchResponse());

        // Find messages in the same topic thread
        var message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return Task.FromResult(new SearchResponse());

        var sql = """
            SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp, substr(ContentPlain, 1, 200)
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic AND ZulipMessageId != @msgId
            ORDER BY Timestamp DESC
            LIMIT @limit
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", message.StreamName);
        cmd.Parameters.AddWithValue("@topic", message.Topic);
        cmd.Parameters.AddWithValue("@msgId", msgId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var response = new SearchResponse();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var relMsgId = reader.GetInt32(0);
            var streamName = reader.GetString(1);
            var topic = reader.GetString(2);

            response.Results.Add(new SearchResultItem
            {
                Source = "zulip",
                Id = relMsgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Url = BuildMessageUrl(streamName, topic, relMsgId),
                UpdatedAt = ParseTimestamp(reader, 4),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out var msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        var message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        var md = BuildThreadMarkdownSnapshot(connection, message.StreamName, message.Topic);

        return Task.FromResult(new SnapshotResponse
        {
            Id = request.Id,
            Source = "zulip",
            Markdown = md,
            Url = BuildMessageUrl(message.StreamName, message.Topic, msgId),
        });
    }

    public override Task<ContentResponse> GetContent(GetContentRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out var msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        var message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        var content = request.Format?.Equals("html", StringComparison.OrdinalIgnoreCase) == true
            ? (message.ContentHtml ?? "")
            : (message.ContentPlain ?? "");

        return Task.FromResult(new ContentResponse
        {
            Id = request.Id,
            Source = "zulip",
            Content = content,
            Format = string.IsNullOrEmpty(request.Format) ? "text" : request.Format,
            Url = BuildMessageUrl(message.StreamName, message.Topic, msgId),
        });
    }

    public override async Task StreamSearchableText(StreamTextRequest request, IServerStreamWriter<SearchableTextItem> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, ContentPlain, Timestamp FROM zulip_messages";
        var parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE Timestamp >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY Timestamp ASC";

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var msgId = reader.GetInt32(0).ToString();
            var item = new SearchableTextItem
            {
                Source = "zulip",
                Id = msgId,
                Title = $"[{reader.GetString(1)}] {reader.GetString(2)}",
                UpdatedAt = ParseTimestamp(reader, 5),
            };

            // Add text fields: stream, topic, sender, content
            for (int i = 1; i <= 4; i++)
            {
                if (!reader.IsDBNull(i))
                    item.TextFields.Add(reader.GetString(i));
            }

            await responseStream.WriteAsync(item);
        }
    }

    public override async Task<IngestionStatusResponse> TriggerIngestion(TriggerIngestionRequest request, ServerCallContext context)
    {
        var type = request.Type?.ToLowerInvariant() ?? "incremental";

        workQueue.Enqueue(ct => type switch
        {
            "full" => pipeline.RunFullIngestionAsync(ct),
            _ => pipeline.RunIncrementalIngestionAsync(ct),
        }, $"zulip-{type}");

        await Task.Delay(100, context.CancellationToken);
        return GetCurrentStatus();
    }

    public override Task<IngestionStatusResponse> GetIngestionStatus(IngestionStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(GetCurrentStatus());
    }

    public override async Task<RebuildResponse> RebuildFromCache(RebuildRequest request, ServerCallContext context)
    {
        return await FhirAugury.Common.Grpc.SourceServiceLifecycle.RebuildFromCacheAsync(
            async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
            context.CancellationToken);
    }

    public override Task<StatsResponse> GetStats(StatsRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var messageCount = ZulipMessageRecord.SelectCount(connection);
        var streamCount = ZulipStreamRecord.SelectCount(connection);
        var dbSize = database.GetDatabaseSizeBytes();
        var cacheStats = cache.GetStats(ZulipCacheLayout.SourceName);

        var response = new StatsResponse
        {
            Source = "zulip",
            TotalItems = messageCount,
            TotalComments = 0,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        var syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);
        if (syncState is not null)
            response.LastSyncAt = Timestamp.FromDateTimeOffset(syncState.LastSyncAt);

        response.AdditionalCounts.Add("streams", streamCount);

        return Task.FromResult(response);
    }

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        return Task.FromResult(FhirAugury.Common.Grpc.SourceServiceLifecycle.BuildHealthCheck(database, pipeline));
    }

    // ── Helpers ──────────────────────────────────────────────────

    private IngestionStatusResponse GetCurrentStatus()
    {
        using var connection = database.OpenConnection();
        var syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

        return new IngestionStatusResponse
        {
            Source = "zulip",
            Status = pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
            LastSyncAt = syncState is not null ? Timestamp.FromDateTimeOffset(syncState.LastSyncAt) : null,
            ItemsTotal = syncState?.ItemsIngested ?? 0,
            LastError = syncState?.LastError ?? "",
            SyncSchedule = options.SyncSchedule,
        };
    }

    private string BuildMessageUrl(string streamName, string topic, int messageId) =>
        $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}/near/{messageId}";

    private static string BuildThreadMarkdownSnapshot(SqliteConnection connection, string streamName, string topic)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# [{streamName}] > {topic}");
        sb.AppendLine();

        using var cmd = new SqliteCommand(
            "SELECT SenderName, ContentPlain, Timestamp FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic ORDER BY Timestamp ASC",
            connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sender = reader.GetString(0);
            var content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var ts = reader.IsDBNull(2) ? "" : reader.GetString(2);

            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                sb.AppendLine($"### {sender} ({dt:yyyy-MM-dd HH:mm})");
            else
                sb.AppendLine($"### {sender}");

            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}

/// <summary>
/// Implements Zulip-specific gRPC extensions from zulip.proto.
/// </summary>
public class ZulipSpecificGrpcService(
    ZulipDatabase database,
    ZulipServiceOptions options)
    : ZulipService.ZulipServiceBase
{
    public override Task<ZulipThread> GetThread(ZulipGetThreadRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 1000) : 200;

        var sql = """
            SELECT ZulipMessageId, StreamId, StreamName, Topic, SenderName, ContentPlain, ContentHtml, Timestamp
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic
            ORDER BY Timestamp ASC
            LIMIT @limit
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", request.StreamName);
        cmd.Parameters.AddWithValue("@topic", request.Topic);
        cmd.Parameters.AddWithValue("@limit", limit);

        var thread = new ZulipThread
        {
            StreamName = request.StreamName,
            Topic = request.Topic,
            Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(request.StreamName)}/topic/{Uri.EscapeDataString(request.Topic)}",
        };

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            thread.Messages.Add(new ZulipMessage
            {
                Id = reader.GetInt32(0),
                StreamId = reader.GetInt32(1),
                StreamName = reader.GetString(2),
                Topic = reader.GetString(3),
                SenderName = reader.GetString(4),
                Content = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ContentHtml = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Timestamp = ParseTimestamp(reader, 7),
                Url = BuildMessageUrl(reader.GetString(2), reader.GetString(3), reader.GetInt32(0)),
            });
        }

        return Task.FromResult(thread);
    }

    public override async Task ListStreams(ZulipListStreamsRequest request, IServerStreamWriter<ZulipStream> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        using var cmd = new SqliteCommand("SELECT ZulipStreamId, Name, Description, MessageCount FROM zulip_streams ORDER BY Name ASC", connection);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            await responseStream.WriteAsync(new ZulipStream
            {
                Id = reader.GetInt32(0),
                Name = name,
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MessageCount = reader.GetInt32(3),
                Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(name)}",
            });
        }
    }

    public override async Task ListTopics(ZulipListTopicsRequest request, IServerStreamWriter<ZulipTopic> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        var sql = """
            SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMsgAt
            FROM zulip_messages
            WHERE StreamName = @streamName
            GROUP BY Topic
            ORDER BY LastMsgAt DESC
            LIMIT @limit OFFSET @offset
            """;

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", request.StreamName);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var topic = reader.GetString(0);
            await responseStream.WriteAsync(new ZulipTopic
            {
                StreamName = request.StreamName,
                Topic = topic,
                MessageCount = reader.GetInt32(1),
                LastMessageAt = ParseTimestamp(reader, 2),
                Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(request.StreamName)}/topic/{Uri.EscapeDataString(topic)}",
            });
        }
    }

    public override async Task GetMessagesByUser(ZulipUserMessagesRequest request, IServerStreamWriter<ZulipMessageSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        string sql;
        SqliteCommand cmd;

        if (!string.IsNullOrEmpty(request.SenderName))
        {
            sql = """
                SELECT ZulipMessageId, StreamName, Topic, SenderName, substr(ContentPlain, 1, 200), Timestamp
                FROM zulip_messages
                WHERE SenderName = @senderName
                ORDER BY Timestamp DESC
                LIMIT @limit OFFSET @offset
                """;
            cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@senderName", request.SenderName);
        }
        else if (request.SenderId > 0)
        {
            sql = """
                SELECT ZulipMessageId, StreamName, Topic, SenderName, substr(ContentPlain, 1, 200), Timestamp
                FROM zulip_messages
                WHERE SenderId = @senderId
                ORDER BY Timestamp DESC
                LIMIT @limit OFFSET @offset
                """;
            cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@senderId", request.SenderId);
        }
        else
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Either sender_name or sender_id is required"));
        }

        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using (cmd)
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msgId = reader.GetInt32(0);
                var streamName = reader.GetString(1);
                var topic = reader.GetString(2);

                await responseStream.WriteAsync(new ZulipMessageSummary
                {
                    Id = msgId,
                    StreamName = streamName,
                    Topic = topic,
                    SenderName = reader.GetString(3),
                    Snippet = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Timestamp = ParseTimestamp(reader, 5),
                    Url = BuildMessageUrl(streamName, topic, msgId),
                });
            }
        }
    }

    public override async Task QueryMessages(ZulipQueryRequest request, IServerStreamWriter<ZulipMessageSummary> responseStream, ServerCallContext context)
    {
        using var connection = database.OpenConnection();
        var (sql, parameters) = ZulipQueryBuilder.Build(request);

        using var cmd = new SqliteCommand(sql, connection);
        foreach (var p in parameters) cmd.Parameters.Add(p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var msgId = reader["ZulipMessageId"] is long l ? (int)l : 0;
            var streamName = reader["StreamName"]?.ToString() ?? "";
            var topic = reader["Topic"]?.ToString() ?? "";
            var content = reader["ContentPlain"]?.ToString() ?? "";

            await responseStream.WriteAsync(new ZulipMessageSummary
            {
                Id = msgId,
                StreamName = streamName,
                Topic = topic,
                SenderName = reader["SenderName"]?.ToString() ?? "",
                Snippet = content.Length > 200 ? content[..200] : content,
                Timestamp = reader["Timestamp"] is string tsStr &&
                    DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                    ? Timestamp.FromDateTimeOffset(ts)
                    : null,
                Url = BuildMessageUrl(streamName, topic, msgId),
            });
        }
    }

    public override Task<SnapshotResponse> GetThreadSnapshot(ZulipSnapshotRequest request, ServerCallContext context)
    {
        using var connection = database.OpenConnection();

        var md = BuildThreadMarkdownSnapshot(connection, request.StreamName, request.Topic, request.IncludeInternalRefs);

        return Task.FromResult(new SnapshotResponse
        {
            Id = $"{request.StreamName}:{request.Topic}",
            Source = "zulip",
            Markdown = md,
            Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(request.StreamName)}/topic/{Uri.EscapeDataString(request.Topic)}",
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private string BuildMessageUrl(string streamName, string topic, int messageId) =>
        $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}/near/{messageId}";

    private static string BuildThreadMarkdownSnapshot(SqliteConnection connection, string streamName, string topic, bool includeRefs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# [{streamName}] > {topic}");
        sb.AppendLine();

        using var cmd = new SqliteCommand(
            "SELECT SenderName, ContentPlain, Timestamp FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic ORDER BY Timestamp ASC",
            connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sender = reader.GetString(0);
            var content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var ts = reader.IsDBNull(2) ? "" : reader.GetString(2);

            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                sb.AppendLine($"### {sender} ({dt:yyyy-MM-dd HH:mm})");
            else
                sb.AppendLine($"### {sender}");

            sb.AppendLine();
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static Timestamp? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}
