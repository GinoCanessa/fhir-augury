using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Fetches issues and PRs from the GitHub REST API, caches responses, and upserts into the database.
/// Supports full and incremental downloads.
/// </summary>
public class GitHubRestProvider(
    IOptions<GitHubServiceOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    GitHubDatabase database,
    IResponseCache cache,
    GitHubBackfillCheckpointStore checkpointStore,
    ILogger<GitHubRestProvider> logger) : IGitHubDataProvider
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;
    public const string SourceName = SourceSystems.GitHub;
    private const string GitHubApiBase= "https://api.github.com";

    /// <summary>Performs a full download of all issues for configured repositories.</summary>
    public async Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        List<string> repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();
        return await DownloadReposAsync(repos, since: null, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        List<string> repos = GetEffectiveRepositories();
        return await DownloadReposAsync(repos, since, ct);
    }

    /// <summary>
    /// Performs a full per-repo history backfill. Reuses the full
    /// <c>?state=all</c> per-repo path (no <c>since</c> bound). Lower fidelity
    /// than the gh-CLI backfill: the REST full path fetches issue/PR rows and
    /// issue comments only — it does not populate PR commit links or PR review /
    /// review-thread comments — so under REST the commit-provenance and some
    /// comment-provenance PR↔ticket edges are thinner. The active provider is
    /// gh-cli; REST backfill is a documented fallback, not parity.
    /// </summary>
    /// <remarks>
    /// This path is not resumable — <paramref name="resumeFrom"/> is accepted for interface
    /// parity and ignored. It still owns its terminal state, writing the completion marker
    /// per repo only when that repo was neither cancelled nor errored.
    /// </remarks>
    public async Task<IngestionResult> DownloadBackfillAsync(
        string? repoFilter = null,
        GitHubBackfillCursor? resumeFrom = null,
        CancellationToken ct = default)
    {
        List<string> repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();

        if (resumeFrom is not null)
        {
            logger.LogDebug(
                "REST backfill is not resumable; ignoring cursor and refetching {Count} repo(s) in full",
                repos.Count);
        }

        IngestionResult result = await DownloadReposAsync(repos, since: null, ct);

        if (!result.Canceled && result.Errors.Count == 0)
        {
            foreach (string repo in repos)
            {
                checkpointStore.MarkComplete(repo, result.ItemsProcessed);
            }
        }

        return result;
    }

    /// <summary>Loads all issues from cached API responses (no network).</summary>
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

        using SqliteConnection connection = database.OpenConnection();

        foreach (string key in cache.EnumerateKeys(GitHubCacheLayout.SourceName))
        {
            if (ct.IsCancellationRequested) break;
            if (key.StartsWith(GitHubCacheLayout.ReposSubDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.EndsWith("." + GitHubCacheLayout.JsonExtension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!cache.TryGet(GitHubCacheLayout.SourceName, key, out Stream? stream) || stream is null) continue;

            using (stream)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(stream);
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("issues", out JsonElement issues)) continue;

                    string repoFullName = root.TryGetProperty("repo", out JsonElement repoEl) ? repoEl.GetString() ?? "" : "";

                    foreach (JsonElement issueJson in issues.EnumerateArray())
                    {
                        GitHubIssueRecord record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
                        GitHubIssueRecord? existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: record.UniqueKey);

                        if (existing is not null)
                        {
                            record.Id = existing.Id;
                            GitHubIssueRecord.Update(connection, record);
                            itemsUpdated++;
                        }
                        else
                        {
                            GitHubIssueRecord.Insert(connection, record, ignoreDuplicates: true);
                            itemsNew++;
                        }

                        itemsProcessed++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process cached file {Key}", key);
                    itemsFailed++;
                    errors.Add($"{key}: {ex.Message}");
                }
            }
        }

        logger.LogInformation(
            "Cache ingestion complete: {Processed} processed, {New} new, {Updated} updated",
            itemsProcessed, itemsNew, itemsUpdated);

        return Task.FromResult(new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt));
    }

    private async Task<IngestionResult> DownloadReposAsync(
        List<string> repos, DateTimeOffset? since, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

        using SqliteConnection connection = database.OpenConnection();

        bool canceled = false;

        foreach (string repoFullName in repos)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }

            try
            {
                logger.LogInformation("Fetching repository: {Repo}", repoFullName);

                // Fetch and upsert repo metadata
                try
                {
                    string repoUrl = $"{GitHubApiBase}/repos/{repoFullName}";
                    using HttpResponseMessage repoResponse = await HttpRetryHelper.GetWithRetryAsync(
                        httpClientFactory.CreateClient("github"), repoUrl, ct, _options.RateLimiting.MaxRetries, "github");
                    repoResponse.EnsureSuccessStatusCode();
                    string repoJson = await repoResponse.Content.ReadAsStringAsync(ct);
                    using JsonDocument repoDoc = JsonDocument.Parse(repoJson);
                    UpsertRepo(connection, repoDoc.RootElement);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch repo metadata for {Repo}", repoFullName);
                }

                // Paginate issues (includes PRs on GitHub API)
                int page = 1;
                bool hasMore = true;
                string sinceParam = since.HasValue
                    ? $"&since={since.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"
                    : "";

                while (hasMore)
                {
                    ct.ThrowIfCancellationRequested();

                    string url = $"{GitHubApiBase}/repos/{repoFullName}/issues?state=all&per_page=100&page={page}&sort=updated&direction=asc{sinceParam}";

                    logger.LogInformation("Fetching issues: repo={Repo}, page={Page}", repoFullName, page);

                    JsonDocument doc;
                    try
                    {
                        using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                            httpClientFactory.CreateClient("github"), url, ct, _options.RateLimiting.MaxRetries, "github");
                        response.EnsureSuccessStatusCode();
                        string json = await response.Content.ReadAsStringAsync(ct);
                        doc = JsonDocument.Parse(json);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to fetch issues for {Repo} at page={Page}", repoFullName, page);
                        errors.Add($"repo:{repoFullName}:page:{page} - {ex.Message}");
                        break;
                    }

                    using (doc)
                    {
                        JsonElement issues = doc.RootElement;
                        if (issues.GetArrayLength() == 0)
                        {
                            hasMore = false;
                            break;
                        }

                        foreach (JsonElement issueJson in issues.EnumerateArray())
                        {
                            ct.ThrowIfCancellationRequested();

                            (ProcessOutcome outcome, string? error) = ProcessIssue(issueJson, repoFullName, connection);
                            itemsProcessed++;

                            switch (outcome)
                            {
                                case ProcessOutcome.New: itemsNew++; break;
                                case ProcessOutcome.Updated: itemsUpdated++; break;
                                case ProcessOutcome.Failed:
                                    itemsFailed++;
                                    if (error is not null) errors.Add(error);
                                    break;
                            }

                            // Fetch comments for this issue
                            if (outcome != ProcessOutcome.Failed)
                            {
                                await FetchIssueCommentsAsync(
                                    connection, repoFullName,
                                    issueJson.GetProperty("number").GetInt32(),
                                    ct, errors);
                            }

                            if (itemsProcessed % 1000 == 0)
                                logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
                        }

                        hasMore = issues.GetArrayLength() >= 100;
                        page++;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
        }

        if (canceled)
        {
            logger.LogInformation(
                "Download canceled after {Processed} item(s): {New} new, {Updated} updated, {Failed} failed",
                itemsProcessed, itemsNew, itemsUpdated, itemsFailed);
        }
        else
        {
            logger.LogInformation(
                "Download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
                itemsProcessed, itemsNew, itemsUpdated, itemsFailed);
        }

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
        {
            Canceled = canceled,
        };
    }

    private (ProcessOutcome Outcome, string? Error) ProcessIssue(
        JsonElement issueJson, string repoFullName, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        string uniqueKey = string.Empty;
        try
        {
            GitHubIssueRecord record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
            uniqueKey = record.UniqueKey;

            GitHubIssueRecord? existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey);
            if (existing is not null)
            {
                record.Id = existing.Id;
                GitHubIssueRecord.Update(connection, record);
                return (ProcessOutcome.Updated, null);
            }

            GitHubIssueRecord.Insert(connection, record, ignoreDuplicates: true);
            return (ProcessOutcome.New, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process issue {Key}", uniqueKey);
            return (ProcessOutcome.Failed, $"{uniqueKey}: {ex.Message}");
        }
    }

    private static void UpsertRepo(Microsoft.Data.Sqlite.SqliteConnection connection, JsonElement repoJson)
    {
        GitHubRepoRecord record = GitHubIssueMapper.MapRepo(repoJson);
        GitHubRepoRecord? existing = GitHubRepoRecord.SelectSingle(connection, FullName: record.FullName);

        if (existing is not null)
        {
            record.Id = existing.Id;
            GitHubRepoRecord.Update(connection, record);
        }
        else
        {
            GitHubRepoRecord.Insert(connection, record, ignoreDuplicates: true);
        }
    }

    private async Task FetchIssueCommentsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string repoFullName, int issueNumber,
        CancellationToken ct, List<string> errors)
    {
        try
        {
            string url = $"{GitHubApiBase}/repos/{repoFullName}/issues/{issueNumber}/comments?per_page=100";
            using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                httpClientFactory.CreateClient("github"), url, ct, _options.RateLimiting.MaxRetries, "github");
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(json);

            // Look up the issue's DB ID
            GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: $"{repoFullName}#{issueNumber}");
            int issueDbId = issue?.Id ?? 0;

            foreach (JsonElement commentJson in doc.RootElement.EnumerateArray())
            {
                GitHubCommentRecord comment = GitHubIssueMapper.MapComment(commentJson, issueDbId, repoFullName, issueNumber);
                GitHubCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch comments for {Repo}#{Number}", repoFullName, issueNumber);
            errors.Add($"comments:{repoFullName}#{issueNumber} - {ex.Message}");
        }
    }

    private List<string> GetEffectiveRepositories()
    {
        return _options.GetAllRepositoryNames();
    }

    private enum ProcessOutcome { New, Updated, Failed }
}

/// <summary>Result of an ingestion run.</summary>
public record IngestionResult(
    int ItemsProcessed,
    int ItemsNew,
    int ItemsUpdated,
    int ItemsFailed,
    List<string> Errors,
    DateTimeOffset StartedAt)
{
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when the run ended early because the caller's token fired.</summary>
    public bool Canceled { get; init; }
}
