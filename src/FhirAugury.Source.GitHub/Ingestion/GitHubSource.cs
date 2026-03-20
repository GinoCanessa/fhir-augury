using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Fetches issues and PRs from the GitHub REST API, caches responses, and upserts into the database.
/// Supports full and incremental downloads.
/// </summary>
public class GitHubSource(
    GitHubServiceOptions options,
    HttpClient httpClient,
    GitHubDatabase database,
    IResponseCache cache,
    ILogger<GitHubSource> logger)
{
    public const string SourceName = "github";
    private const string GitHubApiBase = "https://api.github.com";

    /// <summary>Performs a full download of all issues for configured repositories.</summary>
    public async Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default)
    {
        var repos = repoFilter is not null ? [repoFilter] : GetEffectiveRepositories();
        return await DownloadReposAsync(repos, since: null, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var repos = GetEffectiveRepositories();
        return await DownloadReposAsync(repos, since, ct);
    }

    /// <summary>Loads all issues from cached API responses (no network).</summary>
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        using var connection = database.OpenConnection();

        foreach (var key in cache.EnumerateKeys(GitHubCacheLayout.SourceName))
        {
            if (ct.IsCancellationRequested) break;
            if (!cache.TryGet(GitHubCacheLayout.SourceName, key, out var stream)) continue;

            using (stream)
            {
                try
                {
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("issues", out var issues)) continue;

                    var repoFullName = root.TryGetProperty("repo", out var repoEl) ? repoEl.GetString() ?? "" : "";

                    foreach (var issueJson in issues.EnumerateArray())
                    {
                        var record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
                        var existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: record.UniqueKey);

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
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        using var connection = database.OpenConnection();

        foreach (var repoFullName in repos)
        {
            if (ct.IsCancellationRequested) break;

            logger.LogInformation("Fetching repository: {Repo}", repoFullName);

            // Fetch and upsert repo metadata
            try
            {
                var repoUrl = $"{GitHubApiBase}/repos/{repoFullName}";
                using var repoResponse = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, repoUrl, ct, options.RateLimiting.MaxRetries, "github");
                repoResponse.EnsureSuccessStatusCode();
                var repoJson = await repoResponse.Content.ReadAsStringAsync(ct);
                using var repoDoc = JsonDocument.Parse(repoJson);
                UpsertRepo(connection, repoDoc.RootElement);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch repo metadata for {Repo}", repoFullName);
            }

            // Paginate issues (includes PRs on GitHub API)
            int page = 1;
            bool hasMore = true;
            var sinceParam = since.HasValue
                ? $"&since={since.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"
                : "";

            while (hasMore && !ct.IsCancellationRequested)
            {
                var url = $"{GitHubApiBase}/repos/{repoFullName}/issues?state=all&per_page=100&page={page}&sort=updated&direction=asc{sinceParam}";

                logger.LogInformation("Fetching issues: repo={Repo}, page={Page}", repoFullName, page);

                JsonDocument doc;
                try
                {
                    using var response = await HttpRetryHelper.GetWithRetryAsync(
                        httpClient, url, ct, options.RateLimiting.MaxRetries, "github");
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    doc = JsonDocument.Parse(json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fetch issues for {Repo} at page={Page}", repoFullName, page);
                    errors.Add($"repo:{repoFullName}:page:{page} - {ex.Message}");
                    break;
                }

                using (doc)
                {
                    var issues = doc.RootElement;
                    if (issues.GetArrayLength() == 0)
                    {
                        hasMore = false;
                        break;
                    }

                    foreach (var issueJson in issues.EnumerateArray())
                    {
                        var (outcome, error) = ProcessIssue(issueJson, repoFullName, connection);
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

        logger.LogInformation(
            "Download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    private (ProcessOutcome Outcome, string? Error) ProcessIssue(
        JsonElement issueJson, string repoFullName, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        string uniqueKey = string.Empty;
        try
        {
            var record = GitHubIssueMapper.MapIssue(issueJson, repoFullName);
            uniqueKey = record.UniqueKey;

            var existing = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey);
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
        var record = GitHubIssueMapper.MapRepo(repoJson);
        var existing = GitHubRepoRecord.SelectSingle(connection, FullName: record.FullName);

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
            var url = $"{GitHubApiBase}/repos/{repoFullName}/issues/{issueNumber}/comments?per_page=100";
            using var response = await HttpRetryHelper.GetWithRetryAsync(
                httpClient, url, ct, options.RateLimiting.MaxRetries, "github");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);

            // Look up the issue's DB ID
            var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: $"{repoFullName}#{issueNumber}");
            var issueDbId = issue?.Id ?? 0;

            foreach (var commentJson in doc.RootElement.EnumerateArray())
            {
                var comment = GitHubIssueMapper.MapComment(commentJson, issueDbId, repoFullName, issueNumber);
                GitHubCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch comments for {Repo}#{Number}", repoFullName, issueNumber);
            errors.Add($"comments:{repoFullName}#{issueNumber} - {ex.Message}");
        }
    }

    private List<string> GetEffectiveRepositories()
    {
        var repos = new List<string>(options.Repositories);
        repos.AddRange(options.AdditionalRepositories);
        return repos;
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
}
