using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.Zulip;

/// <summary>Zulip data source implementing IDataSource for FHIR chat messages.</summary>
public class ZulipSource(ZulipSourceOptions options, HttpClient httpClient, ILogger<ZulipSource>? logger = null) : IDataSource
{
    public string SourceName => "zulip";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        // Fetch all streams
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

            int anchor = 0;
            bool hasMore = true;
            int streamMessageCount = 0;

            while (hasMore && !ct.IsCancellationRequested)
            {
                List<JsonElement> messages;
                try
                {
                    (messages, hasMore) = await FetchMessagesAsync(stream.ZulipStreamId, anchor, options.BatchSize, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to fetch messages for stream {Stream} at anchor {Anchor}", stream.Name, anchor);
                    errors.Add(new IngestionError($"stream:{stream.Name}:anchor:{anchor}", ex.Message, ex));
                    break;
                }

                foreach (var msgJson in messages)
                {
                    var result = ProcessMessage(msgJson, stream.Name, stream.Id, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

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
            stream.MessageCount += streamMessageCount;
            stream.LastFetchedAt = DateTimeOffset.UtcNow;
            ZulipStreamRecord.Update(connection, stream);

            // Track highest message ID per stream in sync_state
            if (anchor > 0)
            {
                UpdateSyncCursor(connection, stream.Name, (anchor - 1).ToString());
            }
        }

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
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

        using var connection = db.OpenConnection();

        foreach (var stream in streams)
        {
            if (ct.IsCancellationRequested) break;

            // Read last cursor from sync_state
            var syncState = SyncStateRecord.SelectSingle(connection, SourceName: "zulip", SubSource: stream.Name);
            int anchor = 0;

            if (syncState?.LastCursor is not null && int.TryParse(syncState.LastCursor, out var lastId))
            {
                anchor = lastId + 1;
            }

            logger?.LogInformation("Incremental fetch for {Stream} from anchor {Anchor}", stream.Name, anchor);

            bool hasMore = true;
            while (hasMore && !ct.IsCancellationRequested)
            {
                List<JsonElement> messages;
                try
                {
                    (messages, hasMore) = await FetchMessagesAsync(stream.ZulipStreamId, anchor, options.BatchSize, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed incremental fetch for {Stream}", stream.Name);
                    errors.Add(new IngestionError($"stream:{stream.Name}", ex.Message, ex));
                    break;
                }

                foreach (var msgJson in messages)
                {
                    var result = ProcessMessage(msgJson, stream.Name, stream.Id, connection, ingestionOptions.Verbose);
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
            if (anchor > 0)
            {
                UpdateSyncCursor(connection, stream.Name, (anchor - 1).ToString());
            }
        }

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

            var response = await httpClient.GetAsync(url, ct);
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
        var response = await httpClient.GetAsync(url, ct);
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

        var response = await httpClient.GetAsync(url, ct);
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

    private static string EscapeJsonString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error);
}
