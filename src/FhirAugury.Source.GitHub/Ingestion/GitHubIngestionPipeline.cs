using System.Net.Http.Json;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion.Categories;
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
    GitHubFileContentIndexer fileContentIndexer,
    CanonicalArtifactIndexer canonicalArtifactIndexer,
    StructureDefinitionIndexer structureDefinitionIndexer,
    FshArtifactIndexer fshArtifactIndexer,
    IEnumerable<IRepoCategoryStrategy> categoryStrategies,
    TagWeightResolver weightResolver,
    GitHubXRefRebuilder xrefRebuilder,
    GitHubPrTicketLinkRebuilder prTicketLinkRebuilder,
    IHttpClientFactory httpClientFactory,
    FhirAugury.Common.Indexing.IIndexTracker tracker,
    IOptions<GitHubServiceOptions> optionsAccessor,
    GitHubWorkGroupSupportFileAcquirer workGroupAcquirer,
    GitHubHl7WorkGroupIndexer workGroupIndexer,
    WorkGroupResolver workGroupResolver,
    WorkGroupResolutionPass workGroupResolutionPass,
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

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);
            IngestionResult downloadResult = await source.DownloadAllAsync(repoFilter, ct);
            IngestionResult result = MergeResults(cacheResult, downloadResult);
            await PostIngestionAsync(result, "full", ct);
            await NotifyOrchestratorAsync(result, "full", ct);

            _currentStatus = "idle";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _currentStatus = "canceled";
            logger.LogInformation("Full ingestion canceled (host shutdown or client abort)");
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

            IngestionResult? cacheResult = await LoadCacheIfDatabaseEmptyAsync(ct);

            // One-time full-history backfill for any repo lacking a marker, so the
            // historical PR/issue corpus (and thus xref_jira / PR↔ticket edges) is
            // complete before the moving incremental window takes over.
            List<string> needingBackfill = GetReposNeedingBackfill();
            IngestionResult? backfillResult = null;
            if (needingBackfill.Count > 0)
            {
                logger.LogInformation("Auto-backfilling {Count} repo(s) before incremental sync", needingBackfill.Count);
                backfillResult = await BackfillReposAsync(needingBackfill, ct);
            }

            ct.ThrowIfCancellationRequested();

            IngestionResult downloadResult = await source.DownloadIncrementalAsync(since, ct);
            IngestionResult result = MergeResults(cacheResult, MergeResults(backfillResult, downloadResult));
            await PostIngestionAsync(result, "incremental", ct);
            await NotifyOrchestratorAsync(result, "incremental", ct);

            _currentStatus = "idle";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _currentStatus = "canceled";
            logger.LogInformation("Incremental ingestion canceled (host shutdown or client abort)");
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
            await PostIngestionAsync(result, "rebuild", ct);
            await NotifyOrchestratorAsync(result, "rebuild", ct);

            _currentStatus = "idle";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _currentStatus = "canceled";
            logger.LogInformation("Rebuild from cache canceled (host shutdown or client abort)");
            throw;
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

    private readonly Dictionary<RepoCategory, IRepoCategoryStrategy> _strategyMap =
        categoryStrategies.ToDictionary(s => s.Category);

    private async Task PostIngestionAsync(IngestionResult result, string runType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await EnsureWorkGroupsRefreshedAsync(ct).ConfigureAwait(false);

        // Clone repos and extract commit data
        IReadOnlyList<(string Name, RepoCategory Category)> repos = _options.GetAllRepositories();

        tracker.MarkStarted("commits");
        tracker.MarkStarted("file-contents");
        tracker.MarkStarted("cross-refs");
        try
        {
            foreach ((string repo, RepoCategory category) in repos)
            {
                try
                {
                    _currentStatus = $"cloning:{repo}";
                    string clonePath = await cloner.EnsureCloneAsync(repo, ct);

                    _currentStatus = $"extracting_commits:{repo}";
                    await commitExtractor.ExtractAsync(clonePath, repo, _options.ResolveMaxInitialCommits(repo), ct);

                    // Resolve strategy for this repo's category
                    IRepoCategoryStrategy? strategy = _strategyMap.GetValueOrDefault(category);
                    List<string>? priorityPaths = strategy?.GetPriorityPaths(repo, clonePath);
                    List<string>? additionalIgnorePatterns = strategy?.GetAdditionalIgnorePatterns();

                    _currentStatus = $"indexing_files:{repo}";
                    fileContentIndexer.IndexRepositoryFiles(repo, clonePath, ct,
                        priorityPaths,
                        additionalIgnorePatterns is { Count: > 0 } ? additionalIgnorePatterns : null);

                    // Clean up stale file content records outside priority paths
                    // (from pre-filtering syncs that indexed the full tree)
                    if (priorityPaths is { Count: > 0 })
                    {
                        CleanupStaleFileContents(repo, priorityPaths);
                    }

                    _currentStatus = $"tagging_files:{repo}";
                    ApplyTags(repo, clonePath, strategy, ct);

                    _currentStatus = $"mapping_artifacts:{repo}";
                    if (strategy is not null)
                    {
                        using SqliteConnection connection = database.OpenConnection();
                        strategy.BuildArtifactMappings(repo, clonePath, connection, ct);
                    }

                    _currentStatus = $"indexing_canonical_artifacts:{repo}";
                    if (strategy is not null)
                    {
                        IReadOnlyList<string> artifactFiles = strategy.DiscoverCanonicalArtifactFiles(repo, clonePath, ct);
                        if (artifactFiles.Count > 0)
                        {
                            int indexed = canonicalArtifactIndexer.IndexFiles(repo, clonePath, artifactFiles, ct);
                            logger.LogInformation("Indexed {Count} canonical artifacts for {Repo}", indexed, repo);
                        }
                    }

                    _currentStatus = $"indexing_structure_definitions:{repo}";
                    if (strategy is not null)
                    {
                        List<string> sdFiles = strategy.DiscoverStructureDefinitionFiles(repo, clonePath, ct);
                        if (sdFiles.Count > 0)
                        {
                            using SqliteConnection sdConnection = database.OpenConnection();
                            structureDefinitionIndexer.IndexStructureDefinitions(repo, sdFiles, clonePath, sdConnection, ct);
                        }
                    }

                    _currentStatus = $"indexing_fsh_artifacts:{repo}";
                    if (strategy is not null)
                    {
                        (IReadOnlyList<string> fshFiles, FhirAugury.Parsing.Fsh.SushiConfig? sushiConfig) =
                            strategy.DiscoverFshContent(repo, clonePath, ct);

                        if (fshFiles.Count > 0)
                        {
                            int indexed = fshArtifactIndexer.IndexFshFiles(
                                repo, clonePath, fshFiles, sushiConfig, ct);
                            logger.LogInformation("Indexed {Count} FSH artifacts for {Repo}", indexed, repo);
                        }
                    }
                }
                catch (IngestionDataIntegrityException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clone/extract commits/index files for {Repo}", repo);
                }
            }
            tracker.MarkCompleted("commits");
            tracker.MarkCompleted("file-contents");

            try
            {
                _currentStatus = "resolving_workgroups";
                tracker.MarkStarted("workgroup-resolution");
                List<string> repoNamesForWg = repos.Select(r => r.Name).ToList();
                await workGroupResolutionPass.RunAsync(repoNamesForWg, ct).ConfigureAwait(false);
                tracker.MarkCompleted("workgroup-resolution");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to run work-group resolution pass");
                tracker.MarkFailed("workgroup-resolution", ex.Message);
            }

            try
            {
                _currentStatus = "extracting_cross_refs";
                List<string> allRepoNames = repos.Select(r => r.Name).ToList();
                xrefRebuilder.RebuildAllRepos(allRepoNames, validJiraNumbers: null, ct);
                tracker.MarkCompleted("cross-refs");

                tracker.MarkStarted("pr-ticket-links");
                prTicketLinkRebuilder.RebuildAllRepos(allRepoNames, ct);
                tracker.MarkCompleted("pr-ticket-links");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract cross-references");
                tracker.MarkFailed("cross-refs", ex.Message);
                tracker.MarkFailed("pr-ticket-links", ex.Message);
            }
        }
        catch (Exception ex)
        {
            tracker.MarkFailed("commits", ex.Message);
            tracker.MarkFailed("file-contents", ex.Message);
            tracker.MarkFailed("cross-refs", ex.Message);
            tracker.MarkFailed("pr-ticket-links", ex.Message);
            throw;
        }

        // Rebuild BM25 keyword index
        _currentStatus = "rebuilding_index";
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

        // Update sync state
        UpdateSyncState(result, runType, ct);

        logger.LogInformation(
            "Post-ingestion complete: {Processed} items, {New} new, {Updated} updated",
            result.ItemsProcessed, result.ItemsNew, result.ItemsUpdated);
    }

    /// <summary>
    /// Applies category-specific tags to a repository using the strategy pattern.
    /// Replaces the previous RepoFileTagger logic.
    /// </summary>
    private void ApplyTags(string repoFullName, string clonePath, IRepoCategoryStrategy? strategy, CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Clear existing tags for this repo
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_file_tags WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        if (strategy is null)
        {
            logger.LogDebug("No strategy for {Repo}, skipping tags", repoFullName);
            return;
        }

        if (!strategy.Validate(repoFullName, clonePath))
        {
            logger.LogWarning("Strategy {Strategy} validation failed for {Repo}, skipping tags",
                strategy.StrategyName, repoFullName);
            return;
        }

        logger.LogInformation("Applying {Strategy} strategy to {Repo}",
            strategy.StrategyName, repoFullName);

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags(repoFullName, clonePath, ct);

        // Apply weights from configuration
        foreach (GitHubFileTagRecord tag in tags)
        {
            tag.Weight = weightResolver.ResolveWeight(tag.TagCategory, tag.TagName, tag.TagModifier);
        }

        if (tags.Count > 0)
        {
            const int batchSize = 1000;
            for (int i = 0; i < tags.Count; i += batchSize)
            {
                List<GitHubFileTagRecord> batch = tags.GetRange(i, Math.Min(batchSize, tags.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }

            logger.LogInformation("Applied {Count} tags via {Strategy}",
                tags.Count, strategy.StrategyName);
        }
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
        GitHubSyncStateRecord? state = GitHubSyncStateReader.GetMostRecentOperational(connection);
        return state?.LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-30);
    }

    public DateTimeOffset? GetLastSyncCompletedAt()
    {
        using SqliteConnection connection = database.OpenConnection();
        GitHubSyncStateRecord? state = GitHubSyncStateReader.GetMostRecentOperational(connection);
        return state?.LastSyncAt;
    }

    // ── History backfill (per-repo, marker-gated) ─────────────────────────

    private const string BackfillMarkerPrefix = "backfill:";

    /// <summary>
    /// Returns configured repos that have no <c>backfill:&lt;repo&gt;</c> marker
    /// yet — i.e. that still need the one-time full-history fetch.
    /// </summary>
    public List<string> GetReposNeedingBackfill()
    {
        using SqliteConnection connection = database.OpenConnection();
        HashSet<string> marked = GitHubSyncStateRecord
            .SelectList(connection, SourceName: IGitHubDataProvider.SourceName)
            .Where(r => r.SubSource.StartsWith(BackfillMarkerPrefix, StringComparison.Ordinal))
            .Select(r => r.SubSource[BackfillMarkerPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);

        return _options.GetAllRepositoryNames()
            .Where(repo => !marked.Contains(repo))
            .ToList();
    }

    /// <summary>Writes the <c>backfill:&lt;repo&gt;</c> marker so the repo is not backfilled again.</summary>
    private void MarkRepoBackfilled(string repo)
    {
        using SqliteConnection connection = database.OpenConnection();
        string subSource = BackfillMarkerPrefix + repo;
        GitHubSyncStateRecord? existing = GitHubSyncStateRecord.SelectSingle(
            connection, SourceName: IGitHubDataProvider.SourceName, SubSource: subSource);

        GitHubSyncStateRecord marker = new GitHubSyncStateRecord
        {
            Id = existing?.Id ?? GitHubSyncStateRecord.GetIndex(),
            SourceName = IGitHubDataProvider.SourceName,
            SubSource = subSource,
            LastSyncAt = DateTimeOffset.UtcNow,
            LastCursor = null,
            ItemsIngested = 0,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "success",
            LastError = null,
        };

        if (existing is not null)
            GitHubSyncStateRecord.Update(connection, marker);
        else
            GitHubSyncStateRecord.Insert(connection, marker);
    }

    /// <summary>
    /// Backfills the given repos one at a time, marking each repo backfilled only
    /// after its fetch completed without repo-local errors. The gh-CLI provider
    /// accumulates per-repo errors rather than throwing, so a failed repo simply
    /// leaves its marker absent and is retried on the next incremental run.
    /// A cancelled repo aborts the remaining list rather than sweeping it.
    /// </summary>
    internal async Task<IngestionResult> BackfillReposAsync(IReadOnlyList<string> repos, CancellationToken ct)
    {
        IngestionResult aggregate = new IngestionResult(0, 0, 0, 0, [], DateTimeOffset.UtcNow);

        foreach (string repo in repos)
        {
            if (ct.IsCancellationRequested) break;

            _currentStatus = $"backfilling:{repo}";
            logger.LogInformation("Backfilling full history for {Repo}", repo);
            IngestionResult repoResult = await source.DownloadBackfillAsync(repo, ct);
            ct.ThrowIfCancellationRequested();
            aggregate = MergeResults(aggregate, repoResult);

            if (repoResult.Canceled)
            {
                logger.LogInformation(
                    "Backfill for {Repo} canceled after {Count} item(s); progress checkpointed for resume",
                    repo, repoResult.ItemsProcessed);
                break;
            }

            if (repoResult.Errors.Count == 0)
            {
                MarkRepoBackfilled(repo);
            }
            else
            {
                logger.LogWarning(
                    "Backfill for {Repo} completed with {Count} error(s); marker left absent for retry",
                    repo, repoResult.Errors.Count);
            }
        }

        return aggregate;
    }

    /// <summary>
    /// Runs an explicit history backfill. With a <paramref name="repoFilter"/> it
    /// forces a backfill of that repo; otherwise it backfills every repo that
    /// still lacks a <c>backfill:&lt;repo&gt;</c> marker.
    /// </summary>
    public async Task<IngestionResult> RunBackfillIngestionAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An ingestion is already in progress.");

        _currentStatus = "running_backfill";

        try
        {
            List<string> repos = repoFilter is not null ? [repoFilter] : GetReposNeedingBackfill();
            logger.LogInformation("Starting history backfill for {Count} repo(s)", repos.Count);

            IngestionResult result = await BackfillReposAsync(repos, ct);
            await PostIngestionAsync(result, "backfill", ct);
            await NotifyOrchestratorAsync(result, "backfill", ct);

            _currentStatus = "idle";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _currentStatus = "canceled";
            logger.LogInformation("History backfill canceled (host shutdown or client abort)");
            throw;
        }
        catch (Exception ex)
        {
            _currentStatus = $"error: {ex.Message}";
            logger.LogError(ex, "History backfill failed");
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<IngestionResult?> LoadCacheIfDatabaseEmptyAsync(CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();
        int repoCount = GitHubRepoRecord.SelectCount(connection);

        if (repoCount > 0)
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
            Canceled = first.Canceled || second.Canceled,
        };
    }

    /// <summary>
    /// Removes <c>github_file_contents</c> rows for files outside the given priority paths.
    /// This cleans up stale records from pre-filtering syncs that indexed the full tree.
    /// </summary>
    internal void CleanupStaleFileContents(string repoFullName, List<string> priorityPaths)
    {
        using SqliteConnection connection = database.OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();

        // Build WHERE clause: FilePath NOT LIKE 'path1/%' AND FilePath NOT LIKE 'path2/%' ...
        List<string> conditions = [];
        for (int i = 0; i < priorityPaths.Count; i++)
        {
            string paramName = $"@path{i}";
            string normalizedPath = priorityPaths[i].Replace('\\', '/').TrimEnd('/') + "/";
            cmd.Parameters.AddWithValue(paramName, normalizedPath + "%");
            conditions.Add($"FilePath NOT LIKE {paramName}");
        }

        cmd.CommandText = $"""
            DELETE FROM github_file_contents
            WHERE RepoFullName = @repo
            AND {string.Join(" AND ", conditions)}
            """;
        cmd.Parameters.AddWithValue("@repo", repoFullName);

        int removed = cmd.ExecuteNonQuery();
        if (removed > 0)
        {
            logger.LogInformation(
                "Cleaned up {Count} stale file content records outside priority paths for {Repo}",
                removed, repoFullName);
        }
    }

    private async Task NotifyOrchestratorAsync(IngestionResult result, string runType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.OrchestratorAddress)) return;

        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            await client.PostAsJsonAsync("/api/v1/notify-ingestion", new
            {
                source = IGitHubDataProvider.SourceName,
                type = runType,
                itemsIngested = result.ItemsProcessed,
                completedAt = result.CompletedAt,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify orchestrator of ingestion completion");
        }
    }

    async Task IIngestionPipeline.RunIncrementalIngestionAsync(CancellationToken ct)
        => await RunIncrementalIngestionAsync(ct);

    /// <summary>
    /// Materializes the configured HL7 work-group XML and reloads the
    /// <see cref="WorkGroupResolver"/> snapshot. Invoked at the start of
    /// <see cref="PostIngestionAsync"/> so manual, scheduled, and rebuild
    /// flows all see fresh data inside the existing run-lock.
    /// </summary>
    private async Task EnsureWorkGroupsRefreshedAsync(CancellationToken ct)
    {
        tracker.MarkStarted("workgroups");
        try
        {
            string? xmlPath = await workGroupAcquirer.EnsureAsync(ct).ConfigureAwait(false);
            int total = workGroupIndexer.Rebuild(xmlPath, ct);
            workGroupResolver.Reload();

            WorkGroupRefreshIntegrity.ThrowIfConfiguredButEmpty(
                _options.Hl7WorkGroupSourceXml, total, xmlPath);

            logger.LogInformation(
                "hl7 workgroups refreshed: {Total} rows resolvable (xml={Xml})",
                total, xmlPath ?? "<none>");
            tracker.MarkCompleted("workgroups");
        }
        catch (OperationCanceledException)
        {
            tracker.MarkFailed("workgroups", "cancelled");
            throw;
        }
        catch (IngestionDataIntegrityException ex)
        {
            tracker.MarkFailed("workgroups", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hl7 workgroup refresh failed; continuing ingestion with last-known snapshot");
            tracker.MarkFailed("workgroups", ex.Message);
        }
    }
}
