using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow: fetch → cache → parse → upsert → FTS5 → BM25 → sync state.
/// Optionally clones repos and extracts commit files and Jira references.
/// </summary>
public class GitHubIngestionPipeline(
    GitHubSource source,
    GitHubDatabase database,
    GitHubIndexer indexer,
    GitHubRepoCloner cloner,
    GitHubCommitFileExtractor commitExtractor,
    JiraRefExtractor jiraRefExtractor,
    GitHubServiceOptions options,
    ILogger<GitHubIngestionPipeline> logger)
{
    private volatile bool _isRunning;
    private string _currentStatus = "idle";

    public bool IsRunning => _isRunning;
    public string CurrentStatus => _currentStatus;

    /// <summary>Runs a full ingestion from the GitHub API.</summary>
    public async Task<IngestionResult> RunFullIngestionAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("An ingestion is already in progress.");

        _isRunning = true;
        _currentStatus = "running_full";

        try
        {
            logger.LogInformation("Starting full ingestion");

            var result = await source.DownloadAllAsync(repoFilter, ct);
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
            _isRunning = false;
        }
    }

    /// <summary>Runs an incremental ingestion from the GitHub API.</summary>
    public async Task<IngestionResult> RunIncrementalIngestionAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("An ingestion is already in progress.");

        _isRunning = true;
        _currentStatus = "running_incremental";

        try
        {
            var since = GetLastSyncTime();
            logger.LogInformation("Starting incremental ingestion since {Since}", since);

            var result = await source.DownloadIncrementalAsync(since, ct);
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
            _isRunning = false;
        }
    }

    /// <summary>Rebuilds the database entirely from cached responses.</summary>
    public async Task<IngestionResult> RebuildFromCacheAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("An ingestion is already in progress.");

        _isRunning = true;
        _currentStatus = "rebuilding";

        try
        {
            logger.LogInformation("Rebuilding database from cache");
            database.ResetDatabase();

            var result = await source.LoadFromCacheAsync(ct);
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
            _isRunning = false;
        }
    }

    private async Task PostIngestionAsync(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Clone repos and extract commit data
        var repos = new List<string>(options.Repositories);
        repos.AddRange(options.AdditionalRepositories);

        foreach (var repo in repos)
        {
            try
            {
                _currentStatus = $"cloning:{repo}";
                var clonePath = await cloner.EnsureCloneAsync(repo, ct);

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
        UpdateSyncState(result, runType);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    private void UpdateSyncState(IngestionResult result, string runType)
    {
        using var connection = database.OpenConnection();

        var existing = GitHubSyncStateRecord.SelectSingle(connection, SourceName: GitHubSource.SourceName, SubSource: runType);

        var syncState = new GitHubSyncStateRecord
        {
            Id = existing?.Id ?? GitHubSyncStateRecord.GetIndex(),
            SourceName = GitHubSource.SourceName,
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
            GitHubSyncStateRecord.Update(connection, syncState);
        else
            GitHubSyncStateRecord.Insert(connection, syncState);
    }

    private DateTimeOffset GetLastSyncTime()
    {
        using var connection = database.OpenConnection();
        var state = GitHubSyncStateRecord.SelectSingle(connection, SourceName: GitHubSource.SourceName);
        return state?.LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-30);
    }
}
