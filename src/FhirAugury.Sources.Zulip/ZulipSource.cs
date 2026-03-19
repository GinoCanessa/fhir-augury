using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.Zulip;

/// <summary>Zulip data source implementing IDataSource for FHIR chat messages.</summary>
public class ZulipSource(ZulipSourceOptions options, HttpClient httpClient, ILogger<ZulipSource>? logger = null) : IDataSource
{
    public string SourceName => "zulip";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        List<ZulipStreamRecord> streams;
        try
        {
            streams = await FetchStreamsAsync(ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch streams");
            errors.Add(new IngestionError("streams", $"Failed to fetch streams: {ex.Message}", ex));
            return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
        }

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;

        using var connection = db.OpenConnection();

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

            logger?.LogInformation("Fetching messages for stream: {StreamName}", stream.Name);

            var streamDir = $"s{stream.ZulipStreamId}";
            var generatedFiles = shouldCache ? cache!.EnumerateKeys("zulip", streamDir).ToList() : new List<string>();

            int anchor = 0;
            bool hasMore = true;
            int streamMessageCount = 0;

            while (hasMore && !ct.IsCancellationRequested)
            {
                List<JsonElement> messages;
                string? rawJson = null;
                try
                {
                    if (shouldCache)
                    {
                        var response = await HttpRetryHelper.GetWithRetryAsync(
                            httpClient,
                            $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString($"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]")}&anchor={anchor}&num_before=0&num_after={options.BatchSize}",
                            ct, sourceName: "zulip");
                        response.EnsureSuccessStatusCode();
                        rawJson = await response.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(rawJson);
                        var root = doc.RootElement;
                        var found = root.TryGetProperty("found_newest", out var foundNewest) && foundNewest.GetBoolean();
                        messages = [];
                        foreach (var msg in root.GetProperty("messages").EnumerateArray())
                            messages.Add(msg.Clone());
                        hasMore = !found && messages.Count > 0;
                    }
                    else
                    {
                        (messages, hasMore) = await FetchMessagesAsync(stream.ZulipStreamId, anchor, options.BatchSize, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to fetch messages for stream {Stream} at anchor {Anchor}", stream.Name, anchor);
                    errors.Add(new IngestionError($"stream:{stream.Name}:anchor:{anchor}", ex.Message, ex));
                    break;
                }

                // Write to cache — initial download uses WeekOf
                if (shouldCache && rawJson is not null && messages.Count > 0)
                {
                    var oldestTimestamp = messages[0].GetProperty("timestamp").GetInt64();
                    var oldestDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(oldestTimestamp).UtcDateTime);
                    var cacheKey = $"{streamDir}/{CacheFileNaming.GenerateWeeklyFileName(oldestDate, "json", generatedFiles)}";
                    generatedFiles.Add(Path.GetFileName(cacheKey));
                    using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache!.PutAsync("zulip", cacheKey, cacheStream, ct);
                }

                if (options.CacheMode != CacheMode.WriteOnly)
                {
                    foreach (var msgJson in messages)
                    {
                        var result = ProcessMessage(msgJson, stream.Name, stream.Id, connection, ingestionOptions.Verbose);
                        itemsProcessed++;

                        if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                            logger?.LogInformation("Zulip download progress: {Count} messages processed", itemsProcessed);

                        switch (result.Outcome)
                        {
                            case ProcessOutcome.New:
                                itemsNew++;
                                streamMessageCount++;
                                newAndUpdated.Add(result.Item!);
                                break;
                            case ProcessOutcome.Updated:
                                itemsUpdated++;
                                newAndUpdated.Add(result.Item!);
                                break;
                            case ProcessOutcome.Failed:
                                itemsFailed++;
                                errors.Add(result.Error!);
                                break;
                        }
                    }
                }

                if (messages.Count > 0)
                {
                    anchor = messages[^1].GetProperty("id").GetInt32() + 1;
                }
                else
                {
                    hasMore = false;
                }
            }

            // Update stream message count and sync state
            if (options.CacheMode != CacheMode.WriteOnly)
            {
                stream.MessageCount += streamMessageCount;
                stream.LastFetchedAt = DateTimeOffset.UtcNow;
                ZulipStreamRecord.Update(connection, stream);

                if (anchor > 0)
                    UpdateSyncCursor(connection, stream.Name, (anchor - 1).ToString());
            }

            // Write stream metadata
            if (shouldCache)
            {
                var zulipCacheRoot = Path.Combine(cache!.RootPath, "zulip");
                await CacheMetadataService.WriteMetadataAsync(
                    zulipCacheRoot,
                    $"_meta_s{stream.ZulipStreamId}.json",
                    new ZulipStreamCacheMetadata
                    {
                        StreamId = stream.ZulipStreamId,
                        StreamName = stream.Name,
                        LastSyncDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                        LastSyncTimestamp = DateTimeOffset.UtcNow,
                        InitialDownloadComplete = true,
                    }, ct);
            }

            logger?.LogInformation("Zulip stream '{StreamName}': {Count} messages downloaded", stream.Name, streamMessageCount);
        }

        logger?.LogInformation("Zulip full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        List<ZulipStreamRecord> streams;
        try
        {
            streams = await FetchStreamsAsync(ct);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch streams");
            errors.Add(new IngestionError("streams", $"Failed to fetch streams: {ex.Message}", ex));
            return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
        }

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var connection = db.OpenConnection();

        foreach (var stream in streams)
        {
            if (ct.IsCancellationRequested) break;

            var syncState = SyncStateRecord.SelectSingle(connection, SourceName: "zulip", SubSource: stream.Name);
            int anchor = 0;

            if (syncState?.LastCursor is not null && int.TryParse(syncState.LastCursor, out var lastId))
            {
                anchor = lastId + 1;
            }

            logger?.LogInformation("Incremental fetch for {Stream} from anchor {Anchor}", stream.Name, anchor);

            var streamDir = $"s{stream.ZulipStreamId}";
            var generatedFiles = shouldCache ? cache!.EnumerateKeys("zulip", streamDir).ToList() : new List<string>();

            bool hasMore = true;
            while (hasMore && !ct.IsCancellationRequested)
            {
                List<JsonElement> messages;
                string? rawJson = null;
                try
                {
                    if (shouldCache)
                    {
                        var response = await HttpRetryHelper.GetWithRetryAsync(
                            httpClient,
                            $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString($"[{{\"operator\":\"stream\",\"operand\":{stream.ZulipStreamId}}}]")}&anchor={anchor}&num_before=0&num_after={options.BatchSize}",
                            ct, sourceName: "zulip");
                        response.EnsureSuccessStatusCode();
                        rawJson = await response.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(rawJson);
                        var root = doc.RootElement;
                        var found = root.TryGetProperty("found_newest", out var foundNewest) && foundNewest.GetBoolean();
                        messages = [];
                        foreach (var msg in root.GetProperty("messages").EnumerateArray())
                            messages.Add(msg.Clone());
                        hasMore = !found && messages.Count > 0;
                    }
                    else
                    {
                        (messages, hasMore) = await FetchMessagesAsync(stream.ZulipStreamId, anchor, options.BatchSize, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed incremental fetch for {Stream}", stream.Name);
                    errors.Add(new IngestionError($"stream:{stream.Name}", ex.Message, ex));
                    break;
                }

                // Incremental uses DayOf
                if (shouldCache && rawJson is not null && messages.Count > 0)
                {
                    var cacheKey = $"{streamDir}/{CacheFileNaming.GenerateDailyFileName(today, "json", generatedFiles)}";
                    generatedFiles.Add(Path.GetFileName(cacheKey));
                    using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawJson));
                    await cache!.PutAsync("zulip", cacheKey, cacheStream, ct);
                }

                if (options.CacheMode != CacheMode.WriteOnly)
                {
                    foreach (var msgJson in messages)
                    {
                        var result = ProcessMessage(msgJson, stream.Name, stream.Id, connection, ingestionOptions.Verbose);
                        itemsProcessed++;

                        if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                            logger?.LogInformation("Zulip incremental progress: {Count} messages processed", itemsProcessed);

                        switch (result.Outcome)
                        {
                            case ProcessOutcome.New:
                                itemsNew++;
                                newAndUpdated.Add(result.Item!);
                                break;
                            case ProcessOutcome.Updated:
                                itemsUpdated++;
                                newAndUpdated.Add(result.Item!);
                                break;
                            case ProcessOutcome.Failed:
                                itemsFailed++;
                                errors.Add(result.Error!);
                                break;
                        }
                    }
                }

                if (messages.Count > 0)
                {
                    anchor = messages[^1].GetProperty("id").GetInt32() + 1;
                }
                else
                {
                    hasMore = false;
                }
            }

            // Update sync cursor
            if (options.CacheMode != CacheMode.WriteOnly && anchor > 0)
            {
                UpdateSyncCursor(connection, stream.Name, (anchor - 1).ToString());
            }

            // Write stream metadata
            if (shouldCache)
            {
                var zulipCacheRoot = Path.Combine(cache!.RootPath, "zulip");
                await CacheMetadataService.WriteMetadataAsync(
                    zulipCacheRoot,
                    $"_meta_s{stream.ZulipStreamId}.json",
                    new ZulipStreamCacheMetadata
                    {
                        StreamId = stream.ZulipStreamId,
                        StreamName = stream.Name,
                        LastSyncDate = today.ToString("yyyy-MM-dd"),
                        LastSyncTimestamp = DateTimeOffset.UtcNow,
                        InitialDownloadComplete = true,
                    }, ct);
            }
        }

        logger?.LogInformation("Zulip incremental download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    private async Task<IngestionResult> LoadFromCacheAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        var cache = options.Cache ?? throw new InvalidOperationException("Cache is required for CacheOnly mode.");

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();
        using var connection = db.OpenConnection();

        // Discover stream directories (s{digits}) under the zulip source
        var zulipDir = Path.Combine(cache.RootPath, "zulip");
        if (!Directory.Exists(zulipDir))
        {
            logger?.LogWarning("No zulip cache directory found at {Path}", zulipDir);
            return BuildResult(startedAt, 0, 0, 0, 0, errors, newAndUpdated);
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
            var zulipCacheRoot = Path.Combine(cache.RootPath, "zulip");
            var meta = CacheMetadataService.ReadMetadata<ZulipStreamCacheMetadata>(zulipCacheRoot, $"_meta_s{streamId}.json");
            var streamName = meta?.StreamName ?? $"stream-{streamId}";

            // Upsert stream record
            var existingStream = ZulipStreamRecord.SelectSingle(connection, ZulipStreamId: streamId);
            var streamRecord = existingStream ?? new ZulipStreamRecord
            {
                Id = ZulipStreamRecord.GetIndex(),
                ZulipStreamId = streamId,
                Name = streamName,
                Description = null,
                IsWebPublic = true,
                MessageCount = 0,
                LastFetchedAt = DateTimeOffset.MinValue,
            };
            if (existingStream is null)
                ZulipStreamRecord.Insert(connection, streamRecord, ignoreDuplicates: true);

            var keys = cache.EnumerateKeys("zulip", streamDirName);

            foreach (var key in keys)
            {
                if (ct.IsCancellationRequested) break;

                if (!cache.TryGet("zulip", key, out var stream))
                    continue;

                using (stream)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(stream);
                        var messagesArray = doc.RootElement.GetProperty("messages");

                        foreach (var msgJson in messagesArray.EnumerateArray())
                        {
                            var result = ProcessMessage(msgJson, streamName, streamRecord.Id, connection, false);
                            itemsProcessed++;

                            switch (result.Outcome)
                            {
                                case ProcessOutcome.New:
                                    itemsNew++;
                                    newAndUpdated.Add(result.Item!);
                                    break;
                                case ProcessOutcome.Updated:
                                    itemsUpdated++;
                                    newAndUpdated.Add(result.Item!);
                                    break;
                                case ProcessOutcome.Failed:
                                    itemsFailed++;
                                    errors.Add(result.Error!);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to process cached file {Key}", key);
                        itemsFailed++;
                        errors.Add(new IngestionError(key, $"Failed to process cached file: {ex.Message}", ex));
                    }
                }

                if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                    logger?.LogInformation("Zulip cache ingestion progress: {Count} messages processed", itemsProcessed);
            }

            logger?.LogInformation("Zulip cache-only stream '{StreamName}': processed from cache", streamName);
        }

        logger?.LogInformation("Zulip cache-only ingestion complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        // Parse identifier as "stream:topic"
        var separatorIndex = identifier.IndexOf(':');
        if (separatorIndex < 0)
        {
            errors.Add(new IngestionError(identifier, "Identifier must be in 'stream:topic' format"));
            return BuildResult(startedAt, 0, 0, 0, 1, errors, newAndUpdated);
        }

        var streamName = identifier[..separatorIndex];
        var topic = identifier[(separatorIndex + 1)..];

        try
        {
            var narrow = $"[{{\"operator\":\"stream\",\"operand\":\"{EscapeJsonString(streamName)}\"}},{{\"operator\":\"topic\",\"operand\":\"{EscapeJsonString(topic)}\"}}]";
            var url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}&anchor=oldest&num_before=0&num_after={options.BatchSize}";

            var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "zulip");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            using var connection = db.OpenConnection();

            var messages = doc.RootElement.GetProperty("messages");
            foreach (var msgJson in messages.EnumerateArray())
            {
                var result = ProcessMessage(msgJson, streamName, 0, connection, ingestionOptions.Verbose);

                switch (result.Outcome)
                {
                    case ProcessOutcome.New:
                        itemsNew++;
                        newAndUpdated.Add(result.Item!);
                        break;
                    case ProcessOutcome.Updated:
                        itemsUpdated++;
                        newAndUpdated.Add(result.Item!);
                        break;
                    case ProcessOutcome.Failed:
                        itemsFailed++;
                        errors.Add(result.Error!);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to ingest topic {Identifier}", identifier);
            itemsFailed++;
            errors.Add(new IngestionError(identifier, $"Failed to ingest topic: {ex.Message}", ex));
        }

        return BuildResult(startedAt, itemsNew + itemsUpdated + itemsFailed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    private async Task<List<ZulipStreamRecord>> FetchStreamsAsync(CancellationToken ct)
    {
        var url = $"{options.BaseUrl}/api/v1/streams";
        var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "zulip");
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

        logger?.LogInformation("Found {Count} streams (web_public filter: {Filter})", streams.Count, options.OnlyWebPublic);
        return streams;
    }

    private async Task<(List<JsonElement> Messages, bool HasMore)> FetchMessagesAsync(
        int streamId, int anchor, int batchSize, CancellationToken ct)
    {
        var narrow = $"[{{\"operator\":\"stream\",\"operand\":{streamId}}}]";
        var url = $"{options.BaseUrl}/api/v1/messages?narrow={Uri.EscapeDataString(narrow)}" +
                  $"&anchor={anchor}&num_before=0&num_after={batchSize}";

        var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "zulip");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        var found = root.TryGetProperty("found_newest", out var foundNewest) && foundNewest.GetBoolean();

        var messages = new List<JsonElement>();
        foreach (var msg in root.GetProperty("messages").EnumerateArray())
        {
            messages.Add(msg.Clone());
        }

        return (messages, !found && messages.Count > 0);
    }

    private ProcessResult ProcessMessage(JsonElement msgJson, string streamName, int streamDbId, Microsoft.Data.Sqlite.SqliteConnection connection, bool verbose)
    {
        int msgId = 0;
        try
        {
            var record = ZulipMessageMapper.MapMessage(msgJson, streamName, streamDbId);
            msgId = record.ZulipMessageId;

            var existing = ZulipMessageRecord.SelectSingle(connection, ZulipMessageId: record.ZulipMessageId);
            bool isNew;

            if (existing is not null)
            {
                record.Id = existing.Id;
                ZulipMessageRecord.Update(connection, record);
                isNew = false;
            }
            else
            {
                ZulipMessageRecord.Insert(connection, record, ignoreDuplicates: true);
                isNew = true;
            }

            if (verbose)
            {
                logger?.LogDebug("{Action} message {Id} in {Stream}/{Topic}",
                    isNew ? "Inserted" : "Updated", msgId, streamName, record.Topic);
            }

            var item = new IngestedItem
            {
                SourceType = SourceName,
                SourceId = msgId.ToString(),
                Title = $"{streamName} > {record.Topic}",
                SearchableTextFields = [record.ContentPlain, record.Topic, record.SenderName],
            };

            return new ProcessResult(isNew ? ProcessOutcome.New : ProcessOutcome.Updated, item, null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to process message {Id}", msgId);
            return new ProcessResult(ProcessOutcome.Failed, null, new IngestionError(msgId.ToString(), ex.Message, ex));
        }
    }

    private static void UpdateSyncCursor(Microsoft.Data.Sqlite.SqliteConnection connection, string streamName, string cursor)
    {
        var existing = SyncStateRecord.SelectSingle(connection, SourceName: "zulip", SubSource: streamName);
        if (existing is not null)
        {
            existing.LastCursor = cursor;
            existing.LastSyncAt = DateTimeOffset.UtcNow;
            SyncStateRecord.Update(connection, existing);
        }
        else
        {
            SyncStateRecord.Insert(connection, new SyncStateRecord
            {
                Id = SyncStateRecord.GetIndex(),
                SourceName = "zulip",
                SubSource = streamName,
                LastSyncAt = DateTimeOffset.UtcNow,
                LastCursor = cursor,
                ItemsIngested = 0,
                SyncSchedule = null,
                NextScheduledAt = null,
                Status = "completed",
                LastError = null,
            });
        }
    }

    private static IngestionResult BuildResult(
        DateTimeOffset startedAt, int processed, int newCount, int updated, int failed,
        List<IngestionError> errors, List<IngestedItem> newAndUpdated) => new()
    {
        ItemsProcessed = processed,
        ItemsNew = newCount,
        ItemsUpdated = updated,
        ItemsFailed = failed,
        Errors = errors,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        NewAndUpdatedItems = newAndUpdated,
    };

    private static string EscapeJsonString(string value)
    {
        // JsonSerializer.Serialize wraps in quotes; strip them for embedding inside JSON
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error);
}
