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
    ConfluenceIngestionGate gate,
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
        ThrowIfBlocked();

        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_full";

        try
        {
            logger.LogInformation("Starting full ingestion");

            IngestionResult result = await source.ReconcileAsync(source.BuildPolicy(forceRefetchAll: true), ct);
            PostIngestion(result, "full", ct);
            await NotifyOrchestratorAsync(result, "full");

            _currentStatus = "idle";
            return result;
        }
        catch (ConfluenceHumanInterventionRequiredException ex)
        {
            RecordBlock(ex, "full");
            throw;
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
        ThrowIfBlocked();

        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";

        try
        {
            logger.LogInformation("Starting incremental ingestion (manifest reconciliation)");

            IngestionResult result = await source.ReconcileAsync(source.BuildPolicy(), ct);
            PostIngestion(result, "incremental", ct);
            await NotifyOrchestratorAsync(result, "incremental");

            _currentStatus = "idle";
            return result;
        }
        catch (ConfluenceHumanInterventionRequiredException ex)
        {
            RecordBlock(ex, "incremental");
            throw;
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

    /// <summary>
    /// Refuses a network run that is already known to be doomed. Sits before the
    /// run lock so a job queued <em>before</em> the block appeared re-checks at
    /// execution time rather than walking into the wall.
    /// </summary>
    private void ThrowIfBlocked()
    {
        if (gate.IsBlocked && gate.Current is { } block)
        {
            throw new ConfluenceIngestionBlockedException(block);
        }
    }

    /// <summary>
    /// Parks the service after an edge challenge: record the durable block, mark
    /// the status, and write the blocked sync-state rows.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> call <c>PostIngestion</c> or
    /// <c>NotifyOrchestratorAsync</c>. A blocked run is not a completed pass, and
    /// telling the orchestrator otherwise is how a half-filled cache becomes a
    /// "successful" ingestion.
    /// </remarks>
    private void RecordBlock(ConfluenceHumanInterventionRequiredException ex, string runType)
    {
        gate.Block(ex);
        _currentStatus = ConfluenceIngestionGate.BlockedStatus;
        WriteBlockedSyncState(runType, ex.Remediation);

        logger.LogError(
            ex, "{RunType} ingestion stopped: Confluence is serving an edge challenge and needs a human",
            runType);
    }

    /// <summary>
    /// Writes the blocked status onto both the run's row and the scheduling row,
    /// so the block is visible wherever sync state is read.
    /// </summary>
    private void WriteBlockedSyncState(string runType, string remediation)
    {
        using SqliteConnection connection = database.OpenConnection();

        WriteBlockedSyncStateRow(connection, runType, remediation);
        WriteBlockedSyncStateRow(connection, ConfluenceSource.SchedulingSubSource, remediation);
    }

    private void WriteBlockedSyncStateRow(
        SqliteConnection connection, string subSource, string remediation)
    {
        ConfluenceSyncStateRecord? existing = ConfluenceSyncStateRecord.SelectSingle(
            connection, SourceName: ConfluenceSource.SourceName, SubSource: subSource);

        ConfluenceSyncStateRecord syncState = new()
        {
            Id = existing?.Id ?? ConfluenceSyncStateRecord.GetIndex(),
            SourceName = ConfluenceSource.SourceName,
            SubSource = subSource,
            // A blocked run completed nothing, so the last genuine sync stands.
            LastSyncAt = existing?.LastSyncAt ?? DateTimeOffset.MinValue,
            LastCursor = existing?.LastCursor,
            ItemsIngested = existing?.ItemsIngested ?? 0,
            SyncSchedule = optionsAccessor.Value.SyncSchedule,
            NextScheduledAt = null,
            Status = ConfluenceIngestionGate.BlockedStatus,
            LastError = remediation,
        };

        if (existing is not null)
            ConfluenceSyncStateRecord.Update(connection, syncState);
        else
            ConfluenceSyncStateRecord.Insert(connection, syncState);
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

    /// <summary>
    /// Writes the run's sync-state row, plus the scheduling row when this was a
    /// network reconciliation.
    /// </summary>
    /// <remarks>
    /// The scheduling row is written under a single named sub-source so a
    /// cache-only rebuild can never satisfy <c>MinSyncAge</c> and suppress the
    /// next network sync.
    /// </remarks>
    private void UpdateSyncState(IngestionResult result, string runType, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        string status = ResolveStatus(result, runType);
        WriteSyncStateRow(connection, runType, result, status);

        if (runType is "full" or "incremental")
        {
            WriteSyncStateRow(connection, ConfluenceSource.SchedulingSubSource, result, status);
        }
    }

    /// <summary>
    /// Status now comes from the reconciliation verdict rather than from
    /// <c>Errors.Count == 0</c>: a run with no errors can still leave the cache
    /// incomplete, and saying "success" then would be the false confidence this
    /// design exists to remove.
    /// </summary>
    private string ResolveStatus(IngestionResult result, string runType)
    {
        if (runType == "rebuild")
        {
            return result.Errors.Count == 0 ? "success" : "completed_with_errors";
        }

        IReadOnlyList<ConfluenceReconcilePlan> plans = source.ReconcileReport(source.BuildPolicy());
        if (plans.Count == 0)
        {
            return "unknown";
        }

        if (plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.Unknown))
        {
            return "unknown";
        }

        if (plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.Partial))
        {
            return "partial";
        }

        return plans.Any(p => p.Verdict == ConfluenceSpaceVerdict.CompleteWithSkips)
            ? "complete_with_skips"
            : "complete";
    }

    private void WriteSyncStateRow(
        SqliteConnection connection, string subSource, IngestionResult result, string status)
    {
        ConfluenceSyncStateRecord? existing = ConfluenceSyncStateRecord.SelectSingle(
            connection, SourceName: ConfluenceSource.SourceName, SubSource: subSource);

        ConfluenceSyncStateRecord syncState = new()
        {
            Id = existing?.Id ?? ConfluenceSyncStateRecord.GetIndex(),
            SourceName = ConfluenceSource.SourceName,
            SubSource = subSource,
            LastSyncAt = result.CompletedAt,
            LastCursor = null,
            ItemsIngested = result.ItemsProcessed,
            SyncSchedule = optionsAccessor.Value.SyncSchedule,
            NextScheduledAt = DateTimeOffset.UtcNow.Add(TimeSpan.Parse(optionsAccessor.Value.SyncSchedule)),
            Status = status,
            LastError = result.Errors.Count > 0 ? result.Errors[^1] : null,
        };

        if (existing is not null)
            ConfluenceSyncStateRecord.Update(connection, syncState);
        else
            ConfluenceSyncStateRecord.Insert(connection, syncState);
    }

    /// <summary>
    /// When the last <b>network</b> reconciliation completed. Selects the
    /// scheduling sub-source exactly; previously it had no <c>SubSource</c>
    /// filter and could return the row a cache rebuild wrote.
    /// </summary>
    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        ConfluenceSyncStateRecord? state = ConfluenceSyncStateRecord.SelectSingle(
            connection,
            SourceName: ConfluenceSource.SourceName,
            SubSource: ConfluenceSource.SchedulingSubSource);
        return state?.LastSyncAt;
    }

    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
        => await RunIncrementalIngestionAsync(ct);

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
