using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Fetches issues from the Jira REST API, caches responses, and upserts into the database.
/// Supports full and incremental downloads.
/// </summary>
public class JiraSource(
    IOptions<JiraServiceOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    JiraDatabase database,
    IResponseCache cache,
    ILogger<JiraSource> logger)
{
    private readonly JiraServiceOptions options = optionsAccessor.Value;

    public const string SourceName = "jira";

    /// <summary>Performs a full download of all issues matching the configured JQL.</summary>
    public async Task<IngestionResult> DownloadAllAsync(string? jqlOverride, CancellationToken ct)
    {
        string jql = jqlOverride ?? options.DefaultJql ?? $"project = \"{options.DefaultProject}\"";
        return await DownloadAsync(jql, ct);
    }

    /// <summary>Performs an incremental download of issues updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct)
    {
        string baseJql = options.DefaultJql ?? $"project = \"{options.DefaultProject}\"";
        string sinceStr = since.ToString("yyyy-MM-dd HH:mm");
        string jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
        return await DownloadAsync(jql, ct);
    }

    private async Task<IngestionResult> DownloadAsync(string jql, CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

        List<string> existingKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName).ToList();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            string url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            string json;
            try
            {
                HttpClient httpClient = httpClientFactory.CreateClient("jira");
                HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
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
            string cacheKey = CacheFileNaming.GenerateDailyFileName(today, JiraCacheLayout.JsonExtension, existingKeys);
            existingKeys.Add(cacheKey);
            using (MemoryStream cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                await cache.PutAsync(JiraCacheLayout.SourceName, cacheKey, cacheStream, ct);
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int total = root.GetProperty("total").GetInt32();
            JsonElement issues = root.GetProperty("issues");

            using SqliteConnection connection = database.OpenConnection();

            foreach (JsonElement issueJson in issues.EnumerateArray())
            {
                (ProcessOutcome outcome, string? error) = ProcessIssue(issueJson, connection);
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
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        List<string> errors = new List<string>();

        using SqliteConnection connection = database.OpenConnection();

        foreach (string key in cache.EnumerateKeys(JiraCacheLayout.SourceName))
        {
            if (ct.IsCancellationRequested) break;
            if (!cache.TryGet(JiraCacheLayout.SourceName, key, out Stream? stream)) continue;

            using (stream)
            {
                try
                {
                    IEnumerable<(JiraIssueRecord Issue, List<JiraCommentRecord> Comments)> records = ParseCachedFile(stream, key);
                    foreach ((JiraIssueRecord? issue, List<JiraCommentRecord>? comments) in records)
                    {
                        JiraIssueRecord? existing = JiraIssueRecord.SelectSingle(connection, Key: issue.Key);
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

                        foreach (JiraCommentRecord comment in comments)
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
            JiraIssueRecord issue = JiraFieldMapper.MapIssue(issueJson);
            key = issue.Key;

            JiraIssueRecord? existing = JiraIssueRecord.SelectSingle(connection, Key: key);
            if (existing is not null)
            {
                issue.Id = existing.Id;
                JiraIssueRecord.Update(connection, issue);
            }
            else
            {
                JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
            }

            List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            foreach (JiraCommentRecord comment in comments)
                JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);

            List<JiraIssueLinkRecord> links = JiraFieldMapper.MapIssueLinks(issueJson, issue.Key);
            foreach (JiraIssueLinkRecord link in links)
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
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("issues", out JsonElement issues))
            yield break;

        foreach (JsonElement issueJson in issues.EnumerateArray())
        {
            JiraIssueRecord issue = JiraFieldMapper.MapIssue(issueJson);
            List<JiraCommentRecord> comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
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
