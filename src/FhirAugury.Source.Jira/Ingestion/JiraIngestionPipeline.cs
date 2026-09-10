using FhirAugury.Common.Caching;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

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
    IHttpClientFactory httpClientFactory,
    IOptions<JiraServiceOptions> optionsAccessor,
    IIndexTracker tracker,
    IResponseCache cache,
    WorkGroupSupportFileAcquirer workGroupSupportFileAcquirer,
    Hl7WorkGroupIndexer hl7WorkGroupIndexer,
    ILogger<JiraIngestionPipeline> logger) : IIngestionPipeline
{
    private readonly JiraServiceOptions _options = optionsAccessor.Value;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";
    private int _migratorRan;

    public bool IsRunning => _runLock.CurrentCount == 0;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the Jira API.</summary>
    public async Task<IngestionResult> RunFullIngestionAsync(string? jqlOverride = null, string? project = null, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_full";
        bool mutationStarted = false;

        try
        {
            database.BeginContentMutation(ct);
            mutationStarted = true;
            logger.LogInformation("Starting full ingestion");

            string? wgXmlPath = await workGroupSupportFileAcquirer.EnsureAsync(ct).ConfigureAwait(false);
            if (wgXmlPath is not null)
                hl7WorkGroupIndexer.Rebuild(wgXmlPath, ct);

            // Pre-seed from cache if DB is empty (load ALL projects at once)
            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            ct.ThrowIfCancellationRequested();

            List<JiraProjectConfig> projects = project is not null
                ? [new JiraProjectConfig { Key = project }]
                : _options.GetEffectiveProjects();

            // Ensure jira_projects rows exist for downstream rank joins.
            source.UpsertProjectsFromConfig();

            IngestionResult combined = cacheResult ?? new IngestionResult(0, 0, 0, 0, [], DateTimeOffset.UtcNow);

            await RunCacheFileNameMigratorOnceAsync(ct);

            foreach (JiraProjectConfig proj in projects)
            {
                try
                {
                    string startDesc = proj.StartDate?.ToString("yyyy-MM-dd") ?? "default";
                    logger.LogInformation(
                        "Starting full ingestion for project {Project} (window={Days} days, startDate={Start})",
                        proj.Key, proj.DownloadWindowDays, startDesc);

                    string jql = jqlOverride ?? proj.Jql ?? $"project = \"{proj.Key}\"";
                    IngestionResult downloadResult = await source.DownloadAllAsync(proj, jql, ct);
                    ct.ThrowIfCancellationRequested();
                    combined = MergeResults(combined, downloadResult);

                    DateTimeOffset? watermark = UpdateSyncState(downloadResult, proj.Key, "full");
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ingestion failed for project {Project}", proj.Key);
                    IngestionResult failureResult = new IngestionResult(
                        0, 0, 0, 1, [$"Project {proj.Key}: {ex.Message}"],
                        DateTimeOffset.UtcNow);
                    combined = MergeResults(combined, failureResult);
                    DateTimeOffset? watermark = UpdateSyncState(
                        failureResult,
                        proj.Key,
                        "full",
                        ex);
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
            }

            if (ShouldRunPostIngestion(combined, projects.Count))
                PostIngestion(combined, "full", ct);
            else
                logger.LogWarning("Skipping post-ingestion: all {Count} project runs failed.", projects.Count);
            await NotifyOrchestratorAsync(combined, "full");

            _currentStatus = "idle";
            return combined;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Full ingestion failed");
            throw;
        }
        finally
        {
            try
            {
                if (mutationStarted)
                {
                    database.CompleteContentMutation(CancellationToken.None);
                }
            }
            finally
            {
                _runLock.Release();
            }
        }
    }

    /// <summary>Runs an incremental ingestion from the Jira API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(string? project = null, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";
        bool mutationStarted = false;

        try
        {
            database.BeginContentMutation(ct);
            mutationStarted = true;
            string? wgXmlPath = await workGroupSupportFileAcquirer.EnsureAsync(ct).ConfigureAwait(false);
            if (wgXmlPath is not null)
                hl7WorkGroupIndexer.Rebuild(wgXmlPath, ct);

            // Pre-seed from cache if DB is empty (load ALL projects at once)
            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            ct.ThrowIfCancellationRequested();

            List<JiraProjectConfig> projects = project is not null
                ? [new JiraProjectConfig { Key = project }]
                : _options.GetEffectiveProjects();

            // Ensure jira_projects rows exist for downstream rank joins.
            source.UpsertProjectsFromConfig();

            IngestionResult combined = cacheResult ?? new IngestionResult(0, 0, 0, 0, [], DateTimeOffset.UtcNow);

            await RunCacheFileNameMigratorOnceAsync(ct);

            foreach (JiraProjectConfig proj in projects)
            {
                try
                {
                    DateTimeOffset since = GetLastSyncTime(proj.Key);
                    string startDesc = proj.StartDate?.ToString("yyyy-MM-dd") ?? "default";
                    logger.LogInformation(
                        "Starting incremental ingestion for {Project} since {Since} (window={Days} days, startDate={Start})",
                        proj.Key, since, proj.DownloadWindowDays, startDesc);

                    IngestionResult downloadResult = await source.DownloadIncrementalAsync(
                        proj, proj.Jql, since, ct);
                    ct.ThrowIfCancellationRequested();
                    combined = MergeResults(combined, downloadResult);

                    DateTimeOffset? watermark = UpdateSyncState(downloadResult, proj.Key, "incremental");
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ingestion failed for project {Project}", proj.Key);
                    IngestionResult failureResult = new IngestionResult(
                        0, 0, 0, 1, [$"Project {proj.Key}: {ex.Message}"],
                        DateTimeOffset.UtcNow);
                    combined = MergeResults(combined, failureResult);
                    DateTimeOffset? watermark = UpdateSyncState(
                        failureResult,
                        proj.Key,
                        "incremental",
                        ex);
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
            }

            if (ShouldRunPostIngestion(combined, projects.Count))
                PostIngestion(combined, "incremental", ct);
            else
                logger.LogWarning("Skipping post-ingestion: all {Count} project runs failed.", projects.Count);
            await NotifyOrchestratorAsync(combined, "incremental");

            _currentStatus = "idle";
            return combined;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Incremental ingestion failed");
            throw;
        }
        finally
        {
            try
            {
                if (mutationStarted)
                {
                    database.CompleteContentMutation(CancellationToken.None);
                }
            }
            finally
            {
                _runLock.Release();
            }
        }
    }

    // Explicit interface implementation for IIngestionPipeline
    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
    {
        await RunIncrementalIngestionAsync(project: null, ct);
    }

    /// <summary>Rebuilds the database entirely from cached responses.</summary>
    public async Task<IngestionResult> RebuildFromCacheAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "rebuilding";
        bool mutationStarted = false;

        try
        {
            database.BeginContentMutation(ct);
            mutationStarted = true;
            logger.LogInformation("Rebuilding database from cache");

            IReadOnlyDictionary<string, DateTimeOffset?> watermarks;
            long contentRevision;
            using (SqliteConnection captureConnection = database.OpenConnection())
            {
                using SqliteCommand captureTransaction = captureConnection.CreateCommand();
                captureTransaction.CommandText = "BEGIN DEFERRED;";
                captureTransaction.ExecuteNonQuery();
                watermarks = JiraSyncStateHelper.CaptureProjectWatermarks(captureConnection);
                contentRevision = JiraDatabase.ReadSourceState(captureConnection).ContentRevision;
                captureTransaction.CommandText = "COMMIT;";
                captureTransaction.ExecuteNonQuery();
            }

            string? wgXmlPath = await workGroupSupportFileAcquirer.EnsureAsync(ct).ConfigureAwait(false);
            database.ResetDatabase(watermarks, contentRevision, ct);
            source.ClearUserCache();
            if (wgXmlPath is not null)
                hl7WorkGroupIndexer.Rebuild(wgXmlPath, ct);

            await RunCacheFileNameMigratorOnceAsync(ct);

            List<JiraProjectConfig> projects = _options.GetEffectiveProjects();

            // Ensure jira_projects rows exist for downstream rank joins.
            source.UpsertProjectsFromConfig();

            IngestionResult combined = new IngestionResult(0, 0, 0, 0, [], DateTimeOffset.UtcNow);

            foreach (JiraProjectConfig proj in projects)
            {
                try
                {
                    IngestionResult projectResult = await source.LoadFromCacheAsync(project: proj.Key, ct: ct);
                    ct.ThrowIfCancellationRequested();
                    combined = MergeResults(combined, projectResult);
                    DateTimeOffset? watermark = UpdateSyncState(projectResult, proj.Key, "rebuild");
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Rebuild failed for project {Project}", proj.Key);
                    IngestionResult failureResult = new IngestionResult(
                        0, 0, 0, 1, [$"Project {proj.Key}: {ex.Message}"],
                        DateTimeOffset.UtcNow);
                    combined = MergeResults(combined, failureResult);
                    DateTimeOffset? watermark = UpdateSyncState(
                        failureResult,
                        proj.Key,
                        "rebuild",
                        ex);
                    source.UpdateProjectCounters(proj.Key, watermark);
                }
            }

            if (ShouldRunPostIngestion(combined, projects.Count))
                PostIngestion(combined, "rebuild", ct);
            else
                logger.LogWarning("Skipping post-ingestion: all {Count} project rebuilds failed.", projects.Count);
            await NotifyOrchestratorAsync(combined, "rebuild");

            _currentStatus = "idle";
            return combined;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "Rebuild from cache failed");
            throw;
        }
        finally
        {
            try
            {
                if (mutationStarted)
                {
                    database.CompleteContentMutation(CancellationToken.None);
                }
            }
            finally
            {
                _runLock.Release();
            }
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
        // Load ALL cache data (project: null) to populate the DB fully
        IngestionResult cacheResult = await source.LoadFromCacheAsync(project: null, ct: ct);

        if (cacheResult.ItemsProcessed > 0)
            logger.LogInformation("Pre-loaded {Count} items from cache ({New} new)",
                cacheResult.ItemsProcessed, cacheResult.ItemsNew);
        else
            logger.LogInformation("No cached data found to pre-load");

        return cacheResult;
    }

    private static bool ShouldRunPostIngestion(IngestionResult result, int projectCount)
        => result.ItemsProcessed > 0 || result.ItemsFailed < projectCount;

    private static IngestionResult MergeResults(IngestionResult? first, IngestionResult? second)
    {
        if (first is null && second is null)
            return new IngestionResult(0, 0, 0, 0, [], DateTimeOffset.UtcNow);
        if (first is null)
            return second!;
        if (second is null)
            return first;

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
        tracker.MarkStarted("lookup-tables");
        try
        {
            using (SqliteConnection conn = database.OpenConnection())
            {
                indexBuilder.RebuildIndexTables(conn);
            }
            tracker.MarkCompleted("lookup-tables");
        }
        catch (Exception ex)
        {
            tracker.MarkFailed("lookup-tables", ex.Message);
            throw;
        }

        // Extract cross-references
        logger.LogInformation("Rebuilding cross-references");
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

        // Sync state is now updated per-project in the calling method

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private async Task NotifyOrchestratorAsync(IngestionResult result, string runType)
    {
        if (string.IsNullOrWhiteSpace(_options.OrchestratorAddress)) return;

        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            await client.PostAsJsonAsync("/api/v1/notify-ingestion", new
            {
                source = JiraSource.SourceName,
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

    private DateTimeOffset? UpdateSyncState(
        IngestionResult result,
        string project,
        string runType,
        Exception? failure = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using SqliteConnection connection = database.OpenConnection();

        string subSource = JiraSyncStateHelper.SyncKey(project, runType);
        JiraSyncStateRecord? existing = JiraSyncStateRecord.SelectSingle(connection, SourceName: JiraSource.SourceName, SubSource: subSource);
        DateTimeOffset? previousWatermark =
            JiraSyncStateHelper.GetLastSuccessfulSyncAt(connection, project);
        DateTimeOffset? nextWatermark =
            JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
                previousWatermark,
                runType,
                failure is null ? result : null);
        bool completedWithErrors = result.ItemsFailed > 0 || result.Errors.Count > 0;

        JiraSyncStateRecord syncState = new JiraSyncStateRecord
        {
            Id = existing?.Id ?? JiraSyncStateRecord.GetIndex(),
            SourceName = JiraSource.SourceName,
            SubSource = subSource,
            LastSyncAt = result.CompletedAt,
            LastSuccessfulSyncAt = nextWatermark,
            LastCursor = null,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = _options.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(_options.SyncSchedule)),
            Status = failure is not null
                ? "failed"
                : completedWithErrors
                    ? "completed_with_errors"
                    : "success",
            LastError = failure?.Message ??
                        (result.Errors.Count > 0 ? result.Errors[^1] : null),
        };

        if (existing is not null)
            JiraSyncStateRecord.Update(connection, syncState);
        else
            JiraSyncStateRecord.Insert(connection, syncState);

        return nextWatermark;
    }

    private DateTimeOffset GetLastSyncTime(string project)
    {
        using SqliteConnection connection = database.OpenConnection();

        DateTimeOffset? lastSuccessfulSyncAt =
            JiraSyncStateHelper.GetLastSuccessfulSyncAt(connection, project);
        if (lastSuccessfulSyncAt is not null)
            return lastSuccessfulSyncAt.Value;

        // No DB record — check cache for the latest cached date and resume from the day after
        HashSet<DateOnly> cachedDates = source.GetCachedDates(project);
        if (cachedDates.Count > 0)
        {
            DateOnly latestCached = cachedDates.Max();
            DateOnly resumeDate = latestCached.AddDays(1);
            logger.LogInformation(
                "No sync state for {Project}; latest cache file is {LatestCached}, resuming from {ResumeDate}",
                project, latestCached, resumeDate);
            return new DateTimeOffset(resumeDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        //logger.LogInformation("No sync state or cache files for {Project}; defaulting to last 30 days", project);
        //return DateTimeOffset.UtcNow.AddDays(-30);

        // No cache either — default to the full-sync start date
        logger.LogInformation(
            "No sync state or cache files for {Project}; defaulting to full-sync start date {StartDate}",
            project, JiraCacheLayout.DefaultFullSyncStartDate);
        return new DateTimeOffset(
            JiraCacheLayout.DefaultFullSyncStartDate.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
    }

    private async Task RunCacheFileNameMigratorOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _migratorRan, 1, 0) != 0)
            return;
        await CacheFileNameMigrator.MigrateAsync(cache, JiraCacheLayout.SourceName, logger, ct);
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        return JiraSyncStateHelper.CaptureProjectWatermarks(connection)
            .Values
            .Where(value => value is not null)
            .Max();
    }
}
