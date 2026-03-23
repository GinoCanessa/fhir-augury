using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// </summary>
public class JiraIngestionPipeline(
    JiraSource source,
    JiraDatabase database,
    JiraIndexer indexer,
    IOptions<JiraServiceOptions> optionsAccessor,
    ILogger<JiraIngestionPipeline> logger) : IIngestionPipeline
{
    private readonly JiraServiceOptions _options = optionsAccessor.Value;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";

    public bool IsRunning => _runLock.CurrentCount == 0;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the Jira API.</summary>
    public async Task<IngestionResult> RunFullIngestionAsync(string? jqlOverride = null, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_full";

        try
        {
            logger.LogInformation("Starting full ingestion");

            var result = await source.DownloadAllAsync(jqlOverride, ct);
            PostIngestion(result, "full", ct);

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

    /// <summary>Runs an incremental ingestion from the Jira API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";

        try
        {
            var since = GetLastSyncTime();
            logger.LogInformation("Starting incremental ingestion since {Since}", since);

            var result = await source.DownloadIncrementalAsync(since, ct);
            PostIngestion(result, "incremental", ct);

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

    // Explicit interface implementation for IIngestionPipeline
    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
    {
        await RunIncrementalIngestionAsync(ct);
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
            database.ResetDatabase(ct);

            var result = await source.LoadFromCacheAsync(ct);
            PostIngestion(result, "rebuild", ct);

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

        // Rebuild BM25 keyword index
        logger.LogInformation("Rebuilding BM25 index");
        indexer.RebuildFullIndex(ct);

        // Update sync state
        UpdateSyncState(result, runType, ct);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = database.OpenConnection();

        var existing = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName, SubSource: runType);

        var syncState = new JiraSyncStateRecord
        {
            Id = existing?.Id ?? JiraSyncStateRecord.GetIndex(),
            SourceName = JiraSource.SourceName,
            SubSource = runType,
            LastSyncAt = result.CompletedAt,
            LastCursor = null,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = _options.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(_options.SyncSchedule)),
            Status = result.Errors.Count == 0 ? "success" : "completed_with_errors",
            LastError = result.Errors.Count > 0 ? result.Errors[^1] : null,
        };

        if (existing is not null)
            JiraSyncStateRecord.Update(connection, syncState);
        else
            JiraSyncStateRecord.Insert(connection, syncState);
    }

    private DateTimeOffset GetLastSyncTime()
    {
        using var connection = database.OpenConnection();
        var state = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);
        return state?.LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-30);
    }
}
