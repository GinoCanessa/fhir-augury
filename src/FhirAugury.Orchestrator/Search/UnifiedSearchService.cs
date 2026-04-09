using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Search;

/// <summary>
/// Dispatches search queries to all enabled source services in parallel,
/// then normalizes and applies freshness decay to merge results.
/// </summary>
public class UnifiedSearchService(
    SourceHttpClient httpClient,
    FreshnessDecay freshnessDecay,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<UnifiedSearchService> logger)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;
    /// <summary>
    /// Executes a unified search across all enabled source services.
    /// </summary>
    public async Task<(List<ScoredItem> Results, List<string> Warnings)> SearchAsync(
        string query,
        IReadOnlyList<string>? sources,
        int limit,
        CancellationToken ct)
    {
        int effectiveLimit = Math.Min(
            limit > 0 ? limit : options.Search.DefaultLimit,
            options.Search.MaxLimit);

        IReadOnlyList<string> targetSources = sources?.Count > 0
            ? sources
            : options.Services.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

        // Fan-out search to all target sources in parallel
        Dictionary<string, Task<SearchResponse?>> tasks = new Dictionary<string, Task<SearchResponse?>>();
        List<string> warnings = new List<string>();

        foreach (string sourceName in targetSources)
        {
            if (!httpClient.IsSourceEnabled(sourceName))
            {
                warnings.Add($"Source '{sourceName}' is not configured or disabled");
                continue;
            }

            tasks[sourceName] = httpClient.SearchAsync(sourceName, query, effectiveLimit, ct);
        }

        // Collect results, handling partial failures
        List<ScoredItem> allItems = new List<ScoredItem>();

        foreach ((string? sourceName, Task<SearchResponse?>? task) in tasks)
        {
            try
            {
                SearchResponse? response = await task;
                if (response is null) continue;

                foreach (SearchResult result in response.Results)
                {
                    allItems.Add(new ScoredItem
                    {
                        Source = result.Source,
                        ContentType = result.ContentType,
                        Id = result.Id,
                        Title = result.Title,
                        Snippet = result.Snippet ?? "",
                        Score = result.Score,
                        Url = result.Url ?? "",
                        UpdatedAt = result.UpdatedAt,
                        Metadata = result.Metadata ?? new Dictionary<string, string>(),
                    });
                }
            }
            catch (Exception ex)
            {
                if (ex.IsTransientHttpError(out string statusDescription))
                    logger.LogWarning("Search failed for source {Source} ({HttpStatus})", sourceName, statusDescription);
                else
                    logger.LogWarning(ex, "Search failed for source {Source}", sourceName);
                warnings.Add($"Search failed for source '{sourceName}': {ex.Message}");
            }
        }

        // Pipeline: normalize → decay → limit per-source → sort
        List<ScoredItem> normalized = ScoreNormalizer.Normalize(allItems);
        List<ScoredItem> decayed = freshnessDecay.Apply(normalized);
        List<ScoredItem> sorted = decayed
            .GroupBy(i => i.Source)
            .SelectMany(g => g.OrderByDescending(i => i.Score).Take(effectiveLimit))
            .OrderByDescending(i => i.Score)
            .ToList();

        return (sorted, warnings);
    }
}
