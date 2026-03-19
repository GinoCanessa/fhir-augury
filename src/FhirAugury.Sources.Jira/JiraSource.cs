using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.Jira;

/// <summary>Jira data source implementing IDataSource for HL7 FHIR Jira issues.</summary>
public class JiraSource(JiraSourceOptions options, HttpClient httpClient, ILogger<JiraSource>? logger = null) : IDataSource
{
    public string SourceName => "jira";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var jql = ingestionOptions.Filter ?? options.DefaultJql;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;
        var generatedFiles = shouldCache ? cache!.EnumerateKeys("jira").ToList() : [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger?.LogInformation("Fetching issues: startAt={StartAt}", startAt);

            string json;
            try
            {
                var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "jira");
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to fetch issues at startAt={StartAt}", startAt);
                errors.Add(new IngestionError($"page:{startAt}", $"HTTP request failed: {ex.Message}", ex));
                break;
            }

            // Write to cache if enabled
            if (shouldCache)
            {
                var cacheKey = CacheFileNaming.GenerateDailyFileName(today, "json", generatedFiles);
                generatedFiles.Add(cacheKey);
                using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                await cache!.PutAsync("jira", cacheKey, cacheStream, ct);
            }

            // Skip processing in WriteOnly mode
            if (options.CacheMode == CacheMode.WriteOnly)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                startAt += root.GetProperty("issues").GetArrayLength();
                hasMore = startAt < total;
                continue;
            }

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                var issues = root.GetProperty("issues");

                using var connection = db.OpenConnection();

                foreach (var issueJson in issues.EnumerateArray())
                {
                    var result = ProcessIssue(issueJson, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

                    if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                        logger?.LogInformation("Jira download progress: {Count} issues processed", itemsProcessed);

                    switch (result.Outcome)
                    {
                        case ProcessOutcome.New:
                            itemsNew++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Updated:
                            itemsUpdated++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            errors.Add(result.Error!);
                            break;
                    }
                }

                startAt += issues.GetArrayLength();
                hasMore = startAt < total;
            }
        }

        // Update cache metadata
        if (shouldCache)
        {
            await CacheMetadataService.WriteMetadataAsync(
                cache!.RootPath, "_meta_jira.json",
                new JiraCacheMetadata
                {
                    LastSyncDate = today.ToString("yyyy-MM-dd"),
                    LastSyncTimestamp = DateTimeOffset.UtcNow,
                    TotalFiles = generatedFiles.Count,
                    Format = "json",
                }, ct);
        }

        logger?.LogInformation("Jira full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult
        {
            ItemsProcessed = itemsProcessed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var baseJql = ingestionOptions.Filter ?? options.DefaultJql;
        var sinceStr = since.ToString("yyyy-MM-dd HH:mm");
        var jql = $"{baseJql} AND updated >= '{sinceStr}' ORDER BY updated ASC";
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;
        var generatedFiles = shouldCache ? cache!.EnumerateKeys("jira").ToList() : [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        int startAt = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/2/search?jql={Uri.EscapeDataString(jql)}" +
                      $"&startAt={startAt}&maxResults={options.PageSize}&fields=*all&expand=renderedFields";

            logger?.LogInformation("Fetching incremental issues: startAt={StartAt}, since={Since}", startAt, sinceStr);

            string json;
            try
            {
                var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "jira");
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to fetch incremental issues at startAt={StartAt}", startAt);
                errors.Add(new IngestionError($"page:{startAt}", $"HTTP request failed: {ex.Message}", ex));
                break;
            }

            if (shouldCache)
            {
                var cacheKey = CacheFileNaming.GenerateDailyFileName(today, "json", generatedFiles);
                generatedFiles.Add(cacheKey);
                using var cacheStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                await cache!.PutAsync("jira", cacheKey, cacheStream, ct);
            }

            if (options.CacheMode == CacheMode.WriteOnly)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                startAt += root.GetProperty("issues").GetArrayLength();
                hasMore = startAt < total;
                continue;
            }

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var total = root.GetProperty("total").GetInt32();
                var issues = root.GetProperty("issues");

                using var connection = db.OpenConnection();

                foreach (var issueJson in issues.EnumerateArray())
                {
                    var result = ProcessIssue(issueJson, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

                    if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                        logger?.LogInformation("Jira incremental progress: {Count} issues processed", itemsProcessed);

                    switch (result.Outcome)
                    {
                        case ProcessOutcome.New:
                            itemsNew++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Updated:
                            itemsUpdated++;
                            newAndUpdated.Add(result.Item!);
                            break;
                        case ProcessOutcome.Failed:
                            itemsFailed++;
                            errors.Add(result.Error!);
                            break;
                    }
                }

                startAt += issues.GetArrayLength();
                hasMore = startAt < total;
            }
        }

        if (shouldCache)
        {
            await CacheMetadataService.WriteMetadataAsync(
                cache!.RootPath, "_meta_jira.json",
                new JiraCacheMetadata
                {
                    LastSyncDate = today.ToString("yyyy-MM-dd"),
                    LastSyncTimestamp = DateTimeOffset.UtcNow,
                    TotalFiles = generatedFiles.Count,
                    Format = "json",
                }, ct);
        }

        logger?.LogInformation("Jira incremental download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult
        {
            ItemsProcessed = itemsProcessed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    private async Task<IngestionResult> LoadFromCacheAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        var cache = options.Cache ?? throw new InvalidOperationException("Cache is required for CacheOnly mode.");

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();
        using var connection = db.OpenConnection();

        var keys = cache.EnumerateKeys("jira");

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;

            if (!cache.TryGet("jira", key, out var stream))
                continue;

            using (stream)
            {
                try
                {
                    var records = ParseCachedFile(stream, key);
                    foreach (var (issue, comments) in records)
                    {
                        var existing = JiraIssueRecord.SelectSingle(connection, Key: issue.Key);
                        bool isNew;

                        if (existing is not null)
                        {
                            issue.Id = existing.Id;
                            JiraIssueRecord.Update(connection, issue);
                            isNew = false;
                        }
                        else
                        {
                            JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
                            isNew = true;
                        }

                        foreach (var comment in comments)
                        {
                            JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
                        }

                        itemsProcessed++;
                        var searchableFields = BuildSearchableFields(issue, comments);
                        var item = new IngestedItem
                        {
                            SourceType = SourceName,
                            SourceId = issue.Key,
                            Title = issue.Title,
                            SearchableTextFields = searchableFields,
                        };

                        if (isNew) itemsNew++; else itemsUpdated++;
                        newAndUpdated.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to process cached file {Key}", key);
                    itemsFailed++;
                    errors.Add(new IngestionError(key, $"Failed to process cached file: {ex.Message}", ex));
                }
            }

            if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                logger?.LogInformation("Jira cache ingestion progress: {Count} issues processed", itemsProcessed);
        }

        logger?.LogInformation("Jira cache-only ingestion complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult
        {
            ItemsProcessed = itemsProcessed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
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

    public async Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var url = $"{options.BaseUrl}/rest/api/2/issue/{Uri.EscapeDataString(identifier)}?fields=*all&expand=renderedFields";

        logger?.LogInformation("Fetching single issue: {Identifier}", identifier);

        try
        {
            var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "jira");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            using var connection = db.OpenConnection();

            var result = ProcessIssue(doc.RootElement, connection, ingestionOptions.Verbose);

            switch (result.Outcome)
            {
                case ProcessOutcome.New:
                    itemsNew++;
                    newAndUpdated.Add(result.Item!);
                    break;
                case ProcessOutcome.Updated:
                    itemsUpdated++;
                    newAndUpdated.Add(result.Item!);
                    break;
                case ProcessOutcome.Failed:
                    itemsFailed++;
                    errors.Add(result.Error!);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch issue {Identifier}", identifier);
            itemsFailed++;
            errors.Add(new IngestionError(identifier, $"Failed to fetch issue: {ex.Message}", ex));
        }

        return new IngestionResult
        {
            ItemsProcessed = itemsNew + itemsUpdated + itemsFailed,
            ItemsNew = itemsNew,
            ItemsUpdated = itemsUpdated,
            ItemsFailed = itemsFailed,
            Errors = errors,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            NewAndUpdatedItems = newAndUpdated,
        };
    }

    private ProcessResult ProcessIssue(JsonElement issueJson, Microsoft.Data.Sqlite.SqliteConnection connection, bool verbose)
    {
        string key = string.Empty;
        try
        {
            var issue = JiraFieldMapper.MapIssue(issueJson);
            key = issue.Key;

            // Check if the issue already exists
            var existing = JiraIssueRecord.SelectSingle(connection, Key: key);
            bool isNew;

            if (existing is not null)
            {
                // Preserve the existing ID and update
                issue.Id = existing.Id;
                JiraIssueRecord.Update(connection, issue);
                isNew = false;
            }
            else
            {
                JiraIssueRecord.Insert(connection, issue, ignoreDuplicates: true);
                isNew = true;
            }

            // Process comments
            var comments = JiraFieldMapper.MapComments(issueJson, issue.Id, issue.Key);
            foreach (var comment in comments)
            {
                JiraCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }

            if (verbose)
            {
                logger?.LogDebug("{Action} issue {Key}: {Title}", isNew ? "Inserted" : "Updated", key, issue.Title);
            }

            var searchableFields = BuildSearchableFields(issue, comments);

            var item = new IngestedItem
            {
                SourceType = SourceName,
                SourceId = issue.Key,
                Title = issue.Title,
                SearchableTextFields = searchableFields,
            };

            return new ProcessResult(isNew ? ProcessOutcome.New : ProcessOutcome.Updated, item, null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to process issue {Key}", key);
            return new ProcessResult(ProcessOutcome.Failed, null, new IngestionError(key, ex.Message, ex));
        }
    }

    private static List<string> BuildSearchableFields(JiraIssueRecord issue, List<JiraCommentRecord> comments)
    {
        var fields = new List<string>();

        AddIfNotEmpty(fields, issue.Key);
        AddIfNotEmpty(fields, issue.Title);
        AddIfNotEmpty(fields, issue.Description);
        AddIfNotEmpty(fields, issue.Summary);
        AddIfNotEmpty(fields, issue.Status);
        AddIfNotEmpty(fields, issue.Resolution);
        AddIfNotEmpty(fields, issue.ResolutionDescription);
        AddIfNotEmpty(fields, issue.WorkGroup);
        AddIfNotEmpty(fields, issue.Specification);
        AddIfNotEmpty(fields, issue.Labels);

        foreach (var comment in comments)
        {
            AddIfNotEmpty(fields, comment.Body);
        }

        return fields;
    }

    private static void AddIfNotEmpty(List<string> fields, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(value);
    }

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error);
}
