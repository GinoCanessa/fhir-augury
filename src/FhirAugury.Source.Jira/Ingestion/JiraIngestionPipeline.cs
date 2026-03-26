using Fhiraugury;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
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
    JiraIndexBuilder indexBuilder,
    JiraXRefRebuilder xrefRebuilder,
    OrchestratorService.OrchestratorServiceClient? orchestratorClient,
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

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            IngestionResult downloadResult = await source.DownloadAllAsync(jqlOverride, ct);
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

    /// <summary>Runs an incremental ingestion from the Jira API.</summary>
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
        int issueCount = JiraIssueRecord.SelectCount(connection);

        if (issueCount > 0)
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

        // Rebuild lookup/index tables
        using (SqliteConnection conn = database.OpenConnection())
        {
            indexBuilder.RebuildIndexTables(conn);
        }

        // Extract cross-references
        logger.LogInformation("Rebuilding cross-references");
        xrefRebuilder.ExtractAll(ct);

        // Rebuild BM25 keyword index
        logger.LogInformation("Rebuilding BM25 index");
        indexer.RebuildFullIndex(ct);

        // Update sync state
        UpdateSyncState(result, runType, ct);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private async Task NotifyOrchestratorAsync(IngestionResult result, string runType)
    {
        if (orchestratorClient is null) return;

        try
        {
            await orchestratorClient.NotifyIngestionCompleteAsync(new IngestionCompleteNotification
            {
                Source = JiraSource.SourceName,
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

    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using SqliteConnection connection = database.OpenConnection();

        JiraSyncStateRecord? existing = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName, SubSource: runType);

        JiraSyncStateRecord syncState = new JiraSyncStateRecord
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
        using SqliteConnection connection = database.OpenConnection();
        JiraSyncStateRecord? state = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);

        if (state is not null)
            return state.LastSyncAt;

        // No DB record — check cache for the latest cached date and resume from the day after
        HashSet<DateOnly> cachedDates = source.GetCachedDates();
        if (cachedDates.Count > 0)
        {
            DateOnly latestCached = cachedDates.Max();
            DateOnly resumeDate = latestCached.AddDays(1);
            logger.LogInformation(
                "No sync state in database; latest cache file is {LatestCached}, resuming from {ResumeDate}",
                latestCached, resumeDate);
            return new DateTimeOffset(resumeDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        // No cache either — default to last 30 days
        logger.LogInformation("No sync state or cache files found; defaulting to last 30 days");
        return DateTimeOffset.UtcNow.AddDays(-30);
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        JiraSyncStateRecord? state = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName);
        return state?.LastSyncAt;
    }
}
