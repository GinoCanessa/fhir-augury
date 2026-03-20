using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// Fetches streams and messages from the Zulip REST API, caches responses,
/// and upserts into the database. Supports full, incremental, and cache-only modes.
/// </summary>
public class ZulipSource(
    ZulipServiceOptions options,
    HttpClient httpClient,
    ZulipDatabase database,
    IResponseCache cache,
    ILogger<ZulipSource> logger)
{
    public const string SourceName = "zulip";

    /// <summary>Performs a full download of all streams and their messages.</summary>
    public async Task<IngestionResult> DownloadAllAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        List<ZulipStreamRecord> streams;
        try
        {
            streams = await FetchStreamsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch streams");
            errors.Add($"streams: {ex.Message}");
            return new IngestionResult(0, 0, 0, 0, errors, startedAt);
        }

        using var connection = database.OpenConnection();

        // Upsert streams
        foreach (var stream in streams)
        {
            var existing = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: stream.ZulipStreamId);
            if (existing is not null)
            {
                stream.Id = existing.Id;
                stream.MessageCount = existing.MessageCount;
                ZulipStreamRecord.Update(connection, stream);
            }
            else
            {
                ZulipStreamRecord.Insert(connection, stream, ignoreDuplicates: true);
            }
        }

        // For each stream, paginate all messages
        foreach (var stream in streams)
        {
            if (ct.IsCancellationRequested) break;

            logger.LogInformation("Fetching messages for stream: {StreamName}", stream.Name);

            var streamDir = ZulipCacheLayout.StreamDirectory(stream.ZulipStreamId);
            var existingKeys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDir).ToList();

            int anchor = 0;
            bool hasMore = true;
            int streamMessageCount = 0;

            while (hasMore && !ct.IsCancellationRequested)
            {
                string rawJson;
                List<JsonElement> messages;
                try
                {
                    var narrow = $"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]";
                    var url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}" +
                              $"&anchor={anchor}&num_before=0&num_after={options.BatchSize}";

                    var response = await HttpRetryHelper.GetWithRetryAsync(
                        httpClient, url, ct, options.RateLimiting.MaxRetries, "zulip");
                    response.EnsureSuccessStatusCode();
                    rawJson = await response.Content.ReadAsStringAsync(ct);

                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    var foundNewest = root.TryGetProperty("found_newest", out var fn) && fn.GetBoolean();
                    messages = [];
                    foreach (var msg in root.GetProperty("messages").EnumerateArray())
                        messages.Add(msg.Clone());
                    hasMore = !foundNewest && messages.Count > 0;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fetch messages for stream {Stream} at anchor {Anchor}", stream.Name, anchor);
                    errors.Add($"stream:{stream.Name}:anchor:{anchor} - {ex.Message}");
                    break;
                }

                // Write to cache — initial download uses WeekOf
                if (messages.Count > 0)
                {
                    var oldestTimestamp = messages[0].GetProperty("timestamp").GetInt64();
                    var oldestDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(oldestTimestamp).UtcDateTime);
                    var cacheKey = $"{streamDir}/{CacheFileNaming.GenerateWeeklyFileName(oldestDate, ZulipCacheLayout.JsonExtension, existingKeys)}";
                    existingKeys.Add(Path.GetFileName(cacheKey));
                    using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache.PutAsync(ZulipCacheLayout.SourceName, cacheKey, cacheStream, ct);
                }

                foreach (var msgJson in messages)
                {
                    var (outcome, error) = ProcessMessage(msgJson, stream.Name, stream.Id, connection);
                    itemsProcessed++;

                    switch (outcome)
                    {
                        case ProcessOutcome.New: itemsNew++; streamMessageCount++; break;
                        case ProcessOutcome.Updated: itemsUpdated++; break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            if (error is not null) errors.Add(error);
                            break;
                    }

                    if (itemsProcessed % 5000 == 0)
                        logger.LogInformation("Download progress: {Count} messages processed", itemsProcessed);
                }

                if (messages.Count > 0)
                    anchor = messages[^1].GetProperty("id").GetInt32() + 1;
                else
                    hasMore = false;
            }

            // Update stream message count
            stream.MessageCount += streamMessageCount;
            stream.LastFetchedAt = DateTimeOffset.UtcNow;
            ZulipStreamRecord.Update(connection, stream);

            logger.LogInformation("Stream '{StreamName}': {Count} messages downloaded", stream.Name, streamMessageCount);
        }

        logger.LogInformation(
            "Full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Performs an incremental download using per-stream sync cursors.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        List<ZulipStreamRecord> streams;
        try
        {
            streams = await FetchStreamsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch streams");
            errors.Add($"streams: {ex.Message}");
            return new IngestionResult(0, 0, 0, 0, errors, startedAt);
        }

        using var connection = database.OpenConnection();

        foreach (var stream in streams)
        {
            if (ct.IsCancellationRequested) break;

            // Upsert stream
            var existingStream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: stream.ZulipStreamId);
            if (existingStream is not null)
            {
                stream.Id = existingStream.Id;
                stream.MessageCount = existingStream.MessageCount;
                ZulipStreamRecord.Update(connection, stream);
            }
            else
            {
                ZulipStreamRecord.Insert(connection, stream, ignoreDuplicates: true);
            }

            var syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: SourceName, SubSource: stream.Name);
            int anchor = 0;
            if (syncState?.LastCursor is not null && int.TryParse(syncState.LastCursor, out var lastId))
                anchor = lastId + 1;

            logger.LogInformation("Incremental fetch for {Stream} from anchor {Anchor}", stream.Name, anchor);

            var streamDir = ZulipCacheLayout.StreamDirectory(stream.ZulipStreamId);
            var existingKeys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDir).ToList();

            bool hasMore = true;
            while (hasMore && !ct.IsCancellationRequested)
            {
                string rawJson;
                List<JsonElement> messages;
                try
                {
                    var narrow = $"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]";
                    var url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}" +
                              $"&anchor={anchor}&num_before=0&num_after={options.BatchSize}";

                    var response = await HttpRetryHelper.GetWithRetryAsync(
                        httpClient, url, ct, options.RateLimiting.MaxRetries, "zulip");
                    response.EnsureSuccessStatusCode();
                    rawJson = await response.Content.ReadAsStringAsync(ct);

                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    var foundNewest = root.TryGetProperty("found_newest", out var fn) && fn.GetBoolean();
                    messages = [];
                    foreach (var msg in root.GetProperty("messages").EnumerateArray())
                        messages.Add(msg.Clone());
                    hasMore = !foundNewest && messages.Count > 0;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed incremental fetch for {Stream}", stream.Name);
                    errors.Add($"stream:{stream.Name} - {ex.Message}");
                    break;
                }

                // Incremental uses DayOf
                if (messages.Count > 0)
                {
                    var cacheKey = $"{streamDir}/{CacheFileNaming.GenerateDailyFileName(today, ZulipCacheLayout.JsonExtension, existingKeys)}";
                    existingKeys.Add(Path.GetFileName(cacheKey));
                    using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache.PutAsync(ZulipCacheLayout.SourceName, cacheKey, cacheStream, ct);
                }

                foreach (var msgJson in messages)
                {
                    var (outcome, error) = ProcessMessage(msgJson, stream.Name, stream.Id, connection);
                    itemsProcessed++;

                    switch (outcome)
                    {
                        case ProcessOutcome.New: itemsNew++; break;
                        case ProcessOutcome.Updated: itemsUpdated++; break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            if (error is not null) errors.Add(error);
                            break;
                    }
                }

                if (messages.Count > 0)
                    anchor = messages[^1].GetProperty("id").GetInt32() + 1;
                else
                    hasMore = false;
            }

            // Update sync cursor
            if (anchor > 0)
                UpdateSyncCursor(connection, stream.Name, (anchor - 1).ToString());
        }

        logger.LogInformation(
            "Incremental download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Loads all messages from cached API responses (no network).</summary>
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        using var connection = database.OpenConnection();

        // Discover stream directories under the zulip source cache
        var cacheRoot = cache.RootPath;
        var zulipDir = Path.Combine(cacheRoot, ZulipCacheLayout.SourceName);
        if (!Directory.Exists(zulipDir))
        {
            logger.LogWarning("No zulip cache directory found at {Path}", zulipDir);
            return Task.FromResult(new IngestionResult(0, 0, 0, 0, errors, startedAt));
        }

        var streamDirs = Directory.GetDirectories(zulipDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => n.StartsWith('s') && int.TryParse(n[1..], out _))
            .OrderBy(n => int.Parse(n[1..]))
            .ToList();

        foreach (var streamDirName in streamDirs)
        {
            if (ct.IsCancellationRequested) break;

            var streamId = int.Parse(streamDirName[1..]);

            // Upsert a stream record from metadata or defaults
            var existingStream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: streamId);
            var streamRecord = existingStream ?? new ZulipStreamRecord
            {
                Id = ZulipStreamRecord.GetIndex(),
                ZulipStreamId = streamId,
                Name = $"stream-{streamId}",
                Description = null,
                IsWebPublic = true,
                MessageCount = 0,
                LastFetchedAt = DateTimeOffset.MinValue,
            };
            if (existingStream is null)
                ZulipStreamRecord.Insert(connection, streamRecord, ignoreDuplicates: true);

            var keys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDirName);

            foreach (var key in keys)
            {
                if (ct.IsCancellationRequested) break;

                if (!cache.TryGet(ZulipCacheLayout.SourceName, key, out var stream))
                    continue;

                using (stream)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(stream);
                        var messagesArray = doc.RootElement.GetProperty("messages");

                        foreach (var msgJson in messagesArray.EnumerateArray())
                        {
                            var (outcome, error) = ProcessMessage(msgJson, streamRecord.Name, streamRecord.Id, connection);
                            itemsProcessed++;

                            switch (outcome)
                            {
                                case ProcessOutcome.New: itemsNew++; break;
                                case ProcessOutcome.Updated: itemsUpdated++; break;
                                case ProcessOutcome.Failed:
                                    itemsFailed++;
                                    if (error is not null) errors.Add(error);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to process cached file {Key}", key);
                        itemsFailed++;
                        errors.Add($"{key}: {ex.Message}");
                    }
                }

                if (itemsProcessed % 5000 == 0 && itemsProcessed > 0)
                    logger.LogInformation("Cache ingestion progress: {Count} messages processed", itemsProcessed);
            }
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return Task.FromResult(new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt));
    }

    private async Task<List<ZulipStreamRecord>> FetchStreamsAsync(CancellationToken ct)
    {
        var url = $"{options.BaseUrl}/api/v1/streams";
        var response = await HttpRetryHelper.GetWithRetryAsync(
            httpClient, url, ct, options.RateLimiting.MaxRetries, "zulip");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var streams = new List<ZulipStreamRecord>();

        foreach (var streamJson in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var stream = ZulipMessageMapper.MapStream(streamJson);
            if (options.OnlyWebPublic && !stream.IsWebPublic)
                continue;
            streams.Add(stream);
        }

        logger.LogInformation("Fetched {Count} streams", streams.Count);
        return streams;
    }

    private static (ProcessOutcome Outcome, string? Error) ProcessMessage(
        JsonElement messageJson, string streamName, int streamDbId,
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        try
        {
            var record = ZulipMessageMapper.MapMessage(messageJson, streamName, streamDbId);
            var existing = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId);

            if (existing is not null)
            {
                record.Id = existing.Id;
                ZulipMessageRecord.Update(connection, record);
                return (ProcessOutcome.Updated, null);
            }

            ZulipMessageRecord.Insert(connection, record, ignoreDuplicates: true);
            return (ProcessOutcome.New, null);
        }
        catch (Exception ex)
        {
            var msgId = messageJson.TryGetProperty("id", out var idProp) ? idProp.GetInt32().ToString() : "unknown";
            return (ProcessOutcome.Failed, $"msg:{msgId} - {ex.Message}");
        }
    }

    private static void UpdateSyncCursor(
        Microsoft.Data.Sqlite.SqliteConnection connection, string streamName, string cursor)
    {
        var existing = ZulipSyncStateRecord.SelectSingle(connection, SourceName: SourceName, SubSource: streamName);

        var syncState = new ZulipSyncStateRecord
        {
            Id = existing?.Id ?? ZulipSyncStateRecord.GetIndex(),
            SourceName = SourceName,
            SubSource = streamName,
            LastSyncAt = DateTimeOffset.UtcNow,
            LastCursor = cursor,
            ItemsIngested = 0,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "success",
            LastError = null,
        };

        if (existing is not null)
            ZulipSyncStateRecord.Update(connection, syncState);
        else
            ZulipSyncStateRecord.Insert(connection, syncState);
    }

    private enum ProcessOutcome { New, Updated, Failed }
}

/// <summary>Result of an ingestion run.</summary>
public record IngestionResult(
    int ItemsProcessed,
    int ItemsNew,
    int ItemsUpdated,
    int ItemsFailed,
    List<string> Errors,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
