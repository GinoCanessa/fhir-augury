using Fhiraugury;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Search;

/// <summary>
/// Dispatches search queries to all enabled source services in parallel,
/// then normalizes, boosts, and applies freshness decay to merge results.
/// </summary>
public class UnifiedSearchService(
    SourceRouter router,
    CrossRefBooster booster,
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
        var effectiveLimit = Math.Min(
            limit > 0 ? limit : options.Search.DefaultLimit,
            options.Search.MaxLimit);

        var targetSources = sources?.Count > 0
            ? sources
            : options.Services.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

        // Fan-out search to all target sources in parallel
        var tasks = new Dictionary<string, Task<SearchResponse>>();
        var warnings = new List<string>();

        foreach (var sourceName in targetSources)
        {
            var client = router.GetSourceClient(sourceName);
            if (client is null)
            {
                warnings.Add($"Source '{sourceName}' is not configured or disabled");
                continue;
            }

            tasks[sourceName] = client.SearchAsync(
                new SearchRequest { Query = query, Limit = effectiveLimit },
                cancellationToken: ct).ResponseAsync;
        }

        // Collect results, handling partial failures
        var allItems = new List<ScoredItem>();

        foreach (var (sourceName, task) in tasks)
        {
            try
            {
                var response = await task;
                foreach (var result in response.Results)
                {
                    allItems.Add(new ScoredItem
                    {
                        Source = result.Source,
                        Id = result.Id,
                        Title = result.Title,
                        Snippet = result.Snippet,
                        Score = result.Score,
                        Url = result.Url,
                        UpdatedAt = result.UpdatedAt?.ToDateTimeOffset(),
                        Metadata = new Dictionary<string, string>(result.Metadata),
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Search failed for source {Source}", sourceName);
                warnings.Add($"Search failed for source '{sourceName}': {ex.Message}");
            }
        }

        // Pipeline: normalize → boost → decay → sort → limit
        var normalized = ScoreNormalizer.Normalize(allItems);
        var boosted = booster.Boost(normalized, options.Search.CrossRefBoostFactor);
        var decayed = freshnessDecay.Apply(boosted);
        var sorted = decayed.OrderByDescending(i => i.Score).Take(effectiveLimit).ToList();

        return (sorted, warnings);
    }
}
