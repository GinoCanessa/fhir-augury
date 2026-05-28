using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Database.Records;
using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Matching.Embeddings;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Tests.Matching;

/// <summary>
/// Exercises <see cref="HybridMatcher"/>: lexical + embedding signals
/// fan out, scores are normalized, weights from
/// <see cref="HybridWeightsOptions"/> are applied, and candidates that
/// match in both signals are merged on (canonical, fhirVersion).
/// </summary>
public sealed class HybridMatcherTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TerminologyDatabase _db;

    public HybridMatcherTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"server-terminology-hybrid-{Guid.NewGuid():N}.db");
        _db = new TerminologyDatabase(_dbPath, NullLogger<TerminologyDatabase>.Instance);
        _db.Initialize();
        SeedFixtures();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (string p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Match_CombinesLexicalAndEmbeddingScores()
    {
        HybridMatcher matcher = BuildMatcher(new FakeEmbeddingProvider());

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            CanonicalUrl = "http://example.org/cs/marital",
            CanonicalUrlNormalized = "http://example.org/cs/marital",
            Title = "My Marital Status",
            Description = "Marital status codes for our system.",
            Concepts =
            [
                new NormalizedConcept(
                    "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
                    "M", "Married", "married"),
            ],
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        OverlapCandidate top = results[0];
        Assert.Equal(
            "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            top.CanonicalUrl);

        // Composite must reflect both signals via SubScores.
        Assert.True(top.SubScores.ContainsKey("hybrid_lexical"));
        Assert.True(top.SubScores.ContainsKey("hybrid_embeddings"));
        Assert.True(top.SubScores["hybrid_lexical"] > 0);
        Assert.True(top.SubScores["hybrid_embeddings"] > 0);
        Assert.Equal("both", top.MatchCategory);
    }

    [Fact]
    public async Task Match_WeightsControlComposite()
    {
        // Weight everything onto the embedding side; lexical-only hits
        // must be dropped below the (default) min-score floor.
        HybridMatcher matcher = BuildMatcher(
            new FakeEmbeddingProvider(),
            lexicalWeight: 0.0,
            embeddingsWeight: 1.0);

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            Title = "Marital Status",
        };

        IReadOnlyList<OverlapCandidate> results = await matcher.MatchAsync(
            submission, new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default);

        Assert.NotEmpty(results);
        OverlapCandidate top = results[0];
        // Composite ≈ embedding-normalized score (lexical contributes 0).
        Assert.True(Math.Abs(top.Score - top.SubScores["hybrid_embeddings"]) < 0.001);
    }

    [Fact]
    public async Task Match_Throws_WhenProviderDisabled()
    {
        HybridMatcher matcher = BuildMatcher(new NullEmbeddingProvider());

        NormalizedSubmission submission = new()
        {
            Kind = "CodeSystem",
            FhirVersion = "R4",
            Title = "Marital Status",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            matcher.MatchAsync(submission,
                new OverlapCheckRequest { Limit = 5, MinScore = 0.0 }, default));
    }

    private HybridMatcher BuildMatcher(
        IEmbeddingProvider provider,
        double lexicalWeight = 0.6,
        double embeddingsWeight = 0.4)
    {
        TerminologyServiceOptions opts = new()
        {
            HybridWeights = new HybridWeightsOptions
            {
                Lexical = lexicalWeight,
                Embeddings = embeddingsWeight,
            },
        };
        IOptions<TerminologyServiceOptions> optWrap = Options.Create(opts);
        LexicalMatcher lexical = new(_db, optWrap, NullLogger<LexicalMatcher>.Instance);
        EmbeddingMatcher embeddings = new(_db, provider, optWrap,
            NullLogger<EmbeddingMatcher>.Instance);
        return new HybridMatcher(lexical, embeddings, provider, optWrap);
    }

    private void SeedFixtures()
    {
        TerminologyPackageRecord pkg = new()
        {
            Id = TerminologyPackageRecord.GetIndex(),
            PackageId = "hl7.terminology.r4",
            RequestedVersionTag = "latest",
            ResolvedVersion = "5.4.0",
            FhirVersion = "R4",
            IngestedAt = DateTimeOffset.UtcNow,
            ArtifactCount = 0,
            ConceptCount = 0,
        };
        using SqliteConnection conn = _db.OpenConnection();
        TerminologyPackageRecord.Insert(conn, pkg, insertPrimaryKey: true);

        SeedArtifact(conn,
            "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus",
            "V3 Marital Status", "MaritalStatus",
            "Standardized marital status codes.",
            [("M", "Married"), ("S", "Single"), ("D", "Divorced"), ("W", "Widowed")]);

        SeedArtifact(conn,
            "http://terminology.hl7.org/CodeSystem/condition-status",
            "Condition Status Codes", "ConditionStatus",
            "Codes describing the status of a condition.",
            [("preliminary", "Preliminary"), ("final", "Final")]);
    }

    private static void SeedArtifact(
        SqliteConnection conn, string url, string title, string name, string desc,
        (string Code, string Display)[] concepts)
    {
        TerminologyArtifactRecord artifact = new()
        {
            Id = TerminologyArtifactRecord.GetIndex(),
            Kind = "CodeSystem",
            CanonicalUrl = url,
            CanonicalUrlNormalized = TerminologyTextNormalizer.NormalizeCanonicalUrl(url),
            Version = "5.4.0",
            FhirVersion = "R4",
            Title = title,
            Name = name,
            Status = "Active",
            Experimental = false,
            Publisher = "HL7",
            Description = desc,
            Purpose = null,
            Keywords = null,
            PackageId = "hl7.terminology.r4",
            PackageVersion = "5.4.0",
            Json = "{}",
        };
        TerminologyArtifactRecord.Insert(conn, artifact, insertPrimaryKey: true);

        List<TerminologyConceptRecord> rows = [];
        foreach ((string code, string display) in concepts)
        {
            rows.Add(new TerminologyConceptRecord
            {
                Id = TerminologyConceptRecord.GetIndex(),
                ArtifactId = artifact.Id,
                SystemUrl = url,
                Code = code,
                Display = display,
                DisplayNormalized = TerminologyTextNormalizer.NormalizeDisplay(display),
                Definition = null,
                DesignationsJson = "[]",
                IsRetired = false,
            });
        }
        rows.Insert(conn, ignoreDuplicates: false, insertPrimaryKey: true);
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public bool IsEnabled => true;
        public int Dimensions => 3;
        public string ModelName => "fake-3d";

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            Task.FromResult(Vector(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToArray());

        private static float[] Vector(string text)
        {
            string t = text.ToLowerInvariant();
            float marital = t.Contains("marital") ? 1f : 0f;
            float condition = t.Contains("condition") ? 1f : 0f;
            float observation = t.Contains("observ") ? 1f : 0f;
            return [marital + 0.01f, condition + 0.01f, observation + 0.01f];
        }
    }
}
