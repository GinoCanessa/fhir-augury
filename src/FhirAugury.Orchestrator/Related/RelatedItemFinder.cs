using FhirAugury.Common.Api;
using FhirAugury.Common.Http;
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
    SourceHttpClient httpClient,
    IOptions<OrchestratorOptions> optionsAccessor,
    ILogger<RelatedItemFinder> logger)
{
    private readonly OrchestratorOptions options = optionsAccessor.Value;

    private TimeSpan PerSourceTimeout => TimeSpan.FromSeconds(options.Related.PerSourceTimeoutSeconds);

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
        List<Task<(string Source, FindRelatedResponse? Response)>> relatedTasks = [];
        foreach (string source in sources)
        {
            if (!httpClient.IsSourceEnabled(source)) continue;

            string s = source;
            relatedTasks.Add(Task.Run(async () =>
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerSourceTimeout);
                try
                {
                    FindRelatedResponse? resp = await httpClient.GetRelatedAsync(
                        s, seedSource, seedId, effectiveLimit, timeoutCts.Token);
                    return (s, resp);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("GetRelated timed out for {Source}", s);
                    return (s, (FindRelatedResponse?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex.IsTransientHttpError(out string statusDescription))
                        logger.LogWarning("GetRelated failed for {Source} ({HttpStatus})", s, statusDescription);
                    else
                        logger.LogWarning(ex, "GetRelated failed for {Source}", s);
                    return (s, (FindRelatedResponse?)null);
                }
            }, ct));
        }

        (string Source, FindRelatedResponse? Response)[] relatedResults =
            await Task.WhenAll(relatedTasks);

        foreach ((string source, FindRelatedResponse? relatedResp) in relatedResults)
        {
            if (relatedResp?.Items is null) continue;
            foreach (RelatedItem result in relatedResp.Items)
            {
                if (string.Equals(result.Source, seedSource, StringComparison.OrdinalIgnoreCase) && result.Id == seedId) continue;

                string key = $"{result.Source}:{result.Id}";
                if (!candidates.TryGetValue(key, out RelatedCandidate? candidate))
                {
                    candidate = new RelatedCandidate
                    {
                        Source = result.Source, Id = result.Id
                    };
                    candidates[key] = candidate;
                }

                double score = result.RelevanceScore > 0 ? result.RelevanceScore : 1.0;
                candidate.Score += options.Related.CrossSourceWeight * score;
                if (string.IsNullOrEmpty(candidate.Relationship))
                    candidate.Relationship = result.Relationship ?? "cross_reference";
                if (!string.IsNullOrEmpty(result.Title))
                    candidate.Title = result.Title;
                if (!string.IsNullOrEmpty(result.Url))
                    candidate.Url = result.Url;
                if (string.IsNullOrEmpty(candidate.Context))
                    candidate.Context = result.Context ?? result.Snippet;
            }
        }

        // Signal B: BM25 similarity (existing Signal 3 logic)
        if (seedItem is not null)
        {
            string searchTerms = ExtractKeyTerms(seedItem);
            if (!string.IsNullOrEmpty(searchTerms))
            {
                List<Task<SearchResponse?>> searchTasks = [];
                foreach (string source in sources)
                {
                    if (!httpClient.IsSourceEnabled(source)) continue;

                    searchTasks.Add(SearchSourceAsync(source, searchTerms, effectiveLimit, ct));
                }

                SearchResponse?[] searchResults = await Task.WhenAll(searchTasks);
                foreach (SearchResponse? searchResponse in searchResults)
                {
                    if (searchResponse?.Results is null) continue;
                    foreach (SearchResult result in searchResponse.Results)
                    {
                        if (string.Equals(result.Source, seedSource, StringComparison.OrdinalIgnoreCase) && result.Id == seedId) continue;

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

            // Signal C: Shared metadata (parallel fan-out with per-source timeout)
            if (seedItem.Metadata is not null &&
                seedItem.Metadata.TryGetValue("work_group", out string? workGroup) && !string.IsNullOrEmpty(workGroup))
            {
                List<Task<(string Source, SearchResponse? Response)>> metaTasks = [];
                foreach (string source in sources)
                {
                    if (!httpClient.IsSourceEnabled(source)) continue;

                    string s = source;
                    metaTasks.Add(Task.Run(async () =>
                    {
                        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(PerSourceTimeout);
                        try
                        {
                            SearchResponse? resp = await httpClient.SearchAsync(s, workGroup, effectiveLimit, timeoutCts.Token);
                            return (s, resp);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            logger.LogWarning("Shared metadata search timed out for {Source}", s);
                            return (s, (SearchResponse?)null);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            if (ex.IsTransientHttpError(out string statusDescription))
                                logger.LogWarning("Shared metadata search failed for {Source} ({HttpStatus})", s, statusDescription);
                            else
                                logger.LogWarning(ex, "Shared metadata search failed for {Source}", s);
                            return (s, (SearchResponse?)null);
                        }
                    }, ct));
                }

                (string Source, SearchResponse? Response)[] metaResults = await Task.WhenAll(metaTasks);
                foreach ((string source, SearchResponse? metaResp) in metaResults)
                {
                    if (metaResp?.Results is null) continue;
                    foreach (SearchResult result in metaResp.Results)
                    {
                        if (string.Equals(result.Source, seedSource, StringComparison.OrdinalIgnoreCase) && result.Id == seedId) continue;

                        bool hasSharedMetadata =
                            result.Metadata is not null &&
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
            }
        }

        // Enrich candidates missing title/url (parallel with per-call timeout)
        List<Task> enrichTasks = [];
        foreach (RelatedCandidate candidate in candidates.Values.Where(c => string.IsNullOrEmpty(c.Title)))
        {
            if (!httpClient.IsSourceEnabled(candidate.Source)) continue;

            RelatedCandidate c = candidate;
            enrichTasks.Add(Task.Run(async () =>
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerSourceTimeout);
                try
                {
                    ItemResponse? item = await httpClient.GetItemAsync(c.Source, c.Id, timeoutCts.Token);
                    if (item is not null)
                    {
                        c.Title = item.Title;
                        c.Url = item.Url;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Enrichment timed out for {Source}/{Id}", c.Source, c.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex.IsTransientHttpError(out string statusDescription))
                        logger.LogWarning("Failed to enrich related item {Source}/{Id} ({HttpStatus})", c.Source, c.Id, statusDescription);
                    else
                        logger.LogWarning(ex, "Failed to enrich related item {Source}/{Id}", c.Source, c.Id);
                }
            }, ct));
        }
        await Task.WhenAll(enrichTasks);

        // Build response
        List<RelatedItem> items = [];

        // Apply limit per-source so no single source dominates the result window
        IEnumerable<RelatedCandidate> sorted = candidates.Values
            .GroupBy(c => c.Source)
            .SelectMany(g => g.OrderByDescending(c => c.Score).Take(effectiveLimit))
            .OrderByDescending(c => c.Score);

        foreach (RelatedCandidate candidate in sorted)
        {
            items.Add(new RelatedItem
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

        return new FindRelatedResponse(
            SeedSource: seedSource,
            SeedId: seedId,
            SeedTitle: seedItem?.Title ?? "",
            Items: items);
    }

    private async Task<ItemResponse?> FetchSeedItem(string seedSource, string seedId, CancellationToken ct)
    {
        if (!httpClient.IsSourceEnabled(seedSource)) return null;

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerSourceTimeout);
        try
        {
            return await httpClient.GetItemAsync(seedSource, seedId, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Fetch seed item timed out for {Source}/{Id}", seedSource, seedId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                logger.LogWarning("Failed to fetch seed item {Source}/{Id} ({HttpStatus})", seedSource, seedId, statusDescription);
            else
                logger.LogWarning(ex, "Failed to fetch seed item {Source}/{Id}", seedSource, seedId);
            return null;
        }
    }

    private async Task<SearchResponse?> SearchSourceAsync(
        string sourceName, string query, int limit, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerSourceTimeout);
        try
        {
            return await httpClient.SearchAsync(sourceName, query, limit, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Related search timed out for source via BM25 similarity");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.IsTransientHttpError(out string statusDescription))
                logger.LogWarning("Related search failed for source via BM25 similarity ({HttpStatus})", statusDescription);
            else
                logger.LogWarning(ex, "Related search failed for source via BM25 similarity");
            return null;
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
