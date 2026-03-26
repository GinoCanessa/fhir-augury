using FhirAugury.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Search;

/// <summary>
/// Applies per-source freshness decay at query time.
/// Formula: decay = 1 / (1 + weight × (age_days / 365.0)²)
///          final = boosted × decay
/// </summary>
public class FreshnessDecay(IOptions<OrchestratorOptions> optionsAccessor)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;
    /// <summary>
    /// Applies freshness decay to a list of scored items.
    /// Items from sources with higher freshness weights decay faster.
    /// </summary>
    public List<ScoredItem> Apply(IEnumerable<ScoredItem> items)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<ScoredItem> results = new List<ScoredItem>();

        foreach (ScoredItem item in items)
        {
            double weight = options.Search.FreshnessWeights.GetValueOrDefault(item.Source, 1.0);
            double decay = 1.0;

            if (item.UpdatedAt is DateTimeOffset updatedAt)
            {
                double ageDays = (now - updatedAt).TotalDays;
                decay = 1.0 / (1.0 + weight * Math.Pow(ageDays / 365.0, 2));
            }

            results.Add(item with { Score = item.Score * decay });
        }

        return results;
    }
}
