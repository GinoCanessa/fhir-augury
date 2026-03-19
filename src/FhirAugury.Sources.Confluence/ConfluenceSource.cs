using System.Text.Json;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Sources.Confluence;

/// <summary>Confluence data source implementing IDataSource for wiki pages.</summary>
public class ConfluenceSource(ConfluenceSourceOptions options, HttpClient httpClient, ILogger<ConfluenceSource>? logger = null) : IDataSource
{
    public string SourceName => "confluence";

    public async Task<IngestionResult> DownloadAllAsync(IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var spaces = ingestionOptions.Filter is not null
            ? [ingestionOptions.Filter]
            : options.Spaces;

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;
        int cachedFileCount = 0;

        using var connection = db.OpenConnection();

        foreach (var spaceKey in spaces)
        {
            if (ct.IsCancellationRequested) break;

            logger?.LogInformation("Fetching space: {SpaceKey}", spaceKey);

            // Upsert space record
            try
            {
                var spaceUrl = $"{options.BaseUrl}/rest/api/space/{Uri.EscapeDataString(spaceKey)}";
                using var spaceResponse = await HttpRetryHelper.GetWithRetryAsync(httpClient, spaceUrl, ct, sourceName: "confluence");
                spaceResponse.EnsureSuccessStatusCode();
                var spaceJson = await spaceResponse.Content.ReadAsStringAsync(ct);
                using var spaceDoc = JsonDocument.Parse(spaceJson);
                UpsertSpace(connection, spaceDoc.RootElement, spaceKey);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to fetch space metadata for {SpaceKey}", spaceKey);
            }

            // Paginate pages in this space
            int start = 0;
            bool hasMore = true;
            var processedPages = new List<ConfluencePageRecord>();

            while (hasMore && !ct.IsCancellationRequested)
            {
                var url = $"{options.BaseUrl}/rest/api/content?spaceKey={Uri.EscapeDataString(spaceKey)}" +
                          $"&type=page&expand=body.storage,version,ancestors,metadata.labels" +
                          $"&start={start}&limit={options.PageSize}";

                logger?.LogInformation("Fetching pages: space={Space}, start={Start}", spaceKey, start);

                JsonDocument doc;
                try
                {
                    using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "confluence");
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    doc = JsonDocument.Parse(json);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to fetch pages for space {Space} at start={Start}", spaceKey, start);
                    errors.Add(new IngestionError($"space:{spaceKey}:start:{start}", $"HTTP request failed: {ex.Message}", ex));
                    break;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var results = root.GetProperty("results");

                    foreach (var pageJson in results.EnumerateArray())
                    {
                        // Cache individual page JSON
                        if (shouldCache)
                        {
                            var pageId = pageJson.GetProperty("id").GetString()!;
                            var cacheKey = $"pages/{pageId}.json";
                            var pageBytes = System.Text.Encoding.UTF8.GetBytes(pageJson.GetRawText());
                            using var cacheStream = new MemoryStream(pageBytes);
                            await cache!.PutAsync("confluence", cacheKey, cacheStream, ct);
                            cachedFileCount++;
                        }

                        if (options.CacheMode == CacheMode.WriteOnly)
                        {
                            itemsProcessed++;
                            continue;
                        }

                        var result = ProcessPage(pageJson, spaceKey, connection, ingestionOptions.Verbose);
                        itemsProcessed++;

                        // Track page for comment fetching
                        if (result.Outcome is ProcessOutcome.New or ProcessOutcome.Updated)
                        {
                            var processedPage = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageJson.GetProperty("id").GetString()!);
                            if (processedPage is not null)
                                processedPages.Add(processedPage);
                        }

                        if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                            logger?.LogInformation("Confluence download progress: {Count} pages processed", itemsProcessed);

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

                    // Check for more pages
                    var size = results.GetArrayLength();
                    start += size;
                    hasMore = root.TryGetProperty("_links", out var links) &&
                              links.TryGetProperty("next", out _);
                }

                // Fetch comments for processed pages in this batch
                foreach (var page in processedPages)
                {
                    if (ct.IsCancellationRequested) break;
                    await FetchPageCommentsAsync(connection, page.ConfluenceId, page.Id, ct, errors);
                }
                processedPages.Clear();
            }
        }

        logger?.LogInformation("Confluence full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        if (shouldCache)
        {
            await CacheMetadataService.WriteMetadataAsync(
                cache!.RootPath, "_meta_confluence.json",
                new ConfluenceCacheMetadata
                {
                    LastSyncDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                    LastSyncTimestamp = DateTimeOffset.UtcNow,
                    TotalFiles = cachedFileCount,
                    Format = "json",
                }, ct);
        }

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        if (options.CacheMode == CacheMode.CacheOnly)
            return await LoadFromCacheAsync(ingestionOptions, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var spaces = ingestionOptions.Filter is not null
            ? [ingestionOptions.Filter]
            : options.Spaces;

        var cache = options.Cache;
        var shouldCache = cache is not null && options.CacheMode is CacheMode.WriteThrough or CacheMode.WriteOnly;

        using var connection = db.OpenConnection();

        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        var spacesParam = string.Join(",", spaces.Select(s => $"\"{s}\""));
        var cql = $"lastModified >= \"{sinceStr}\" AND space in ({spacesParam}) AND type = page";

        int start = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/content/search?cql={Uri.EscapeDataString(cql)}" +
                      $"&expand=body.storage,version,ancestors,metadata.labels" +
                      $"&start={start}&limit={options.PageSize}";

            logger?.LogInformation("Fetching incremental pages: start={Start}, since={Since}", start, sinceStr);

            JsonDocument doc;
            try
            {
                using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "confluence");
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to fetch incremental pages at start={Start}", start);
                errors.Add(new IngestionError($"incremental:start:{start}", $"HTTP request failed: {ex.Message}", ex));
                break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var results = root.GetProperty("results");

                foreach (var pageJson in results.EnumerateArray())
                {
                    // Cache individual page JSON
                    if (shouldCache)
                    {
                        var pageId = pageJson.GetProperty("id").GetString()!;
                        var cacheKey = $"pages/{pageId}.json";
                        var pageBytes = System.Text.Encoding.UTF8.GetBytes(pageJson.GetRawText());
                        using var cacheStream = new MemoryStream(pageBytes);
                        await cache!.PutAsync("confluence", cacheKey, cacheStream, ct);
                    }

                    if (options.CacheMode == CacheMode.WriteOnly)
                    {
                        itemsProcessed++;
                        continue;
                    }

                    var spaceKey = GetNestedString(pageJson, "space", "key") ?? spaces.FirstOrDefault() ?? "FHIR";
                    var result = ProcessPage(pageJson, spaceKey, connection, ingestionOptions.Verbose);
                    itemsProcessed++;

                    if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                        logger?.LogInformation("Confluence incremental progress: {Count} pages processed", itemsProcessed);

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

                var size = results.GetArrayLength();
                start += size;
                hasMore = root.TryGetProperty("_links", out var links) &&
                          links.TryGetProperty("next", out _);
            }
        }

        logger?.LogInformation("Confluence incremental download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    public async Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions ingestionOptions, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<IngestionError>();
        var newAndUpdated = new List<IngestedItem>();
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0;

        using var db = new DatabaseService(ingestionOptions.DatabasePath);
        db.InitializeDatabase();

        var url = $"{options.BaseUrl}/rest/api/content/{Uri.EscapeDataString(identifier)}" +
                  $"?expand=body.storage,version,ancestors,metadata.labels,space";

        logger?.LogInformation("Fetching single page: {Identifier}", identifier);

        try
        {
            using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "confluence");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            using var connection = db.OpenConnection();

            var spaceKey = GetNestedString(doc.RootElement, "space", "key") ?? "FHIR";
            var result = ProcessPage(doc.RootElement, spaceKey, connection, ingestionOptions.Verbose);

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

            // Fetch comments for this page
            await FetchPageCommentsAsync(connection, identifier, result.DbId, ct, errors);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to fetch page {Identifier}", identifier);
            itemsFailed++;
            errors.Add(new IngestionError(identifier, $"Failed to fetch page: {ex.Message}", ex));
        }

        return BuildResult(startedAt, itemsNew + itemsUpdated + itemsFailed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
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

        var keys = cache.EnumerateKeys("confluence");

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;

            if (!cache.TryGet("confluence", key, out var stream))
                continue;

            using (stream)
            {
                try
                {
                    using var doc = JsonDocument.Parse(stream);
                    var spaceKey = GetNestedString(doc.RootElement, "space", "key") ?? "FHIR";

                    // Upsert space record from page content
                    if (doc.RootElement.TryGetProperty("space", out var spaceJson))
                    {
                        UpsertSpace(connection, spaceJson, spaceKey);
                    }

                    var result = ProcessPage(doc.RootElement, spaceKey, connection, false);
                    itemsProcessed++;

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
                    logger?.LogWarning(ex, "Failed to process cached file {Key}", key);
                    itemsFailed++;
                    errors.Add(new IngestionError(key, $"Failed to process cached file: {ex.Message}", ex));
                }
            }

            if (itemsProcessed % 1000 == 0 && itemsProcessed > 0)
                logger?.LogInformation("Confluence cache ingestion progress: {Count} pages processed", itemsProcessed);
        }

        logger?.LogInformation("Confluence cache-only ingestion complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return BuildResult(startedAt, itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, newAndUpdated);
    }

    private ProcessResult ProcessPage(JsonElement pageJson, string spaceKey, Microsoft.Data.Sqlite.SqliteConnection connection, bool verbose)
    {
        string pageId = string.Empty;
        int dbId = 0;
        try
        {
            pageId = pageJson.GetProperty("id").GetString()!;
            var title = GetString(pageJson, "title") ?? string.Empty;

            var bodyStorage = GetNestedString(pageJson, "body", "storage", "value");
            var bodyPlain = ConfluenceContentParser.ToPlainText(bodyStorage);

            string? labels = null;
            if (pageJson.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("labels", out var labelsObj) &&
                labelsObj.TryGetProperty("results", out var labelsArray))
            {
                var labelNames = new List<string>();
                foreach (var label in labelsArray.EnumerateArray())
                {
                    var name = label.GetProperty("name").GetString();
                    if (!string.IsNullOrEmpty(name)) labelNames.Add(name);
                }
                if (labelNames.Count > 0) labels = string.Join(",", labelNames);
            }

            string? parentId = null;
            if (pageJson.TryGetProperty("ancestors", out var ancestors) &&
                ancestors.ValueKind == JsonValueKind.Array &&
                ancestors.GetArrayLength() > 0)
            {
                var lastAncestor = ancestors[ancestors.GetArrayLength() - 1];
                parentId = lastAncestor.GetProperty("id").GetString();
            }

            var versionNumber = 1;
            string? lastModifiedBy = null;
            if (pageJson.TryGetProperty("version", out var version))
            {
                if (version.TryGetProperty("number", out var num))
                    versionNumber = num.GetInt32();
                lastModifiedBy = GetNestedString(version, "by", "displayName");
            }

            var url = $"{options.BaseUrl}/pages/{pageId}";
            if (pageJson.TryGetProperty("_links", out var links) &&
                links.TryGetProperty("webui", out var webui))
            {
                url = $"{options.BaseUrl}{webui.GetString()}";
            }

            var record = new ConfluencePageRecord
            {
                Id = ConfluencePageRecord.GetIndex(),
                ConfluenceId = pageId,
                SpaceKey = spaceKey,
                Title = title,
                ParentId = parentId,
                BodyStorage = bodyStorage,
                BodyPlain = bodyPlain,
                Labels = labels,
                VersionNumber = versionNumber,
                LastModifiedBy = lastModifiedBy,
                LastModifiedAt = ParseDate(GetNestedString(pageJson, "version", "when")),
                Url = url,
            };

            var existing = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
            bool isNew;

            if (existing is not null)
            {
                record.Id = existing.Id;
                ConfluencePageRecord.Update(connection, record);
                isNew = false;
            }
            else
            {
                ConfluencePageRecord.Insert(connection, record, ignoreDuplicates: true);
                isNew = true;
            }

            dbId = record.Id;

            if (verbose)
            {
                logger?.LogDebug("{Action} page {PageId}: {Title}", isNew ? "Inserted" : "Updated", pageId, title);
            }

            var searchableFields = new List<string>();
            AddIfNotEmpty(searchableFields, title);
            AddIfNotEmpty(searchableFields, bodyPlain);
            AddIfNotEmpty(searchableFields, labels);

            var item = new IngestedItem
            {
                SourceType = SourceName,
                SourceId = pageId,
                Title = title,
                SearchableTextFields = searchableFields,
            };

            return new ProcessResult(isNew ? ProcessOutcome.New : ProcessOutcome.Updated, item, null, dbId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to process page {PageId}", pageId);
            return new ProcessResult(ProcessOutcome.Failed, null, new IngestionError(pageId, ex.Message, ex), 0);
        }
    }

    private void UpsertSpace(Microsoft.Data.Sqlite.SqliteConnection connection, JsonElement spaceJson, string spaceKey)
    {
        var record = new ConfluenceSpaceRecord
        {
            Id = ConfluenceSpaceRecord.GetIndex(),
            Key = spaceKey,
            Name = GetString(spaceJson, "name") ?? spaceKey,
            Description = GetNestedString(spaceJson, "description", "plain", "value"),
            Url = $"{options.BaseUrl}/display/{spaceKey}",
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        var existing = ConfluenceSpaceRecord.SelectSingle(connection, Key: spaceKey);
        if (existing is not null)
        {
            record.Id = existing.Id;
            ConfluenceSpaceRecord.Update(connection, record);
        }
        else
        {
            ConfluenceSpaceRecord.Insert(connection, record, ignoreDuplicates: true);
        }
    }

    private async Task FetchPageCommentsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string confluencePageId, int pageDbId,
        CancellationToken ct, List<IngestionError> errors)
    {
        try
        {
            var url = $"{options.BaseUrl}/rest/api/content/{Uri.EscapeDataString(confluencePageId)}/child/comment?expand=body.storage";
            using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: "confluence");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");

            foreach (var commentJson in results.EnumerateArray())
            {
                var comment = new ConfluenceCommentRecord
                {
                    Id = ConfluenceCommentRecord.GetIndex(),
                    PageId = pageDbId,
                    ConfluencePageId = confluencePageId,
                    Author = GetNestedString(commentJson, "version", "by", "displayName") ?? "Unknown",
                    CreatedAt = ParseDate(GetNestedString(commentJson, "version", "when")),
                    Body = ConfluenceContentParser.ToPlainText(GetNestedString(commentJson, "body", "storage", "value")),
                };
                ConfluenceCommentRecord.Insert(connection, comment, ignoreDuplicates: true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch comments for page {PageId}", confluencePageId);
            errors.Add(new IngestionError($"comments:{confluencePageId}", ex.Message, ex));
        }
    }

    private static string? GetString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.ToString();
    }

    private static string? GetNestedString(JsonElement parent, string prop1, string prop2, string? prop3 = null)
    {
        if (!parent.TryGetProperty(prop1, out var p1) || p1.ValueKind == JsonValueKind.Null)
            return null;
        if (!p1.TryGetProperty(prop2, out var p2) || p2.ValueKind == JsonValueKind.Null)
            return null;
        if (prop3 is null)
            return p2.ToString();
        if (!p2.TryGetProperty(prop3, out var p3) || p3.ValueKind == JsonValueKind.Null)
            return null;
        return p3.ToString();
    }

    private static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? DateTimeOffset.MinValue : DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;

    private static void AddIfNotEmpty(List<string> fields, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields.Add(value);
    }

    private static IngestionResult BuildResult(
        DateTimeOffset startedAt, int processed, int newCount, int updated, int failed,
        List<IngestionError> errors, List<IngestedItem> newAndUpdated) => new()
    {
        ItemsProcessed = processed,
        ItemsNew = newCount,
        ItemsUpdated = updated,
        ItemsFailed = failed,
        Errors = errors,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        NewAndUpdatedItems = newAndUpdated,
    };

    private enum ProcessOutcome { New, Updated, Failed }

    private record ProcessResult(ProcessOutcome Outcome, IngestedItem? Item, IngestionError? Error, int DbId);
}
