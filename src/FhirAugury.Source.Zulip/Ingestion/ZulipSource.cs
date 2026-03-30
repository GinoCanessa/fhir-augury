using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// Fetches streams and messages from the Zulip REST API, caches responses,
/// and upserts into the database. Supports full, incremental, and cache-only modes.
/// </summary>
public class ZulipSource(
    IOptions<ZulipServiceOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    ZulipDatabase database,
    IResponseCache cache,
    ILogger<ZulipSource> logger)
{
    private readonly ZulipServiceOptions options = optionsAccessor.Value;

    public const string SourceName = "zulip";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Performs a full download of all streams and their messages.</summary>
    public async Task<IngestionResult> DownloadAllAsync(CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

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

        using SqliteConnection connection = database.OpenConnection();

        // Upsert streams and apply exclusions
        foreach (ZulipStreamRecord stream in streams)
        {
            ZulipStreamRecord? existing = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: stream.ZulipStreamId);
            if (existing is not null)
            {
                stream.Id = existing.Id;
                stream.MessageCount = existing.MessageCount;
                // Preserve user-set IncludeStream and BaselineValue; only apply config for new streams
                stream.IncludeStream = existing.IncludeStream;
                stream.BaselineValue = existing.BaselineValue;
                ZulipStreamRecord.Update(connection, stream);
            }
            else
            {
                ApplyExclusion(stream);
                ApplyBaselineValue(stream);
                ZulipStreamRecord.Insert(connection, stream, ignoreDuplicates: true);
            }
        }

        // Cache stream data for rebuild support (Z-10)
        await CacheStreamDataAsync(streams, ct);

        // Filter to included streams only (Z-05)
        List<ZulipStreamRecord> activeStreams = streams.Where(s => s.IncludeStream).ToList();
        logger.LogInformation("Processing {Active} of {Total} streams (excluded: {Excluded})",
            activeStreams.Count, streams.Count, streams.Count - activeStreams.Count);

        // For each stream, paginate all messages
        foreach (ZulipStreamRecord stream in activeStreams)
        {
            if (ct.IsCancellationRequested) break;

            logger.LogInformation("Fetching messages for stream: {StreamName}", stream.Name);

            string streamDir = ZulipCacheLayout.StreamDirectory(stream.ZulipStreamId);
            List<string> existingKeys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDir).ToList();

            int anchor = 0;
            bool hasMore = true;
            int streamMessageCount = 0;

            while (hasMore && !ct.IsCancellationRequested)
            {
                string rawJson;
                List<JsonElement> messages;
                try
                {
                    string narrow = $"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]";
                    string url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}" +
                              $"&anchor={anchor}&num_before=0&num_after={options.BatchSize}";

                    HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                        httpClientFactory.CreateClient("zulip"), url, ct, options.RateLimiting.MaxRetries, "zulip");
                    response.EnsureSuccessStatusCode();
                    rawJson = await response.Content.ReadAsStringAsync(ct);

                    using JsonDocument doc = JsonDocument.Parse(rawJson);
                    JsonElement root = doc.RootElement;
                    bool foundNewest = root.TryGetProperty("found_newest", out JsonElement fn) && fn.GetBoolean();
                    messages = [];
                    foreach (JsonElement msg in root.GetProperty("messages").EnumerateArray())
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
                    long oldestTimestamp = messages[0].GetProperty("timestamp").GetInt64();
                    DateOnly oldestDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(oldestTimestamp).UtcDateTime);
                    string cacheKey = $"{streamDir}/{CacheFileNaming.GenerateWeeklyFileName(oldestDate, ZulipCacheLayout.JsonExtension, existingKeys)}";
                    existingKeys.Add(Path.GetFileName(cacheKey));
                    using MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache.PutAsync(ZulipCacheLayout.SourceName, cacheKey, cacheStream, ct);
                }

                Dictionary<int, ZulipMessageRecord> messagesToUpdate = [];
                Dictionary<int, ZulipMessageRecord> messagesToInsert = [];

                foreach (JsonElement msgJson in messages)
                {
                    itemsProcessed++;

                    if (itemsProcessed % 5000 == 0)
                        logger.LogInformation("Download progress: {Count} messages processed", itemsProcessed);

                    ZulipMessageRecord record;
                    try
                    {
                        record = ZulipMessageMapper.MapMessage(msgJson, stream.Name, stream.Id);
                    }
                    catch (Exception ex)
                    {
                        string msgId = msgJson.TryGetProperty("id", out JsonElement idProp) ? idProp.GetInt32().ToString() : "unknown";
                        itemsFailed++;
                        errors.Add($"msg:{msgId} - {ex.Message}");
                        continue;
                    }

                    if (messagesToInsert.TryGetValue(record.ZulipMessageId, out ZulipMessageRecord? existing))
                    {
                        record.Id = existing.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else if (messagesToUpdate.TryGetValue(record.ZulipMessageId, out existing))
                    {
                        record.Id = existing.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else if (ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId) is ZulipMessageRecord dbExisting)
                    {
                        record.Id = dbExisting.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else
                    {
                        record.Id = ZulipMessageRecord.GetIndex();
                        messagesToInsert[record.ZulipMessageId] = record;
                    }
                }

                messagesToUpdate.Values.Update(connection);
                messagesToInsert.Values.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

                itemsNew += messagesToInsert.Count;
                itemsUpdated += messagesToUpdate.Count;

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
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

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

        using SqliteConnection connection = database.OpenConnection();

        // Upsert streams and apply exclusions
        foreach (ZulipStreamRecord stream in streams)
        {
            ZulipStreamRecord? existingStream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: stream.ZulipStreamId);
            if (existingStream is not null)
            {
                stream.Id = existingStream.Id;
                stream.MessageCount = existingStream.MessageCount;
                stream.IncludeStream = existingStream.IncludeStream;
                stream.BaselineValue = existingStream.BaselineValue;
                ZulipStreamRecord.Update(connection, stream);
            }
            else
            {
                ApplyExclusion(stream);
                ApplyBaselineValue(stream);
                ZulipStreamRecord.Insert(connection, stream, ignoreDuplicates: true);
            }
        }

        // Cache stream data for rebuild support (Z-10)
        await CacheStreamDataAsync(streams, ct);

        // Filter to included streams only (Z-05)
        List<ZulipStreamRecord> activeStreams = streams.Where(s => s.IncludeStream).ToList();

        foreach (ZulipStreamRecord stream in activeStreams)
        {
            if (ct.IsCancellationRequested) break;

            ZulipSyncStateRecord? syncState = ZulipSyncStateRecord.SelectSingle(connection, SourceName: SourceName, SubSource: stream.Name);
            int anchor = 0;
            if (syncState?.LastCursor is not null && int.TryParse(syncState.LastCursor, out int lastId))
                anchor = lastId + 1;

            logger.LogInformation("Incremental fetch for {Stream} from anchor {Anchor}", stream.Name, anchor);

            string streamDir = ZulipCacheLayout.StreamDirectory(stream.ZulipStreamId);
            List<string> existingKeys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDir).ToList();

            bool hasMore = true;
            while (hasMore && !ct.IsCancellationRequested)
            {
                string rawJson;
                List<JsonElement> messages;
                try
                {
                    string narrow = $"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]";
                    string url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}" +
                              $"&anchor={anchor}&num_before=0&num_after={options.BatchSize}";

                    HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                        httpClientFactory.CreateClient("zulip"), url, ct, options.RateLimiting.MaxRetries, "zulip");
                    response.EnsureSuccessStatusCode();
                    rawJson = await response.Content.ReadAsStringAsync(ct);

                    using JsonDocument doc = JsonDocument.Parse(rawJson);
                    JsonElement root = doc.RootElement;
                    bool foundNewest = root.TryGetProperty("found_newest", out JsonElement fn) && fn.GetBoolean();
                    messages = [];
                    foreach (JsonElement msg in root.GetProperty("messages").EnumerateArray())
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
                    string cacheKey = $"{streamDir}/{CacheFileNaming.GenerateDailyFileName(today, ZulipCacheLayout.JsonExtension, existingKeys)}";
                    existingKeys.Add(Path.GetFileName(cacheKey));
                    using MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache.PutAsync(ZulipCacheLayout.SourceName, cacheKey, cacheStream, ct);
                }

                Dictionary<int, ZulipMessageRecord> messagesToUpdate = [];
                Dictionary<int, ZulipMessageRecord> messagesToInsert = [];

                foreach (JsonElement msgJson in messages)
                {
                    itemsProcessed++;

                    ZulipMessageRecord record;
                    try
                    {
                        record = ZulipMessageMapper.MapMessage(msgJson, stream.Name, stream.Id);
                    }
                    catch (Exception ex)
                    {
                        string msgId = msgJson.TryGetProperty("id", out JsonElement idProp) ? idProp.GetInt32().ToString() : "unknown";
                        itemsFailed++;
                        errors.Add($"msg:{msgId} - {ex.Message}");
                        continue;
                    }

                    if (messagesToInsert.TryGetValue(record.ZulipMessageId, out ZulipMessageRecord? existing))
                    {
                        record.Id = existing.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else if (messagesToUpdate.TryGetValue(record.ZulipMessageId, out existing))
                    {
                        record.Id = existing.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else if (ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId) is ZulipMessageRecord dbExisting)
                    {
                        record.Id = dbExisting.Id;
                        messagesToUpdate[record.ZulipMessageId] = record;
                    }
                    else
                    {
                        record.Id = ZulipMessageRecord.GetIndex();
                        messagesToInsert[record.ZulipMessageId] = record;
                    }
                }

                messagesToUpdate.Values.Update(connection);
                messagesToInsert.Values.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

                itemsNew += messagesToInsert.Count;
                itemsUpdated += messagesToUpdate.Count;

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
    public async Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

        using SqliteConnection connection = database.OpenConnection();

        // Resolve stream metadata: cached file → API fallback → placeholder (Z-10, Z-06)
        List<ZulipStreamRecord>? cachedStreams = await LoadCachedStreamDataAsync();

        if (cachedStreams is null)
        {
            // streams.json not in cache — try fetching from API
            try
            {
                cachedStreams = await FetchStreamsAsync(ct);
                logger.LogInformation("streams.json not found in cache; fetched {Count} streams from API", cachedStreams.Count);
                await CacheStreamDataAsync(cachedStreams, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch streams from API; falling back to placeholder records from cache directories");
            }
        }

        // Apply exclusions and upsert cached/API stream records
        if (cachedStreams is not null)
        {
            foreach (ZulipStreamRecord stream in cachedStreams)
            {
                ApplyExclusion(stream);
                ApplyBaselineValue(stream);
                ZulipStreamRecord? existing = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: stream.ZulipStreamId);
                if (existing is not null)
                {
                    stream.Id = existing.Id;
                    ZulipStreamRecord.Update(connection, stream);
                }
                else
                {
                    ZulipStreamRecord.Insert(connection, stream, ignoreDuplicates: true);
                }
            }
        }

        // Discover stream directories under the zulip source cache
        string cacheRoot = cache.RootPath;
        string zulipDir = Path.Combine(cacheRoot, ZulipCacheLayout.SourceName);
        if (!Directory.Exists(zulipDir))
        {
            logger.LogWarning("No zulip cache directory found at {Path}", zulipDir);
            return new IngestionResult(0, 0, 0, 0, errors, startedAt);
        }

        List<string> streamDirs = Directory.GetDirectories(zulipDir)
            .Select(d => Path.GetFileName(d))
            .Where(n => n.StartsWith('s') && int.TryParse(n.AsSpan(1), out _))
            .OrderBy(n => int.Parse(n.AsSpan(1)))
            .ToList();

        foreach (string? streamDirName in streamDirs)
        {
            if (ct.IsCancellationRequested) break;

            int streamId = int.Parse(streamDirName.AsSpan(1));

            // Look up or create stream record
            ZulipStreamRecord? existingStream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: streamId);

            // If no stream record exists (no cached streams.json and no API), create a placeholder
            if (existingStream is null)
            {
                ZulipStreamRecord placeholder = new ZulipStreamRecord
                {
                    Id = ZulipStreamRecord.GetIndex(),
                    ZulipStreamId = streamId,
                    Name = $"stream-{streamId}",
                    Description = null,
                    IsWebPublic = true,
                    MessageCount = 0,
                    IncludeStream = !options.ExcludedStreamIds.Contains(streamId),
                    BaselineValue = 5,
                    LastFetchedAt = DateTimeOffset.MinValue,
                };
                ApplyBaselineValue(placeholder);
                ZulipStreamRecord.Insert(connection, placeholder, ignoreDuplicates: true);
                existingStream = placeholder;
            }

            // Honor IncludeStream during cache load (Z-06)
            if (!existingStream.IncludeStream)
            {
                logger.LogDebug("Skipping excluded stream {StreamId} during cache load", streamId);
                continue;
            }

            int maxMessageId = 0;
            int streamMessageCount = 0;

            IEnumerable<string> keys = cache.EnumerateKeys(ZulipCacheLayout.SourceName, streamDirName);

            foreach (string key in keys)
            {
                if (ct.IsCancellationRequested) break;

                if (!cache.TryGet(ZulipCacheLayout.SourceName, key, out Stream? stream))
                    continue;

                using (stream)
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(stream);
                        JsonElement messagesArray = doc.RootElement.GetProperty("messages");

                        Dictionary<int, ZulipMessageRecord> messagesToUpdate = [];
                        Dictionary<int, ZulipMessageRecord> messagesToInsert = [];

                        foreach (JsonElement msgJson in messagesArray.EnumerateArray())
                        {
                            itemsProcessed++;

                            if (itemsProcessed % 5000 == 0 && itemsProcessed > 0)
                                logger.LogInformation("Cache ingestion progress: {Count} messages processed", itemsProcessed);

                            ZulipMessageRecord record;
                            try
                            {
                                record = ZulipMessageMapper.MapMessage(msgJson, existingStream.Name, existingStream.Id);
                            }
                            catch (Exception ex)
                            {
                                string msgId = msgJson.TryGetProperty("id", out JsonElement idProp) ? idProp.GetInt32().ToString() : "unknown";
                                itemsFailed++;
                                errors.Add($"msg:{msgId} - {ex.Message}");
                                continue;
                            }

                            if (messagesToInsert.TryGetValue(record.ZulipMessageId, out ZulipMessageRecord? existing))
                            {
                                record.Id = existing.Id;
                                messagesToUpdate[record.ZulipMessageId] = record;
                            }
                            else if (messagesToUpdate.TryGetValue(record.ZulipMessageId, out existing))
                            {
                                record.Id = existing.Id;
                                messagesToUpdate[record.ZulipMessageId] = record;
                            }
                            else if (ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId) is ZulipMessageRecord dbExisting)
                            {
                                record.Id = dbExisting.Id;
                                messagesToUpdate[record.ZulipMessageId] = record;
                            }
                            else
                            {
                                record.Id = ZulipMessageRecord.GetIndex();
                                messagesToInsert[record.ZulipMessageId] = record;
                            }

                            if (msgJson.TryGetProperty("id", out JsonElement idElement))
                            {
                                int msgId = idElement.GetInt32();
                                if (msgId > maxMessageId)
                                    maxMessageId = msgId;
                            }
                        }

                        messagesToUpdate.Values.Update(connection);
                        messagesToInsert.Values.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);

                        itemsNew += messagesToInsert.Count;
                        itemsUpdated += messagesToUpdate.Count;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to process cached file {Key}", key);
                        itemsFailed++;
                        errors.Add($"{key}: {ex.Message}");
                    }
                }
            }

            // Set per-stream sync cursor so incremental downloads start after cached data
            if (maxMessageId > 0)
                UpdateSyncCursor(connection, existingStream.Name, maxMessageId.ToString());

            existingStream.MessageCount += streamMessageCount;
            existingStream.LastFetchedAt = DateTimeOffset.UtcNow;
            ZulipStreamRecord.Update(connection, existingStream);
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Applies the ExcludedStreamIds config to a stream record.</summary>
    private void ApplyExclusion(ZulipStreamRecord stream)
    {
        stream.IncludeStream = !options.ExcludedStreamIds.Contains(stream.ZulipStreamId);
    }

    /// <summary>
    /// Sets the stream's BaselineValue from config if a match exists, otherwise keeps the default (5).
    /// </summary>
    private void ApplyBaselineValue(ZulipStreamRecord stream)
    {
        foreach ((string? name, int value) in options.StreamBaselineValues)
        {
            if (stream.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                stream.BaselineValue = Math.Clamp(value, 0, 10);
                return;
            }
        }
    }

    /// <summary>Caches stream metadata to disk for rebuild support (Z-10).</summary>
    private async Task CacheStreamDataAsync(List<ZulipStreamRecord> streams, CancellationToken ct)
    {
        StreamCacheModel model = new()
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Streams = streams.Select(s => new StreamCacheEntry
            {
                ZulipStreamId = s.ZulipStreamId,
                Name = s.Name,
                Description = s.Description,
                IsWebPublic = s.IsWebPublic,
                IncludeStream = s.IncludeStream,
            }).ToList()
        };

        using MemoryStream ms = new();
        await JsonSerializer.SerializeAsync(ms, model, JsonOptions, ct);
        ms.Position = 0;
        await cache.PutAsync(ZulipCacheLayout.SourceName, ZulipCacheLayout.StreamsCacheKey, ms, ct);
    }

    /// <summary>Loads cached stream metadata from streams.json (Z-10).</summary>
    private async Task<List<ZulipStreamRecord>?> LoadCachedStreamDataAsync()
    {
        if (!cache.TryGet(ZulipCacheLayout.SourceName, ZulipCacheLayout.StreamsCacheKey, out Stream? stream))
            return null;

        using (stream)
        {
            StreamCacheModel? model = await JsonSerializer.DeserializeAsync<StreamCacheModel>(stream, JsonOptions);
            if (model is null) return null;

            return model.Streams.Select(s => new ZulipStreamRecord
            {
                Id = ZulipStreamRecord.GetIndex(),
                ZulipStreamId = s.ZulipStreamId,
                Name = s.Name,
                Description = s.Description,
                IsWebPublic = s.IsWebPublic,
                MessageCount = 0,
                IncludeStream = s.IncludeStream,
                BaselineValue = 5,
                LastFetchedAt = model.FetchedAt,
            }).ToList();
        }
    }

    private async Task<List<ZulipStreamRecord>> FetchStreamsAsync(CancellationToken ct)
    {
        string url = $"{options.BaseUrl}/api/v1/streams";
        HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
            httpClientFactory.CreateClient("zulip"), url, ct, options.RateLimiting.MaxRetries, "zulip");
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        List<ZulipStreamRecord> streams = new List<ZulipStreamRecord>();

        foreach (JsonElement streamJson in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            ZulipStreamRecord stream = ZulipMessageMapper.MapStream(streamJson);
            if (options.OnlyWebPublic && !stream.IsWebPublic)
                continue;
            streams.Add(stream);
        }

        logger.LogInformation("Fetched {Count} streams", streams.Count);
        return streams;
    }

    //private static (ProcessOutcome Outcome, string? Error) ProcessMessage(
    //    JsonElement messageJson, string streamName, int streamDbId,
    //    Microsoft.Data.Sqlite.SqliteConnection connection)
    //{
    //    try
    //    {
    //        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(messageJson, streamName, streamDbId);
    //        ZulipMessageRecord? existing = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId);

    //        if (existing is not null)
    //        {
    //            record.Id = existing.Id;
    //            ZulipMessageRecord.Update(connection, record);
    //            return (ProcessOutcome.Updated, null);
    //        }

    //        ZulipMessageRecord.Insert(connection, record, ignoreDuplicates: true);
    //        return (ProcessOutcome.New, null);
    //    }
    //    catch (Exception ex)
    //    {
    //        string msgId = messageJson.TryGetProperty("id", out JsonElement idProp) ? idProp.GetInt32().ToString() : "unknown";
    //        return (ProcessOutcome.Failed, $"msg:{msgId} - {ex.Message}");
    //    }
    //}

    private static void UpdateSyncCursor(
        Microsoft.Data.Sqlite.SqliteConnection connection, string streamName, string cursor)
    {
        ZulipSyncStateRecord? existing = ZulipSyncStateRecord.SelectSingle(connection, SourceName: SourceName, SubSource: streamName);

        ZulipSyncStateRecord syncState = new ZulipSyncStateRecord
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

    private record StreamCacheModel
    {
        public DateTimeOffset FetchedAt { get; init; }
        public List<StreamCacheEntry> Streams { get; init; } = [];
    }

    private record StreamCacheEntry
    {
        public int ZulipStreamId { get; init; }
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsWebPublic { get; init; }
        public bool IncludeStream { get; init; } = true;
    }
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
