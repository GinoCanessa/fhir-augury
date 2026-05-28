using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Matching.Embeddings;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Semantic / cosine matcher. Loads all artifact vectors once per
/// service instance (lazy + cached, regenerated on first request
/// after a Phase 2 refresh that bumps the artifact count), embeds the
/// submission via the configured <see cref="IEmbeddingProvider"/>,
/// and ranks by cosine similarity.
/// </summary>
/// <remarks>
/// In v1 the only shipped provider is <see cref="NullEmbeddingProvider"/>
/// (always <c>IsEnabled = false</c>), so this matcher only executes
/// when an operator wires in a real provider externally or when a
/// fake provider is injected by tests. The <c>CheckController</c>
/// short-circuits requests for this mode when
/// <c>Terminology:Embeddings:Enabled</c> is <c>false</c>, so reaching
/// <see cref="MatchAsync"/> implies the provider can be invoked.
/// </remarks>
public sealed class EmbeddingMatcher : ITerminologyMatcher
{
    private readonly TerminologyDatabase _db;
    private readonly IEmbeddingProvider _provider;
    private readonly TerminologyServiceOptions _options;
    private readonly ILogger<EmbeddingMatcher> _logger;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    private List<ArtifactVector>? _cache;
    private int _cachedRowCount;

    public string Mode => "embeddings";

    public EmbeddingMatcher(
        TerminologyDatabase db,
        IEmbeddingProvider provider,
        IOptions<TerminologyServiceOptions> options,
        ILogger<EmbeddingMatcher> logger)
    {
        _db = db;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OverlapCandidate>> MatchAsync(
        NormalizedSubmission submission,
        OverlapCheckRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(request);

        if (!_provider.IsEnabled)
        {
            throw new InvalidOperationException(
                "EmbeddingMatcher invoked while the embedding provider is disabled.");
        }

        int limit = request.Limit ?? _options.Defaults.Limit;
        double minScore = request.MinScore ?? _options.Defaults.MinScore;

        List<ArtifactVector> artifacts = await EnsureCacheAsync(ct);
        if (artifacts.Count == 0) return [];

        string submissionText = BuildText(submission.Title, submission.Name, submission.Description,
            string.Join(' ', submission.Concepts.Take(50).Select(c => c.Display ?? c.Code)));
        float[] queryVec = await _provider.EmbedAsync(submissionText, ct);

        List<OverlapCandidate> output = new(artifacts.Count);
        foreach (ArtifactVector av in artifacts)
        {
            double cosine = Cosine(queryVec, av.Vector);
            if (cosine < minScore) continue;

            string[] reasons = cosine >= 0.5
                ? [$"semantic similarity {cosine:0.00}"]
                : [];

            output.Add(new OverlapCandidate
            {
                CanonicalUrl = av.CanonicalUrl,
                Version = av.Version,
                Title = av.Title,
                Kind = av.Kind,
                FhirVersion = av.FhirVersion,
                MatchCategory = "content",
                Score = Math.Round(cosine, 4),
                SubScores = new Dictionary<string, double>
                {
                    ["cosine"] = Math.Round(cosine, 4),
                },
                Reasons = reasons,
                SampleConcepts = [],
                CrossVersion = !string.Equals(av.FhirVersion, submission.FhirVersion, StringComparison.OrdinalIgnoreCase),
            });
        }

        return output.OrderByDescending(c => c.Score).Take(limit).ToList();
    }

    private async Task<List<ArtifactVector>> EnsureCacheAsync(CancellationToken ct)
    {
        int currentCount = CountArtifacts();
        if (_cache is not null && currentCount == _cachedRowCount) return _cache;

        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cache is not null && currentCount == _cachedRowCount) return _cache;

            List<ArtifactRow> rows = LoadArtifacts();
            if (rows.Count == 0)
            {
                _cache = [];
                _cachedRowCount = 0;
                return _cache;
            }

            string[] texts = rows.Select(r =>
                BuildText(r.Title, r.Name, r.Description, r.Sample)).ToArray();
            _logger.LogInformation(
                "Embedding {Count} artifacts via {Model} (dim={Dim}).",
                texts.Length, _provider.ModelName, _provider.Dimensions);

            IReadOnlyList<float[]> vecs = await _provider.EmbedBatchAsync(texts, ct);
            if (vecs.Count != rows.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding provider returned {vecs.Count} vectors for {rows.Count} artifacts.");
            }

            List<ArtifactVector> built = new(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                built.Add(new ArtifactVector(
                    rows[i].CanonicalUrl, rows[i].Version, rows[i].Title,
                    rows[i].Kind, rows[i].FhirVersion, vecs[i]));
            }

            _cache = built;
            _cachedRowCount = rows.Count;
            return _cache;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private int CountArtifacts()
    {
        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM terminology_artifacts;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private List<ArtifactRow> LoadArtifacts()
    {
        List<ArtifactRow> rows = [];
        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.Id, a.Kind, a.CanonicalUrl, a.Version, a.FhirVersion,
                   a.Title, a.Name, a.Description,
                   (SELECT GROUP_CONCAT(c.Display, ' ')
                      FROM (SELECT Display FROM terminology_concepts
                            WHERE ArtifactId = a.Id LIMIT 25) c) AS Sample
            FROM terminology_artifacts a;
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new ArtifactRow
            {
                Kind = r.GetString(1),
                CanonicalUrl = r.GetString(2),
                Version = r.IsDBNull(3) ? null : r.GetString(3),
                FhirVersion = r.GetString(4),
                Title = r.IsDBNull(5) ? null : r.GetString(5),
                Name = r.IsDBNull(6) ? null : r.GetString(6),
                Description = r.IsDBNull(7) ? null : r.GetString(7),
                Sample = r.IsDBNull(8) ? null : r.GetString(8),
            });
        }
        return rows;
    }

    private static string BuildText(params string?[] parts) =>
        string.Join(" \n ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0.0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private sealed record ArtifactRow
    {
        public required string Kind { get; init; }
        public required string CanonicalUrl { get; init; }
        public required string FhirVersion { get; init; }
        public string? Version { get; init; }
        public string? Title { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Sample { get; init; }
    }

    private sealed record ArtifactVector(
        string CanonicalUrl, string? Version, string? Title,
        string Kind, string FhirVersion, float[] Vector);
}
