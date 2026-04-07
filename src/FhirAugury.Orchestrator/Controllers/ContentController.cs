using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Controllers;

[ApiController]
[Route("api/v1/content")]
public class ContentController(
    SourceHttpClient httpClient,
    FreshnessDecay freshnessDecay,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILoggerFactory loggerFactory) : ControllerBase
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("ContentController");
    private readonly OrchestratorOptions _options = optionsAccessor.Value;

    [HttpGet("refers-to")]
    public async Task<IActionResult> RefersTo(
        [FromQuery] string value,
        [FromQuery] string? sourceType,
        [FromQuery] int? limit,
        [FromQuery] string? sort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        (List<CrossReferenceHit> hits, List<string> warnings) = await FanOutXRefAsync(
            (sourceName, c) => httpClient.ContentRefersToAsync(sourceName, value, sourceType, limit, c), ct);

        int effectiveLimit = Math.Min(limit ?? 200, 500);
        ResultSortOrder sortOrder = ParseSortOrder(sort);
        List<CrossReferenceHit> limited = ScoreAndSort(hits, effectiveLimit, sortOrder);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "refers-to",
            Total = limited.Count,
            Hits = limited,
            Warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [HttpGet("referred-by")]
    public async Task<IActionResult> ReferredBy(
        [FromQuery] string value,
        [FromQuery] string? sourceType,
        [FromQuery] int? limit,
        [FromQuery] string? sort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        (List<CrossReferenceHit> hits, List<string> warnings) = await FanOutXRefAsync(
            (sourceName, c) => httpClient.ContentReferredByAsync(sourceName, value, sourceType, limit, c), ct);

        int effectiveLimit = Math.Min(limit ?? 200, 500);
        ResultSortOrder sortOrder = ParseSortOrder(sort);
        List<CrossReferenceHit> limited = ScoreAndSort(hits, effectiveLimit, sortOrder);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "referred-by",
            Total = limited.Count,
            Hits = limited,
            Warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [HttpGet("cross-referenced")]
    public async Task<IActionResult> CrossReferenced(
        [FromQuery] string value,
        [FromQuery] string? sourceType,
        [FromQuery] int? limit,
        [FromQuery] string? sort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return BadRequest(new { error = "Query parameter 'value' is required" });

        (List<CrossReferenceHit> hits, List<string> warnings) = await FanOutXRefAsync(
            (sourceName, c) => httpClient.ContentCrossReferencedAsync(sourceName, value, sourceType, limit, c), ct);

        int effectiveLimit = Math.Min(limit ?? 200, 500);
        ResultSortOrder sortOrder = ParseSortOrder(sort);
        List<CrossReferenceHit> limited = ScoreAndSort(hits, effectiveLimit, sortOrder);

        return Ok(new CrossReferenceQueryResponse
        {
            Value = value,
            SourceType = sourceType,
            Direction = "cross-referenced",
            Total = limited.Count,
            Hits = limited,
            Warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] List<string> values,
        [FromQuery] List<string>? sources,
        [FromQuery] int? limit,
        [FromQuery] string? sort,
        CancellationToken ct)
    {
        if (values is not { Count: > 0 })
            return BadRequest(new { error = "At least one 'values' parameter is required" });

        if (values.Count > 20)
            return BadRequest(new { error = "Maximum 20 search values allowed" });

        int effectiveLimit = Math.Min(
            limit > 0 ? limit.Value : _options.Search.DefaultLimit,
            _options.Search.MaxLimit);

        IReadOnlyList<string> targetSources = sources is { Count: > 0 }
            ? sources
            : httpClient.GetEnabledSourceNames();

        Dictionary<string, Task<ContentSearchResponse?>> tasks = [];
        List<string> warnings = [];

        foreach (string sourceName in targetSources)
        {
            if (!httpClient.IsSourceEnabled(sourceName))
            {
                warnings.Add($"Source '{sourceName}' is not configured or disabled");
                continue;
            }

            tasks[sourceName] = httpClient.ContentSearchAsync(sourceName, values, sources, effectiveLimit, ct);
        }

        List<ScoredItem> allItems = [];

        foreach ((string sourceName, Task<ContentSearchResponse?> task) in tasks)
        {
            try
            {
                ContentSearchResponse? response = await task;
                if (response is null) continue;

                foreach (ContentSearchHit hit in response.Hits)
                {
                    allItems.Add(new ScoredItem
                    {
                        Source = hit.Source,
                        ContentType = hit.ContentType,
                        Id = hit.Id,
                        Title = hit.Title,
                        Snippet = hit.Snippet ?? "",
                        Score = hit.Score,
                        Url = hit.Url ?? "",
                        UpdatedAt = hit.UpdatedAt,
                        Metadata = hit.Metadata ?? new Dictionary<string, string>(),
                    });
                }

                if (response.Warnings is { Count: > 0 })
                    warnings.AddRange(response.Warnings);
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    _logger.LogWarning("Content search failed for source {Source} ({HttpStatus})", sourceName, statusDescription);
                else
                    _logger.LogWarning(ex, "Content search failed for source {Source}", sourceName);
                warnings.Add($"Search failed for source '{sourceName}': {ex.Message}");
            }
        }

        // Pipeline: normalize → decay → limit per-source → sort
        List<ScoredItem> normalized = ScoreNormalizer.Normalize(allItems);
        List<ScoredItem> decayed = freshnessDecay.Apply(normalized);

        ResultSortOrder sortOrder = ParseSortOrder(sort);
        List<ScoredItem> sorted = sortOrder switch
        {
            ResultSortOrder.Date => decayed
                .GroupBy(i => i.Source)
                .SelectMany(g => g.OrderByDescending(i => i.UpdatedAt ?? DateTimeOffset.MinValue).Take(effectiveLimit))
                .OrderByDescending(i => i.UpdatedAt ?? DateTimeOffset.MinValue)
                .Take(effectiveLimit)
                .ToList(),
            _ => decayed
                .GroupBy(i => i.Source)
                .SelectMany(g => g.OrderByDescending(i => i.Score).Take(effectiveLimit))
                .OrderByDescending(i => i.Score)
                .Take(effectiveLimit)
                .ToList(),
        };

        List<ContentSearchHit> hits = sorted.Select(i => new ContentSearchHit
        {
            Source = i.Source,
            ContentType = i.ContentType,
            Id = i.Id,
            Title = i.Title,
            Snippet = string.IsNullOrEmpty(i.Snippet) ? null : i.Snippet,
            Score = i.Score,
            Url = string.IsNullOrEmpty(i.Url) ? null : i.Url,
            UpdatedAt = i.UpdatedAt,
            Metadata = i.Metadata.Count > 0 ? i.Metadata : null,
        }).ToList();

        return Ok(new ContentSearchResponse
        {
            Values = values,
            Total = hits.Count,
            Hits = hits,
            Warnings = warnings.Count > 0 ? warnings : null,
        });
    }

    [HttpGet("item/{source}/{*id}")]
    public async Task<IActionResult> GetItem(
        [FromRoute] string source,
        [FromRoute] string id,
        [FromQuery] bool includeContent = false,
        [FromQuery] bool includeComments = false,
        [FromQuery] bool includeSnapshot = false,
        CancellationToken ct = default)
    {
        if (!httpClient.IsSourceEnabled(source))
            return NotFound(new { error = $"Source '{source}' not found or disabled" });

        try
        {
            ContentItemResponse? item = await httpClient.ContentGetItemAsync(
                source, source, id, includeContent, includeComments, includeSnapshot, ct);
            if (item is null)
                return NotFound(new { error = $"Item '{id}' not found in source '{source}'" });

            return Ok(item);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<(List<CrossReferenceHit> Hits, List<string> Warnings)> FanOutXRefAsync(
        Func<string, CancellationToken, Task<CrossReferenceQueryResponse?>> call,
        CancellationToken ct)
    {
        Dictionary<string, Task<CrossReferenceQueryResponse?>> tasks = [];
        List<string> warnings = [];

        foreach (string sourceName in httpClient.GetEnabledSourceNames())
        {
            tasks[sourceName] = call(sourceName, ct);
        }

        List<CrossReferenceHit> allHits = [];

        foreach ((string sourceName, Task<CrossReferenceQueryResponse?> task) in tasks)
        {
            try
            {
                CrossReferenceQueryResponse? response = await task;
                if (response is null) continue;
                allHits.AddRange(response.Hits);

                if (response.Warnings is { Count: > 0 })
                    warnings.AddRange(response.Warnings);
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    _logger.LogWarning("XRef query failed for source {Source} ({HttpStatus})", sourceName, statusDescription);
                else
                    _logger.LogDebug(ex, "XRef query failed for source {Source}", sourceName);
                warnings.Add($"Query failed for source '{sourceName}': {ex.Message}");
            }
        }

        return (allHits, warnings);
    }

    private List<CrossReferenceHit> ScoreAndSort(
        List<CrossReferenceHit> hits, int effectiveLimit,
        ResultSortOrder sortOrder = ResultSortOrder.Score)
    {
        List<CrossReferenceHit> deduplicated = DeduplicateHits(hits);

        List<ScoredItem> items = deduplicated.Select(h => new ScoredItem
        {
            Source = h.SourceType,
            ContentType = h.ContentType,
            Id = h.SourceId,
            Title = h.SourceTitle ?? "",
            Snippet = h.Context ?? "",
            Score = h.Score,
            Url = h.SourceUrl ?? "",
            UpdatedAt = h.UpdatedAt,
        }).ToList();

        List<ScoredItem> normalized = ScoreNormalizer.Normalize(items);
        List<ScoredItem> decayed = freshnessDecay.Apply(normalized);

        List<CrossReferenceHit> scored = new(deduplicated.Count);
        for (int i = 0; i < deduplicated.Count; i++)
            scored.Add(deduplicated[i] with { Score = decayed[i].Score });

        return sortOrder switch
        {
            ResultSortOrder.Date => scored
                .OrderByDescending(h => h.UpdatedAt ?? DateTimeOffset.MinValue)
                .Take(effectiveLimit)
                .ToList(),
            _ => scored
                .OrderByDescending(h => h.Score)
                .Take(effectiveLimit)
                .ToList(),
        };
    }

    private static List<CrossReferenceHit> DeduplicateHits(List<CrossReferenceHit> hits)
    {
        Dictionary<string, CrossReferenceHit> best = new();
        foreach (CrossReferenceHit hit in hits)
        {
            string key = $"{hit.SourceType}|{hit.SourceId}|{hit.TargetType}|{hit.TargetId}";
            if (!best.TryGetValue(key, out CrossReferenceHit? existing) || hit.Score > existing.Score)
                best[key] = hit;
        }
        return best.Values.ToList();
    }

    private static ResultSortOrder ParseSortOrder(string? sort) =>
        string.Equals(sort, "date", StringComparison.OrdinalIgnoreCase)
            ? ResultSortOrder.Date
            : ResultSortOrder.Score;
}
