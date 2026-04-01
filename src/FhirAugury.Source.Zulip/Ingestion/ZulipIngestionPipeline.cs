using Fhiraugury;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Indexing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// </summary>
public class ZulipIngestionPipeline(
    ZulipSource source,
    ZulipDatabase database,
    ZulipIndexer indexer,
    ZulipXRefRebuilder xrefRebuilder,
    OrchestratorService.OrchestratorServiceClient? orchestratorClient,
    IOptions<ZulipServiceOptions> optionsAccessor,
    FhirAugury.Common.Indexing.IIndexTracker tracker,
    ILogger<ZulipIngestionPipeline> logger) : IIngestionPipeline
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";

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
            first.StartedAt);
    }

    private void PostIngestion(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Rebuild BM25 keyword index
        logger.LogInformation("Rebuilding BM25 index");
        tracker.MarkStarted("bm25");
        try
        {
            indexer.RebuildFullIndex(ct);
            tracker.MarkCompleted("bm25");
        }
        catch (Exception ex)
        {
            tracker.MarkFailed("bm25", ex.Message);
            throw;
        }

        // Extract and index ticket references
        logger.LogInformation("Indexing ticket references");
        tracker.MarkStarted("cross-refs");
        try
        {
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

    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        ZulipSyncStateRecord? existing = ZulipSyncStateRecord.SelectSingle(connection, SourceName: ZulipSource.SourceName, SubSource: runType);

        ZulipServiceOptions options = optionsAccessor.Value;
        ZulipSyncStateRecord syncState = new ZulipSyncStateRecord
        {
            Id = existing?.Id ?? ZulipSyncStateRecord.GetIndex(),
            SourceName = ZulipSource.SourceName,
            SubSource = runType,
            LastSyncAt = result.CompletedAt,
            LastCursor = null,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = options.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(options.SyncSchedule)),
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
        if (orchestratorClient is null) return;

        try
        {
            await orchestratorClient.NotifyIngestionCompleteAsync(new IngestionCompleteNotification
            {
                Source = ZulipSource.SourceName,
                Type = runType,
                ItemsIngested = result.ItemsProcessed,
                CompletedAt = Timestamp.FromDateTimeOffset(result.CompletedAt),
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
