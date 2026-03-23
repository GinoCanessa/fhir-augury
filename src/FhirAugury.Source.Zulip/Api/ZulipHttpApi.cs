using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Api;

/// <summary>HTTP Minimal API endpoints for standalone use and debugging.</summary>
public static class ZulipHttpApi
{
    public static IEndpointRouteBuilder MapZulipHttpApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/search", (string? q, int? limit, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            var options = optsAccessor.Value;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            using var connection = db.OpenConnection();
            var ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
            if (string.IsNullOrEmpty(ftsQuery))
                return Results.Ok(new { query = q, results = Array.Empty<object>() });

            var maxResults = Math.Min(limit ?? 20, 200);

            var sql = """
                SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                       snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       zulip_messages_fts.rank, zm.SenderName, zm.Timestamp
                FROM zulip_messages_fts
                JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
                WHERE zulip_messages_fts MATCH @query
                ORDER BY zulip_messages_fts.rank
                LIMIT @limit
                """;

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            var results = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msgId = reader.GetInt32(0);
                var streamName = reader.GetString(1);
                var topic = reader.GetString(2);
                results.Add(new
                {
                    id = msgId,
                    stream = streamName,
                    topic,
                    snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                    score = -reader.GetDouble(4),
                    sender = reader.IsDBNull(5) ? null : reader.GetString(5),
                    url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}/near/{msgId}",
                });
            }

            return Results.Ok(new { query = q, total = results.Count, results });
        });

        api.MapGet("/messages/{id:int}", (int id, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            var options = optsAccessor.Value;
            using var connection = db.OpenConnection();
            var message = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: id);
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
                url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(message.StreamName)}/topic/{Uri.EscapeDataString(message.Topic)}/near/{message.ZulipMessageId}",
            });
        });

        api.MapGet("/streams", (ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            var options = optsAccessor.Value;
            using var connection = db.OpenConnection();
            var streams = ZulipStreamRecord.SelectList(connection);

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
                    url = $"{options.BaseUrl}/#narrow/stream/{Uri.EscapeDataString(s.Name)}",
                }),
            });
        });

        api.MapGet("/streams/{streamName}/topics", (string streamName, int? limit, int? offset, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            var options = optsAccessor.Value;
            using var connection = db.OpenConnection();
            var maxResults = Math.Min(limit ?? 50, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var sql = """
                SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMsgAt
                FROM zulip_messages
                WHERE StreamName = @streamName
                GROUP BY Topic
                ORDER BY LastMsgAt DESC
                LIMIT @limit OFFSET @offset
                """;

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@streamName", streamName);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            var topics = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var topic = reader.GetString(0);
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

        api.MapGet("/threads/{streamName}/{topic}", (string streamName, string topic, int? limit, ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) =>
        {
            var options = optsAccessor.Value;
            using var connection = db.OpenConnection();
            var maxResults = Math.Min(limit ?? 200, 1000);

            var sql = """
                SELECT ZulipMessageId, SenderName, ContentPlain, ContentHtml, Timestamp
                FROM zulip_messages
                WHERE StreamName = @streamName AND Topic = @topic
                ORDER BY Timestamp ASC
                LIMIT @limit
                """;

            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@streamName", streamName);
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@limit", maxResults);

            var messages = new List<object>();
            using var reader = cmd.ExecuteReader();
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

        api.MapPost("/ingest", async (HttpRequest req, ZulipIngestionPipeline pipeline) =>
        {
            var type = req.Query["type"].FirstOrDefault() ?? "incremental";
            try
            {
                var result = type == "full"
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

        api.MapGet("/status", (ZulipIngestionPipeline pipeline, ZulipDatabase db) =>
        {
            using var connection = db.OpenConnection();
            var syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);

            return Results.Ok(new
            {
                isRunning = pipeline.IsRunning,
                currentStatus = pipeline.CurrentStatus,
                lastSyncAt = syncState?.LastSyncAt,
                itemsIngested = syncState?.ItemsIngested ?? 0,
                lastError = syncState?.LastError,
            });
        });

        api.MapPost("/rebuild", async (ZulipIngestionPipeline pipeline) =>
        {
            try
            {
                var result = await pipeline.RebuildFromCacheAsync();
                return Results.Ok(new { result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapGet("/stats", (ZulipDatabase db, FhirAugury.Common.Caching.IResponseCache cache) =>
        {
            using var connection = db.OpenConnection();
            var messageCount = ZulipMessageRecord.SelectCount(connection);
            var streamCount = ZulipStreamRecord.SelectCount(connection);
            var dbSize = db.GetDatabaseSizeBytes();
            var cacheStats = cache.GetStats(ZulipCacheLayout.SourceName);

            return Results.Ok(new
            {
                source = "zulip",
                totalMessages = messageCount,
                totalStreams = streamCount,
                databaseSizeBytes = dbSize,
                cacheSizeBytes = cacheStats.TotalBytes,
                cacheFiles = cacheStats.FileCount,
            });
        });

        return app;
    }
}
