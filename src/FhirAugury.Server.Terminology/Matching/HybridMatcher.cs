using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Matching.Embeddings;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Composite matcher: combines the lexical and embedding signals
/// using <see cref="HybridWeightsOptions"/>. If embeddings are
/// disabled (the v1 default) this matcher will throw — the
/// controller gates it behind the same <c>Embeddings.Enabled</c>
/// check used for the plain embeddings mode.
/// </summary>
public sealed class HybridMatcher : ITerminologyMatcher
{
    private readonly LexicalMatcher _lexical;
    private readonly EmbeddingMatcher _embeddings;
    private readonly IEmbeddingProvider _provider;
    private readonly TerminologyServiceOptions _options;

    public string Mode => "hybrid";

    public HybridMatcher(
        LexicalMatcher lexical,
        EmbeddingMatcher embeddings,
        IEmbeddingProvider provider,
        IOptions<TerminologyServiceOptions> options)
    {
        _lexical = lexical;
        _embeddings = embeddings;
        _provider = provider;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<OverlapCandidate>> MatchAsync(
        NormalizedSubmission submission,
        OverlapCheckRequest request,
        CancellationToken ct)
    {
        if (!_provider.IsEnabled)
        {
            throw new InvalidOperationException(
                "HybridMatcher invoked while the embedding provider is disabled.");
        }

        // We always want the full ranked list (post-limit merging needs
        // the long tail), so issue both sub-queries with the caller's
        // limit but no MinScore — the composite filter applies later.
        OverlapCheckRequest lexicalReq = request with { MinScore = 0.0 };
        OverlapCheckRequest embeddingsReq = request with { MinScore = 0.0 };

        Task<IReadOnlyList<OverlapCandidate>> lexicalTask = _lexical.MatchAsync(submission, lexicalReq, ct);
        Task<IReadOnlyList<OverlapCandidate>> embedTask = _embeddings.MatchAsync(submission, embeddingsReq, ct);

        IReadOnlyList<OverlapCandidate> lexicalResults = await lexicalTask;
        IReadOnlyList<OverlapCandidate> embedResults = await embedTask;

        int limit = request.Limit ?? _options.Defaults.Limit;
        double minScore = request.MinScore ?? _options.Defaults.MinScore;

        double maxLex = lexicalResults.Count == 0 ? 0 : lexicalResults.Max(c => c.Score);
        double maxEmb = embedResults.Count == 0 ? 0 : embedResults.Max(c => c.Score);
        double wLex = _options.HybridWeights.Lexical;
        double wEmb = _options.HybridWeights.Embeddings;

        Dictionary<string, MergedCandidate> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (OverlapCandidate c in lexicalResults)
        {
            string key = MergeKey(c);
            double lexNorm = maxLex > 0 ? c.Score / maxLex : 0.0;
            if (!merged.TryGetValue(key, out MergedCandidate? slot))
            {
                slot = new MergedCandidate(c);
                merged[key] = slot;
            }
            slot.LexicalScore = lexNorm;
            slot.LexicalReasons = c.Reasons;
            slot.MergeSubScores(c.SubScores);
            slot.MergeSampleConcepts(c.SampleConcepts);
        }

        foreach (OverlapCandidate c in embedResults)
        {
            string key = MergeKey(c);
            double embNorm = maxEmb > 0 ? c.Score / maxEmb : 0.0;
            if (!merged.TryGetValue(key, out MergedCandidate? slot))
            {
                slot = new MergedCandidate(c);
                merged[key] = slot;
            }
            slot.EmbeddingScore = embNorm;
            slot.EmbeddingReasons = c.Reasons;
            slot.MergeSubScores(c.SubScores);
            slot.MergeSampleConcepts(c.SampleConcepts);
        }

        List<OverlapCandidate> output = new(merged.Count);
        foreach (MergedCandidate m in merged.Values)
        {
            double composite = wLex * m.LexicalScore + wEmb * m.EmbeddingScore;
            if (composite < minScore) continue;

            HashSet<string> reasonSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (string r in m.LexicalReasons) reasonSet.Add(r);
            foreach (string r in m.EmbeddingReasons) reasonSet.Add(r);

            m.SubScores["hybrid_lexical"] = Math.Round(m.LexicalScore, 4);
            m.SubScores["hybrid_embeddings"] = Math.Round(m.EmbeddingScore, 4);

            output.Add(m.Base with
            {
                Score = Math.Round(composite, 4),
                MatchCategory = ResolveCategory(m.LexicalScore, m.EmbeddingScore),
                SubScores = m.SubScores,
                Reasons = reasonSet.ToArray(),
                SampleConcepts = m.SampleConcepts.Take(5).ToArray(),
            });
        }

        return output.OrderByDescending(c => c.Score).Take(limit).ToList();
    }

    private static string MergeKey(OverlapCandidate c) =>
        $"{c.CanonicalUrl}|{c.FhirVersion}";

    private static string ResolveCategory(double lex, double emb) =>
        (lex > 0, emb > 0) switch
        {
            (true, true) => "both",
            (true, false) => "metadata",
            (false, true) => "content",
            _ => "metadata",
        };

    private sealed class MergedCandidate
    {
        public OverlapCandidate Base { get; }
        public double LexicalScore { get; set; }
        public double EmbeddingScore { get; set; }
        public string[] LexicalReasons { get; set; } = [];
        public string[] EmbeddingReasons { get; set; } = [];
        public Dictionary<string, double> SubScores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CodeDisplay> SampleConcepts { get; } = [];

        public MergedCandidate(OverlapCandidate basis) { Base = basis; }

        public void MergeSubScores(IReadOnlyDictionary<string, double> src)
        {
            foreach ((string k, double v) in src) SubScores[k] = v;
        }

        public void MergeSampleConcepts(IReadOnlyList<CodeDisplay> src)
        {
            foreach (CodeDisplay s in src)
            {
                if (!SampleConcepts.Any(x => x.Code == s.Code && x.System == s.System))
                {
                    SampleConcepts.Add(s);
                }
            }
        }
    }
}
