using Fhiraugury;
using FhirAugury.Common.Grpc;
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
        List<Task<(string Source, SearchResponse Response)>> relatedTasks = [];
        foreach (string source in sources)
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(source);
            if (client is null) continue;

            string s = source;
            relatedTasks.Add(Task.Run(async () =>
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerSourceTimeout);
                try
                {
                    SearchResponse resp = await client.GetRelatedAsync(new GetRelatedRequest
                    {
                        SeedSource = seedSource,
                        SeedId = seedId,
                        Limit = effectiveLimit,
                    }, cancellationToken: timeoutCts.Token);
                    return (s, resp);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("GetRelated timed out for {Source}", s);
                    return (s, new SearchResponse());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex.IsTransientGrpcError(out string status))
                        logger.LogWarning("GetRelated failed for {Source} ({GrpcStatus})", s, status);
                    else
                        logger.LogWarning(ex, "GetRelated failed for {Source}", s);
                    return (s, new SearchResponse());
                }
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

            // Signal C: Shared metadata (parallel fan-out with per-source timeout)
            if (seedItem.Metadata.TryGetValue("work_group", out string? workGroup) && !string.IsNullOrEmpty(workGroup))
            {
                List<Task<(string Source, SearchResponse Response)>> metaTasks = [];
                foreach (string source in sources)
                {
                    SourceService.SourceServiceClient? client = router.GetSourceClient(source);
                    if (client is null) continue;

                    string s = source;
                    metaTasks.Add(Task.Run(async () =>
                    {
                        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(PerSourceTimeout);
                        try
                        {
                            SearchResponse resp = await client.SearchAsync(
                                new SearchRequest { Query = workGroup, Limit = effectiveLimit },
                                cancellationToken: timeoutCts.Token);
                            return (s, resp);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            logger.LogWarning("Shared metadata search timed out for {Source}", s);
                            return (s, new SearchResponse());
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            if (ex.IsTransientGrpcError(out string status))
                                logger.LogWarning("Shared metadata search failed for {Source} ({GrpcStatus})", s, status);
                            else
                                logger.LogWarning(ex, "Shared metadata search failed for {Source}", s);
                            return (s, new SearchResponse());
                        }
                    }, ct));
                }

                (string Source, SearchResponse Response)[] metaResults = await Task.WhenAll(metaTasks);
                foreach ((string source, SearchResponse metaResp) in metaResults)
                {
                    foreach (SearchResultItem result in metaResp.Results)
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
            }
        }

        // Enrich candidates missing title/url (parallel with per-call timeout)
        List<Task> enrichTasks = [];
        foreach (RelatedCandidate candidate in candidates.Values.Where(c => string.IsNullOrEmpty(c.Title)))
        {
            SourceService.SourceServiceClient? client = router.GetSourceClient(candidate.Source);
            if (client is null) continue;

            RelatedCandidate c = candidate;
            enrichTasks.Add(Task.Run(async () =>
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PerSourceTimeout);
                try
                {
                    ItemResponse item = await client.GetItemAsync(
                        new GetItemRequest { Id = c.Id }, cancellationToken: timeoutCts.Token);
                    c.Title = item.Title;
                    c.Url = item.Url;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Enrichment timed out for {Source}/{Id}", c.Source, c.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex.IsTransientGrpcError(out string status))
                        logger.LogWarning("Failed to enrich related item {Source}/{Id} ({GrpcStatus})", c.Source, c.Id, status);
                    else
                        logger.LogWarning(ex, "Failed to enrich related item {Source}/{Id}", c.Source, c.Id);
                }
            }, ct));
        }
        await Task.WhenAll(enrichTasks);

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

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerSourceTimeout);
        try
        {
            return await seedClient.GetItemAsync(
                new GetItemRequest { Id = seedId, IncludeContent = true }, cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Fetch seed item timed out for {Source}/{Id}", seedSource, seedId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.IsTransientGrpcError(out string status))
                logger.LogWarning("Failed to fetch seed item {Source}/{Id} ({GrpcStatus})", seedSource, seedId, status);
            else
                logger.LogWarning(ex, "Failed to fetch seed item {Source}/{Id}", seedSource, seedId);
            return null;
        }
    }

    private async Task<SearchResponse> SearchSourceAsync(
        SourceService.SourceServiceClient client, string query, int limit, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PerSourceTimeout);
        try
        {
            return await client.SearchAsync(
                new SearchRequest { Query = query, Limit = limit },
                cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Related search timed out for source via BM25 similarity");
            return new SearchResponse();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.IsTransientGrpcError(out string status))
                logger.LogWarning("Related search failed for source via BM25 similarity ({GrpcStatus})", status);
            else
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
