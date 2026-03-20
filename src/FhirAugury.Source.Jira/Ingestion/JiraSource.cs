using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Fetches issues from the Jira REST API, caches responses, and upserts into the database.
/// Supports full and incremental downloads.
/// </summary>
public class JiraSource(
    JiraServiceOptions options,
    HttpClient httpClient,
    JiraDatabase database,
    IResponseCache cache,
    ILogger<JiraSource> logger)
{
    public const string SourceName = "jira";

    /// <summary>Performs a full download of all issues matching the configured JQL.</summary>
    public async Task<IngestionResult> DownloadAllAsync(string? jqlOverride, CancellationToken ct)
    {
        var jql = jqlOverride ?? options.DefaultJql ?? $"project = \"{options.DefaultProject}\"";
        return await DownloadAsync(jql, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct)
    {
        var baseJql = options.DefaultJql ?? $"project = \"{options.DefaultProject}\"";
        var sinceStr = since.ToString("yyyy-MM-dd HH:mm");
        var jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
        return await DownloadAsync(jql, ct);
    }

    private async Task<IngestionResult> DownloadAsync(string jql, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        var existingKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            string json;
            try
            {
                var response = await HttpRetryHelper.GetWithRetryAsync(
                    httpClient, url, ct, options.RateLimiting.MaxRetries, "jira");
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch issues at startAt={StartAt}", startAt);
                errors.Add($"page:{startAt} - {ex.Message}");
                break;
            }

            // Write to cache
            var cacheKey = CacheFileNaming.GenerateDailyFileName(today, JiraCacheLayout.JsonExtension, existingKeys);
            existingKeys.Add(cacheKey);
            using (var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, cacheKey, cacheStream, ct);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var total = root.GetProperty("total").GetInt32();
            var issues = root.GetProperty("issues");

            using var connection = database.OpenConnection();

            foreach (var issueJson in issues.EnumerateArray())
            {
                var (outcome, error) = ProcessIssue(issueJson, connection);
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

                if (itemsProcessed % 1000 == 0)
                    logger.LogInformation("Download progress: {Count} issues processed", itemsProcessed);
            }

            startAt += issues.GetArrayLength();
            hasMore = startAt < total;
        }

        logger.LogInformation(
            "Download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt);
    }

    /// <summary>Loads all issues from cached API responses (no network).</summary>
    public Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        using var connection = database.OpenConnection();

        foreach (var key in cache.EnumerateKeys(JiraCacheLayout.SourceName))
        {
            if (ct.IsCancellationRequested) break;
            if (!cache.TryGet(JiraCacheLayout.SourceName, key, out var stream)) continue;

            using (stream)
            {
                try
                {
                    var records = ParseCachedFile(stream, key);
                    foreach (var (issue, comments) in records)
                    {
                        var existing = JiraIssueRecord.SelectSingle(connection, Key: issue.Key);
                        if (existing is not null)
                        {
                            issue.Id = existing.Id;
                            JiraIssueRecord.Update(connection, issue);
                            itemsUpdated++;
                        }
                        else
                        {
                            JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
                            itemsNew++;
                        }

                        foreach (var comment in comments)
                            JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);

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

    private (ProcessOutcome Outcome, string? Error) ProcessIssue(
        JsonElement issueJson, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        string key = string.Empty;
        try
        {
            var issue = JiraFieldMapper.MapIssue(issueJson);
            key = issue.Key;

            var existing = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (existing is not null)
            {
                issue.Id = existing.Id;
                JiraIssueRecord.Update(connection, issue);
            }
            else
            {
                JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
            }

            var comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            foreach (var comment in comments)
                JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);

            var links = JiraFieldMapper.MapIssueLinks(issueJson, issue.Key);
            foreach (var link in links)
                JiraIssueLinkRecord.Insert(connection, link, ignoreDuplicates: true);

            return (existing is not null ? ProcessOutcome.Updated : ProcessOutcome.New, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process issue {Key}", key);
            return (ProcessOutcome.Failed, $"{key}: {ex.Message}");
        }
    }

    private static IEnumerable<(JiraIssueRecord Issue, List<JiraCommentRecord> Comments)> ParseCachedFile(Stream stream, string key)
    {
        if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return JiraXmlParser.ParseExport(stream);

        return ParseJsonCacheFile(stream);
    }

    private static IEnumerable<(JiraIssueRecord Issue, List<JiraCommentRecord> Comments)> ParseJsonCacheFile(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("issues", out var issues))
            yield break;

        foreach (var issueJson in issues.EnumerateArray())
        {
            var issue = JiraFieldMapper.MapIssue(issueJson);
            var comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            yield return (issue, comments);
        }
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
