using FhirAugury.Common.Caching;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Zulip.Cache;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// </summary>
public class ZulipIngestionPipeline(
    ZulipSource source,
    ZulipDatabase database,
    ZulipIndexer indexer,
    ZulipXRefRebuilder xrefRebuilder,
    IHttpClientFactory httpClientFactory,
    IOptions<ZulipServiceOptions> optionsAccessor,
    FhirAugury.Common.Indexing.IIndexTracker tracker,
    IResponseCache cache,
    ILogger<ZulipIngestionPipeline> logger) : IIngestionPipeline
{
    private readonly ZulipServiceOptions _options = optionsAccessor.Value;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";
    private int _migratorRan;

    public bool IsRunning => _runLock.CurrentCount == 0;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the Zulip API.</summary>
    public async Task<IngestionResult> RunFullIngestionAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_full";

        try
        {
            logger.LogInformation("Starting full ingestion");

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            await RunCacheFileNameMigratorOnceAsync(ct);
            IngestionResult downloadResult = await source.DownloadAllAsync(ct);
            IngestionResult result = MergeResults(cacheResult, downloadResult);
            PostIngestion(result, "full", ct);
            await NotifyOrchestratorAsync(result, "full");

            _currentStatus = "idle";
            return result;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Full ingestion failed");
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Runs an incremental ingestion from the Zulip API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";

        try
        {
            logger.LogInformation("Starting incremental ingestion");

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            await RunCacheFileNameMigratorOnceAsync(ct);
            IngestionResult downloadResult = await source.DownloadIncrementalAsync(ct);
            IngestionResult result = MergeResults(cacheResult, downloadResult);
            PostIngestion(result, "incremental", ct);
            await NotifyOrchestratorAsync(result, "incremental");

            _currentStatus = "idle";
            return result;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Incremental ingestion failed");
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Runs an incremental ingestion from the Zulip API.</summary>
    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
        => await RunIncrementalIngestionAsync(ct);

    /// <summary>Rebuilds the database entirely from cached responses.</summary>
    public async Task<IngestionResult> RebuildFromCacheAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "rebuilding";

        try
        {
            logger.LogInformation("Rebuilding database from cache");
            database.ResetDatabase();

            await RunCacheFileNameMigratorOnceAsync(ct);

            IngestionResult result = await source.LoadFromCacheAsync(ct);
            PostIngestion(result, "rebuild", ct);
            await NotifyOrchestratorAsync(result, "rebuild");

            _currentStatus = "idle";
            return result;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Rebuild from cache failed");
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunCacheFileNameMigratorOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _migratorRan, 1, 0) != 0)
            return;
        await CacheFileNameMigrator.MigrateAsync(cache, ZulipCacheLayout.SourceName, logger, ct);
    }

    /// <summary>
    /// Loads cached data into the database if it is empty, setting sync cursors
    /// so subsequent downloads only fetch new data.
    /// </summary>
    private async Task<IngestionResult?> LoadCacheIfDatabaseEmptyAsync(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();
        int streamCount = ZulipStreamRecord.SelectCount(connection);

        if (streamCount > 0)
            return null;

        logger.LogInformation("Database is empty; loading local cache before downloading");
        IngestionResult cacheResult = await source.LoadFromCacheAsync(ct);

        if (cacheResult.ItemsProcessed > 0)
            logger.LogInformation("Pre-loaded {Count} items from cache ({New} new)",
                cacheResult.ItemsProcessed, cacheResult.ItemsNew);
        else
            logger.LogInformation("No cached data found to pre-load");

        return cacheResult;
    }

    private static IngestionResult MergeResults(IngestionResult? first, IngestionResult second)
    {
        if (first is null)
            return second;

        return new IngestionResult(
            first.ItemsProcessed + second.ItemsProcessed,
            first.ItemsNew + second.ItemsNew,
            first.ItemsUpdated + second.ItemsUpdated,
            first.ItemsFailed + second.ItemsFailed,
            [.. first.Errors, .. second.Errors],
            first.StartedAt)
        {
            NewMessageIds = [.. first.NewMessageIds, .. second.NewMessageIds]
        };
    }

    private void PostIngestion(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        const int IncrementalThreshold = 5000;
        bool useIncremental = runType == "incremental"
            && result.NewMessageIds.Count > 0
            && result.NewMessageIds.Count < IncrementalThreshold;

        // BM25 keyword index
        logger.LogInformation(useIncremental
            ? "Updating BM25 index incrementally ({Count} new messages)"
            : "Rebuilding BM25 index",
            result.NewMessageIds.Count);
        tracker.MarkStarted("bm25");
        try
        {
            if (useIncremental)
            {
                List<IndexContent> items = BuildIndexContent(result.NewMessageIds);
                indexer.UpdateIndex(items, ct);
            }
            else
            {
                indexer.RebuildFullIndex(ct);
            }
            tracker.MarkCompleted("bm25");
        }
        catch (Exception ex)
        {
            tracker.MarkFailed("bm25", ex.Message);
            throw;
        }

        // Cross-references
        logger.LogInformation(useIncremental
            ? "Indexing ticket references incrementally ({Count} new messages)"
            : "Indexing ticket references",
            result.NewMessageIds.Count);
        tracker.MarkStarted("cross-refs");
        try
        {
            if (useIncremental)
                xrefRebuilder.IndexNewMessages(result.NewMessageIds, ct);
            else
                xrefRebuilder.RebuildAll(ct);
            tracker.MarkCompleted("cross-refs");
        }
        catch (Exception ex)
        {
            tracker.MarkFailed("cross-refs", ex.Message);
            throw;
        }

        // Update sync state
        UpdateSyncState(result, runType, ct);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    /// <summary>
    /// Converts Zulip message IDs into IndexContent items by querying the DB.
    /// </summary>
    private List<IndexContent> BuildIndexContent(IReadOnlyList<int> zulipMessageIds)
    {
        List<IndexContent> items = [];
        if (zulipMessageIds.Count == 0) return items;

        using SqliteConnection connection = database.OpenConnection();

        int[] idArray = [.. zulipMessageIds];
        List<ZulipMessageRecord> msgs =
            ZulipMessageRecord.SelectList(connection, ZulipMessageIdValues: idArray);

        Dictionary<int, ZulipMessageRecord> byId = msgs.ToDictionary(m => m.ZulipMessageId);

        foreach (int msgId in zulipMessageIds)
        {
            if (!byId.TryGetValue(msgId, out ZulipMessageRecord? msg)) continue;

            string text = string.Join(" ",
                new[] { msg.ContentPlain, msg.Topic, msg.SenderName }
                    .Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrWhiteSpace(text))
            {
                items.Add(new IndexContent
                {
                    ContentType = ContentTypes.Message,
                    SourceId = msg.ZulipMessageId.ToString(),
                    Text = text
                });
            }
        }
        return items;
    }

    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Compute global max cursor from per-stream sync states
        string? globalCursor = null;
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT MAX(CAST(LastCursor AS INTEGER)) FROM sync_state " +
                "WHERE SourceName = @source AND SubSource NOT IN ('full', 'incremental', 'rebuild')";
            cmd.Parameters.AddWithValue("@source", ZulipSource.SourceName);
            object? maxResult = cmd.ExecuteScalar();
            if (maxResult is long maxId && maxId > 0)
                globalCursor = maxId.ToString();
        }

        ZulipSyncStateRecord? existing = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName, SubSource: runType);

        ZulipSyncStateRecord syncState = new ZulipSyncStateRecord
        {
            Id = existing?.Id ?? ZulipSyncStateRecord.GetIndex(),
            SourceName = ZulipSource.SourceName,
            SubSource = runType,
            LastSyncAt = result.CompletedAt,
            LastCursor = globalCursor,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = _options.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(_options.SyncSchedule)),
            Status = result.Errors.Count == 0 ? "success" : "completed_with_errors",
            LastError = result.Errors.Count > 0 ? result.Errors[^1] : null,
        };

        if (existing is not null)
            ZulipSyncStateRecord.Update(connection, syncState);
        else
            ZulipSyncStateRecord.Insert(connection, syncState);
    }

    private async Task NotifyOrchestratorAsync(IngestionResult result, string runType)
    {
        if (string.IsNullOrWhiteSpace(_options.OrchestratorAddress)) return;

        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            await client.PostAsJsonAsync("/api/v1/notify-ingestion", new
            {
                source = ZulipSource.SourceName,
                type = runType,
                itemsIngested = result.ItemsProcessed,
                completedAt = result.CompletedAt,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify orchestrator of ingestion completion");
        }
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        ZulipSyncStateRecord? state = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName);
        return state?.LastSyncAt;
    }
}
