using System.Globalization;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
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
    ZulipTicketIndexer ticketIndexer,
    ZulipIndexer indexer,
    IOptions<ZulipServiceOptions> optionsAccessor)
    : SourceService.SourceServiceBase
{
    private readonly ZulipServiceOptions options = optionsAccessor.Value;
    private static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;

    // ── SourceService RPCs ────────────────────────────────────────

    public override Task<SearchResponse> Search(SearchRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(request.Query);

        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult(new SearchResponse { Query = request.Query });

        int limit = request.Limit > 0 ? Math.Min(request.Limit, 200) : 20;

        string sql = """
            SELECT zm.ZulipMessageId, zm.Topic, zm.StreamName,
                   snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank,
                   zm.SenderName, zm.Timestamp,
                   COALESCE(zs.BaselineValue, 5) as BaselineValue
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH @query
            ORDER BY (zulip_messages_fts.rank * COALESCE(zs.BaselineValue, 5) / 5.0)
            LIMIT @limit OFFSET @offset
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        SearchResponse response = new SearchResponse { Query = request.Query };
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            int msgId = reader.GetInt32(0);
            string topic = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string streamName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            double rawRank = reader.GetDouble(4);
            int baselineValue = reader.GetInt32(7);

            response.Results.Add(new SearchResultItem
            {
                Source = "zulip",
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Score = -rawRank * (baselineValue / 5.0),
                Url = BuildMessageUrl(streamName, topic, msgId),
                UpdatedAt = ParseTimestamp(reader, 6),
            });
        }

        response.TotalResults = response.Results.Count;
        return Task.FromResult(response);
    }

    public override Task<ItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out int msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        ZulipMessageRecord message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        ItemResponse response = new ItemResponse
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
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        string sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, Timestamp FROM zulip_messages ORDER BY Timestamp DESC LIMIT @limit OFFSET @offset";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);

            ItemSummary summary = new ItemSummary
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

    public override async Task<SearchResponse> GetRelated(GetRelatedRequest request, ServerCallContext context)
    {
        if (!string.IsNullOrEmpty(request.SeedSource) && request.SeedSource != "zulip")
            return await GetCrossSourceRelated(request, context);

        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        if (!int.TryParse(request.Id, out int msgId))
            return new SearchResponse();

        // Find messages in the same topic thread
        ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
        if (message is null)
            return new SearchResponse();

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
        cmd.Parameters.AddWithValue("@limit", limit);

        SearchResponse response = new SearchResponse();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int relMsgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);

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
        return response;
    }

    private async Task<SearchResponse> GetCrossSourceRelated(GetRelatedRequest request, ServerCallContext context)
    {
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 50) : 10;

        if (request.SeedSource == "jira")
        {
            using SqliteConnection connection = database.OpenConnection();

            string sql = """
                SELECT tt.StreamName, tt.Topic, tt.ReferenceCount,
                       (SELECT ZulipMessageId FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestMsgId,
                       (SELECT Timestamp FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestTimestamp
                FROM zulip_thread_tickets tt
                WHERE tt.JiraKey = @jiraKey
                ORDER BY tt.LastSeenAt DESC
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@jiraKey", request.SeedId);
            cmd.Parameters.AddWithValue("@limit", limit);

            SearchResponse response = new SearchResponse();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string streamName = reader.GetString(0);
                string topic = reader.GetString(1);
                int latestMsgId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);

                if (latestMsgId == 0)
                    continue;

                response.Results.Add(new SearchResultItem
                {
                    Source = "zulip",
                    Id = latestMsgId.ToString(),
                    Title = $"[{streamName}] {topic}",
                    Score = 1.0,
                    Url = BuildMessageUrl(streamName, topic, latestMsgId),
                    UpdatedAt = ParseTimestamp(reader, 4),
                });
            }

            response.TotalResults = response.Results.Count;
            return response;
        }

        // Unknown seed source: fall back to FTS with SeedId as keyword, reduced score
        SearchResponse ftsResult = await Search(new SearchRequest
        {
            Query = request.SeedId,
            Limit = limit,
        }, context);

        foreach (SearchResultItem item in ftsResult.Results)
            item.Score *= 0.3;

        return ftsResult;
    }

    public override Task<GetItemXRefResponse> GetItemCrossReferences(GetItemXRefRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        GetItemXRefResponse response = new GetItemXRefResponse();
        string direction = request.Direction?.ToLowerInvariant() ?? "both";

        if (request.Source == "zulip" && direction is "outgoing" or "both")
        {
            if (int.TryParse(request.Id, out int msgId))
            {
                ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                string sourceTitle = message is not null ? $"{message.StreamName} > {message.Topic}" : "";
                string sourceUrl = message is not null ? BuildMessageUrl(message.StreamName, message.Topic, msgId) : "";

                foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "zulip", SourceId = request.Id,
                        TargetType = "jira", TargetId = r.JiraKey,
                        LinkType = "mentions", Context = r.Context ?? "",
                        SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                    });
                }

                foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "zulip", SourceId = request.Id,
                        TargetType = "github", TargetId = r.TargetId,
                        LinkType = "mentions", Context = r.Context ?? "",
                        SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                    });
                }

                foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "zulip", SourceId = request.Id,
                        TargetType = "confluence", TargetId = r.TargetId,
                        LinkType = "mentions", Context = r.Context ?? "",
                        SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                    });
                }

                foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: request.Id))
                {
                    response.References.Add(new SourceCrossReference
                    {
                        SourceType = "zulip", SourceId = request.Id,
                        TargetType = "fhir", TargetId = r.TargetId,
                        LinkType = "mentions", Context = r.Context ?? "",
                        SourceTitle = sourceTitle, SourceUrl = sourceUrl,
                    });
                }
            }
        }

        if (request.Source == "jira" && direction is "incoming" or "both")
        {
            List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(connection, JiraKey: request.Id);
            HashSet<string> seen = [];
            foreach (JiraXRefRecord jiraRef in refs)
            {
                if (!seen.Add(jiraRef.SourceId)) continue;
                if (!int.TryParse(jiraRef.SourceId, out int msgId)) continue;

                ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                if (message is null) continue;

                response.References.Add(new SourceCrossReference
                {
                    SourceType = "zulip",
                    SourceId = jiraRef.SourceId,
                    TargetType = "jira",
                    TargetId = request.Id,
                    LinkType = "mentions",
                    Context = jiraRef.Context ?? "",
                    SourceTitle = $"{message.StreamName} > {message.Topic}",
                    SourceUrl = BuildMessageUrl(message.StreamName, message.Topic, msgId),
                });
            }
        }

        return Task.FromResult(response);
    }

    public override Task<SnapshotResponse> GetSnapshot(GetSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out int msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        ZulipMessageRecord message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        string md = BuildThreadMarkdownSnapshot(connection, message.StreamName, message.Topic);

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
        using SqliteConnection connection = database.OpenConnection();

        if (!int.TryParse(request.Id, out int msgId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ID must be a Zulip message ID integer"));

        ZulipMessageRecord message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Message {request.Id} not found"));

        string content = request.Format?.Equals("html", StringComparison.OrdinalIgnoreCase) == true
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
        using SqliteConnection connection = database.OpenConnection();

        string sql = "SELECT ZulipMessageId, StreamName, Topic, SenderName, ContentPlain, Timestamp FROM zulip_messages";
        List<SqliteParameter> parameters = new List<SqliteParameter>();

        if (request.Since is not null)
        {
            sql += " WHERE Timestamp >= @since";
            parameters.Add(new SqliteParameter("@since", request.Since.ToDateTimeOffset().ToString("o")));
        }

        sql += " ORDER BY Timestamp ASC";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string msgId = reader.GetInt32(0).ToString();
            SearchableTextItem item = new SearchableTextItem
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
        if (options.IngestionPaused)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Ingestion is paused"));

        string type = request.Type?.ToLowerInvariant() ?? "incremental";

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
        using SqliteConnection connection = database.OpenConnection();

        int messageCount = ZulipMessageRecord.SelectCount(connection);
        int streamCount = ZulipStreamRecord.SelectCount(connection);
        long dbSize = database.GetDatabaseSizeBytes();
        CacheStats cacheStats = cache.GetStats(ZulipCacheLayout.SourceName);

        StatsResponse response = new StatsResponse
        {
            Source = "zulip",
            TotalItems = messageCount,
            TotalComments = 0,
            DatabaseSizeBytes = dbSize,
            CacheSizeBytes = cacheStats.TotalBytes,
        };

        ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);
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
        using SqliteConnection connection = database.OpenConnection();
        ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

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
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# [{streamName}] > {topic}");
        sb.AppendLine();

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT SenderName, ContentPlain, Timestamp FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic ORDER BY Timestamp ASC",
            connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sender = reader.GetString(0);
            string content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string ts = reader.IsDBNull(2) ? "" : reader.GetString(2);

            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt))
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
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }

    public override Task<PeerIngestionAck> NotifyPeerIngestionComplete(
        PeerIngestionNotification request, ServerCallContext context)
    {
        if (request.Source.Equals("jira", StringComparison.OrdinalIgnoreCase))
        {
            workQueue.Enqueue(ct =>
            {
                ticketIndexer.RebuildFullIndex(ct);
                return Task.CompletedTask;
            }, "rebuild-jira-xrefs");

            return Task.FromResult(new PeerIngestionAck
                { Acknowledged = true, ActionTaken = "queued jira ticket index rebuild" });
        }

        return Task.FromResult(new PeerIngestionAck
            { Acknowledged = true, ActionTaken = "no action needed" });
    }

    public override Task<RebuildIndexResponse> RebuildIndex(
        RebuildIndexRequest request, ServerCallContext context)
    {
        string indexType = request.IndexType?.ToLowerInvariant() ?? "all";

        workQueue.Enqueue(ct =>
        {
            switch (indexType)
            {
                case "bm25":
                    indexer.RebuildFullIndex(ct);
                    break;
                case "cross-refs":
                    ticketIndexer.RebuildFullIndex(ct);
                    break;
                case "fts":
                    database.RebuildFtsIndexes();
                    break;
                case "all":
                    indexer.RebuildFullIndex(ct);
                    ticketIndexer.RebuildFullIndex(ct);
                    database.RebuildFtsIndexes();
                    break;
            }
            return Task.CompletedTask;
        }, $"rebuild-index-{indexType}");

        return Task.FromResult(new RebuildIndexResponse
            { Success = true, ActionTaken = $"queued {indexType} index rebuild" });
    }
}

/// <summary>
/// Implements Zulip-specific gRPC extensions from zulip.proto.
/// </summary>
public class ZulipSpecificGrpcService(
    ZulipDatabase database,
    IOptions<ZulipServiceOptions> optionsAccessor)
    : ZulipService.ZulipServiceBase
{
    private readonly ZulipServiceOptions options = optionsAccessor.Value;

    public override Task<ZulipThread> GetThread(ZulipGetThreadRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 1000) : 200;

        string sql = """
            SELECT ZulipMessageId, StreamId, StreamName, Topic, SenderName, ContentPlain, ContentHtml, Timestamp
            FROM zulip_messages
            WHERE StreamName = @streamName AND Topic = @topic
            ORDER BY Timestamp ASC
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", request.StreamName);
        cmd.Parameters.AddWithValue("@topic", request.Topic);
        cmd.Parameters.AddWithValue("@limit", limit);

        ZulipThread thread = new ZulipThread
        {
            StreamName = request.StreamName,
            Topic = request.Topic,
            Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(request.StreamName)}/topic/{Uri.EscapeDataString(request.Topic)}",
        };

        using SqliteDataReader reader = cmd.ExecuteReader();
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
        using SqliteConnection connection = database.OpenConnection();

        using SqliteCommand cmd = new SqliteCommand("SELECT ZulipStreamId, Name, Description, MessageCount, IncludeStream, BaselineValue FROM zulip_streams ORDER BY Name ASC", connection);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(1);
            await responseStream.WriteAsync(new ZulipStream
            {
                Id = reader.GetInt32(0),
                Name = name,
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MessageCount = reader.GetInt32(3),
                IncludeStream = reader.GetBoolean(4),
                BaselineValue = reader.GetInt32(5),
                Url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(name)}",
            });
        }
    }

    public override Task<ZulipStreamInfo> GetStream(GetStreamRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ZulipStreamRecord stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: request.ZulipStreamId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Stream with Zulip ID {request.ZulipStreamId} not found"));

        return Task.FromResult(MapToStreamInfo(stream));
    }

    public override Task<ZulipStreamInfo> UpdateStream(UpdateStreamRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        ZulipStreamRecord stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: request.ZulipStreamId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Stream with Zulip ID {request.ZulipStreamId} not found"));

        stream.IncludeStream = request.IncludeStream;
        if (request.BaselineValue > 0)
            stream.BaselineValue = Math.Clamp(request.BaselineValue, 0, 10);
        ZulipStreamRecord.Update(connection, stream);

        return Task.FromResult(MapToStreamInfo(stream));
    }

    private static ZulipStreamInfo MapToStreamInfo(ZulipStreamRecord stream)
    {
        return new ZulipStreamInfo
        {
            Id = stream.Id,
            ZulipStreamId = stream.ZulipStreamId,
            Name = stream.Name,
            Description = stream.Description ?? "",
            IsWebPublic = stream.IsWebPublic,
            MessageCount = stream.MessageCount,
            IncludeStream = stream.IncludeStream,
            BaselineValue = stream.BaselineValue,
        };
    }

    public override async Task ListTopics(ZulipListTopicsRequest request, IServerStreamWriter<ZulipTopic> responseStream, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

        string sql = """
            SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMsgAt
            FROM zulip_messages
            WHERE StreamName = @streamName
            GROUP BY Topic
            ORDER BY LastMsgAt DESC
            LIMIT @limit OFFSET @offset
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@streamName", request.StreamName);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", Math.Max(0, request.Offset));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string topic = reader.GetString(0);
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
        using SqliteConnection connection = database.OpenConnection();
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 500) : 50;

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
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int msgId = reader.GetInt32(0);
                string streamName = reader.GetString(1);
                string topic = reader.GetString(2);

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
        using SqliteConnection connection = database.OpenConnection();
        (string? sql, List<SqliteParameter>? parameters) = ZulipQueryBuilder.Build(request);

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader["ZulipMessageId"] is long l ? (int)l : 0;
            string streamName = reader["StreamName"]?.ToString() ?? "";
            string topic = reader["Topic"]?.ToString() ?? "";
            string content = reader["ContentPlain"]?.ToString() ?? "";

            await responseStream.WriteAsync(new ZulipMessageSummary
            {
                Id = msgId,
                StreamName = streamName,
                Topic = topic,
                SenderName = reader["SenderName"]?.ToString() ?? "",
                Snippet = content.Length > 200 ? content[..200] : content,
                Timestamp = reader["Timestamp"] is string tsStr &&
                    DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset ts)
                    ? Timestamp.FromDateTimeOffset(ts)
                    : null,
                Url = BuildMessageUrl(streamName, topic, msgId),
            });
        }
    }

    public override Task<SnapshotResponse> GetThreadSnapshot(ZulipSnapshotRequest request, ServerCallContext context)
    {
        using SqliteConnection connection = database.OpenConnection();

        string md = BuildThreadMarkdownSnapshot(connection, request.StreamName, request.Topic, request.IncludeInternalRefs);

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
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# [{streamName}] > {topic}");
        sb.AppendLine();

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT SenderName, ContentPlain, Timestamp FROM zulip_messages WHERE StreamName = @streamName AND Topic = @topic ORDER BY Timestamp ASC",
            connection);
        cmd.Parameters.AddWithValue("@streamName", streamName);
        cmd.Parameters.AddWithValue("@topic", topic);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sender = reader.GetString(0);
            string content = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string ts = reader.IsDBNull(2) ? "" : reader.GetString(2);

            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt))
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
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? Timestamp.FromDateTimeOffset(dt)
            : null;
    }
}
