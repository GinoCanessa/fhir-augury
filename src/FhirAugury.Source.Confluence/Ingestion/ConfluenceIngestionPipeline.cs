using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// </summary>
public class ConfluenceIngestionPipeline(
    ConfluenceSource source,
    ConfluenceDatabase database,
    ConfluenceIndexer indexer,
    ConfluenceXRefRebuilder xrefRebuilder,
    FhirAugury.Common.Indexing.IIndexTracker tracker,
    IHttpClientFactory httpClientFactory,
    IOptions<ConfluenceServiceOptions> optionsAccessor,
    ILogger<ConfluenceIngestionPipeline> logger)
    : IIngestionPipeline
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";

    public bool IsRunning => _runLock.CurrentCount == 0;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the Confluence API.</summary>
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

    /// <summary>Runs an incremental ingestion from the Confluence API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";

        try
        {
            DateTimeOffset since = GetLastSyncTime();
            logger.LogInformation("Starting incremental ingestion since {Since}", since);

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            IngestionResult downloadResult = await source.DownloadIncrementalAsync(since, ct);
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

    private void PostIngestion(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

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

        logger.LogInformation("Rebuilding cross-reference index");
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

        UpdateSyncState(result, runType, ct);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        ConfluenceSyncStateRecord? existing = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName, SubSource: runType);

        ConfluenceSyncStateRecord syncState = new ConfluenceSyncStateRecord
        {
            Id = existing?.Id ?? ConfluenceSyncStateRecord.GetIndex(),
            SourceName = ConfluenceSource.SourceName,
            SubSource = runType,
            LastSyncAt = result.CompletedAt,
            LastCursor = null,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = optionsAccessor.Value.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(optionsAccessor.Value.SyncSchedule)),
            Status = result.Errors.Count == 0 ? "success" : "completed_with_errors",
            LastError = result.Errors.Count > 0 ? result.Errors[^1] : null,
        };

        if (existing is not null)
            ConfluenceSyncStateRecord.Update(connection, syncState);
        else
            ConfluenceSyncStateRecord.Insert(connection, syncState);
    }

    private DateTimeOffset GetLastSyncTime()
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluenceSyncStateRecord? state = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);
        return state?.LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-30);
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluenceSyncStateRecord? state = ConfluenceSyncStateRecord.SelectSingle(connection, SourceName: ConfluenceSource.SourceName);
        return state?.LastSyncAt;
    }

    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
        => await RunIncrementalIngestionAsync(ct);

    private async Task<IngestionResult?> LoadCacheIfDatabaseEmptyAsync(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();
        int spaceCount = ConfluenceSpaceRecord.SelectCount(connection);

        if (spaceCount > 0)
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

    private async Task NotifyOrchestratorAsync(IngestionResult result, string runType)
    {
        if (string.IsNullOrWhiteSpace(optionsAccessor.Value.OrchestratorAddress)) return;

        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            await client.PostAsJsonAsync("/api/v1/notify-ingestion", new
            {
                source = ConfluenceSource.SourceName,
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
}
