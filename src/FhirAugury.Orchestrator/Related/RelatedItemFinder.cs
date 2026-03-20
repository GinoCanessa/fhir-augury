using Fhiraugury;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Related;

/// <summary>
/// Finds related items across all sources using multiple signals:
/// explicit xrefs, reverse xrefs, BM25 similarity, and shared metadata.
/// </summary>
public class RelatedItemFinder(
    OrchestratorDatabase database,
    SourceRouter router,
    OrchestratorOptions options,
    ILogger<RelatedItemFinder> logger)
{
    /// <summary>
    /// Finds items related to a seed item identified by source and id.
    /// </summary>
    public async Task<FindRelatedResponse> FindRelatedAsync(
        string seedSource,
        string seedId,
        int limit,
        IReadOnlyList<string>? targetSources,
        CancellationToken ct)
    {
        var effectiveLimit = limit > 0 ? limit : options.Related.DefaultLimit;

        // Get the seed item for context
        var seedClient = router.GetSourceClient(seedSource);
        ItemResponse? seedItem = null;
        if (seedClient is not null)
        {
            try
            {
                seedItem = await seedClient.GetItemAsync(
                    new GetItemRequest { Id = seedId, IncludeContent = true }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch seed item {Source}/{Id}", seedSource, seedId);
            }
        }

        var candidates = new Dictionary<string, RelatedCandidate>(); // key: "source:id"
        using var connection = database.OpenConnection();

        // Signal 1: Explicit cross-references (items this item mentions)
        var outgoing = CrossRefLinkRecord.SelectList(connection,
            SourceType: seedSource, SourceId: seedId);
        foreach (var link in outgoing)
        {
            if (targetSources?.Count > 0 && !targetSources.Contains(link.TargetType))
                continue;

            var key = $"{link.TargetType}:{link.TargetId}";
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidate = new RelatedCandidate { Source = link.TargetType, Id = link.TargetId };
                candidates[key] = candidate;
            }
            candidate.Score += options.Related.ExplicitXrefWeight;
            candidate.Relationship = "referenced_by_seed";
            candidate.Context = link.Context ?? "";
        }

        // Signal 2: Reverse cross-references (items that mention this item)
        var incoming = CrossRefLinkRecord.SelectList(connection,
            TargetType: seedSource, TargetId: seedId);
        foreach (var link in incoming)
        {
            if (targetSources?.Count > 0 && !targetSources.Contains(link.SourceType))
                continue;

            var key = $"{link.SourceType}:{link.SourceId}";
            if (!candidates.TryGetValue(key, out var candidate))
            {
                candidate = new RelatedCandidate { Source = link.SourceType, Id = link.SourceId };
                candidates[key] = candidate;
            }
            candidate.Score += options.Related.ReverseXrefWeight;
            candidate.Relationship = "references_seed";
            candidate.Context = link.Context ?? "";
        }

        // Signal 3: BM25 similarity — extract key terms from seed and search
        if (seedItem is not null)
        {
            var searchTerms = ExtractKeyTerms(seedItem);
            if (!string.IsNullOrEmpty(searchTerms))
            {
                var searchSources = targetSources?.Count > 0
                    ? targetSources
                    : options.Services.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

                var searchTasks = new List<Task<SearchResponse>>();
                foreach (var source in searchSources)
                {
                    var client = router.GetSourceClient(source);
                    if (client is null) continue;

                    searchTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            return await client.SearchAsync(
                                new SearchRequest { Query = searchTerms, Limit = effectiveLimit },
                                cancellationToken: ct);
                        }
                        catch
                        {
                            return new SearchResponse();
                        }
                    }, ct));
                }

                var searchResults = await Task.WhenAll(searchTasks);
                foreach (var searchResponse in searchResults)
                {
                    foreach (var result in searchResponse.Results)
                    {
                        // Skip the seed item itself
                        if (result.Source == seedSource && result.Id == seedId)
                            continue;

                        var key = $"{result.Source}:{result.Id}";
                        if (!candidates.TryGetValue(key, out var candidate))
                        {
                            candidate = new RelatedCandidate { Source = result.Source, Id = result.Id };
                            candidates[key] = candidate;
                        }
                        candidate.Score += options.Related.Bm25SimilarityWeight * (result.Score > 0 ? Math.Min(result.Score, 1.0) : 0.5);
                        if (string.IsNullOrEmpty(candidate.Relationship))
                            candidate.Relationship = "similar_content";
                        if (string.IsNullOrEmpty(candidate.Context))
                            candidate.Context = result.Snippet;
                        candidate.Title = result.Title;
                        candidate.Url = result.Url;
                    }
                }
            }

            // Signal 4: Shared metadata (work group, specification, labels)
            if (seedItem.Metadata.TryGetValue("work_group", out var workGroup) && !string.IsNullOrEmpty(workGroup))
            {
                foreach (var (source, config) in options.Services)
                {
                    if (!config.Enabled) continue;
                    if (targetSources?.Count > 0 && !targetSources.Contains(source)) continue;

                    var client = router.GetSourceClient(source);
                    if (client is null) continue;

                    try
                    {
                        var metaResults = await client.SearchAsync(
                            new SearchRequest { Query = workGroup, Limit = effectiveLimit },
                            cancellationToken: ct);

                        foreach (var result in metaResults.Results)
                        {
                            if (result.Source == seedSource && result.Id == seedId) continue;

                            var hasSharedMetadata =
                                result.Metadata.TryGetValue("work_group", out var rWg) && rWg == workGroup;
                            if (!hasSharedMetadata) continue;

                            var key = $"{result.Source}:{result.Id}";
                            if (!candidates.TryGetValue(key, out var candidate))
                            {
                                candidate = new RelatedCandidate { Source = result.Source, Id = result.Id };
                                candidates[key] = candidate;
                            }
                            candidate.Score += options.Related.SharedMetadataWeight;
                            if (string.IsNullOrEmpty(candidate.Relationship))
                                candidate.Relationship = "shared_metadata";
                            candidate.Title = result.Title;
                            candidate.Url = result.Url;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Shared metadata search failed for {Source}", source);
                    }
                }
            }
        }

        // Enrich candidates that don't have title/url yet
        foreach (var candidate in candidates.Values.Where(c => string.IsNullOrEmpty(c.Title)))
        {
            var client = router.GetSourceClient(candidate.Source);
            if (client is null) continue;

            try
            {
                var item = await client.GetItemAsync(
                    new GetItemRequest { Id = candidate.Id }, cancellationToken: ct);
                candidate.Title = item.Title;
                candidate.Url = item.Url;
            }
            catch
            {
                // Item may not exist or service may be down
            }
        }

        // Build response
        var response = new FindRelatedResponse
        {
            SeedSource = seedSource,
            SeedId = seedId,
            SeedTitle = seedItem?.Title ?? "",
        };

        var sorted = candidates.Values
            .OrderByDescending(c => c.Score)
            .Take(effectiveLimit);

        foreach (var candidate in sorted)
        {
            response.Items.Add(new RelatedItem
            {
                Source = candidate.Source,
                Id = candidate.Id,
                Title = candidate.Title ?? "",
                Snippet = "",
                Url = candidate.Url ?? "",
                RelevanceScore = candidate.Score,
                Relationship = candidate.Relationship ?? "related",
                Context = candidate.Context ?? "",
            });
        }

        return response;
    }

    private string ExtractKeyTerms(ItemResponse item)
    {
        var text = $"{item.Title} {item.Content}";
        var tokens = Tokenizer.Tokenize(text)
            .Where(t => !StopWords.IsStopWord(t) && t.Length > 2)
            .Distinct()
            .Take(options.Related.MaxKeyTerms);
        return string.Join(" ", tokens);
    }

    private class RelatedCandidate
    {
        public required string Source { get; set; }
        public required string Id { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
        public double Score { get; set; }
        public string? Relationship { get; set; }
        public string? Context { get; set; }
    }
}
