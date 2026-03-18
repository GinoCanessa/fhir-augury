using FhirAugury.Models;

namespace FhirAugury.Indexing;

/// <summary>
/// Normalizes search scores for cross-source comparability using min-max scaling.
/// </summary>
public static class ScoreNormalizer
{
    /// <summary>
    /// Applies min-max normalization within each source group, scaling scores to [0, 1].
    /// </summary>
    /// <param name="results">The list of search results to normalize in place.</param>
    /// <param name="sourceWeights">Optional per-source weight multipliers.</param>
    public static void Normalize(List<SearchResult> results, IReadOnlyDictionary<string, double>? sourceWeights = null)
    {
        if (results.Count == 0)
        {
            return;
        }

        var sourceGroups = results
            .Select((result, index) => (Result: result, Index: index))
            .GroupBy(x => x.Result.Source);

        foreach (var group in sourceGroups)
        {
            var items = group.ToList();
            var min = items.Min(x => x.Result.Score);
            var max = items.Max(x => x.Result.Score);
            var range = max - min;

            var weight = 1.0;
            if (sourceWeights is not null && sourceWeights.TryGetValue(group.Key, out var w))
            {
                weight = w;
            }

            foreach (var (result, index) in items)
            {
                var normalized = range == 0 ? 1.0 : (result.Score - min) / range;
                results[index] = result with { NormalizedScore = normalized * weight };
            }
        }
    }
}
