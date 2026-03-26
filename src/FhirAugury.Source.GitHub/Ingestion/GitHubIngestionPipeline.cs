using FhirAugury.Common.Ingestion;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// Optionally clones repos and extracts commit files and Jira references.
/// </summary>
public class GitHubIngestionPipeline(
    IGitHubDataProvider source,
    GitHubDatabase database,
    GitHubIndexer indexer,
    GitHubRepoCloner cloner,
    GitHubCommitFileExtractor commitExtractor,
    JiraRefExtractor jiraRefExtractor,
    IOptions<GitHubServiceOptions> optionsAccessor,
    ILogger<GitHubIngestionPipeline> logger) : IIngestionPipeline
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private volatile string _currentStatus = "idle";
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;

    public bool IsRunning => _runLock.CurrentCount == 0;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the GitHub API.</summary>
    public async Task<IngestionResult> RunFullIngestionAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_full";

        try
        {
            logger.LogInformation("Starting full ingestion");

            IngestionResult result = await source.DownloadAllAsync(repoFilter, ct);
            await PostIngestionAsync(result, "full", ct);

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

    /// <summary>Runs an incremental ingestion from the GitHub API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_incremental";

        try
        {
            DateTimeOffset since = GetLastSyncTime();
            logger.LogInformation("Starting incremental ingestion since {Since}", since);

            IngestionResult result = await source.DownloadIncrementalAsync(since, ct);
            await PostIngestionAsync(result, "incremental", ct);

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
            await PostIngestionAsync(result, "rebuild", ct);

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

    private async Task PostIngestionAsync(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Clone repos and extract commit data
        List<string> repos = new List<string>(_options.Repositories);
        repos.AddRange(_options.AdditionalRepositories);

        foreach (string repo in repos)
        {
            try
            {
                _currentStatus = $"cloning:{repo}";
                string clonePath = await cloner.EnsureCloneAsync(repo, ct);

                _currentStatus = $"extracting_commits:{repo}";
                await commitExtractor.ExtractAsync(clonePath, repo, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clone/extract commits for {Repo}", repo);
            }

            try
            {
                _currentStatus = $"extracting_jira_refs:{repo}";
                jiraRefExtractor.ExtractAll(repo, validJiraNumbers: null, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract Jira references for {Repo}", repo);
            }
        }

        // Rebuild BM25 keyword index
        _currentStatus = "rebuilding_index";
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
        using SqliteConnection connection = database.OpenConnection();

        GitHubSyncStateRecord? existing = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName, SubSource: runType);

        GitHubSyncStateRecord syncState = new GitHubSyncStateRecord
        {
            Id = existing?.Id ?? GitHubSyncStateRecord.GetIndex(),
            SourceName = IGitHubDataProvider.SourceName,
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
            GitHubSyncStateRecord.Update(connection, syncState);
        else
            GitHubSyncStateRecord.Insert(connection, syncState);
    }

    private DateTimeOffset GetLastSyncTime()
    {
        using SqliteConnection connection = database.OpenConnection();
        GitHubSyncStateRecord? state = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);
        return state?.LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-30);
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        GitHubSyncStateRecord? state = GitHubSyncStateRecord.SelectSingle(connection, SourceName: IGitHubDataProvider.SourceName);
        return state?.LastSyncAt;
    }

    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
        => await RunIncrementalIngestionAsync(ct);
}
