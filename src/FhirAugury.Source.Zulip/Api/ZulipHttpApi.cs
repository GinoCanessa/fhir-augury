using System.Globalization;
using System.Text;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Http;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Api;

/// <summary>HTTP Minimal API endpoints — full source service contract plus Zulip-specific extensions.</summary>
public static class ZulipHttpApi
{
    public static IEndpointRouteBuilder MapZulipHttpApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/v1");

        // ── Search ──────────────────────────────────────────────────
        api.MapGet("/search", (string? q, int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using SqliteConnection connection = db.OpenConnection();
            string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new SearchResponse(q, 0, [], null));

            int maxResults = Math.Min(limit ?? 20, 200);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = """
                SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                       snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       zulip_messages_fts.rank, zm.SenderName, zm.Timestamp,
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
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<SearchResult> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int msgId = reader.GetInt32(0);
                string streamName = reader.GetString(1);
                string topic = reader.GetString(2);
                double rawRank = reader.GetDouble(4);
                int baselineValue = reader.GetInt32(7);
                results.Add(new SearchResult
                {
                    Source = SourceSystems.Zulip,
                    Id = msgId.ToString(),
                    Title = $"[{streamName}] {topic}",
                    Snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Score = -rawRank * (baselineValue / 5.0),
                    Url = BuildMessageUrl(options, streamName, topic, msgId),
                    UpdatedAt = ParseTimestamp(reader, 6),
                });
            }

            return Results.Ok(new SearchResponse(q, results.Count, results, null));
        });

        // ── Get item ────────────────────────────────────────────────
        api.MapGet("/items/{id}", (string id, bool? includeContent, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (!int.TryParse(id, out int msgId))
                return Results.BadRequest(new { error = "ID must be a Zulip message ID integer" });

            using SqliteConnection connection = db.OpenConnection();
            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
            if (message is null)
                return Results.NotFound(new { error = $"Message {id} not found" });

            Dictionary<string, string> metadata = new()
            {
                ["stream_name"] = message.StreamName,
                ["topic"] = message.Topic,
                ["sender_name"] = message.SenderName,
                ["sender_id"] = message.SenderId.ToString(),
            };
            if (message.SenderEmail is not null) metadata["sender_email"] = message.SenderEmail;

            return Results.Ok(new ItemResponse
            {
                Source = SourceSystems.Zulip,
                Id = message.ZulipMessageId.ToString(),
                Title = $"[{message.StreamName}] {message.Topic}",
                Content = (includeContent ?? false) ? (message.ContentPlain ?? "") : null,
                Url = BuildMessageUrl(options, message.StreamName, message.Topic, message.ZulipMessageId),
                CreatedAt = message.Timestamp,
                UpdatedAt = message.Timestamp,
                Metadata = metadata,
            });
        });

        // ── List items ──────────────────────────────────────────────
        api.MapGet("/items", (int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
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
                    Url = BuildMessageUrl(options, streamName, topic, msgId),
                    UpdatedAt = ParseTimestamp(reader, 4),
                    Metadata = new Dictionary<string, string>
                    {
                        ["sender_name"] = reader.GetString(3),
                        ["stream_name"] = streamName,
                        ["topic"] = topic,
                    },
                });
            }

            return Results.Ok(new ItemListResponse(items.Count, items));
        });

        // ── Get related ─────────────────────────────────────────────
        api.MapGet("/items/{id}/related", (string id, int? limit, string? seedSource, string? seedId, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;

            // Cross-source related: use seed source/id if provided
            if (!string.IsNullOrEmpty(seedSource) && !string.Equals(seedSource, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
                return GetCrossSourceRelated(seedSource, seedId ?? id, limit, db, options);

            if (!int.TryParse(id, out int msgId))
                return Results.Ok(new SearchResponse("", 0, [], null));

            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 10, 50);

            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
            if (message is null)
                return Results.Ok(new SearchResponse("", 0, [], null));

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
                    Url = BuildMessageUrl(options, streamName, topic, relMsgId),
                    UpdatedAt = ParseTimestamp(reader, 4),
                });
            }

            return Results.Ok(new SearchResponse("", results.Count, results, null));
        });

        // ── Snapshot ────────────────────────────────────────────────
        api.MapGet("/items/{id}/snapshot", (string id, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (!int.TryParse(id, out int msgId))
                return Results.BadRequest(new { error = "ID must be a Zulip message ID integer" });

            using SqliteConnection connection = db.OpenConnection();
            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
            if (message is null)
                return Results.NotFound(new { error = $"Message {id} not found" });

            string md = BuildThreadMarkdownSnapshot(connection, message.StreamName, message.Topic);

            return Results.Ok(new SnapshotResponse(
                id,
                SourceSystems.Zulip,
                md,
                BuildMessageUrl(options, message.StreamName, message.Topic, msgId),
                null));
        });

        // ── Content ─────────────────────────────────────────────────
        api.MapGet("/items/{id}/content", (string id, string? format, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (!int.TryParse(id, out int msgId))
                return Results.BadRequest(new { error = "ID must be a Zulip message ID integer" });

            using SqliteConnection connection = db.OpenConnection();
            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
            if (message is null)
                return Results.NotFound(new { error = $"Message {id} not found" });

            string content = format?.Equals("html", StringComparison.OrdinalIgnoreCase) == true
                ? (message.ContentHtml ?? "")
                : (message.ContentPlain ?? "");

            return Results.Ok(new ContentResponse(
                id,
                SourceSystems.Zulip,
                content,
                string.IsNullOrEmpty(format) ? "text" : format,
                BuildMessageUrl(options, message.StreamName, message.Topic, msgId),
                null, null));
        });

        // ── Cross-references ────────────────────────────────────────
        api.MapGet("/xref/{id}", (string id, string? source, string? direction, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            List<SourceCrossReference> refs = [];
            string dir = direction?.ToLowerInvariant() ?? "both";
            string src = source ?? SourceSystems.Zulip;

            if (string.Equals(src, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase) && dir is "outgoing" or "both")
            {
                if (int.TryParse(id, out int msgId))
                {
                    ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                    string sourceTitle = message is not null ? $"{message.StreamName} > {message.Topic}" : "";
                    string sourceUrl = message is not null ? BuildMessageUrl(options, message.StreamName, message.Topic, msgId) : "";

                    foreach (JiraXRefRecord r in JiraXRefRecord.SelectList(connection, SourceId: id))
                    {
                        refs.Add(new SourceCrossReference(
                            SourceSystems.Zulip, id,
                            SourceSystems.Jira, r.JiraKey,
                            "mentions", r.Context ?? "",
                            null, sourceTitle, sourceUrl));
                    }

                    foreach (GitHubXRefRecord r in GitHubXRefRecord.SelectList(connection, SourceId: id))
                    {
                        refs.Add(new SourceCrossReference(
                            SourceSystems.Zulip, id,
                            SourceSystems.GitHub, r.TargetId,
                            "mentions", r.Context ?? "",
                            null, sourceTitle, sourceUrl));
                    }

                    foreach (ConfluenceXRefRecord r in ConfluenceXRefRecord.SelectList(connection, SourceId: id))
                    {
                        refs.Add(new SourceCrossReference(
                            SourceSystems.Zulip, id,
                            SourceSystems.Confluence, r.TargetId,
                            "mentions", r.Context ?? "",
                            null, sourceTitle, sourceUrl));
                    }

                    foreach (FhirElementXRefRecord r in FhirElementXRefRecord.SelectList(connection, SourceId: id))
                    {
                        refs.Add(new SourceCrossReference(
                            SourceSystems.Zulip, id,
                            SourceSystems.Fhir, r.TargetId,
                            "mentions", r.Context ?? "",
                            null, sourceTitle, sourceUrl));
                    }
                }
            }

            if (string.Equals(src, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase) && dir is "incoming" or "both")
            {
                List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, JiraKey: id);
                HashSet<string> seen = [];
                foreach (JiraXRefRecord jiraRef in jiraRefs)
                {
                    if (!seen.Add(jiraRef.SourceId)) continue;
                    if (!int.TryParse(jiraRef.SourceId, out int msgId)) continue;

                    ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: msgId);
                    if (message is null) continue;

                    refs.Add(new SourceCrossReference(
                        SourceSystems.Zulip, jiraRef.SourceId,
                        SourceSystems.Jira, id,
                        "mentions", jiraRef.Context ?? "",
                        null,
                        $"{message.StreamName} > {message.Topic}",
                        BuildMessageUrl(options, message.StreamName, message.Topic, msgId)));
                }
            }

            return Results.Ok(new CrossReferenceResponse(src, id, dir, refs));
        });

        // ── Health check ────────────────────────────────────────────
        api.MapGet("/health", (ZulipDatabase db, ZulipIngestionPipeline pipeline) =>
        {
            return Results.Ok(HttpServiceLifecycle.BuildHealthCheck(db, pipeline));
        });

        // ── Notify peer ingestion ───────────────────────────────────
        api.MapPost("/notify-peer", (PeerIngestionNotification notification, IngestionWorkQueue workQueue, ZulipXRefRebuilder xrefRebuilder) =>
        {
            workQueue.Enqueue(ct =>
            {
                xrefRebuilder.RebuildAll(ct);
                return Task.CompletedTask;
            }, "rebuild-xrefs");

            return Results.Ok(new PeerIngestionAck(Acknowledged: true));
        });

        // ── Messages by user ────────────────────────────────────────
        api.MapGet("/messages/by-user/{user}", (string user, int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
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
                        url = BuildMessageUrl(options, streamName, topic, msgId),
                    });
                }
            }

            return Results.Ok(new { total = results.Count, messages = results });
        });

        // ── Flexible query ──────────────────────────────────────────
        api.MapPost("/query", (ZulipQueryRequest request, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            (string sql, List<SqliteParameter> parameters) = ZulipQueryBuilder.Build(request);

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

            List<object> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int msgId = reader["ZulipMessageId"] is long l ? (int)l : 0;
                string streamName = reader["StreamName"]?.ToString() ?? "";
                string topic = reader["Topic"]?.ToString() ?? "";
                string content = reader["ContentPlain"]?.ToString() ?? "";

                results.Add(new
                {
                    id = msgId,
                    streamName,
                    topic,
                    senderName = reader["SenderName"]?.ToString() ?? "",
                    snippet = content.Length > 200 ? content[..200] : content,
                    timestamp = reader["Timestamp"]?.ToString(),
                    url = BuildMessageUrl(options, streamName, topic, msgId),
                });
            }

            return Results.Ok(new { total = results.Count, messages = results });
        });

        // ── Messages (legacy compat) ────────────────────────────────
        api.MapGet("/messages/{id:int}", (int id, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ZulipMessageRecord? message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: id);
            if (message is null)
                return Results.NotFound(new { error = $"Message {id} not found" });

            return Results.Ok(new
            {
                message.ZulipMessageId,
                message.StreamName,
                message.Topic,
                message.SenderName,
                message.SenderEmail,
                message.ContentPlain,
                message.ContentHtml,
                message.Timestamp,
                url = BuildMessageUrl(options, message.StreamName, message.Topic, message.ZulipMessageId),
            });
        });

        api.MapGet("/messages", (int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
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

            return Results.Ok(new { total = items.Count, items });
        });

        // ── Streams ─────────────────────────────────────────────────
        api.MapGet("/streams", (ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            List<ZulipStreamRecord> streams = ZulipStreamRecord.SelectList(connection);

            return Results.Ok(new
            {
                total = streams.Count,
                streams = streams.Select(s => new
                {
                    s.ZulipStreamId,
                    s.Name,
                    s.Description,
                    s.MessageCount,
                    s.IsWebPublic,
                    s.IncludeStream,
                    s.BaselineValue,
                    url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(s.Name)}",
                }),
            });
        });

        api.MapGet("/streams/{zulipStreamId:int}", (int zulipStreamId, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: zulipStreamId);
            if (stream is null)
                return Results.NotFound(new { error = $"Stream with Zulip ID {zulipStreamId} not found" });

            return Results.Ok(new
            {
                stream.Id,
                stream.ZulipStreamId,
                stream.Name,
                stream.Description,
                stream.MessageCount,
                stream.IsWebPublic,
                stream.IncludeStream,
                stream.BaselineValue,
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(stream.Name)}",
            });
        });

        api.MapPut("/streams/{zulipStreamId:int}", (int zulipStreamId, ZulipStreamUpdateRequest body, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: zulipStreamId);
            if (stream is null)
                return Results.NotFound(new { error = $"Stream with Zulip ID {zulipStreamId} not found" });

            stream.IncludeStream = body.IncludeStream;
            if (body.BaselineValue.HasValue)
                stream.BaselineValue = Math.Clamp(body.BaselineValue.Value, 0, 10);
            ZulipStreamRecord.Update(connection, stream);

            return Results.Ok(new
            {
                stream.Id,
                stream.ZulipStreamId,
                stream.Name,
                stream.Description,
                stream.MessageCount,
                stream.IsWebPublic,
                stream.IncludeStream,
                stream.BaselineValue,
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(stream.Name)}",
            });
        });

        // ── Topics ──────────────────────────────────────────────────
        api.MapGet("/streams/{streamName}/topics", (string streamName, int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            int maxResults = Math.Min(limit ?? 50, 500);
            int skip = Math.Max(offset ?? 0, 0);

            string sql = """
                SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMsgAt
                FROM zulip_messages
                WHERE StreamName = @streamName
                GROUP BY Topic
                ORDER BY LastMsgAt DESC
                LIMIT @limit OFFSET @offset
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@streamName", streamName);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            List<object> topics = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string topic = reader.GetString(0);
                topics.Add(new
                {
                    topic,
                    messageCount = reader.GetInt32(1),
                    lastMessageAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                    url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
                });
            }

            return Results.Ok(new { stream = streamName, total = topics.Count, topics });
        });

        // ── Threads ─────────────────────────────────────────────────
        api.MapGet("/threads/{streamName}/{topic}", (string streamName, string topic, int? limit, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
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

            return Results.Ok(new
            {
                stream = streamName,
                topic,
                total = messages.Count,
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
                messages,
            });
        });

        // ── Thread snapshot ─────────────────────────────────────────
        api.MapGet("/threads/{streamName}/{topic}/snapshot", (string streamName, string topic, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();

            string md = BuildThreadMarkdownSnapshot(connection, streamName, topic);

            return Results.Ok(new SnapshotResponse(
                $"{streamName}:{topic}",
                SourceSystems.Zulip,
                md,
                $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}",
                null));
        });

        // ── Ingestion ───────────────────────────────────────────────
        api.MapPost("/ingest", async (HttpRequest req, ZulipIngestionPipeline pipeline, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (options.IngestionPaused)
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

            string type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                IngestionResult result = type == "full"
                    ? await pipeline.RunFullIngestionAsync(ct: req.HttpContext.RequestAborted)
                    : await pipeline.RunIncrementalIngestionAsync(req.HttpContext.RequestAborted);

                return Results.Ok(new
                {
                    result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated, result.ItemsFailed,
                    errors = result.Errors,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPost("/ingest/trigger", (HttpRequest req, IngestionWorkQueue workQueue, ZulipIngestionPipeline pipeline, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            if (options.IngestionPaused)
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

            string type = (req.Query["type"].FirstOrDefault() ?? "incremental").ToLowerInvariant();

            workQueue.Enqueue(ct => type switch
            {
                "full" => pipeline.RunFullIngestionAsync(ct),
                _ => pipeline.RunIncrementalIngestionAsync(ct),
            }, $"zulip-{type}");

            return Results.Accepted(value: new { status = "queued", type });
        });

        // ── Status ──────────────────────────────────────────────────
        api.MapGet("/status", (ZulipIngestionPipeline pipeline, ZulipDatabase db, IIndexTracker indexTracker, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            ZulipServiceOptions options = optsAccessor.Value;
            using SqliteConnection connection = db.OpenConnection();
            ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

            return Results.Ok(new IngestionStatusResponse(
                SourceSystems.Zulip,
                pipeline.IsRunning ? pipeline.CurrentStatus : (syncState?.Status ?? "unknown"),
                syncState?.LastSyncAt,
                syncState?.ItemsIngested ?? 0,
                0,
                syncState?.LastError,
                options.SyncSchedule,
                HttpServiceLifecycle.ToIndexStatuses(indexTracker.GetAllStatuses())));
        });

        // ── Rebuild from cache ──────────────────────────────────────
        api.MapPost("/rebuild", async (ZulipIngestionPipeline pipeline) =>
        {
            RebuildResponse result = await HttpServiceLifecycle.RebuildFromCacheAsync(
                async ct => (await pipeline.RebuildFromCacheAsync(ct)).ItemsProcessed,
                CancellationToken.None);
            return Results.Ok(result);
        });

        // ── Rebuild index ───────────────────────────────────────────
        api.MapPost("/rebuild-index", (
            HttpRequest req,
            IngestionWorkQueue workQueue,
            ZulipDatabase database,
            ZulipIndexer indexer,
            ZulipXRefRebuilder xrefRebuilder,
            IIndexTracker indexTracker) =>
        {
            string indexType = (req.Query["type"].FirstOrDefault() ?? "all").ToLowerInvariant();

            workQueue.Enqueue(ct =>
            {
                switch (indexType)
                {
                    case "bm25":
                        indexTracker.MarkStarted("bm25");
                        try
                        {
                            indexer.RebuildFullIndex(ct);
                            indexTracker.MarkCompleted("bm25");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("bm25", ex.Message);
                            throw;
                        }
                        break;
                    case "cross-refs":
                        indexTracker.MarkStarted("cross-refs");
                        try
                        {
                            xrefRebuilder.RebuildAll(ct);
                            indexTracker.MarkCompleted("cross-refs");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("cross-refs", ex.Message);
                            throw;
                        }
                        break;
                    case "fts":
                        indexTracker.MarkStarted("fts");
                        try
                        {
                            database.RebuildFtsIndexes();
                            indexTracker.MarkCompleted("fts");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("fts", ex.Message);
                            throw;
                        }
                        break;
                    case "all":
                        indexTracker.MarkStarted("bm25");
                        try
                        {
                            indexer.RebuildFullIndex(ct);
                            indexTracker.MarkCompleted("bm25");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("bm25", ex.Message);
                            throw;
                        }
                        indexTracker.MarkStarted("cross-refs");
                        try
                        {
                            xrefRebuilder.RebuildAll(ct);
                            indexTracker.MarkCompleted("cross-refs");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("cross-refs", ex.Message);
                            throw;
                        }
                        indexTracker.MarkStarted("fts");
                        try
                        {
                            database.RebuildFtsIndexes();
                            indexTracker.MarkCompleted("fts");
                        }
                        catch (Exception ex)
                        {
                            indexTracker.MarkFailed("fts", ex.Message);
                            throw;
                        }
                        break;
                    default:
                        return Task.CompletedTask;
                }
                return Task.CompletedTask;
            }, $"rebuild-index-{indexType}");

            return Results.Ok(new RebuildIndexResponse(true, $"queued {indexType} index rebuild", null, null));
        });

        // ── Stats ───────────────────────────────────────────────────
        api.MapGet("/stats", (ZulipDatabase db, IResponseCache cache) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            int messageCount = ZulipMessageRecord.SelectCount(connection);
            int streamCount = ZulipStreamRecord.SelectCount(connection);
            long dbSize = db.GetDatabaseSizeBytes();
            CacheStats cacheStats = cache.GetStats(ZulipCacheLayout.SourceName);

            ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

            return Results.Ok(new StatsResponse
            {
                Source = SourceSystems.Zulip,
                TotalItems = messageCount,
                TotalComments = 0,
                DatabaseSizeBytes = dbSize,
                CacheSizeBytes = cacheStats.TotalBytes,
                CacheFiles = cacheStats.FileCount,
                LastSyncAt = syncState?.LastSyncAt,
                AdditionalCounts = new Dictionary<string, int> { ["streams"] = streamCount },
            });
        });

        return app;
    }

    // ── Private helpers ─────────────────────────────────────────────

    private static IResult GetCrossSourceRelated(string seedSource, string seedId, int? limit, ZulipDatabase db, ZulipServiceOptions options)
    {
        int maxResults = Math.Min(limit ?? 10, 50);

        if (string.Equals(seedSource, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            using SqliteConnection connection = db.OpenConnection();

            string sql = """
                SELECT tt.StreamName, tt.Topic, tt.ReferenceCount,
                       (SELECT ZulipMessageId FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestMsgId,
                       (SELECT Timestamp FROM zulip_messages
                        WHERE StreamName = tt.StreamName AND Topic = tt.Topic
                        ORDER BY Timestamp DESC LIMIT 1) AS LatestTimestamp,
                       COALESCE(zs.BaselineValue, 5) AS BaselineValue
                FROM zulip_thread_tickets tt
                LEFT JOIN zulip_streams zs ON zs.Name = tt.StreamName
                WHERE tt.JiraKey = @jiraKey
                ORDER BY tt.LastSeenAt DESC
                LIMIT @limit
                """;

            using SqliteCommand cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@jiraKey", seedId);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            List<SearchResult> results = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string streamName = reader.GetString(0);
                string topic = reader.GetString(1);
                int latestMsgId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                int baselineValue = reader.IsDBNull(5) ? 5 : reader.GetInt32(5);

                if (latestMsgId == 0)
                    continue;

                results.Add(new SearchResult
                {
                    Source = SourceSystems.Zulip,
                    Id = latestMsgId.ToString(),
                    Title = $"[{streamName}] {topic}",
                    Score = 1.0 * (baselineValue / 5.0),
                    Url = BuildMessageUrl(options, streamName, topic, latestMsgId),
                    UpdatedAt = ParseTimestamp(reader, 4),
                });
            }

            return Results.Ok(new SearchResponse("", results.Count, results, null));
        }

        // Unknown seed source: fall back to FTS with seedId as keyword, reduced score
        using SqliteConnection conn = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(seedId);
        if (string.IsNullOrEmpty(ftsQuery))
            return Results.Ok(new SearchResponse("", 0, [], null));

        string ftsSql = """
            SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                   snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank, zm.Timestamp,
                   COALESCE(zs.BaselineValue, 5) as BaselineValue
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH @query
            ORDER BY (zulip_messages_fts.rank * COALESCE(zs.BaselineValue, 5) / 5.0)
            LIMIT @limit
            """;

        using SqliteCommand ftsCmd = new SqliteCommand(ftsSql, conn);
        ftsCmd.Parameters.AddWithValue("@query", ftsQuery);
        ftsCmd.Parameters.AddWithValue("@limit", maxResults);

        List<SearchResult> ftsResults = [];
        using SqliteDataReader ftsReader = ftsCmd.ExecuteReader();
        while (ftsReader.Read())
        {
            int msgId = ftsReader.GetInt32(0);
            string streamName = ftsReader.GetString(1);
            string topic = ftsReader.GetString(2);
            double rawRank = ftsReader.GetDouble(4);
            int baselineValue = ftsReader.GetInt32(6);

            ftsResults.Add(new SearchResult
            {
                Source = SourceSystems.Zulip,
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = ftsReader.IsDBNull(3) ? null : ftsReader.GetString(3),
                Score = -rawRank * (baselineValue / 5.0) * 0.3,
                Url = BuildMessageUrl(options, streamName, topic, msgId),
                UpdatedAt = ParseTimestamp(ftsReader, 5),
            });
        }

        return Results.Ok(new SearchResponse("", ftsResults.Count, ftsResults, null));
    }

    private static string BuildMessageUrl(ZulipServiceOptions options, string streamName, string topic, int messageId) =>
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

    private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }
}

/// <summary>Request body for updating a Zulip stream's mutable properties.</summary>
public record ZulipStreamUpdateRequest(bool IncludeStream, int? BaselineValue = null);
