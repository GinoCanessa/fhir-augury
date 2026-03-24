using Fhiraugury;
using FhirAugury.Common.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Related;

/// <summary>
/// Finds related items across all sources using multiple signals:
/// explicit xrefs, reverse xrefs, BM25 similarity, and shared metadata.
/// </summary>
public class RelatedItemFinder(
    OrchestratorDatabase database,
    SourceRouter router,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<RelatedItemFinder> logger)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;
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
        int effectiveLimit = limit > 0 ? limit : options.Related.DefaultLimit;

        // Get the seed item for context
        SourceService.SourceServiceClient? seedClient = router.GetSourceClient(seedSource);
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

        Dictionary<string, RelatedCandidate> candidates = new Dictionary<string, RelatedCandidate>(); // key: "source:id"
        using SqliteConnection connection = database.OpenConnection();

        // Signal 1: Explicit cross-references (items this item mentions)
        List<CrossRefLinkRecord> outgoing = CrossRefLinkRecord.SelectList(connection,
            SourceType: seedSource, SourceId: seedId);
        foreach (CrossRefLinkRecord link in outgoing)
        {
            if (targetSources?.Count > 0 && !targetSources.Contains(link.TargetType))
                continue;

            string key = $"{link.TargetType}:{link.TargetId}";
            if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
            {
                candidate = new RelatedCandidate { Source = link.TargetType, Id = link.TargetId };
                candidates[key] = candidate;
            }
            candidate.Score += options.Related.ExplicitXrefWeight;
            candidate.Relationship = "referenced_by_seed";
            candidate.Context = link.Context ?? "";
        }

        // Signal 2: Reverse cross-references (items that mention this item)
        List<CrossRefLinkRecord> incoming = CrossRefLinkRecord.SelectList(connection,
            TargetType: seedSource, TargetId: seedId);
        foreach (CrossRefLinkRecord link in incoming)
        {
            if (targetSources?.Count > 0 && !targetSources.Contains(link.SourceType))
                continue;

            string key = $"{link.SourceType}:{link.SourceId}";
            if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
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
            string searchTerms = ExtractKeyTerms(seedItem);
            if (!string.IsNullOrEmpty(searchTerms))
            {
                IReadOnlyList<string> searchSources = targetSources?.Count > 0
                    ? targetSources
                    : options.Services.Where(s => s.Value.Enabled).Select(s => s.Key).ToList();

                List<Task<SearchResponse>> searchTasks = new List<Task<SearchResponse>>();
                foreach (string source in searchSources)
                {
                    SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                    if (client is null) continue;

                    searchTasks.Add(SearchSourceAsync(client, searchTerms, effectiveLimit, ct));
                }

                SearchResponse[] searchResults = await Task.WhenAll(searchTasks);
                foreach (SearchResponse? searchResponse in searchResults)
                {
                    foreach (SearchResultItem? result in searchResponse.Results)
                    {
                        // Skip the seed item itself
                        if (result.Source == seedSource && result.Id == seedId)
                            continue;

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

            // Signal 4: Shared metadata (work group, specification, labels)
            if (seedItem.Metadata.TryGetValue("work_group", out string? workGroup) && !string.IsNullOrEmpty(workGroup))
            {
                foreach ((string? source, SourceServiceConfig? config) in options.Services)
                {
                    if (!config.Enabled) continue;
                    if (targetSources?.Count > 0 && !targetSources.Contains(source)) continue;

                    SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                    if (client is null) continue;

                    try
                    {
                        SearchResponse metaResults = await client.SearchAsync(
                            new SearchRequest { Query = workGroup, Limit = effectiveLimit },
                            cancellationToken: ct);

                        foreach (SearchResultItem? result in metaResults.Results)
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

        // Enrich candidates that don't have title/url yet
        foreach (RelatedCandidate? candidate in candidates.Values.Where(c => string.IsNullOrEmpty(c.Title)))
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

        foreach (RelatedCandidate? candidate in sorted)
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
