using Fhiraugury;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Related;

/// <summary>
/// Finds related items across all sources using fan-out queries:
/// cross-source GetRelated, BM25 similarity, and shared metadata.
/// </summary>
public class RelatedItemFinder(
    SourceRouter router,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<RelatedItemFinder> logger)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;

    public async Task<FindRelatedResponse> FindRelatedAsync(
        string seedSource,
        string seedId,
        int limit,
        IReadOnlyList<string>? targetSources,
        CancellationToken ct)
    {
        int effectiveLimit = Math.Clamp(
            limit > 0 ? limit : options.Related.DefaultLimit, 1, 100);
        Dictionary<string, RelatedCandidate> candidates = new();

        IReadOnlyList<string> sources = targetSources?.Count > 0
            ? targetSources
            : options.Services.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

        // Fetch seed item for title/metadata
        ItemResponse? seedItem = await FetchSeedItem(seedSource, seedId, ct);

        // Signal A: Cross-source fan-out GetRelated
        List<Task<(string Source, SearchResponse Response)>> relatedTasks = [];
        foreach (string source in sources)
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null) continue;

            string s = source;
            relatedTasks.Add(Task.Run(async () =>
            {
                SearchResponse resp = await client.GetRelatedAsync(new GetRelatedRequest
                {
                    SeedSource = seedSource,
                    SeedId = seedId,
                    Limit = effectiveLimit,
                }, cancellationToken: ct);
                return (s, resp);
            }, ct));
        }

        (string Source, SearchResponse Response)[] relatedResults =
            await Task.WhenAll(relatedTasks);

        foreach ((string source, SearchResponse relatedResp) in relatedResults)
        {
            foreach (SearchResultItem result in relatedResp.Results)
            {
                if (result.Source == seedSource && result.Id == seedId) continue;

                string key = $"{result.Source}:{result.Id}";
                if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
                {
                    candidate = new RelatedCandidate
                    {
                        Source = result.Source, Id = result.Id
                    };
                    candidates[key] = candidate;
                }

                double score = result.Score > 0 ? result.Score : 1.0;
                candidate.Score += options.Related.CrossSourceWeight * score;
                if (string.IsNullOrEmpty(candidate.Relationship))
                    candidate.Relationship = "cross_reference";
                if (!string.IsNullOrEmpty(result.Title))
                    candidate.Title = result.Title;
                if (!string.IsNullOrEmpty(result.Url))
                    candidate.Url = result.Url;
                if (!string.IsNullOrEmpty(result.Snippet) &&
                    string.IsNullOrEmpty(candidate.Context))
                    candidate.Context = result.Snippet;
            }
        }

        // Signal B: BM25 similarity (existing Signal 3 logic)
        if (seedItem is not null)
        {
            string searchTerms = ExtractKeyTerms(seedItem);
            if (!string.IsNullOrEmpty(searchTerms))
            {
                List<Task<SearchResponse>> searchTasks = [];
                foreach (string source in sources)
                {
                    SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                    if (client is null) continue;

                    searchTasks.Add(SearchSourceAsync(client, searchTerms, effectiveLimit, ct));
                }

                SearchResponse[] searchResults = await Task.WhenAll(searchTasks);
                foreach (SearchResponse searchResponse in searchResults)
                {
                    foreach (SearchResultItem result in searchResponse.Results)
                    {
                        if (result.Source == seedSource && result.Id == seedId) continue;

                        string key = $"{result.Source}:{result.Id}";
                        if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
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

            // Signal C: Shared metadata
            if (seedItem.Metadata.TryGetValue("work_group", out string? workGroup) && !string.IsNullOrEmpty(workGroup))
            {
                foreach (string source in sources)
                {
                    SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                    if (client is null) continue;

                    try
                    {
                        SearchResponse metaResults = await client.SearchAsync(
                            new SearchRequest { Query = workGroup, Limit = effectiveLimit },
                            cancellationToken: ct);

                        foreach (SearchResultItem result in metaResults.Results)
                        {
                            if (result.Source == seedSource && result.Id == seedId) continue;

                            bool hasSharedMetadata =
                                result.Metadata.TryGetValue("work_group", out string? rWg) && rWg == workGroup;
                            if (!hasSharedMetadata) continue;

                            string key = $"{result.Source}:{result.Id}";
                            if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
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

        // Enrich candidates missing title/url
        foreach (RelatedCandidate candidate in candidates.Values.Where(c => string.IsNullOrEmpty(c.Title)))
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(candidate.Source);
            if (client is null) continue;

            try
            {
                ItemResponse item = await client.GetItemAsync(
                    new GetItemRequest { Id = candidate.Id }, cancellationToken: ct);
                candidate.Title = item.Title;
                candidate.Url = item.Url;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enrich related item {Source}/{Id}", candidate.Source, candidate.Id);
            }
        }

        // Build response
        FindRelatedResponse response = new FindRelatedResponse
        {
            SeedSource = seedSource,
            SeedId = seedId,
            SeedTitle = seedItem?.Title ?? "",
        };

        IEnumerable<RelatedCandidate> sorted = candidates.Values
            .OrderByDescending(c => c.Score)
            .Take(effectiveLimit);

        foreach (RelatedCandidate candidate in sorted)
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

    private async Task<ItemResponse?> FetchSeedItem(string seedSource, string seedId, CancellationToken ct)
    {
        SourceService.SourceServiceClient? seedClient = router.GetSourceClient(seedSource);
        if (seedClient is null) return null;

        try
        {
            return await seedClient.GetItemAsync(
                new GetItemRequest { Id = seedId, IncludeContent = true }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch seed item {Source}/{Id}", seedSource, seedId);
            return null;
        }
    }

    private async Task<SearchResponse> SearchSourceAsync(
        SourceService.SourceServiceClient client, string query, int limit, CancellationToken ct)
    {
        try
        {
            return await client.SearchAsync(
                new SearchRequest { Query = query, Limit = limit },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Related search failed for source via BM25 similarity");
            return new SearchResponse();
        }
    }

    private string ExtractKeyTerms(ItemResponse item)
    {
        string text = $"{item.Title} {item.Content}";
        IEnumerable<string> tokens = Tokenizer.Tokenize(text)
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
