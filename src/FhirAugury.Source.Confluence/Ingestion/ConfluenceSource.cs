using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Caching;
using FhirAugury.Source.Confluence.Cache;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static FhirAugury.Common.DateTimeHelper;
using static FhirAugury.Common.JsonElementHelper;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Fetches pages from the Confluence REST API, caches responses, and upserts into the database.
/// </summary>
public class ConfluenceSource(
    ConfluenceServiceOptions options,
    IHttpClientFactory httpClientFactory,
    ConfluenceDatabase database,
    IResponseCache cache,
    ILogger<ConfluenceSource> logger)
{
    public const string SourceName = "confluence";

    /// <summary>Performs a full download of all pages in configured spaces.</summary>
    public async Task<IngestionResult> DownloadAllAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        var httpClient = httpClientFactory.CreateClient("confluence");
        using var connection = database.OpenConnection();

        foreach (var spaceKey in options.Spaces)
        {
            if (ct.IsCancellationRequested) break;

            logger.LogInformation("Fetching space: {SpaceKey}", spaceKey);
            await UpsertSpaceAsync(connection, spaceKey, ct, errors);

            int start = 0;
            bool hasMore = true;

            while (hasMore && !ct.IsCancellationRequested)
            {
                var url = $"{options.BaseUrl}/rest/api/content?spaceKey={Uri.EscapeDataString(spaceKey)}" +
                          $"&type=page&expand=body.storage,version,ancestors,metadata.labels" +
                          $"&start={start}&limit={options.PageSize}";

                logger.LogInformation("Fetching pages: space={Space}, start={Start}", spaceKey, start);

                JsonDocument doc;
                try
                {
                    using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: SourceName);
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct);
                    doc = JsonDocument.Parse(json);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to fetch pages for space {Space} at start={Start}", spaceKey, start);
                    errors.Add($"space:{spaceKey}:start:{start}: {ex.Message}");
                    break;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var results = root.GetProperty("results");

                    foreach (var pageJson in results.EnumerateArray())
                    {
                        // Cache individual page JSON
                        var pageId = pageJson.GetProperty("id").GetString()!;
                        var cacheKey = ConfluenceCacheLayout.GetPageCacheKey(spaceKey, pageId);
                        var pageBytes = System.Text.Encoding.UTF8.GetBytes(pageJson.GetRawText());
                        using var cacheStream = new MemoryStream(pageBytes);
                        await cache.PutAsync(SourceName, cacheKey, cacheStream, ct);

                        var result = ProcessPage(pageJson, spaceKey, connection);
                        itemsProcessed++;

                        switch (result)
                        {
                            case PageResult.New: itemsNew++; break;
                            case PageResult.Updated: itemsUpdated++; break;
                            case PageResult.Failed: itemsFailed++; break;
                        }

                        if (itemsProcessed % 500 == 0)
                            logger.LogInformation("Confluence progress: {Count} pages processed", itemsProcessed);
                    }

                    var size = results.GetArrayLength();
                    start += size;
                    hasMore = root.TryGetProperty("_links", out var links) &&
                              links.TryGetProperty("next", out _);
                }
            }
        }

        // Write cache metadata
        await CacheMetadataService.WriteMetadataAsync(
            cache.RootPath, ConfluenceCacheLayout.MetadataFileName,
            new ConfluenceCacheMetadata
            {
                LastSyncDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                LastSyncTimestamp = DateTimeOffset.UtcNow,
                TotalFiles = itemsProcessed,
                Format = "json",
            }, ct);

        logger.LogInformation(
            "Confluence full download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
        {
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Performs an incremental download of pages updated since the given timestamp.</summary>
    public async Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        var httpClient = httpClientFactory.CreateClient("confluence");
        using var connection = database.OpenConnection();

        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        var spacesParam = string.Join(",", options.Spaces.Select(s => $"\"{s}\""));
        var cql = $"lastModified >= \"{sinceStr}\" AND space in ({spacesParam}) AND type = page";

        int start = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"{options.BaseUrl}/rest/api/content/search?cql={Uri.EscapeDataString(cql)}" +
                      $"&expand=body.storage,version,ancestors,metadata.labels" +
                      $"&start={start}&limit={options.PageSize}";

            logger.LogInformation("Fetching incremental pages: start={Start}, since={Since}", start, sinceStr);

            JsonDocument doc;
            try
            {
                using var response = await HttpRetryHelper.GetWithRetryAsync(httpClient, url, ct, sourceName: SourceName);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch incremental pages at start={Start}", start);
                errors.Add($"incremental:start:{start}: {ex.Message}");
                break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var results = root.GetProperty("results");

                foreach (var pageJson in results.EnumerateArray())
                {
                    var spaceKey = GetNestedString(pageJson, "space", "key") ?? options.Spaces.FirstOrDefault() ?? "FHIR";

                    var pageId = pageJson.GetProperty("id").GetString()!;
                    var cacheKey = ConfluenceCacheLayout.GetPageCacheKey(spaceKey, pageId);
                    var pageBytes = System.Text.Encoding.UTF8.GetBytes(pageJson.GetRawText());
                    using var cacheStream = new MemoryStream(pageBytes);
                    await cache.PutAsync(SourceName, cacheKey, cacheStream, ct);

                    var result = ProcessPage(pageJson, spaceKey, connection);
                    itemsProcessed++;

                    switch (result)
                    {
                        case PageResult.New: itemsNew++; break;
                        case PageResult.Updated: itemsUpdated++; break;
                        case PageResult.Failed: itemsFailed++; break;
                    }
                }

                var size = results.GetArrayLength();
                start += size;
                hasMore = root.TryGetProperty("_links", out var links) &&
                          links.TryGetProperty("next", out _);
            }
        }

        logger.LogInformation(
            "Confluence incremental download complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
        {
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Rebuilds the database from cached page JSON files.</summary>
    public async Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        int itemsNew = 0, itemsUpdated = 0, itemsFailed = 0, itemsProcessed = 0;
        var errors = new List<string>();

        using var connection = database.OpenConnection();

        var keys = cache.EnumerateKeys(SourceName);

        foreach (var key in keys)
        {
            if (ct.IsCancellationRequested) break;
            if (!cache.TryGet(SourceName, key, out var stream)) continue;

            using (stream)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var spaceKey = GetNestedString(doc.RootElement, "space", "key") ?? "FHIR";

                    if (doc.RootElement.TryGetProperty("space", out var spaceJson))
                        UpsertSpaceFromJson(connection, spaceJson, spaceKey, ct);

                    var result = ProcessPage(doc.RootElement, spaceKey, connection);
                    itemsProcessed++;

                    switch (result)
                    {
                        case PageResult.New: itemsNew++; break;
                        case PageResult.Updated: itemsUpdated++; break;
                        case PageResult.Failed: itemsFailed++; break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process cached file {Key}", key);
                    itemsFailed++;
                    errors.Add($"cache:{key}: {ex.Message}");
                }
            }

            if (itemsProcessed % 500 == 0 && itemsProcessed > 0)
                logger.LogInformation("Confluence cache ingestion progress: {Count} pages processed", itemsProcessed);
        }

        logger.LogInformation(
            "Confluence cache-only ingestion complete: {Processed} processed, {New} new, {Updated} updated, {Failed} failed",
            itemsProcessed, itemsNew, itemsUpdated, itemsFailed);

        return new IngestionResult(itemsProcessed, itemsNew, itemsUpdated, itemsFailed, errors, startedAt)
        {
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private PageResult ProcessPage(JsonElement pageJson, string spaceKey, SqliteConnection connection)
    {
        string pageId = string.Empty;
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

            // Extract and store internal page links
            var extractedLinks = ConfluenceLinkExtractor.ExtractLinks(bodyStorage);
            foreach (var (targetPageId, linkType) in extractedLinks)
            {
                var linkRecord = new ConfluencePageLinkRecord
                {
                    Id = ConfluencePageLinkRecord.GetIndex(),
                    SourcePageId = pageId,
                    TargetPageId = targetPageId,
                    LinkType = linkType,
                };
                ConfluencePageLinkRecord.Insert(connection, linkRecord, ignoreDuplicates: true);
            }

            return isNew ? PageResult.New : PageResult.Updated;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process page {PageId}", pageId);
            return PageResult.Failed;
        }
    }

    private async Task UpsertSpaceAsync(SqliteConnection connection, string spaceKey, CancellationToken ct, List<string> errors)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient("confluence");
            var spaceUrl = $"{options.BaseUrl}/rest/api/space/{Uri.EscapeDataString(spaceKey)}";
            using var spaceResponse = await HttpRetryHelper.GetWithRetryAsync(httpClient, spaceUrl, ct, sourceName: SourceName);
            spaceResponse.EnsureSuccessStatusCode();
            var spaceJson = await spaceResponse.Content.ReadAsStringAsync(ct);
            using var spaceDoc = JsonDocument.Parse(spaceJson);
            UpsertSpaceFromJson(connection, spaceDoc.RootElement, spaceKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch space metadata for {SpaceKey}", spaceKey);
            errors.Add($"space-metadata:{spaceKey}: {ex.Message}");
        }
    }

    private void UpsertSpaceFromJson(SqliteConnection connection, JsonElement spaceJson, string spaceKey, CancellationToken ct = default)
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

    private enum PageResult { New, Updated, Failed }
}

/// <summary>Represents the outcome of an ingestion run.</summary>
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
